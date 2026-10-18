using System.Globalization;
using System.Text;
using LiveArchiveEncoder.Configuration;

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
/// </summary>
public static class Completion
{
    /// <summary>Options that take a value; everything else is a bare flag.</summary>
    private static readonly (string Flag, string[] Values)[] ValueOptions =
    [
        ("--start-time", []), ("--start", []), ("--end-time", []), ("--end", []),
        ("--encoder", ["@ENCODERS"]),
        ("--audio-encoder", ["libmp3lame", "aac", "ac3", "mp2", "wmav2", "adpcm_ima_amv", "roq_dpcm"]),
        ("--limit", []), ("--recover", []), ("--window", []), ("--fps", []),
        ("--fps-levels", ["13,8,5,3", "25,15,10,5"]),
        ("--qv", []), ("--threads", []), ("--buffer-frames", []),
        ("--audio-kbps", []), ("--audio-channels", ["1", "2"]), ("--audio-rate", ["22050", "44100"]),
        ("--width", []), ("--height", []),
        ("--scale-algo", [.. Constants.ScaleAlgorithmAliases.Keys.OrderBy(x => x, StringComparer.Ordinal)]),
        ("--scale-flags", [.. Constants.ScaleAlgorithmAliases.Keys.OrderBy(x => x, StringComparer.Ordinal)]),
        ("--deinterlace", ["off", .. Constants.DeinterlaceFilters]),
        ("--hwaccel", ["off", "auto", "cuda", "vaapi", "qsv", "d3d11va", "dxva2", "vdpau", "vulkan"]),
        ("--deint-mode", ["send_frame", "send_field"]),
        ("--deint-parity", ["auto", "tff", "bff"]),
        ("--x264-preset", ["ultrafast", "veryfast", "faster", "fast", "medium", "slow", "slower"]),
        ("--x264-tune", ["film", "animation", "grain", "stillimage", "fastdecode", "zerolatency"]),
        ("--audio-mode", ["inline", "separate", "auto"]),
        ("--lang", ["en", "zh"]),
        ("--completion", ["bash", "zsh", "fish", "powershell"]),
        ("--parallel", ["1", "2", "4", "8"]),
        ("--ffmpeg", ["@FILE"]), ("--ffprobe", ["@FILE"]),
        // Available, not All: only the ids installable on this platform are
        // worth completing, and All would list "btbn" twice.
        ("--ffmpeg-source", ["path", .. FFmpeg.FfmpegCatalog.Available.Select(d => d.Id).Distinct()]),
        ("--ffmpeg-download", [.. FFmpeg.FfmpegCatalog.Available.Select(d => d.Id).Distinct()]),
        ("--ffmpeg-archive", ["@FILE"]),
        ("--ffmpeg-delete", [.. FFmpeg.FfmpegCatalog.Available.Select(d => d.Id).Distinct()]),
        ("--play", ["off", "ffplay", "external"]),
        ("--player", ["@FILE"]),
        ("--proxy", ["http://127.0.0.1:10808", "http://127.0.0.1:7890", "socks5://127.0.0.1:10808"]),
    ];

    private const string FileMarker = "@FILE";

    private static bool IsFileValues(string[] values) =>
        values.Length == 1 && values[0] == FileMarker;

    private static bool HasChoices(string[] values) =>
        values.Length > 0 && !IsFileValues(values);

    private static readonly string[] FlagOptions =
    [
        "-h", "--help", "--no-crop", "--deint-marked-only", "--no-cuda", "--turbo",
        "--list-filters", "--quiet", "--no-cui", "--cpu-info", "--ffmpeg-list",
    ];

    private static string[] Resolve(string[] values, IEnumerable<string> encoders)
    {
        if (values.Length == 1 && values[0] == "@ENCODERS") return [.. encoders.OrderBy(x => x, StringComparer.Ordinal)];
        return values;
    }

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
        sb.Append("_").Append(exe.ToLowerInvariant()).Append("_opts='");
        sb.Append(string.Join(' ', FlagOptions.Concat(ValueOptions.Select(o => o.Flag))));
        sb.Append("'\n\n");
        sb.Append('_').Append(exe.ToLowerInvariant()).Append("() {\n");
        sb.Append("    local cur prev\n");
        sb.Append("    cur=\"${COMP_WORDS[COMP_CWORD]}\"\n");
        sb.Append("    prev=\"${COMP_WORDS[COMP_CWORD-1]}\"\n\n");
        sb.Append("    case \"$prev\" in\n");
        foreach ((string flag, string[] values) in ValueOptions)
        {
            // No known choices (numbers, timecodes): leave it to the caller's
            // default completion instead of emitting an empty -W list.
            if (!HasChoices(values) && !IsFileValues(values)) continue;

            sb.Append("        ").Append(flag).Append(")\n");
            sb.Append(IsFileValues(values)
                ? "            COMPREPLY=( $(compgen -f -- \"$cur\") )\n"
                : "            COMPREPLY=( $(compgen -W \""
                  + string.Join(' ', Resolve(values, encoders)) + "\" -- \"$cur\") )\n");
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
        sb.Append("_").Append(exe.ToLowerInvariant()).Append("() {\n");
        sb.Append("    local -a opts\n    opts=(\n");
        foreach (string flag in FlagOptions) sb.Append("        '").Append(flag).Append("'\n");
        foreach ((string flag, string[] values) in ValueOptions)
        {
            if (IsFileValues(values))
            {
                sb.Append("        '").Append(flag).Append(":file:_files'\n");
                continue;
            }
            string valueList = string.Join(' ', Resolve(values, encoders));
            if (valueList.Length == 0)
                sb.Append("        '").Append(flag).Append(":value:'\n");
            else
                sb.Append("        '").Append(flag).Append(":value:(").Append(valueList).Append(")'\n");
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
        foreach (string flag in FlagOptions)
        {
            sb.Append("complete -c ").Append(exe).Append(" -f -a '").Append(flag).Append("'\n");
        }
        foreach ((string flag, string[] values) in ValueOptions)
        {
            if (IsFileValues(values))
            {
                sb.Append("complete -c ").Append(exe).Append(" -f -a '(__fish_complete_path)' -n \"__fish_seen_subcommand_from ")
                  .Append(flag).Append("\"\n");
                continue;
            }
            string valueList = string.Join(' ', Resolve(values, encoders));
            if (valueList.Length == 0) continue;
            sb.Append("complete -c ").Append(exe).Append(" -f -a '").Append(valueList).Append("' -n \"__fish_seen_subcommand_from ")
              .Append(flag).Append("\"\n");
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
        foreach ((string flag, string[] values) in ValueOptions)
        {
            // No fixed choices: let PowerShell fall back to its own file/argument
            // completion rather than offering an empty list.
            if (!HasChoices(values)) continue;
            string list = string.Join(",", Resolve(values, encoders).Select(v => $"'{Escape(v)}'"));
            sb.Append("        '").Append(flag).Append("' = @(").Append(list).Append(")\n");
        }
        sb.Append("    }\n\n");
        sb.Append("    if ($prev -and $map.ContainsKey($prev)) {\n");
        sb.Append("        $map[$prev] | Where-Object { $_ -like \"$wordToComplete*\" }\n        return\n    }\n\n");
        sb.Append("    @(").Append(string.Join(",", FlagOptions.Concat(ValueOptions.Select(o => o.Flag))
            .Select(f => $"'{Escape(f)}'"))).Append(") | Where-Object { $_ -like \"$wordToComplete*\" }\n}\n");
        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
