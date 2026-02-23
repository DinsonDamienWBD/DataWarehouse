using System;
using System.Collections.Generic;
using System.Text;

namespace DataWarehouse.SDK.Security.OsHardening
{
    /// <summary>
    /// Action taken when a syscall matches a seccomp rule.
    /// </summary>
    public enum SyscallAction
    {
        /// <summary>Allow the syscall to proceed.</summary>
        Allow,
        /// <summary>Immediately kill the process (SECCOMP_RET_KILL).</summary>
        Kill,
        /// <summary>Send SIGSYS signal to the process (SECCOMP_RET_TRAP).</summary>
        Trap,
        /// <summary>Return an errno value to the caller (SECCOMP_RET_ERRNO).</summary>
        Errno,
        /// <summary>Log the syscall but allow it (SECCOMP_RET_LOG).</summary>
        Log,
        /// <summary>Notify a tracing process (SECCOMP_RET_TRACE).</summary>
        Trace
    }

    /// <summary>
    /// A single seccomp-BPF rule mapping a Linux syscall number to an action.
    /// </summary>
    /// <param name="SyscallNumber">The Linux syscall number (architecture-specific).</param>
    /// <param name="Action">The action to take when this syscall is invoked.</param>
    /// <param name="SyscallName">Human-readable syscall name for documentation and logging.</param>
    public sealed record SeccompRule(int SyscallNumber, SyscallAction Action, string? SyscallName = null);

    /// <summary>
    /// Defines a seccomp-BPF profile for Linux syscall filtering.
    /// Profiles specify a default-deny policy with an explicit allowlist of permitted syscalls.
    /// Used to restrict the kernel attack surface for both core DataWarehouse processes
    /// and untrusted plugin sandboxes.
    /// </summary>
    public sealed class SeccompProfile
    {
        /// <summary>Profile name (e.g., "datawarehouse-core", "datawarehouse-plugin").</summary>
        public string Name { get; }

        /// <summary>
        /// Default action for syscalls not in the allowlist. Should be <see cref="SyscallAction.Kill"/>
        /// for maximum security (deny all not explicitly allowed).
        /// </summary>
        public SyscallAction DefaultAction { get; }

        /// <summary>The ordered list of syscall filtering rules (allowlist).</summary>
        public IReadOnlyList<SeccompRule> Rules { get; }

        /// <summary>
        /// Creates a new seccomp profile with the specified rules.
        /// </summary>
        /// <param name="name">Profile name for identification and logging.</param>
        /// <param name="defaultAction">Action for unmatched syscalls (typically Kill).</param>
        /// <param name="rules">Ordered allowlist of syscall rules.</param>
        public SeccompProfile(string name, SyscallAction defaultAction, IReadOnlyList<SeccompRule> rules)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            DefaultAction = defaultAction;
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
        }

