using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Plugin isolation verification tests. Ensures no plugin references another plugin directly,
/// all plugins reference only SDK/Shared, and no cross-plugin namespace imports exist.
/// </summary>
[Trait("Category", "Integration")]
public class ProjectReferenceTests
{
    private static readonly string SolutionRoot = FindSolutionRoot();

    private static string FindSolutionRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DataWarehouse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        dir = Path.GetDirectoryName(typeof(ProjectReferenceTests).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DataWarehouse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not locate DataWarehouse.slnx. Ensure tests run from within the solution tree.");
    }

    [Fact]
    public void Plugins_ShouldNotReferenceOtherPlugins()
    {
        // Arrange
        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        Directory.Exists(pluginsDir).Should().BeTrue("Plugins directory must exist");

        var csprojFiles = Directory.GetFiles(pluginsDir, "*.csproj", SearchOption.AllDirectories);
        csprojFiles.Should().NotBeEmpty("there should be plugin .csproj files");

        var violations = new List<string>();

        foreach (var csprojFile in csprojFiles)
        {
            var doc = XDocument.Load(csprojFile);
            var projectRefs = doc.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value ?? "")
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            var pluginName = Path.GetFileNameWithoutExtension(csprojFile);

            foreach (var refPath in projectRefs)
            {
                var normalizedRef = refPath.Replace('\\', '/');
                // Check if this reference points to another plugin
                if (normalizedRef.Contains("Plugins/DataWarehouse.Plugins.", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{pluginName} references plugin: {refPath}");
                }
            }
        }

        violations.Should().BeEmpty(
            "no plugin should reference another plugin directly. " +
            $"Violations found:\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void Plugins_ShouldOnlyReferenceSDKOrShared()
    {
        // Arrange
        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        var csprojFiles = Directory.GetFiles(pluginsDir, "*.csproj", SearchOption.AllDirectories);

        // Allowed project reference targets
        var allowedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DataWarehouse.SDK",
            "DataWarehouse.Shared"
        };

        var violations = new List<string>();

        foreach (var csprojFile in csprojFiles)
        {
            var doc = XDocument.Load(csprojFile);
            var projectRefs = doc.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value ?? "")
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            var pluginName = Path.GetFileNameWithoutExtension(csprojFile);

            foreach (var refPath in projectRefs)
            {
                // Extract the project name from the reference path
                var refFileName = Path.GetFileNameWithoutExtension(refPath);

                if (!allowedTargets.Contains(refFileName))
                {
                    violations.Add($"{pluginName} references non-SDK/Shared project: {refFileName} (path: {refPath})");
                }
            }
        }

        violations.Should().BeEmpty(
            "all plugin ProjectReferences should point only to DataWarehouse.SDK or DataWarehouse.Shared. " +
            $"Violations:\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void PluginSourceFiles_ShouldNotImportOtherPluginNamespaces()
    {
        // Arrange
        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        var pluginDirs = Directory.GetDirectories(pluginsDir, "DataWarehouse.Plugins.*");

        var violations = new List<string>();

        foreach (var pluginDir in pluginDirs)
        {
            var pluginDirName = Path.GetFileName(pluginDir);
            // Extract the plugin namespace suffix (e.g., "UltimateStorage" from "DataWarehouse.Plugins.UltimateStorage")
            var ownNamespace = pluginDirName; // e.g., DataWarehouse.Plugins.UltimateStorage

            var csFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", "")))
                .ToList();

            foreach (var csFile in csFiles)
            {
                var lines = File.ReadAllLines(csFile);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();

                    // Only check 'using' directives
                    if (!line.StartsWith("using ") || line.StartsWith("using (") || line.Contains("="))
                        continue;

                    // Check if this using references another plugin namespace
                    if (line.Contains("DataWarehouse.Plugins."))
                    {
                        // Extract the namespace being imported
                        var usingNs = line.TrimEnd(';').Replace("using ", "").Trim();

                        // Skip if it's the plugin's own namespace
                        if (usingNs.StartsWith(ownNamespace, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // This is a cross-plugin namespace import
                        var relPath = Path.GetRelativePath(SolutionRoot, csFile);
                        violations.Add($"{relPath}:{i + 1} imports cross-plugin namespace: {usingNs}");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "no plugin source file should import another plugin's namespace. " +
            $"Violations:\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void AllPluginProjectFiles_ShouldBeValidXml()
    {
        // Arrange
        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        var csprojFiles = Directory.GetFiles(pluginsDir, "*.csproj", SearchOption.AllDirectories);

        var invalidFiles = new List<string>();

        foreach (var csprojFile in csprojFiles)
        {
            try
            {
                XDocument.Load(csprojFile);
            }
            catch (Exception ex)
            {
                invalidFiles.Add($"{Path.GetRelativePath(SolutionRoot, csprojFile)}: {ex.Message}");
            }
        }

        invalidFiles.Should().BeEmpty(
            $"all plugin .csproj files should be valid XML. " +
            $"Invalid: {string.Join(", ", invalidFiles)}");
    }
}
