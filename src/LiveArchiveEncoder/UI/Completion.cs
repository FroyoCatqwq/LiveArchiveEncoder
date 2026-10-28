using System.Text;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// Emits shell completion scripts, in the spirit of bash-completion and
/// python3-argcomplete: the program itself is the source of truth for its option
/// list and for the encoder names, which are read from FFmpeg at generation time.
///
/// Usage:
///   LiveArchiveEncoder --completion bash      &gt; /etc/bash_completion.d/lae
///   LiveArchiveEncoder --completion zsh       &gt; "${fpath[1]}/_lae"
///   LiveArchiveEncoder --completion fish      &gt; ~/.config/fish/completions/lae.fish
///   LiveArchiveEncoder --completion powershell &gt; lae.ps1   (then dot-source it)
///
/// v7.9.8.6: the option list now comes from <see cref="OptionCatalog"/> instead of
/// a second copy maintained here. The copy had drifted to 69 options against the
/// parser's 112, so most of what the tool learned after v7.9 completed nothing.
/// </summary>
public static class Completion
{
    private static string[] Resolve(string[] choices, string[] encoders) =>
        choices.Length == 1 && choices[0] == OptionCatalog.EncodersMarker ? encoders : choices;

    public static string Generate(string shell, IEnumerable<string> encoders, string exeName = "LiveArchiveEncoder")
    {
        string[] encoderList = [.. encoders.OrderBy(x => x, StringComparer.Ordinal)];
        return shell switch
        {
            "bash" => Bash(encoderList, exeName),
            "zsh" => Zsh(encoderList, exeName),
            "fish" => Fish(encoderList, exeName),
            "powershell" => PowerShell(encoderList, exeName),
            _ => throw new ArgumentException($"unsupported shell '{shell}'"),
        };
    }

    private static string Bash(string[] encoders, string exe)
    {
        var sb = new StringBuilder();
        sb.Append("# bash completion for ").Append(exe).Append("  (generated)\n");
        sb.Append("#   LiveArchiveEncoder --completion bash > /etc/bash_completion.d/").Append(exe.ToLowerInvariant()).Append('\n');
        sb.Append('_').Append(exe.ToLowerInvariant()).Append("_opts='");
        sb.Append(string.Join(' ', OptionCatalog.All.Select(o => o.Name)));
        sb.Append("'\n\n");
        sb.Append('_').Append(exe.ToLowerInvariant()).Append("() {\n");
        sb.Append("    local cur prev\n");
        sb.Append("    cur=\"${COMP_WORDS[COMP_CWORD]}\"\n");
        sb.Append("    prev=\"${COMP_WORDS[COMP_CWORD-1]}\"\n\n");
        sb.Append("    case \"$prev\" in\n");
        foreach (OptionCatalog.Spec option in OptionCatalog.ValueOptions)
        {
            if (option.OptionKind == OptionCatalog.Kind.File)
            {
                sb.Append("        ").Append(option.Name).Append(")\n");
                sb.Append("            COMPREPLY=( $(compgen -f -- \"$cur\") )\n");
                sb.Append("            return 0\n            ;;\n");
                continue;
            }

            // No known choices (numbers, timecodes, codec names): leave it to the
            // caller's default completion instead of emitting an empty -W list.
            string[] choices = Resolve(option.Choices, encoders);
            if (choices.Length == 0) continue;

            sb.Append("        ").Append(option.Name).Append(")\n");
            sb.Append("            COMPREPLY=( $(compgen -W \"")
              .Append(string.Join(' ', choices))
              .Append("\" -- \"$cur\") )\n");
            sb.Append("            return 0\n            ;;\n");
        }
        sb.Append("    esac\n\n");
        // Positional 1/2 are paths, so only complete files there.
        sb.Append("    local positional=0\n");
        sb.Append("    for w in \"${COMP_WORDS[@]:1:COMP_CWORD-1}\"; do\n");
        sb.Append("        case \"$w\" in -*) ;; *) positional=$((positional+1));; esac\n");
        sb.Append("    done\n");
        sb.Append("    if [[ $positional -lt 2 ]]; then\n");
        sb.Append("        COMPREPLY=( $(compgen -f -- \"$cur\") )\n        return 0\n    fi\n\n");
        sb.Append("    COMPREPLY=( $(compgen -W \"$_").Append(exe.ToLowerInvariant()).Append("_opts\" -- \"$cur\") )\n");
        sb.Append("}\ncomplete -F _").Append(exe.ToLowerInvariant()).Append(' ').Append(exe).Append('\n');
        return sb.ToString();
    }