        /// <summary>
        /// Creates the core DataWarehouse process seccomp profile with a comprehensive syscall allowlist.
        /// Permits standard I/O, memory management, networking, threading, and file operations
        /// required for normal DataWarehouse operation.
        /// </summary>
        /// <returns>A seccomp profile suitable for the main DataWarehouse process.</returns>
        public static SeccompProfile CreateCoreProfile()
        {
            var rules = new List<SeccompRule>
            {
                // File I/O
                new(0, SyscallAction.Allow, "read"),
                new(1, SyscallAction.Allow, "write"),
                new(2, SyscallAction.Allow, "open"),
                new(3, SyscallAction.Allow, "close"),
                new(5, SyscallAction.Allow, "fstat"),
                new(17, SyscallAction.Allow, "pread64"),
                new(18, SyscallAction.Allow, "pwrite64"),
                new(21, SyscallAction.Allow, "access"),
                new(22, SyscallAction.Allow, "pipe"),
                new(72, SyscallAction.Allow, "fcntl"),
                new(73, SyscallAction.Allow, "flock"),
                new(74, SyscallAction.Allow, "fsync"),
                new(75, SyscallAction.Allow, "fdatasync"),
                new(76, SyscallAction.Allow, "truncate"),
                new(77, SyscallAction.Allow, "ftruncate"),
                new(78, SyscallAction.Allow, "getdents"),
                new(79, SyscallAction.Allow, "getcwd"),
                new(80, SyscallAction.Allow, "chdir"),
                new(83, SyscallAction.Allow, "mkdir"),
                new(82, SyscallAction.Allow, "rename"),
                new(87, SyscallAction.Allow, "unlink"),
                new(89, SyscallAction.Allow, "readlink"),
                new(90, SyscallAction.Allow, "chmod"),
                new(92, SyscallAction.Allow, "chown"),
                new(95, SyscallAction.Allow, "umask"),
                new(257, SyscallAction.Allow, "openat"),
                new(262, SyscallAction.Allow, "newfstatat"),

                // Memory management
                new(9, SyscallAction.Allow, "mmap"),
                new(10, SyscallAction.Allow, "mprotect"),
                new(11, SyscallAction.Allow, "munmap"),
                new(12, SyscallAction.Allow, "brk"),
                new(25, SyscallAction.Allow, "mremap"),
                new(26, SyscallAction.Allow, "msync"),
                new(28, SyscallAction.Allow, "madvise"),
                new(29, SyscallAction.Allow, "shmget"),
                new(30, SyscallAction.Allow, "shmat"),

                // Signal handling
                new(13, SyscallAction.Allow, "rt_sigaction"),
                new(14, SyscallAction.Allow, "rt_sigprocmask"),

                // I/O control and scheduling
                new(16, SyscallAction.Allow, "ioctl"),
                new(23, SyscallAction.Allow, "select"),
                new(24, SyscallAction.Allow, "sched_yield"),

                // Networking
                new(41, SyscallAction.Allow, "socket"),
                new(42, SyscallAction.Allow, "connect"),
                new(44, SyscallAction.Allow, "sendto"),
                new(45, SyscallAction.Allow, "recvfrom"),
                new(46, SyscallAction.Allow, "sendmsg"),
                new(47, SyscallAction.Allow, "recvmsg"),
                new(49, SyscallAction.Allow, "bind"),
                new(50, SyscallAction.Allow, "listen"),
                new(43, SyscallAction.Allow, "accept"),

                // Process management
                new(56, SyscallAction.Allow, "clone"),
                new(59, SyscallAction.Allow, "execve"),
                new(60, SyscallAction.Allow, "exit"),
                new(231, SyscallAction.Allow, "exit_group"),
                new(61, SyscallAction.Allow, "wait4"),
                new(62, SyscallAction.Allow, "kill"),

                // System info
                new(96, SyscallAction.Allow, "gettimeofday"),
                new(97, SyscallAction.Allow, "getrlimit"),
                new(102, SyscallAction.Allow, "getuid"),
                new(104, SyscallAction.Allow, "getgid"),
                new(39, SyscallAction.Allow, "getpid"),
                new(186, SyscallAction.Allow, "gettid"),

                // Epoll (event loop)
                new(213, SyscallAction.Allow, "epoll_create"),
                new(233, SyscallAction.Allow, "epoll_ctl"),
                new(232, SyscallAction.Allow, "epoll_wait"),

                // Futex (threading primitives)
                new(202, SyscallAction.Allow, "futex"),
                new(273, SyscallAction.Allow, "set_robust_list"),
                new(274, SyscallAction.Allow, "get_robust_list"),

                // Clock
                new(228, SyscallAction.Allow, "clock_gettime"),
                new(230, SyscallAction.Allow, "clock_nanosleep"),

                // io_uring (modern async I/O)
                new(425, SyscallAction.Allow, "io_uring_setup"),
                new(426, SyscallAction.Allow, "io_uring_enter"),
                new(427, SyscallAction.Allow, "io_uring_register"),
            };

            return new SeccompProfile("datawarehouse-core", SyscallAction.Kill, rules);
        }

