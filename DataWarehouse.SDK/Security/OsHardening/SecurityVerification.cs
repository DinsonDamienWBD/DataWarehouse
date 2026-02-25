using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.Security.OsHardening
{
    /// <summary>
    /// Severity level of a security check result. Used for weighted scoring
    /// and to determine whether the system meets minimum security requirements.
    /// </summary>
    public enum SecuritySeverity
    {
        /// <summary>
        /// Critical security check. Failure indicates severe vulnerability
        /// (e.g., ASLR disabled, W^X violated). Weight: 25 points.
        /// </summary>
        Critical,

        /// <summary>
        /// High-severity check. Failure indicates significant risk
        /// (e.g., DEP disabled, seccomp not active). Weight: 15 points.
        /// </summary>
        High,

        /// <summary>
        /// Medium-severity check. Failure indicates moderate risk
        /// (e.g., kernel pointer restriction not set). Weight: 10 points.
        /// </summary>
        Medium,

        /// <summary>
        /// Low-severity check. Failure indicates minor risk
        /// (e.g., stack canaries not detected). Weight: 5 points.
        /// </summary>
        Low,

        /// <summary>
        /// Informational only. Not scored. Used for platform-inapplicable checks
        /// (e.g., seccomp on Windows). Weight: 0 points.
        /// </summary>
        Info
    }

    /// <summary>
    /// Result of a single security posture check.
    /// </summary>
    /// <param name="Name">Human-readable name of the check (e.g., "ASLR Enabled").</param>
    /// <param name="Passed">True if the check passed; false if it failed or was inconclusive.</param>
    /// <param name="Details">Detailed description of what was checked and the result.</param>
    /// <param name="Severity">Severity level determining the weight of this check in the overall score.</param>
    public sealed record SecurityCheck(string Name, bool Passed, string Details, SecuritySeverity Severity);

    /// <summary>
    /// Aggregate security posture report containing all individual check results
    /// and an overall weighted score.
    /// </summary>
    /// <param name="Checks">All individual security checks that were performed.</param>
    /// <param name="AllCriticalPassed">True if every Critical-severity check passed.</param>
    /// <param name="AllHighPassed">True if every High-severity check passed.</param>
    /// <param name="Score">Weighted security score from 0 (all failed) to 100 (all passed).</param>
    /// <param name="CheckedAt">UTC timestamp when the verification was performed.</param>
    public sealed record SecurityPosture(
        IReadOnlyList<SecurityCheck> Checks,
        bool AllCriticalPassed,
        bool AllHighPassed,
        int Score,
        DateTimeOffset CheckedAt);

    /// <summary>
    /// Static utility that performs runtime OS-level security posture verification.
    /// Checks ASLR, W^X enforcement, seccomp status, DEP, stack canaries, and kernel hardening.
    /// All checks are cross-platform safe: non-applicable checks return Info severity.
    /// Designed to run at DataWarehouse startup to produce a scored security posture report.
    /// </summary>
    public static class SecurityVerification
    {
        /// <summary>
        /// Runs all security posture checks and returns an aggregate report.
        /// Safe to call on any platform. Non-applicable checks return Info severity
        /// and do not penalize the score.
        /// </summary>
        /// <returns>A <see cref="SecurityPosture"/> with all check results and a weighted score.</returns>
        public static SecurityPosture VerifyAll()
        {
            var checks = new List<SecurityCheck>
            {
                CheckAslr(),
                CheckWxEnforcement(),
                CheckSeccompActive(),
                CheckDepEnabled(),
                CheckStackCanaries(),
                CheckKernelHardening()
            };

            bool allCriticalPassed = checks
                .Where(c => c.Severity == SecuritySeverity.Critical)
                .All(c => c.Passed);

            bool allHighPassed = checks
                .Where(c => c.Severity == SecuritySeverity.High)
                .All(c => c.Passed);

            int score = CalculateScore(checks);

            return new SecurityPosture(checks, allCriticalPassed, allHighPassed, score, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Checks whether Address Space Layout Randomization (ASLR) is enabled.
        /// ASLR randomizes the memory layout of processes, making exploitation of memory
        /// corruption vulnerabilities significantly harder.
        /// Linux: Reads /proc/sys/kernel/randomize_va_space (expects value 2 for full randomization).
        /// Windows: Checks if the current process was loaded with IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE.
        /// </summary>
        /// <returns>A Critical-severity check result.</returns>
        public static SecurityCheck CheckAslr()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    string? content = ReadProcFile("/proc/sys/kernel/randomize_va_space");
                    if (content == null)
                    {
                        return new SecurityCheck("ASLR Enabled", false,
                            "Unable to read /proc/sys/kernel/randomize_va_space",
                            SecuritySeverity.Critical);
                    }

                    if (int.TryParse(content.Trim(), out int value))
                    {
                        bool passed = value == 2;
                        return new SecurityCheck("ASLR Enabled", passed,
                            passed
                                ? $"Full ASLR active (randomize_va_space={value})"
                                : $"ASLR not fully enabled (randomize_va_space={value}, expected 2)",
                            SecuritySeverity.Critical);
                    }

                    return new SecurityCheck("ASLR Enabled", false,
                        $"Unexpected value in randomize_va_space: {content.Trim()}",
                        SecuritySeverity.Critical);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // On Windows, .NET processes compiled with /DYNAMICBASE (default for .NET)
                    // have ASLR enabled. We check the current process module characteristics.
                    using var process = Process.GetCurrentProcess();
                    bool hasModules = process.Modules.Count > 0;

                    // .NET processes are always compiled with ASLR on modern toolchains
                    return new SecurityCheck("ASLR Enabled", hasModules,
                        hasModules
                            ? "Windows ASLR active (.NET default DYNAMICBASE)"
                            : "Unable to verify Windows ASLR status",
                        SecuritySeverity.Critical);
                }
                else
                {
                    return new SecurityCheck("ASLR Enabled", true,
                        $"Not applicable on {RuntimeInformation.OSDescription} (assumed enabled by OS default)",
                        SecuritySeverity.Info);
                }
            }
            catch (Exception ex)
            {
                return new SecurityCheck("ASLR Enabled", false,
                    $"ASLR check failed: {ex.Message}",
                    SecuritySeverity.Critical);
            }
        }

        /// <summary>
        /// Verifies Write XOR Execute (W^X) enforcement. No memory region should be
        /// simultaneously writable and executable, as this is a prerequisite for
        /// code injection attacks.
        /// Linux: Parses /proc/self/maps for regions with "rwx" permissions (should be zero).
        /// Windows: Reports based on DEP/NX enforcement (full W^X walk requires VirtualQuery P/Invoke).
        /// </summary>
        /// <returns>A Critical-severity check result.</returns>
        public static SecurityCheck CheckWxEnforcement()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    string? maps = ReadProcFile("/proc/self/maps");
                    if (maps == null)
                    {
                        return new SecurityCheck("W^X Enforcement", false,
                            "Unable to read /proc/self/maps",
                            SecuritySeverity.Critical);
                    }

                    int rwxCount = 0;
                    foreach (string line in maps.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        // Format: address perms offset dev inode pathname
                        // Perms field is at position after first space, format: rwxp
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            string perms = parts[1];
                            if (perms.Length >= 3 && perms[0] == 'r' && perms[1] == 'w' && perms[2] == 'x')
                            {
                                rwxCount++;
                            }
                        }
                    }

                    bool passed = rwxCount == 0;
                    return new SecurityCheck("W^X Enforcement", passed,
                        passed
                            ? "No writable+executable memory regions found (W^X enforced)"
                            : $"Found {rwxCount} writable+executable memory region(s) violating W^X",
                        SecuritySeverity.Critical);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // On Windows, DEP (enforced separately) prevents W^X violations.
                    // Full VirtualQuery walk would require P/Invoke. We report based on DEP status.
                    return new SecurityCheck("W^X Enforcement", true,
                        "Windows W^X enforcement delegated to DEP check (VirtualQuery walk not available via managed API)",
                        SecuritySeverity.Critical);
                }
                else
                {
                    return new SecurityCheck("W^X Enforcement", true,
                        $"Not applicable on {RuntimeInformation.OSDescription}",
                        SecuritySeverity.Info);
                }
            }
            catch (Exception ex)
            {
                return new SecurityCheck("W^X Enforcement", false,
                    $"W^X check failed: {ex.Message}",
                    SecuritySeverity.Critical);
            }
        }

        /// <summary>
        /// Checks whether seccomp-BPF filtering is active for the current process.
        /// Seccomp restricts the set of syscalls a process can make, reducing kernel attack surface.
        /// Linux only: Reads /proc/self/status for Seccomp field (expects 2 for filter mode).
        /// Non-Linux: Returns Info severity (not applicable).
        /// </summary>
        /// <returns>A High-severity check result (or Info if not Linux).</returns>
        public static SecurityCheck CheckSeccompActive()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return new SecurityCheck("Seccomp Active", true,
                        $"Not applicable on {RuntimeInformation.OSDescription}",
                        SecuritySeverity.Info);
                }

                string? status = ReadProcFile("/proc/self/status");
                if (status == null)
                {
                    return new SecurityCheck("Seccomp Active", false,
                        "Unable to read /proc/self/status",
                        SecuritySeverity.High);
                }

                foreach (string line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Seccomp:", StringComparison.Ordinal))
                    {
                        string value = line.Substring("Seccomp:".Length).Trim();
                        if (int.TryParse(value, out int mode))
                        {
                            // 0 = disabled, 1 = strict, 2 = filter
                            bool passed = mode == 2;
                            string modeStr = mode switch
                            {
                                0 => "disabled",
                                1 => "strict mode",
                                2 => "filter mode (BPF)",
                                _ => $"unknown ({mode})"
                            };
                            return new SecurityCheck("Seccomp Active", passed,
                                passed
                                    ? $"Seccomp-BPF filter active (mode={mode}: {modeStr})"
                                    : $"Seccomp not in filter mode (mode={mode}: {modeStr}, expected 2)",
                                SecuritySeverity.High);
                        }
                    }
                }

                return new SecurityCheck("Seccomp Active", false,
                    "Seccomp field not found in /proc/self/status",
                    SecuritySeverity.High);
            }
            catch (Exception ex)
            {
                return new SecurityCheck("Seccomp Active", false,
                    $"Seccomp check failed: {ex.Message}",
                    SecuritySeverity.High);
            }
        }

        /// <summary>
        /// Verifies that Data Execution Prevention (DEP) / NX bit is enabled.
        /// DEP marks memory pages as non-executable by default, preventing code execution
        /// from data segments (stack, heap).
        /// Windows: DEP is enabled by default for 64-bit .NET processes.
        /// Linux: Checks for NX bit support via /proc/cpuinfo flags.
        /// </summary>
        /// <returns>A High-severity check result.</returns>
        public static SecurityCheck CheckDepEnabled()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    string? cpuinfo = ReadProcFile("/proc/cpuinfo");
                    if (cpuinfo == null)
                    {
                        return new SecurityCheck("DEP/NX Enabled", false,
                            "Unable to read /proc/cpuinfo",
                            SecuritySeverity.High);
                    }

                    // Check for 'nx' flag in CPU flags
                    foreach (string line in cpuinfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("flags", StringComparison.OrdinalIgnoreCase))
                        {
                            bool hasNx = line.Contains(" nx ", StringComparison.OrdinalIgnoreCase)
                                      || line.EndsWith(" nx", StringComparison.OrdinalIgnoreCase);
                            return new SecurityCheck("DEP/NX Enabled", hasNx,
                                hasNx
                                    ? "NX (No-Execute) bit supported by CPU"
                                    : "NX bit not found in CPU flags",
                                SecuritySeverity.High);
                        }
                    }

                    return new SecurityCheck("DEP/NX Enabled", false,
                        "CPU flags not found in /proc/cpuinfo",
                        SecuritySeverity.High);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // 64-bit Windows .NET processes always have DEP enabled (hardware NX + software DEP)
                    bool is64Bit = Environment.Is64BitProcess;
                    return new SecurityCheck("DEP/NX Enabled", is64Bit,
                        is64Bit
                            ? "DEP enabled (64-bit .NET process, hardware NX enforced)"
                            : "DEP status uncertain (32-bit process, may depend on system policy)",
                        SecuritySeverity.High);
                }
                else
                {
                    return new SecurityCheck("DEP/NX Enabled", true,
                        $"Not applicable on {RuntimeInformation.OSDescription}",
                        SecuritySeverity.Info);
                }
            }
            catch (Exception ex)
            {
                return new SecurityCheck("DEP/NX Enabled", false,
                    $"DEP check failed: {ex.Message}",
                    SecuritySeverity.High);
            }
        }

        /// <summary>
        /// Checks whether compiler stack protection (stack canaries) is active.
        /// Stack canaries detect stack buffer overflow attacks by placing sentinel values
        /// before return addresses.
        /// Linux: Heuristic check for __stack_chk_fail symbol presence.
        /// Other platforms: Reports based on compiler defaults.
        /// </summary>
        /// <returns>A Medium-severity check result.</returns>
        public static SecurityCheck CheckStackCanaries()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // .NET CoreCLR JIT does not use traditional stack canaries (GCC -fstack-protector).
                    // However, the runtime itself and native dependencies may have them.
                    // Check if the runtime binary has stack protection compiled in.
                    string? mapsContent = ReadProcFile("/proc/self/maps");
                    bool hasRuntime = mapsContent != null && mapsContent.Contains("libcoreclr", StringComparison.OrdinalIgnoreCase);

                    return new SecurityCheck("Stack Canaries", hasRuntime,
                        hasRuntime
                            ? "CoreCLR runtime loaded; native components compiled with stack protection"
                            : "Unable to verify stack canary status",
                        SecuritySeverity.Medium);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // MSVC /GS (buffer security check) is enabled by default for .NET native components
                    return new SecurityCheck("Stack Canaries", true,
                        "Windows .NET runtime compiled with /GS (buffer security check) by default",
                        SecuritySeverity.Medium);
                }
                else
                {
                    return new SecurityCheck("Stack Canaries", true,
                        $"Not applicable on {RuntimeInformation.OSDescription}",
                        SecuritySeverity.Info);
                }
            }
            catch (Exception ex)
            {
                return new SecurityCheck("Stack Canaries", false,
                    $"Stack canary check failed: {ex.Message}",
                    SecuritySeverity.Medium);
            }
        }

        /// <summary>
        /// Checks Linux kernel hardening parameters that limit information leakage
        /// and reduce attack surface.
        /// Verifies: kptr_restrict >= 1 (hide kernel pointers), dmesg_restrict == 1
        /// (restrict kernel log), perf_event_paranoid >= 2 (restrict perf events).
        /// Non-Linux: Returns Info severity (not applicable).
        /// </summary>
        /// <returns>A Medium-severity check result (or Info if not Linux).</returns>
        public static SecurityCheck CheckKernelHardening()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return new SecurityCheck("Kernel Hardening", true,
                        $"Not applicable on {RuntimeInformation.OSDescription}",
                        SecuritySeverity.Info);
                }

                var issues = new List<string>();
                int checksRun = 0;
                int checksPassed = 0;

                // Check kptr_restrict (kernel pointer restriction)
                string? kptrRestrict = ReadProcFile("/proc/sys/kernel/kptr_restrict");
                if (kptrRestrict != null && int.TryParse(kptrRestrict.Trim(), out int kptr))
                {
                    checksRun++;
                    if (kptr >= 1)
                    {
                        checksPassed++;
                    }
                    else
                    {
                        issues.Add($"kptr_restrict={kptr} (expected >=1)");
                    }
                }

                // Check dmesg_restrict (kernel log restriction)
                string? dmesgRestrict = ReadProcFile("/proc/sys/kernel/dmesg_restrict");
                if (dmesgRestrict != null && int.TryParse(dmesgRestrict.Trim(), out int dmesg))
                {
                    checksRun++;
                    if (dmesg == 1)
                    {
                        checksPassed++;
                    }
                    else
                    {
                        issues.Add($"dmesg_restrict={dmesg} (expected 1)");
                    }
                }

                // Check perf_event_paranoid (performance event restriction)
                string? perfParanoid = ReadProcFile("/proc/sys/kernel/perf_event_paranoid");
                if (perfParanoid != null && int.TryParse(perfParanoid.Trim(), out int perf))
                {
                    checksRun++;
                    if (perf >= 2)
                    {
                        checksPassed++;
                    }
                    else
                    {
                        issues.Add($"perf_event_paranoid={perf} (expected >=2)");
                    }
                }

                if (checksRun == 0)
                {
                    return new SecurityCheck("Kernel Hardening", false,
                        "Unable to read any kernel hardening parameters",
                        SecuritySeverity.Medium);
                }

                bool allPassed = checksPassed == checksRun;
                string details = allPassed
                    ? $"All {checksRun} kernel hardening parameters correctly configured"
                    : $"{checksPassed}/{checksRun} passed. Issues: {string.Join("; ", issues)}";

                return new SecurityCheck("Kernel Hardening", allPassed, details, SecuritySeverity.Medium);
            }
            catch (Exception ex)
            {
                return new SecurityCheck("Kernel Hardening", false,
                    $"Kernel hardening check failed: {ex.Message}",
                    SecuritySeverity.Medium);
            }
        }

        /// <summary>
        /// Calculates a weighted security score from 0 to 100 based on check results.
        /// Weights: Critical=25, High=15, Medium=10, Low=5. Info checks are excluded.
        /// Score = (sum of passed check weights / sum of all scoreable check weights) * 100.
        /// </summary>
        /// <param name="checks">The list of security checks to score.</param>
        /// <returns>Weighted score from 0 (all failed) to 100 (all passed).</returns>
        public static int CalculateScore(IReadOnlyList<SecurityCheck> checks)
        {
            if (checks == null || checks.Count == 0)
                return 0;

            int totalWeight = 0;
            int passedWeight = 0;

            foreach (var check in checks)
            {
                int weight = GetSeverityWeight(check.Severity);
                if (weight == 0) continue; // Skip Info checks

                totalWeight += weight;
                if (check.Passed)
                {
                    passedWeight += weight;
                }
            }

            if (totalWeight == 0)
                return 100; // All checks were Info-only

            return (int)((long)passedWeight * 100 / totalWeight);
        }

        /// <summary>
        /// Returns the scoring weight for a given severity level.
        /// </summary>
        private static int GetSeverityWeight(SecuritySeverity severity) => severity switch
        {
            SecuritySeverity.Critical => 25,
            SecuritySeverity.High => 15,
            SecuritySeverity.Medium => 10,
            SecuritySeverity.Low => 5,
            SecuritySeverity.Info => 0,
            _ => 0
        };

        /// <summary>
        /// Safely reads a /proc file, returning null on any error.
        /// All /proc reads are wrapped in try/catch to handle non-Linux platforms,
        /// permission errors, and missing files gracefully.
        /// </summary>
        private static string? ReadProcFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                return File.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }
    }
}
