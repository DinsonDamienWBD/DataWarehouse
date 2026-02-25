using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiArchitectureMapper;

class Program
{
    // FIX: Explicitly typed to avoid .NET 10 Preview compiler syntax quirks
    static readonly HashSet<string> IgnoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".claude", ".github", ".omc", ".planning", "Metadata",
        "bin", "obj", "coverage", "coverage-report", "TestResults",
        "packages", "node_modules", ".vs", "scientist"
    };

    static readonly HashSet<string> IgnoredExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cache", ".log", ".trx", ".user", ".suo", ".dll", ".pdb", ".exe", ".a", ".dylib"
    };

    static void Main(string[] args)
    {
        Console.WriteLine("=== AI Architecture Map Generator ===");
        string rootFolder;

        // Automatically use the passed argument if running in GitHub Actions
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            rootFolder = args[0];
            Console.WriteLine($"Automated mode active. Root folder: {rootFolder}");
        }
        else
        {
            // Fallback for when you run it manually in Visual Studio
            Console.Write("Enter the root folder path of the DataWarehouse solution: ");
            string? rootFolderInput = Console.ReadLine();
            rootFolder = rootFolderInput?.Trim('"', ' ', '\'') ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        {
            Console.WriteLine("Invalid directory.");
            return;
        }

        // 1. Setup the output directories
        string docsDir = Path.Combine(rootFolder, "docs", "ai-maps");
        string pluginsDir = Path.Combine(docsDir, "plugins");
        Directory.CreateDirectory(docsDir);
        Directory.CreateDirectory(pluginsDir);

        // 2. Generate the Master Directory Tree Map
        Console.WriteLine("\nGenerating Project Structure Map...");
        string treePath = Path.Combine(docsDir, "map-structure.md");
        GenerateDirectoryTree(rootFolder, treePath);

        // 3. Extract the projects from the .slnx file
        string slnxPath = Path.Combine(rootFolder, "DataWarehouse.slnx");

        // FIX: Explicitly typed List to prevent preview compiler issues
        List<string> projectPaths = new List<string>();

        if (File.Exists(slnxPath))
        {
            Console.WriteLine($"Found DataWarehouse.slnx. Extracting projects...");
            var doc = XDocument.Load(slnxPath);
            projectPaths = doc.Descendants("Project")
                .Select(p => p.Attribute("Path")?.Value)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(Path.Combine(rootFolder, p!.Replace('\\', Path.DirectorySeparatorChar))))
                .ToList();
        }
        else
        {
            Console.WriteLine(".slnx not found. Scanning for .csproj files...");
            projectPaths = Directory.GetFiles(rootFolder, "*.csproj", SearchOption.AllDirectories).ToList();
        }

        var coreSb = new StringBuilder();
        coreSb.AppendLine("# Core Architecture: Kernel & SDK");
        coreSb.AppendLine("> **IMPORTANT:** Defines base classes, interfaces, and core domain contracts.\n");

        var companionsSb = new StringBuilder();
        companionsSb.AppendLine("# Companion Projects (Shared, UI, CLI, Tests)");
        companionsSb.AppendLine("> **DEPENDENCY:** Rely on contracts defined in `map-core.md`.\n");

        var pluginBuilders = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);

        // 4. Process each project
        Console.WriteLine("Parsing C# Syntax Trees...");
        foreach (var projPath in projectPaths)
        {
            if (!File.Exists(projPath)) continue;

            if (IgnoredDirs.Any(dir => projPath.Contains($"{Path.DirectorySeparatorChar}{dir}{Path.DirectorySeparatorChar}")))
            {
                continue;
            }

            string projectName = Path.GetFileNameWithoutExtension(projPath);
            string projDir = Path.GetDirectoryName(projPath) ?? string.Empty;

            StringBuilder targetSb;

            if (projectName.Contains("Kernel", StringComparison.OrdinalIgnoreCase) ||
                projectName.Contains("SDK", StringComparison.OrdinalIgnoreCase))
            {
                targetSb = coreSb;
            }
            else if (projectName.Contains("Plugin", StringComparison.OrdinalIgnoreCase) ||
                     projDir.Contains($"{Path.DirectorySeparatorChar}Plugins{Path.DirectorySeparatorChar}"))
            {
                string pluginName = projectName.Split('.').Last();
                if (!pluginBuilders.TryGetValue(pluginName, out targetSb!))
                {
                    targetSb = new StringBuilder();
                    targetSb.AppendLine($"# Plugin: {pluginName}");
                    targetSb.AppendLine("> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.");
                    targetSb.AppendLine("> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.\n");
                    pluginBuilders[pluginName] = targetSb;
                }
            }
            else
            {
                targetSb = companionsSb;
            }

            targetSb.AppendLine($"\n## Project: {projectName}");

            var csFiles = Directory.GetFiles(projDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

            foreach (var csFile in csFiles)
            {
                string code = File.ReadAllText(csFile);
                var tree = CSharpSyntaxTree.ParseText(code);
                var rootNode = tree.GetRoot();

                var typeDeclarations = rootNode.DescendantNodes().OfType<TypeDeclarationSyntax>();
                bool documentHeaderAdded = false;

                foreach (var typeDecl in typeDeclarations)
                {
                    bool isInterface = typeDecl is InterfaceDeclarationSyntax;

                    var visibleMembers = typeDecl.Members.Where(m =>
                        isInterface ||
                        m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword) ||
                                               mod.IsKind(SyntaxKind.ProtectedKeyword) ||
                                               mod.IsKind(SyntaxKind.InternalKeyword))).ToList();

                    if (!visibleMembers.Any() && typeDecl.BaseList == null) continue;

                    if (!documentHeaderAdded)
                    {
                        string relativePath = Path.GetRelativePath(rootFolder, csFile).Replace('\\', '/');
                        targetSb.AppendLine($"\n### File: {relativePath}");
                        documentHeaderAdded = true;
                    }

                    string typeSignature = "";
                    SyntaxList<MemberDeclarationSyntax> emptyMembers = SyntaxFactory.List<MemberDeclarationSyntax>();

                    if (typeDecl is ClassDeclarationSyntax classDecl)
                        typeSignature = classDecl.WithMembers(emptyMembers).NormalizeWhitespace().ToString();
                    else if (typeDecl is InterfaceDeclarationSyntax interfaceDecl)
                        typeSignature = interfaceDecl.WithMembers(emptyMembers).NormalizeWhitespace().ToString();
                    else if (typeDecl is RecordDeclarationSyntax recordDecl)
                        typeSignature = recordDecl.WithMembers(emptyMembers).NormalizeWhitespace().ToString();
                    else if (typeDecl is StructDeclarationSyntax structDecl)
                        typeSignature = structDecl.WithMembers(emptyMembers).NormalizeWhitespace().ToString();
                    else
                        continue;

                    targetSb.AppendLine("```csharp");
                    targetSb.AppendLine(typeSignature.Replace("{}", "").Trim());

                    foreach (var member in visibleMembers)
                    {
                        targetSb.AppendLine($"    {GetMemberSignature(member)}");
                    }
                    targetSb.AppendLine("}");
                    targetSb.AppendLine("```");
                }
            }
        }

        // 5. Save all architecture maps
        File.WriteAllText(Path.Combine(docsDir, "map-core.md"), coreSb.ToString());
        File.WriteAllText(Path.Combine(docsDir, "map-companions.md"), companionsSb.ToString());

        foreach (var plugin in pluginBuilders)
        {
            string cleanName = string.Join("_", plugin.Key.Split(Path.GetInvalidFileNameChars()));
            File.WriteAllText(Path.Combine(pluginsDir, $"map-{cleanName}.md"), plugin.Value.ToString());
        }

        Console.WriteLine($"\nSuccess! AI Maps and Project Structure generated in: {docsDir}");
    }

    // --- DIRECTORY TREE GENERATOR ---
    static void GenerateDirectoryTree(string rootPath, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Project Structure Map");
        sb.AppendLine("> **IMPORTANT:** This is the master directory tree. Use this to locate exact folders and file paths before reading code.\n");
        sb.AppendLine("```text");

        var rootDir = new DirectoryInfo(rootPath);
        sb.AppendLine(rootDir.Name + "/");

        BuildTree(rootDir, "", sb);

        sb.AppendLine("```");
        File.WriteAllText(outputPath, sb.ToString());
    }

    static void BuildTree(DirectoryInfo dir, string indent, StringBuilder sb)
    {
        var dirs = dir.GetDirectories()
            .Where(d => !IgnoredDirs.Contains(d.Name))
            .OrderBy(d => d.Name)
            .ToList();

        var files = dir.GetFiles()
            .Where(f => !IgnoredExtensions.Contains(f.Extension))
            .OrderBy(f => f.Name)
            .ToList();

        for (int i = 0; i < dirs.Count; i++)
        {
            bool isLast = (i == dirs.Count - 1) && (files.Count == 0);
            sb.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}{dirs[i].Name}/");
            BuildTree(dirs[i], indent + (isLast ? "    " : "│   "), sb);
        }

        for (int i = 0; i < files.Count; i++)
        {
            bool isLast = (i == files.Count - 1);
            sb.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}{files[i].Name}");
        }
    }

    // --- ROSLYN MEMBER EXTRACTOR ---
    static string GetMemberSignature(MemberDeclarationSyntax member)
    {
        try
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    return method.WithBody(null).WithExpressionBody(null).NormalizeWhitespace().ToString() + ";";
                case PropertyDeclarationSyntax prop:
                    return prop.WithInitializer(null).WithExpressionBody(null).NormalizeWhitespace().ToString() + (prop.AccessorList == null ? ";" : "");
                case ConstructorDeclarationSyntax ctor:
                    return ctor.WithBody(null).WithExpressionBody(null).NormalizeWhitespace().ToString() + ";";
                case FieldDeclarationSyntax field:
                case EventFieldDeclarationSyntax evt:
                    return member.NormalizeWhitespace().ToString();
                default:
                    var lines = member.NormalizeWhitespace().ToString().Split('\n');
                    return lines[0].TrimEnd('\r') + ";";
            }
        }
        catch
        {
            return "// Unparseable member signature";
        }
    }
}