        /// <summary>
        /// Creates a restricted seccomp profile for untrusted plugin processes.
        /// Denies execve (no spawning child processes), socket (no network access unless
        /// explicitly granted), clone (no forking), mount/umount (no filesystem manipulation),
        /// and ptrace (no debugging other processes).
        /// </summary>
        /// <returns>A restricted seccomp profile suitable for sandboxed plugins.</returns>
        public static SeccompProfile CreatePluginProfile()
        {
            var rules = new List<SeccompRule>
            {
                // File I/O (restricted set)
                new(0, SyscallAction.Allow, "read"),
                new(1, SyscallAction.Allow, "write"),
                new(2, SyscallAction.Allow, "open"),
                new(3, SyscallAction.Allow, "close"),
                new(5, SyscallAction.Allow, "fstat"),
                new(17, SyscallAction.Allow, "pread64"),
                new(18, SyscallAction.Allow, "pwrite64"),
                new(21, SyscallAction.Allow, "access"),
                new(72, SyscallAction.Allow, "fcntl"),
                new(74, SyscallAction.Allow, "fsync"),
                new(75, SyscallAction.Allow, "fdatasync"),
                new(78, SyscallAction.Allow, "getdents"),
                new(79, SyscallAction.Allow, "getcwd"),
                new(257, SyscallAction.Allow, "openat"),
                new(262, SyscallAction.Allow, "newfstatat"),

                // Memory management
                new(9, SyscallAction.Allow, "mmap"),
                new(10, SyscallAction.Allow, "mprotect"),
                new(11, SyscallAction.Allow, "munmap"),
                new(12, SyscallAction.Allow, "brk"),
                new(25, SyscallAction.Allow, "mremap"),
                new(26, SyscallAction.Allow, "msync"),
                new(28, SyscallAction.Allow, "madvise"),

                // Signal handling
                new(13, SyscallAction.Allow, "rt_sigaction"),
                new(14, SyscallAction.Allow, "rt_sigprocmask"),

                // I/O control and scheduling
                new(16, SyscallAction.Allow, "ioctl"),
                new(23, SyscallAction.Allow, "select"),
                new(24, SyscallAction.Allow, "sched_yield"),

                // Restricted process - exit only, no clone/execve/kill
                new(60, SyscallAction.Allow, "exit"),
                new(231, SyscallAction.Allow, "exit_group"),

                // System info (read-only)
                new(96, SyscallAction.Allow, "gettimeofday"),
                new(97, SyscallAction.Allow, "getrlimit"),
                new(102, SyscallAction.Allow, "getuid"),
                new(104, SyscallAction.Allow, "getgid"),
                new(39, SyscallAction.Allow, "getpid"),
                new(186, SyscallAction.Allow, "gettid"),

                // Epoll (event loop)
                new(213, SyscallAction.Allow, "epoll_create"),
                new(233, SyscallAction.Allow, "epoll_ctl"),
                new(232, SyscallAction.Allow, "epoll_wait"),

                // Futex (threading primitives)
                new(202, SyscallAction.Allow, "futex"),
                new(273, SyscallAction.Allow, "set_robust_list"),
                new(274, SyscallAction.Allow, "get_robust_list"),

                // Clock
                new(228, SyscallAction.Allow, "clock_gettime"),
                new(230, SyscallAction.Allow, "clock_nanosleep"),

                // Explicitly denied (logged for audit):
                // execve (59) - no child process spawning
                // socket (41) - no network access
                // clone (56) - no forking
                // mount (165) - no filesystem manipulation
                // umount2 (166) - no filesystem manipulation
                // ptrace (101) - no process debugging
                new(59, SyscallAction.Kill, "execve"),
                new(41, SyscallAction.Kill, "socket"),
                new(56, SyscallAction.Kill, "clone"),
                new(165, SyscallAction.Kill, "mount"),
                new(166, SyscallAction.Kill, "umount2"),
                new(101, SyscallAction.Kill, "ptrace"),
            };

            return new SeccompProfile("datawarehouse-plugin", SyscallAction.Kill, rules);
        }

        /// <summary>
        /// Generates libseccomp-compatible BPF assembly text representation of this profile.
        /// The output can be compiled with seccomp-tools or loaded via libseccomp.
        /// </summary>
        /// <returns>BPF assembly text suitable for seccomp filter installation.</returns>
        public string GenerateBpfAssembly()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Seccomp BPF profile: {Name}");
            sb.AppendLine($"# Default action: {DefaultAction}");
            sb.AppendLine($"# Rules: {Rules.Count}");
            sb.AppendLine();

            // Load syscall number
            sb.AppendLine(" ld [0]                   # Load syscall number (offset 0 of seccomp_data)");
            sb.AppendLine();

            foreach (var rule in Rules)
            {
                string label = rule.SyscallName ?? $"syscall_{rule.SyscallNumber}";
                sb.AppendLine($" jeq #{rule.SyscallNumber}, {ActionToLabel(rule.Action)}    # {label}");
            }

            sb.AppendLine();
            sb.AppendLine($" ret #{ActionToReturnValue(DefaultAction)}    # default: {DefaultAction}");
            sb.AppendLine();

