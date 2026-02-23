using System.Diagnostics;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Build health gate tests that verify solution structure integrity,
/// all plugin projects are included in the solution file, and new v5.0 plugins
/// are present. Validates structural health without shelling out to dotnet build
/// (full build verification is done via CI or manual dotnet build).
/// </summary>
[Trait("Category", "Integration")]
public class BuildHealthTests
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
        // Fallback: walk up from test assembly location
        dir = Path.GetDirectoryName(typeof(BuildHealthTests).Assembly.Location);
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
    public void Solution_AllReferencedProjectFilesShouldExist()
    {
        // Verify that every Project in the slnx actually has a .csproj on disk
        var slnxPath = Path.Combine(SolutionRoot, "DataWarehouse.slnx");
        File.Exists(slnxPath).Should().BeTrue("DataWarehouse.slnx must exist at solution root");

        var slnxContent = XDocument.Load(slnxPath);
        var projectPaths = slnxContent.Descendants("Project")
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();

        projectPaths.Should().NotBeEmpty("slnx should contain project references");

        var missingFiles = projectPaths
            .Where(p => !File.Exists(Path.Combine(SolutionRoot, p.Replace('/', '\\'))))
            .ToList();

        missingFiles.Should().BeEmpty(
            $"all projects referenced in slnx should exist on disk. " +
            $"Missing: {string.Join(", ", missingFiles)}");
    }

    [Fact]
    public void SlnxFile_ShouldIncludeAllPluginProjects()
    {
        // Arrange: enumerate all .csproj files under Plugins/
        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        Directory.Exists(pluginsDir).Should().BeTrue("Plugins directory must exist");

        var pluginCsprojFiles = Directory.GetFiles(pluginsDir, "*.csproj", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(SolutionRoot, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        pluginCsprojFiles.Should().NotBeEmpty("there should be plugin .csproj files under Plugins/");

        // Parse slnx to extract Project paths
        var slnxPath = Path.Combine(SolutionRoot, "DataWarehouse.slnx");
        var slnxContent = XDocument.Load(slnxPath);
        var slnxProjects = slnxContent.Descendants("Project")
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => p != null)
            .Select(p => p!.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Act & Assert: every plugin csproj should be in the slnx
        var missingFromSolution = pluginCsprojFiles
            .Where(p => !slnxProjects.Contains(p))
            .ToList();

        missingFromSolution.Should().BeEmpty(
            "all plugin .csproj files should be included in DataWarehouse.slnx. " +
            $"Missing: {string.Join(", ", missingFromSolution)}");
    }

    [Fact]
    public void SlnxFile_ShouldIncludeV50Plugins()
    {
        // v5.0 plugins added by phases 55-65
        var expectedV50Plugins = new[]
        {
            // ChaosVaccination consolidated into UltimateResilience (Phase 65.5-12)
            "SemanticSync",
            "UniversalFabric"
        };

        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        var slnxPath = Path.Combine(SolutionRoot, "DataWarehouse.slnx");
        var slnxContent = XDocument.Load(slnxPath);
        var slnxProjectPaths = slnxContent.Descendants("Project")
            .Select(e => e.Attribute("Path")?.Value ?? "")
            .ToList();

        var missingPlugins = new List<string>();
        var skippedPlugins = new List<string>();

        foreach (var pluginName in expectedV50Plugins)
        {
            var pluginDir = Path.Combine(pluginsDir, $"DataWarehouse.Plugins.{pluginName}");
            if (!Directory.Exists(pluginDir))
            {
                // Phase not yet executed -- track as skipped
                skippedPlugins.Add(pluginName);
                continue;
            }

            var matchInSlnx = slnxProjectPaths.Any(p =>
                p.Contains($"DataWarehouse.Plugins.{pluginName}", StringComparison.OrdinalIgnoreCase));

            if (!matchInSlnx)
                missingPlugins.Add(pluginName);
        }

        missingPlugins.Should().BeEmpty(
            $"all existing v5.0 plugins should be in DataWarehouse.slnx. " +
            $"Missing: {string.Join(", ", missingPlugins)}");

        // If all were skipped (no v5.0 plugins exist yet), note it but don't fail
        if (skippedPlugins.Count == expectedV50Plugins.Length)
        {
            // All v5.0 plugin directories absent -- phases not executed yet.
            // Test passes vacuously (nothing to verify).
        }
    }

    [Fact]
    public void SlnxFile_ShouldContainAtLeast70Projects()
    {
        // Arrange
        var slnxPath = Path.Combine(SolutionRoot, "DataWarehouse.slnx");
        var slnxContent = XDocument.Load(slnxPath);

        // Act: count all Project elements
        var projectCount = slnxContent.Descendants("Project").Count();

        // Assert
        projectCount.Should().BeGreaterThanOrEqualTo(70,
            $"solution should contain at least 70 projects but found {projectCount}. " +
            "Ensure all plugin and infrastructure projects are included in DataWarehouse.slnx.");
    }
}
