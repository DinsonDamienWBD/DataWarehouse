using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension.OsIntegration;

/// <summary>
/// Represents a single shell verb that can be registered in the Windows shell
/// for a ProgID. Each verb defines a right-click context menu action.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Windows shell verb model (FEXT-02)")]
public readonly struct WindowsShellVerb
{
    /// <summary>Shell verb name (e.g., "open", "inspect", "verify").</summary>
    public string Name { get; }

    /// <summary>Display name shown in the context menu (e.g., "&amp;Open with DataWarehouse").</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Command template with "%1" placeholder for the file path.
    /// Example: <c>dw open "%1"</c>.
    /// </summary>
    public string CommandTemplate { get; }

    /// <summary>Whether this verb is the default action (double-click behavior).</summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Initializes a new <see cref="WindowsShellVerb"/>.
    /// </summary>
    public WindowsShellVerb(string name, string displayName, string commandTemplate, bool isDefault = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        CommandTemplate = commandTemplate ?? throw new ArgumentNullException(nameof(commandTemplate));
        IsDefault = isDefault;
    }
}

/// <summary>
/// Provides shell verb definitions for DWVD file types. Shell verbs define the
/// actions available in the Windows Explorer right-click context menu when
/// interacting with registered file extensions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: Windows shell handler (FEXT-02)")]
public static class WindowsShellHandler
{
    private static readonly WindowsShellVerb VerbOpen = new(
        "open",
        "&Open with DataWarehouse",
        "dw open \"%1\"",
        isDefault: true);

    private static readonly WindowsShellVerb VerbInspect = new(
        "inspect",
        "&Inspect Metadata",
        "dw inspect \"%1\"");

    private static readonly WindowsShellVerb VerbVerify = new(
        "verify",
        "&Verify Integrity",
        "dw verify \"%1\"");

    /// <summary>
    /// Returns the shell verbs for the primary .dwvd extension: Open (default), Inspect, and Verify.
    /// </summary>
    public static IReadOnlyList<WindowsShellVerb> GetPrimaryVerbs() =>
        [VerbOpen, VerbInspect, VerbVerify];

    /// <summary>
    /// Returns the appropriate shell verbs for a secondary extension kind.
    /// Different secondary types support different subsets of operations:
    /// <list type="bullet">
    ///   <item><description>Snapshot (.dwvd.snap): Open + Inspect (read-only, no verify)</description></item>
    ///   <item><description>Delta (.dwvd.delta): Open + Inspect</description></item>
    ///   <item><description>Metadata (.dwvd.meta): Inspect only</description></item>
    ///   <item><description>Lock (.dwvd.lock): Inspect only</description></item>
    /// </list>
    /// </summary>
    /// <param name="kind">The secondary extension kind.</param>
    /// <returns>The list of shell verbs appropriate for the extension kind.</returns>
    public static IReadOnlyList<WindowsShellVerb> GetSecondaryVerbs(SecondaryExtensionKind kind) => kind switch
    {
        SecondaryExtensionKind.Snapshot => [VerbOpen, VerbInspect],
        SecondaryExtensionKind.Delta => [VerbOpen, VerbInspect],
        SecondaryExtensionKind.Metadata => [VerbInspect],
        SecondaryExtensionKind.Lock => [VerbInspect],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown secondary extension kind."),
    };

    /// <summary>
    /// Resolves a command template by replacing the CLI tool placeholder with the
    /// specified path to the <c>dw</c> CLI executable.
    /// </summary>
    /// <param name="verb">The shell verb whose command template to resolve.</param>
    /// <param name="dwCliPath">The full path to the dw CLI executable (e.g., "C:\Program Files\DataWarehouse\dw.exe").</param>
    /// <returns>The fully resolved command line string.</returns>
    public static string GetCommandLine(WindowsShellVerb verb, string dwCliPath)
    {
        if (string.IsNullOrEmpty(dwCliPath))
            throw new ArgumentException("CLI path must not be null or empty.", nameof(dwCliPath));

        // Replace the "dw" prefix in the command template with the actual CLI path.
        // Templates are formatted as: dw <verb> "%1"
        // Anchor replacement to the start of the template to avoid matching "dw " mid-string
        var template = verb.CommandTemplate;
        if (template.StartsWith("dw ", StringComparison.Ordinal))
        {
            return $"\"{dwCliPath}\" " + template.Substring(3);
        }
        return template.Replace("dw ", $"\"{dwCliPath}\" ", StringComparison.Ordinal);
    }
}