    private static string Zsh(string[] encoders, string exe)
    {
        var sb = new StringBuilder();
        sb.Append("#compdef ").Append(exe).Append('\n');
        sb.Append("# zsh completion for ").Append(exe).Append("  (generated)\n");
        sb.Append("#   LiveArchiveEncoder --completion zsh > \"${fpath[1]}/_").Append(exe.ToLowerInvariant()).Append("\"\n\n");
        sb.Append('_').Append(exe.ToLowerInvariant()).Append("() {\n");
        sb.Append("    local -a opts\n    opts=(\n");
        foreach (OptionCatalog.Spec option in OptionCatalog.All)
        {
            if (option.OptionKind == OptionCatalog.Kind.Flag)
            {
                sb.Append("        '").Append(option.Name).Append("'\n");
                continue;
            }

            if (option.OptionKind == OptionCatalog.Kind.File)
            {
                sb.Append("        '").Append(option.Name).Append(":file:_files'\n");
                continue;
            }

            string valueList = string.Join(' ', Resolve(option.Choices, encoders));
            if (valueList.Length == 0)
                sb.Append("        '").Append(option.Name).Append(":value:'\n");
            else
                sb.Append("        '").Append(option.Name).Append(":value:(").Append(valueList).Append(")'\n");
        }
        sb.Append("        '*:file:_files'\n    )\n");
        sb.Append("    _arguments $opts\n}\n");
        sb.Append("compdef _").Append(exe.ToLowerInvariant()).Append(' ').Append(exe).Append('\n');
        return sb.ToString();
    }

    private static string Fish(string[] encoders, string exe)
    {
        var sb = new StringBuilder();
        sb.Append("# fish completion for ").Append(exe).Append("  (generated)\n");
        sb.Append("#   LiveArchiveEncoder --completion fish > ~/.config/fish/completions/")
          .Append(exe.ToLowerInvariant()).Append(".fish\n\n");
        sb.Append("complete -c ").Append(exe).Append(" -f\n\n");
        // Every option by name, not only the bare switches. bash, zsh and
        // PowerShell all finish their completer with "any option not matched
        // above", so an option with no value table is still suggested there --
        // fish has no such fallback, and until v7.9.8.6 a free-text option such as
        // --threads or --net-test was simply absent from the generated script.
        foreach (OptionCatalog.Spec option in OptionCatalog.All)
        {
            sb.Append("complete -c ").Append(exe).Append(" -f -a '").Append(option.Name).Append("'\n");
        }
        foreach (OptionCatalog.Spec option in OptionCatalog.ValueOptions)
        {
            if (option.OptionKind == OptionCatalog.Kind.File)
            {
                sb.Append("complete -c ").Append(exe).Append(" -f -a '(__fish_complete_path)' -n \"__fish_seen_subcommand_from ")
                  .Append(option.Name).Append("\"\n");
                continue;
            }

            string valueList = string.Join(' ', Resolve(option.Choices, encoders));
            if (valueList.Length == 0) continue;
            sb.Append("complete -c ").Append(exe).Append(" -f -a '").Append(valueList)
              .Append("' -n \"__fish_seen_subcommand_from ").Append(option.Name).Append("\"\n");
        }
        return sb.ToString();
    }

    private static string PowerShell(string[] encoders, string exe)
    {
        var sb = new StringBuilder();
        sb.Append("# PowerShell completion for ").Append(exe).Append("  (generated)\n");
        sb.Append("#   LiveArchiveEncoder --completion powershell > lae.ps1\n");
        sb.Append("#   . .\\lae.ps1; Register-ArgumentCompleter -Native -CommandName '")
          .Append(exe).Append("' -ScriptBlock $laeCompleter\n\n");
        sb.Append("$laeCompleter = {\n    param($wordToComplete, $commandAst, $cursorPosition)\n");
        sb.Append("    $prev = $null\n    $elements = $commandAst.CommandElements\n");
        sb.Append("    if ($elements.Count -gt 1) { $prev = [string]$elements[-1] }\n\n");
        sb.Append("    $map = @{\n");
        foreach (OptionCatalog.Spec option in OptionCatalog.ValueOptions)
        {
            // No fixed choices: let PowerShell fall back to its own file/argument
            // completion rather than offering an empty list.
            string[] choices = Resolve(option.Choices, encoders);
            if (choices.Length == 0) continue;
            string list = string.Join(",", choices.Select(v => $"'{Escape(v)}'"));
            sb.Append("        '").Append(option.Name).Append("' = @(").Append(list).Append(")\n");
        }
        sb.Append("    }\n\n");
        sb.Append("    if ($prev -and $map.ContainsKey($prev)) {\n");
        sb.Append("        $map[$prev] | Where-Object { $_ -like \"$wordToComplete*\" }\n        return\n    }\n\n");
        sb.Append("    @(").Append(string.Join(",", OptionCatalog.All.Select(o => $"'{Escape(o.Name)}'")))
          .Append(") | Where-Object { $_ -like \"$wordToComplete*\" }\n}\n");
        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
