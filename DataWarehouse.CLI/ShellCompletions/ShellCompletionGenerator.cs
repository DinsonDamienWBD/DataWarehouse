// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using DataWarehouse.Shared.Commands;

namespace DataWarehouse.CLI.ShellCompletions;

/// <summary>
/// Generates shell completion scripts for various shells.
/// </summary>
public sealed class ShellCompletionGenerator
{
    private readonly CommandExecutor _executor;

    /// <summary>
    /// Creates a new ShellCompletionGenerator.
    /// </summary>
    public ShellCompletionGenerator(CommandExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>
    /// Generates completion script for the specified shell.
    /// </summary>
    public string Generate(ShellType shell)
    {
        return shell switch
        {
            ShellType.Bash => GenerateBashCompletion(),
            ShellType.Zsh => GenerateZshCompletion(),
            ShellType.Fish => GenerateFishCompletion(),
            ShellType.PowerShell => GeneratePowerShellCompletion(),
            _ => throw new ArgumentException($"Unknown shell type: {shell}")
        };
    }

    /// <summary>
    /// Generates bash completion script.
    /// </summary>
    public string GenerateBashCompletion()
    {
        var commands = _executor.AllCommands.Select(c => c.Name).ToList();
        var categories = _executor.GetCommandsByCategory().Keys.ToList();

        var sb = new StringBuilder();

        sb.AppendLine("# DataWarehouse CLI bash completion");
        sb.AppendLine("# Install: eval \"$(dw completions bash)\"");
        sb.AppendLine("# Or add to ~/.bashrc: source <(dw completions bash)");
        sb.AppendLine();
        sb.AppendLine("_dw_completions() {");
        sb.AppendLine("    local cur prev words cword");
        sb.AppendLine("    _init_completion || return");
        sb.AppendLine();
        sb.AppendLine("    # Top-level commands");
        sb.AppendLine($"    local commands=\"{string.Join(" ", commands)}\"");
        sb.AppendLine($"    local categories=\"{string.Join(" ", categories)}\"");
        sb.AppendLine();
        sb.AppendLine("    # Global options");
        sb.AppendLine("    local global_opts=\"--verbose -v --config -c --format -f --instance -i --help -h\"");
        sb.AppendLine();
        sb.AppendLine("    case \"$prev\" in");
        sb.AppendLine("        --format|-f)");
        sb.AppendLine("            COMPREPLY=($(compgen -W \"table json yaml csv\" -- \"$cur\"))");
        sb.AppendLine("            return");
        sb.AppendLine("            ;;");
        sb.AppendLine("        --config|-c)");
        sb.AppendLine("            COMPREPLY=($(compgen -f -- \"$cur\"))");
        sb.AppendLine("            return");
        sb.AppendLine("            ;;");
        sb.AppendLine("        --instance|-i)");
        sb.AppendLine("            # Could read from profiles");
        sb.AppendLine("            return");
        sb.AppendLine("            ;;");
        sb.AppendLine("    esac");
        sb.AppendLine();
        sb.AppendLine("    if [[ ${cword} -eq 1 ]]; then");
        sb.AppendLine("        COMPREPLY=($(compgen -W \"$commands $global_opts\" -- \"$cur\"))");
        sb.AppendLine("        return");
        sb.AppendLine("    fi");
        sb.AppendLine();

        // Add subcommand completions for each category
        foreach (var (category, categoryCommands) in _executor.GetCommandsByCategory())
        {
            var subCommands = categoryCommands.Select(c => c.Name.Split('.').Last()).Distinct();
            sb.AppendLine($"    if [[ \"${{words[1]}}\" == \"{category}\"* ]]; then");
            sb.AppendLine($"        COMPREPLY=($(compgen -W \"{string.Join(" ", subCommands)}\" -- \"$cur\"))");
            sb.AppendLine("        return");
            sb.AppendLine("    fi");
            sb.AppendLine();
        }

        sb.AppendLine("    # Default to commands");
        sb.AppendLine("    COMPREPLY=($(compgen -W \"$commands $global_opts\" -- \"$cur\"))");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("complete -F _dw_completions dw");

        return sb.ToString();
    }

    /// <summary>
    /// Generates zsh completion script.
    /// </summary>
    public string GenerateZshCompletion()
    {
        var sb = new StringBuilder();

        sb.AppendLine("#compdef dw");
        sb.AppendLine();
        sb.AppendLine("# DataWarehouse CLI zsh completion");
        sb.AppendLine("# Install: eval \"$(dw completions zsh)\"");
        sb.AppendLine("# Or add to ~/.zshrc: source <(dw completions zsh)");
        sb.AppendLine();
        sb.AppendLine("_dw() {");
        sb.AppendLine("    local -a commands");
        sb.AppendLine("    local -a global_options");
        sb.AppendLine();
        sb.AppendLine("    global_options=(");
        sb.AppendLine("        '--verbose[Enable verbose output]'");
        sb.AppendLine("        '-v[Enable verbose output]'");
        sb.AppendLine("        '--config[Path to configuration file]:file:_files'");
        sb.AppendLine("        '-c[Path to configuration file]:file:_files'");
        sb.AppendLine("        '--format[Output format]:format:(table json yaml csv)'");
        sb.AppendLine("        '-f[Output format]:format:(table json yaml csv)'");
        sb.AppendLine("        '--instance[Instance profile to use]:profile:'");
        sb.AppendLine("        '-i[Instance profile to use]:profile:'");
        sb.AppendLine("        '--help[Show help]'");
        sb.AppendLine("        '-h[Show help]'");
        sb.AppendLine("    )");
        sb.AppendLine();
        sb.AppendLine("    commands=(");

        foreach (var cmd in _executor.AllCommands)
        {
            sb.AppendLine($"        '{cmd.Name}:{cmd.Description}'");
        }

        sb.AppendLine("    )");
        sb.AppendLine();
        sb.AppendLine("    _arguments -s \\");
        sb.AppendLine("        $global_options \\");
        sb.AppendLine("        '1:command:->commands' \\");
        sb.AppendLine("        '*::arg:->args'");
        sb.AppendLine();
        sb.AppendLine("    case $state in");
        sb.AppendLine("        commands)");
        sb.AppendLine("            _describe -t commands 'dw commands' commands");
        sb.AppendLine("            ;;");
        sb.AppendLine("        args)");
        sb.AppendLine("            case $words[1] in");

        // Add subcommand completions
        foreach (var (category, categoryCommands) in _executor.GetCommandsByCategory())
        {
            sb.AppendLine($"                {category}.*)");
            sb.AppendLine("                    local -a subcmds");
            sb.AppendLine("                    subcmds=(");

            foreach (var cmd in categoryCommands)
            {
                var subName = cmd.Name.Split('.').Last();
                sb.AppendLine($"                        '{subName}:{cmd.Description}'");
            }

            sb.AppendLine("                    )");
            sb.AppendLine("                    _describe -t subcmds 'subcommands' subcmds");
            sb.AppendLine("                    ;;");
        }

        sb.AppendLine("            esac");
        sb.AppendLine("            ;;");
        sb.AppendLine("    esac");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("_dw \"$@\"");

        return sb.ToString();
    }

    /// <summary>
    /// Generates fish completion script.
    /// </summary>
    public string GenerateFishCompletion()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# DataWarehouse CLI fish completion");
        sb.AppendLine("# Install: dw completions fish | source");
        sb.AppendLine("# Or add to ~/.config/fish/completions/dw.fish");
        sb.AppendLine();
        sb.AppendLine("# Disable file completions for the dw command");
        sb.AppendLine("complete -c dw -f");
        sb.AppendLine();
        sb.AppendLine("# Global options");
        sb.AppendLine("complete -c dw -l verbose -s v -d 'Enable verbose output'");
        sb.AppendLine("complete -c dw -l config -s c -r -d 'Path to configuration file'");
        sb.AppendLine("complete -c dw -l format -s f -x -a 'table json yaml csv' -d 'Output format'");
        sb.AppendLine("complete -c dw -l instance -s i -x -d 'Instance profile to use'");
        sb.AppendLine("complete -c dw -l help -s h -d 'Show help'");
        sb.AppendLine();
        sb.AppendLine("# Commands");

        foreach (var cmd in _executor.AllCommands)
        {
            var escapedDesc = cmd.Description.Replace("'", "\\'");
            sb.AppendLine($"complete -c dw -n '__fish_use_subcommand' -a '{cmd.Name}' -d '{escapedDesc}'");
        }

        sb.AppendLine();
        sb.AppendLine("# Command-specific completions");

        foreach (var (category, categoryCommands) in _executor.GetCommandsByCategory())
        {
            sb.AppendLine($"# {category} commands");

            foreach (var cmd in categoryCommands)
            {
                var escapedDesc = cmd.Description.Replace("'", "\\'");
                sb.AppendLine($"complete -c dw -n '__fish_seen_subcommand_from {category}' -a '{cmd.Name.Split('.').Last()}' -d '{escapedDesc}'");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates PowerShell completion script.
    /// </summary>
    public string GeneratePowerShellCompletion()
    {
        var commands = _executor.AllCommands.Select(c => c.Name).ToList();

        var sb = new StringBuilder();

        sb.AppendLine("# DataWarehouse CLI PowerShell completion");
        sb.AppendLine("# Install: Invoke-Expression (dw completions powershell)");
        sb.AppendLine("# Or add to your PowerShell profile");
        sb.AppendLine();
        sb.AppendLine("$script:dwCommands = @(");

        foreach (var cmd in _executor.AllCommands)
        {
            sb.AppendLine($"    [PSCustomObject]@{{ Name = '{cmd.Name}'; Description = '{cmd.Description.Replace("'", "''")}' }}");
        }

        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("$script:dwGlobalOptions = @(");
        sb.AppendLine("    [PSCustomObject]@{ Name = '--verbose'; Description = 'Enable verbose output' }");
        sb.AppendLine("    [PSCustomObject]@{ Name = '-v'; Description = 'Enable verbose output' }");
        sb.AppendLine("    [PSCustomObject]@{ Name = '--config'; Description = 'Path to configuration file' }");
        sb.AppendLine("    [PSCustomObject]@{ Name = '-c'; Description = 'Path to configuration file' }");
        sb.AppendLine("    [PSCustomObject]@{ Name = '--format'; Description = 'Output format' }");
        sb.AppendLine("    [PSCustomObject]@{ Name = '-f'; Description = 'Output format' }");
        sb.AppendLine("    [PSCustomObject]@{ Name = '--instance'; Description = 'Instance profile to use' }");
        sb.AppendLine("    [PSCustomObject]@{ Name = '-i'; Description = 'Instance profile to use' }");
        sb.AppendLine("    [PSCustomObject]@{ Name = '--help'; Description = 'Show help' }");
        sb.AppendLine("    [PSCustomObject]@{ Name = '-h'; Description = 'Show help' }");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("$script:dwFormats = @('table', 'json', 'yaml', 'csv')");
        sb.AppendLine();
        sb.AppendLine("Register-ArgumentCompleter -Native -CommandName dw -ScriptBlock {");
        sb.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        sb.AppendLine();
        sb.AppendLine("    $elements = $commandAst.CommandElements");
        sb.AppendLine("    $lastElement = $elements[-1].Extent.Text");
        sb.AppendLine("    $prevElement = if ($elements.Count -gt 1) { $elements[-2].Extent.Text } else { $null }");
        sb.AppendLine();
        sb.AppendLine("    # Handle --format argument");
        sb.AppendLine("    if ($prevElement -eq '--format' -or $prevElement -eq '-f') {");
        sb.AppendLine("        $script:dwFormats | Where-Object { $_ -like \"$wordToComplete*\" } | ForEach-Object {");
        sb.AppendLine("            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)");
        sb.AppendLine("        }");
        sb.AppendLine("        return");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    # Handle --config argument");
        sb.AppendLine("    if ($prevElement -eq '--config' -or $prevElement -eq '-c') {");
        sb.AppendLine("        Get-ChildItem -Path \"$wordToComplete*\" -File | ForEach-Object {");
        sb.AppendLine("            [System.Management.Automation.CompletionResult]::new($_.FullName, $_.Name, 'ProviderItem', $_.Name)");
        sb.AppendLine("        }");
        sb.AppendLine("        return");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    # Complete commands and options");
        sb.AppendLine("    if ($elements.Count -le 2) {");
        sb.AppendLine("        # First argument - show commands and global options");
        sb.AppendLine("        $script:dwCommands | Where-Object { $_.Name -like \"$wordToComplete*\" } | ForEach-Object {");
        sb.AppendLine("            [System.Management.Automation.CompletionResult]::new($_.Name, $_.Name, 'Command', $_.Description)");
        sb.AppendLine("        }");
        sb.AppendLine("        $script:dwGlobalOptions | Where-Object { $_.Name -like \"$wordToComplete*\" } | ForEach-Object {");
        sb.AppendLine("            [System.Management.Automation.CompletionResult]::new($_.Name, $_.Name, 'ParameterName', $_.Description)");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}

/// <summary>
/// Supported shell types for completion generation.
/// </summary>
public enum ShellType
{
    /// <summary>Bash shell.</summary>
    Bash,

    /// <summary>Zsh shell.</summary>
    Zsh,

    /// <summary>Fish shell.</summary>
    Fish,

    /// <summary>PowerShell.</summary>
    PowerShell
}