            // Action return labels
            sb.AppendLine("allow:");
            sb.AppendLine($" ret #{ActionToReturnValue(SyscallAction.Allow)}");
            sb.AppendLine("kill:");
            sb.AppendLine($" ret #{ActionToReturnValue(SyscallAction.Kill)}");
            sb.AppendLine("trap:");
            sb.AppendLine($" ret #{ActionToReturnValue(SyscallAction.Trap)}");
            sb.AppendLine("log:");
            sb.AppendLine($" ret #{ActionToReturnValue(SyscallAction.Log)}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates an AppArmor profile for the specified binary path.
        /// The profile restricts the binary to the capabilities implied by this seccomp profile.
        /// </summary>
        /// <param name="binaryPath">Absolute path to the binary to confine (e.g., /usr/bin/datawarehouse).</param>
        /// <returns>AppArmor profile text suitable for loading with apparmor_parser.</returns>
        public string GenerateAppArmorProfile(string binaryPath)
        {
            if (string.IsNullOrWhiteSpace(binaryPath))
                throw new ArgumentException("Binary path is required.", nameof(binaryPath));

            var sb = new StringBuilder();
            sb.AppendLine($"# AppArmor profile generated from seccomp profile: {Name}");
            sb.AppendLine($"# Generated at: {DateTimeOffset.UtcNow:O}");
            sb.AppendLine();
            sb.AppendLine("#include <tunables/global>");
            sb.AppendLine();
            sb.AppendLine($"profile datawarehouse-{Name} {binaryPath} flags=(enforce) {{");
            sb.AppendLine("  #include <abstractions/base>");
            sb.AppendLine();

            // Capabilities based on allowed syscalls
            bool hasNetworking = false;
            bool hasProcessCreation = false;
            bool hasMount = false;

            foreach (var rule in Rules)
            {
                if (rule.Action == SyscallAction.Allow)
                {
                    switch (rule.SyscallName)
                    {
                        case "socket":
                        case "bind":
                        case "listen":
                        case "connect":
                            hasNetworking = true;
                            break;
                        case "clone":
                        case "execve":
                            hasProcessCreation = true;
                            break;
                        case "mount":
                            hasMount = true;
                            break;
                    }
                }
            }

            // Capabilities
            sb.AppendLine("  # Capabilities");
            if (hasNetworking)
            {
                sb.AppendLine("  capability net_bind_service,");
                sb.AppendLine("  capability net_raw,");
            }
            if (hasProcessCreation)
            {
                sb.AppendLine("  capability sys_resource,");
            }
            if (hasMount)
            {
                sb.AppendLine("  capability sys_admin,");
            }
            sb.AppendLine();

            // File access
            sb.AppendLine("  # File access");
            sb.AppendLine($"  {binaryPath} mr,");
            sb.AppendLine("  /tmp/datawarehouse-** rw,");
            sb.AppendLine("  /var/lib/datawarehouse/** rwk,");
            sb.AppendLine("  /var/log/datawarehouse/** w,");
            sb.AppendLine("  /etc/datawarehouse/** r,");
            sb.AppendLine();

            // Network access
            if (hasNetworking)
            {
                sb.AppendLine("  # Network access");
                sb.AppendLine("  network inet stream,");
                sb.AppendLine("  network inet dgram,");
                sb.AppendLine("  network inet6 stream,");
                sb.AppendLine("  network inet6 dgram,");
                sb.AppendLine("  network unix stream,");
            }
            else
            {
                sb.AppendLine("  # Network access denied");
                sb.AppendLine("  deny network,");
            }
            sb.AppendLine();

            // Process rules
            if (!hasProcessCreation)
            {
                sb.AppendLine("  # No process creation");
                sb.AppendLine("  deny /bin/** x,");
                sb.AppendLine("  deny /usr/bin/** x,");
                sb.AppendLine("  deny /sbin/** x,");
            }
            sb.AppendLine();

            // Signal rules
            sb.AppendLine("  # Signals");
            sb.AppendLine("  signal send set=(term, kill) peer=unconfined,");
            sb.AppendLine();

            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Generates an SELinux Type Enforcement (.te) policy module for this seccomp profile.
        /// The generated module defines a confined domain with permissions matching the syscall allowlist.
        /// </summary>
        /// <param name="moduleNamespace">SELinux module namespace (e.g., "datawarehouse").</param>
        /// <returns>SELinux .te policy module text suitable for compilation with checkmodule.</returns>
        public string GenerateSelinuxPolicy(string moduleNamespace)
        {
            if (string.IsNullOrWhiteSpace(moduleNamespace))
                throw new ArgumentException("Module namespace is required.", nameof(moduleNamespace));

            var sb = new StringBuilder();
            string domainType = $"{moduleNamespace}_{Name.Replace("-", "_")}_t";
            string execType = $"{moduleNamespace}_{Name.Replace("-", "_")}_exec_t";
            string logType = $"{moduleNamespace}_log_t";
            string dataType = $"{moduleNamespace}_data_t";

            sb.AppendLine($"# SELinux policy module generated from seccomp profile: {Name}");
            sb.AppendLine($"# Generated at: {DateTimeOffset.UtcNow:O}");
            sb.AppendLine();
            sb.AppendLine($"policy_module({moduleNamespace}_{Name.Replace("-", "_")}, 1.0.0)");
            sb.AppendLine();

            // Type declarations
            sb.AppendLine("########################################");
            sb.AppendLine("# Type declarations");
            sb.AppendLine("########################################");
            sb.AppendLine();
            sb.AppendLine($"type {domainType};");
            sb.AppendLine($"type {execType};");
            sb.AppendLine($"type {logType};");
            sb.AppendLine($"type {dataType};");
            sb.AppendLine();

            // Domain transition
            sb.AppendLine("########################################");
            sb.AppendLine("# Domain transition");
            sb.AppendLine("########################################");
            sb.AppendLine();
            sb.AppendLine($"init_daemon_domain({domainType}, {execType})");
            sb.AppendLine();

            // Permissions based on syscall allowlist
            sb.AppendLine("########################################");
            sb.AppendLine("# Allowed operations");
            sb.AppendLine("########################################");
            sb.AppendLine();

            // File permissions
            sb.AppendLine($"allow {domainType} {dataType}:file {{ read write create open getattr setattr lock append }};");
            sb.AppendLine($"allow {domainType} {dataType}:dir {{ read write search add_name remove_name create getattr }};");
            sb.AppendLine($"allow {domainType} {logType}:file {{ write create open append getattr }};");
            sb.AppendLine($"allow {domainType} {logType}:dir {{ write search add_name getattr }};");
            sb.AppendLine();

            // Network permissions
            bool hasNetwork = false;
            foreach (var rule in Rules)
            {
                if (rule.Action == SyscallAction.Allow && rule.SyscallName == "socket")
                {
                    hasNetwork = true;
                    break;
                }
            }

            if (hasNetwork)
            {
                sb.AppendLine($"allow {domainType} self:tcp_socket {{ create connect accept listen bind getattr setopt shutdown read write }};");
                sb.AppendLine($"allow {domainType} self:udp_socket {{ create connect bind getattr setopt read write }};");
                sb.AppendLine($"allow {domainType} self:unix_stream_socket {{ create connect accept listen bind getattr read write }};");
            }
            else
            {
                sb.AppendLine($"# Network access denied for {domainType}");
                sb.AppendLine($"neverallow {domainType} self:tcp_socket *;");
                sb.AppendLine($"neverallow {domainType} self:udp_socket *;");
            }
            sb.AppendLine();

            // Process permissions
            bool hasClone = false;
            bool hasExecve = false;
            foreach (var rule in Rules)
            {
                if (rule.Action == SyscallAction.Allow)
                {
                    if (rule.SyscallName == "clone") hasClone = true;
                    if (rule.SyscallName == "execve") hasExecve = true;
                }
            }

            sb.AppendLine($"allow {domainType} self:process {{ signal sigkill sigstop getsched setsched }};");
            if (hasClone)
            {
                sb.AppendLine($"allow {domainType} self:process {{ fork }};");
            }
            if (!hasExecve)
            {
                sb.AppendLine($"neverallow {domainType} self:process {{ execmem execstack execheap transition }};");
            }
            sb.AppendLine();

            // Shared memory
            sb.AppendLine($"allow {domainType} self:shm {{ create read write associate unix_read unix_write }};");
            sb.AppendLine();

            // Deny ptrace
            sb.AppendLine($"neverallow {domainType} self:capability {{ sys_ptrace }};");
            sb.AppendLine();

            return sb.ToString();
        }

        private static string ActionToLabel(SyscallAction action) => action switch
        {
            SyscallAction.Allow => "allow",
            SyscallAction.Kill => "kill",
            SyscallAction.Trap => "trap",
            SyscallAction.Errno => "kill",
            SyscallAction.Log => "log",
            SyscallAction.Trace => "allow",
            _ => "kill"
        };

        private static string ActionToReturnValue(SyscallAction action) => action switch
        {
            SyscallAction.Allow => "0x7FFF0000",   // SECCOMP_RET_ALLOW
            SyscallAction.Kill => "0x00000000",      // SECCOMP_RET_KILL
            SyscallAction.Trap => "0x00030000",      // SECCOMP_RET_TRAP
            SyscallAction.Errno => "0x00050001",     // SECCOMP_RET_ERRNO | EPERM
            SyscallAction.Log => "0x7FFC0000",       // SECCOMP_RET_LOG
            SyscallAction.Trace => "0x7FF00000",     // SECCOMP_RET_TRACE
            _ => "0x00000000"
        };
    }
}
