using System.Globalization;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.UI;

/// <summary>Parsed command line, mirroring the argparse definition of v6.3.3.</summary>
public sealed class CliOptions
{
    public string? Input { get; init; }
    public string? Output { get; init; }
    public double StartTime { get; init; }
    public double? EndTime { get; init; }
    public string Encoder { get; init; } = "rv20";
    public string? AudioEncoder { get; init; }
    public double Limit { get; init; } = 200.0;
    public double? Recover { get; init; }
    public double Window { get; init; } = 2.0;
    public double Fps { get; init; } = 13.0;
    public string FpsLevels { get; init; } = "13,8,5,3";
    public int? Qv { get; init; }
    public int Threads { get; init; } = 16;
    public int BufferFrames { get; init; } = 6;
    public int AudioKbps { get; init; } = 32;
    public int AudioChannels { get; init; } = 1;
    public int? AudioRate { get; init; }
    public int Width { get; init; } = 256;
    public int Height { get; init; } = 192;
    public bool NoCrop { get; init; }
    public string ScaleAlgo { get; init; } = "neighbor";
    public string Deinterlace { get; init; } = "off";
    public string DeintMode { get; init; } = "send_frame";
    public string DeintParity { get; init; } = "auto";
    public bool DeintMarkedOnly { get; init; }
    public string X264Preset { get; init; } = "medium";
    public string X264Tune { get; init; } = "";
    public string X264Profile { get; init; } = "";
    public string X264Level { get; init; } = "";
    /// <summary>
    /// Hardware decode method. "off" is the default since v7.5: using one needs
    /// a working driver and device, both of which are verified (by decoding a
    /// frame) before the method is actually used.
    /// </summary>
    public string Hwaccel { get; init; } = "off";

    /// <summary>Kept for v7.4 scripts; means <c>--hwaccel off</c>.</summary>
    public bool NoCuda { get; init; }
    public string? FFmpeg { get; init; }
    public string? FFprobe { get; init; }
    public bool ListFilters { get; init; }
    public bool Quiet { get; init; }
    public bool NoCui { get; init; }
    /// <summary>
    /// Inline is the default because it is what v6.3.3 does. The split pipeline is
    /// opt-in: measured over a 5 s segment it is ~0.7 s <em>slower</em> (extra audio
    /// pass plus a remux) and it perturbs the adaptive controller's frame selection,
    /// which shifts the container duration on mpeg1video and asf.
    /// </summary>
    public Encoding.AudioMode AudioMode { get; init; } = Encoding.AudioMode.Inline;
    public bool Help { get; init; }
    public bool CpuInfo { get; init; }

    /// <summary>
    /// Drop the deliberate 1/fps wall-clock pacing. Measured ~58x on a 20s source.
    /// The fresh/repeat mix and the adaptive-FPS ladder are wall-clock driven, so
    /// this changes the output; it stays opt-in for that reason.
    /// </summary>
    public bool Turbo { get; init; }

    /// <summary>Shard count for the parallel pipeline. 1 disables sharding.</summary>
    public int Parallel { get; init; } = 1;

    /// <summary>
    /// UI language, e.g. <c>en</c> or <c>zh</c>. Null means "not given on the
    /// command line", which is what lets the interactive shell fall back to the
    /// language stored in the settings file instead of forcing the default.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>When set, print a shell completion script and exit.</summary>
    public string? Completion { get; init; }

    /// <summary>FFmpeg distribution to use: btbn / gyan / avbuild / nonfree.</summary>
    public string FfmpegSource { get; init; } = LiveArchiveEncoder.FFmpeg.FfmpegCatalog.DefaultId;

    /// <summary>Proxy for FFmpeg downloads, e.g. http://127.0.0.1:10808.</summary>
    public string? FfmpegProxy { get; init; }

    /// <summary>When set, download and install that distribution, then exit.</summary>
    public string? FfmpegDownload { get; init; }

    /// <summary>
    /// Local archive to install instead of downloading. Needed for sources whose
    /// host blocks automated downloads (SourceForge / avbuild).
    /// </summary>
    public string? FfmpegArchive { get; init; }

    /// <summary>When set, delete an installed distribution, then exit.</summary>
    public string? FfmpegDelete { get; init; }

    public bool FfmpegList { get; init; }

    /// <summary>What to open when the encode finishes.</summary>
    public PlayMode Play { get; init; } = PlayMode.Off;

    /// <summary>External player executable; empty uses the shell association.</summary>
    public string? PlayerPath { get; init; }

    public double EffectiveRecover => Recover ?? Limit * 0.75;
}

/// <summary>Minimal, dependency-free parser with argparse-compatible semantics.</summary>
public static class CliParser
{
    public static (CliOptions? Options, string? Error) Parse(string[] args)
    {
        string? input = null;
        string? output = null;
        double start = 0.0;
        double? end = null;
        string encoder = "rv20";
        string? audioEncoder = null;
        double limit = 200.0;
        double? recover = null;
        double window = 2.0;
        double fps = 13.0;
        string fpsLevels = "13,8,5,3";
        int? qv = null;
        int threads = 16;
        int bufferFrames = 6;
        int audioKbps = 32;
        int audioChannels = 1;
        int? audioRate = null;
        int width = 256;
        int height = 192;
        bool noCrop = false;
        string scaleAlgo = "neighbor";
        string deinterlace = "off";
        string deintMode = "send_frame";
        string deintParity = "auto";
        bool deintMarkedOnly = false;
        string x264Preset = "medium";
        string x264Tune = "";
        string x264Profile = "";
        string x264Level = "";
        bool noCuda = false;
        string hwaccel = "off";
        string? ffmpeg = null;
        string? ffprobe = null;
        bool listFilters = false;
        bool quiet = false;
        bool noCui = false;
        Encoding.AudioMode audioMode = Encoding.AudioMode.Inline;
        bool help = false;
        bool cpuInfo = false;
        bool turbo = false;
        int parallel = 1;
        string? language = null;
        string? completion = null;
        string ffmpegSource = FFmpeg.FfmpegCatalog.DefaultId;
        string? ffmpegProxy = null;
        string? ffmpegDownload = null;
        string? ffmpegArchive = null;
        string? ffmpegDelete = null;
        bool ffmpegList = false;
        PlayMode play = PlayMode.Off;
        string? playerPath = null;

        var positionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith('-'))
            {
                positionals.Add(arg);
                continue;
            }

            string name = arg;
            string? inlineValue = null;
            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                name = arg[..eq];
                inlineValue = arg[(eq + 1)..];
            }

            bool TryValue(out string value)
            {
                if (inlineValue is not null) { value = inlineValue; return true; }
                if (i + 1 < args.Length) { value = args[++i]; return true; }
                value = "";
                return false;
            }

            // --ffmpeg-download is usable bare ("install the default"), so its
            // value is only consumed when it actually names a source. Without this
            // `--ffmpeg-download --quiet` would swallow --quiet.
            bool TrySourceValue(out string value)
            {
                value = "";
                if (inlineValue is not null) { value = inlineValue; return true; }
                if (i + 1 < args.Length && FFmpeg.FfmpegCatalog.Find(args[i + 1]) is not null)
                {
                    value = args[++i];
                    return true;
                }
                return false;
            }

            switch (name)
            {
                case "--start-time" or "--start":
                    if (!TryValue(out string startRaw)) return (null, "--start-time requires a value");
                    try { start = Core.Timecode.Parse(startRaw); }
                    catch (FormatException exc) { return (null, exc.Message); }
                    break;
                case "--end-time" or "--end":
                    if (!TryValue(out string endRaw)) return (null, "--end-time requires a value");
                    try { end = Core.Timecode.Parse(endRaw); }
                    catch (FormatException exc) { return (null, exc.Message); }
                    break;
                case "--encoder":
                    if (!TryValue(out encoder)) return (null, "--encoder requires a value");
                    break;
                case "--audio-encoder":
                    if (!TryValue(out audioEncoder)) return (null, "--audio-encoder requires a value");
                    break;
                case "--limit":
                    if (!TryValue(out string limitRaw) || !TryDouble(limitRaw, out limit))
                        return (null, "--limit requires a number");
                    break;
                case "--recover":
                    if (!TryValue(out string recoverRaw) || !TryDouble(recoverRaw, out double recoverValue))
                        return (null, "--recover requires a number");
                    recover = recoverValue;
                    break;
                case "--window":
                    if (!TryValue(out string windowRaw) || !TryDouble(windowRaw, out window))
                        return (null, "--window requires a number");
                    break;
                case "--fps":
                    if (!TryValue(out string fpsRaw) || !TryDouble(fpsRaw, out fps))
                        return (null, "--fps requires a number");
                    break;
                case "--fps-levels":
                    if (!TryValue(out fpsLevels)) return (null, "--fps-levels requires a value");
                    break;
                case "--qv":
                    if (!TryValue(out string qvRaw) || !int.TryParse(qvRaw, out int qvValue))
                        return (null, "--qv requires an integer");
                    qv = qvValue;
                    break;
                case "--threads":
                    if (!TryValue(out string threadsRaw) || !int.TryParse(threadsRaw, out threads))
                        return (null, "--threads requires an integer");
                    break;
                case "--buffer-frames":
                    if (!TryValue(out string bufferRaw) || !int.TryParse(bufferRaw, out bufferFrames))
                        return (null, "--buffer-frames requires an integer");
                    break;
                case "--audio-kbps":
                    if (!TryValue(out string kbpsRaw) || !int.TryParse(kbpsRaw, out audioKbps))
                        return (null, "--audio-kbps requires an integer");
                    break;
                case "--audio-channels":
                    if (!TryValue(out string chRaw) || !int.TryParse(chRaw, out audioChannels))
                        return (null, "--audio-channels requires an integer");
                    break;
                case "--audio-rate":
                    if (!TryValue(out string rateRaw) || !int.TryParse(rateRaw, out int rateValue))
                        return (null, "--audio-rate requires an integer");
                    audioRate = rateValue;
                    break;
                case "--width":
                    if (!TryValue(out string wRaw) || !int.TryParse(wRaw, out width))
                        return (null, "--width requires an integer");
                    break;
                case "--height":
                    if (!TryValue(out string hRaw) || !int.TryParse(hRaw, out height))
                        return (null, "--height requires an integer");
                    break;
                case "--no-crop": noCrop = true; break;
                case "--scale-algo" or "--scale-flags":
                    if (!TryValue(out scaleAlgo)) return (null, "--scale-algo requires a value");
                    break;
                case "--deinterlace":
                    if (!TryValue(out deinterlace)) return (null, "--deinterlace requires a value");
                    deinterlace = deinterlace.ToLowerInvariant();
                    break;
                case "--deint-mode":
                    if (!TryValue(out deintMode)) return (null, "--deint-mode requires a value");
                    break;
                case "--deint-parity":
                    if (!TryValue(out deintParity)) return (null, "--deint-parity requires a value");
                    break;
                case "--deint-marked-only": deintMarkedOnly = true; break;
                case "--x264-preset":
                    if (!TryValue(out x264Preset)) return (null, "--x264-preset requires a value");
                    break;
                case "--x264-tune":
                    if (!TryValue(out x264Tune)) return (null, "--x264-tune requires a value");
                    break;
                case "--x264-profile":
                    if (!TryValue(out x264Profile)) return (null, "--x264-profile requires a value");
                    break;
                case "--x264-level":
                    if (!TryValue(out x264Level)) return (null, "--x264-level requires a value");
                    break;
                case "--lang":
                    if (!TryValue(out language)) return (null, "--lang requires a value");
                    break;
                case "--completion":
                    if (!TryValue(out completion)) return (null, "--completion requires a shell name");
                    completion = completion.Trim().ToLowerInvariant();
                    if (completion is not ("bash" or "zsh" or "fish" or "powershell"))
                        return (null, "--completion supports bash, zsh, fish or powershell");
                    break;
                case "--no-cuda": noCuda = true; break;
                case "--hwaccel":
                    if (!TryValue(out hwaccel)) return (null, "--hwaccel requires a value");
                    hwaccel = hwaccel.Trim().ToLowerInvariant();
                    if (hwaccel.Length == 0) return (null, "--hwaccel requires a method name");
                    break;
                case "--turbo": turbo = true; break;
                case "--parallel":
                    if (!TryValue(out string parRaw) || !int.TryParse(parRaw, out parallel))
                        return (null, "--parallel requires an integer");
                    if (parallel < 1) return (null, "--parallel must be at least 1");
                    if (parallel > Constants.MaxParallelShards)
                        return (null, $"--parallel must be at most {Constants.MaxParallelShards}");
                    break;
                case "--ffmpeg":
                    if (!TryValue(out ffmpeg)) return (null, "--ffmpeg requires a value");
                    break;
                case "--ffprobe":
                    if (!TryValue(out ffprobe)) return (null, "--ffprobe requires a value");
                    break;
                case "--ffmpeg-source":
                    if (!TryValue(out ffmpegSource)) return (null, "--ffmpeg-source requires a value");
                    ffmpegSource = ffmpegSource.Trim().ToLowerInvariant();
                    // "path" is the escape hatch back to the system FFmpeg.
                    if (FFmpeg.FfmpegCatalog.Find(ffmpegSource) is null
                        && !string.Equals(ffmpegSource, "path", StringComparison.OrdinalIgnoreCase))
                        return (null, $"--ffmpeg-source must be one of {FFmpeg.FfmpegCatalog.Ids} or path");
                    break;
                case "--ffmpeg-download":
                    ffmpegDownload = TrySourceValue(out string dl) ? dl.ToLowerInvariant()
                        : FFmpeg.FfmpegCatalog.DefaultId;
                    break;
                case "--ffmpeg-archive":
                    if (!TryValue(out ffmpegArchive)) return (null, "--ffmpeg-archive requires a path");
                    ffmpegArchive = ffmpegArchive.Trim().Trim('"');
                    break;
                case "--ffmpeg-delete":
                    if (!TrySourceValue(out ffmpegDelete))
                        return (null, $"--ffmpeg-delete requires one of {FFmpeg.FfmpegCatalog.Ids}");
                    ffmpegDelete = ffmpegDelete.ToLowerInvariant();
                    break;
                case "--ffmpeg-list": ffmpegList = true; break;
                case "--proxy":
                    if (!TryValue(out ffmpegProxy)) return (null, "--proxy requires a value");
                    break;
                case "--play":
                    if (!TryValue(out string playRaw)) return (null, "--play requires a mode");
                    play = Player.ParseMode(playRaw);
                    if (play == PlayMode.Off && !playRaw.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
                        && playRaw.Trim().Length > 0)
                        return (null, "--play supports off, ffplay or external");
                    break;
                case "--player":
                    if (!TryValue(out playerPath)) return (null, "--player requires a path");
                    break;
                case "--list-filters": listFilters = true; break;
                case "--quiet": quiet = true; break;
                case "--no-cui": noCui = true; break;
                case "--audio-mode":
                    if (!TryValue(out string modeRaw)) return (null, "--audio-mode requires a value");
                    audioMode = modeRaw.Trim().ToLowerInvariant() switch
                    {
                        "auto" => Encoding.AudioMode.Auto,
                        "inline" => Encoding.AudioMode.Inline,
                        "separate" => Encoding.AudioMode.Separate,
                        _ => (Encoding.AudioMode?)null ?? throw new FormatException("bad audio mode"),
                    };
                    break;
                case "--cpu-info": cpuInfo = true; break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    return (null, $"unrecognized argument: {arg}");
            }
        }

        if (positionals.Count > 0) input = positionals[0];
        if (positionals.Count > 1) output = positionals[1];
        if (positionals.Count > 2) return (null, "too many positional arguments");

        return (new CliOptions
        {
            Input = input,
            Output = output,
            StartTime = start,
            EndTime = end,
            Encoder = encoder,
            AudioEncoder = audioEncoder,
            Limit = limit,
            Recover = recover,
            Window = window,
            Fps = fps,
            FpsLevels = fpsLevels,
            Qv = qv,
            Threads = threads,
            BufferFrames = bufferFrames,
            AudioKbps = audioKbps,
            AudioChannels = audioChannels,
            AudioRate = audioRate,
            Width = width,
            Height = height,
            NoCrop = noCrop,
            ScaleAlgo = scaleAlgo,
            Deinterlace = deinterlace,
            DeintMode = deintMode,
            DeintParity = deintParity,
            DeintMarkedOnly = deintMarkedOnly,
            X264Preset = x264Preset,
            X264Tune = x264Tune,
            X264Profile = x264Profile,
            X264Level = x264Level,
            // --no-cuda is the v7.4 spelling of "off" and wins over --hwaccel
            // when both are given, because it is the more explicit request.
            Hwaccel = noCuda ? "off" : hwaccel,
            NoCuda = noCuda,
            FFmpeg = ffmpeg,
            FFprobe = ffprobe,
            ListFilters = listFilters,
            Quiet = quiet,
            NoCui = noCui,
            AudioMode = audioMode,
            Help = help,
            CpuInfo = cpuInfo,
            Turbo = turbo,
            Parallel = parallel,
            Language = language,
            Completion = completion,
            FfmpegSource = ffmpegSource,
            FfmpegProxy = ffmpegProxy,
            FfmpegDownload = ffmpegDownload,
            FfmpegArchive = ffmpegArchive,
            FfmpegDelete = ffmpegDelete,
            FfmpegList = ffmpegList,
            Play = play,
            PlayerPath = playerPath,
        }, null);
    }

    /// <summary>argparse-style help text. Printed to stdout, exit code 0.</summary>
    public static string Usage()
    {
        (string flag, string meta, string help)[] rows =
        [
            ("-h, --help", "", "show this help message and exit"),
            ("--start-time, --start", "HH:MM:SS", "segment start time, e.g. 00:12:30 (default: 00:00:00)"),
            ("--end-time, --end", "HH:MM:SS", "exclusive segment end time (default: end of source)"),
            ("--encoder", "NAME", "video encoder (default: rv20)"),
            ("--audio-encoder", "NAME", "audio encoder (default: chosen per container)"),
            ("--limit", "KBPS", "bitrate ceiling that triggers an FPS downgrade (default: 200)"),
            ("--recover", "KBPS", "bitrate floor that allows an upgrade (default: limit * 0.75)"),
            ("--window", "SEC", "rolling window for the bitrate average (default: 2)"),
            ("--fps", "FPS", "target frame rate (default: 13)"),
            ("--fps-levels", "LIST", "adaptive FPS ladder (default: 13,8,5,3)"),
            ("--qv", "N", "video quality: CRF for libx264, q:v for other codecs"),
            ("--threads", "N", "FFmpeg thread count (default: 16)"),
            ("--buffer-frames", "N", "capture buffer depth (default: 6)"),
            ("--audio-kbps", "N", "audio bitrate (default: 32)"),
            ("--audio-channels", "N", "audio channels (default: 1)"),
            ("--audio-rate", "HZ", "audio sample rate (default: source)"),
            ("--width", "N", "output width (default: 256)"),
            ("--height", "N", "output height (default: 192)"),
            ("--no-crop", "", "scale instead of crop to the target size"),
            ("--scale-algo, --scale-flags", "NAME", "scaler kernel; cubic is an alias for bicubic (default: neighbor)"),
            ("--deinterlace", "NAME", "off, yadif, bwdif, ... (default: off)"),
            ("--deint-mode", "MODE", "send_frame or send_field (default: send_frame)"),
            ("--deint-parity", "PAR", "auto, tff or bff (default: auto)"),
            ("--deint-marked-only", "", "only deinterlace frames flagged interlaced"),
            ("--x264-preset", "NAME", "libx264 preset (default: medium)"),
            ("--x264-tune", "NAME", "libx264 tune"),
            ("--x264-profile", "NAME", "libx264 profile"),
            ("--x264-level", "NAME", "libx264 level"),
            ("--hwaccel", "NAME", "hardware decode: off (default), auto, or a method from "
                                + "\"ffmpeg -hwaccels\" (cuda, vaapi, qsv, ...); verified before use"),
            ("--no-cuda", "", "shorthand for --hwaccel off"),
            ("--ffmpeg", "PATH", "explicit ffmpeg executable"),
            ("--ffprobe", "PATH", "explicit ffprobe executable"),
            ("--list-filters", "", "print the detected filters and scalers, then exit"),
            ("--audio-mode", "MODE", "auto, inline or separate (default: inline)"),
            ("--lang", "CODE", "UI language: en or zh (default: en)"),
            ("--completion", "SHELL", "print a completion script (bash/zsh/fish/powershell) and exit"),
            ("--turbo", "", "drop the 1/fps realtime pacing (much faster; output differs)"),
            ("--parallel", "N", "shard the range and encode concurrently (default: 1)"),
            ("--quiet", "", "suppress the progress banner"),
            ("--no-cui", "", "never enter the interactive UI"),
            ("--ffmpeg-source", "ID", $"FFmpeg distribution: {FFmpeg.FfmpegCatalog.Ids} or path (default: btbn)"),
            ("--ffmpeg-download", "ID", "download and install a distribution, then exit"),
            ("--ffmpeg-archive", "PATH", "install from a local archive instead of downloading"),
            ("--ffmpeg-delete", "ID", "delete an installed distribution, then exit"),
            ("--ffmpeg-list", "", "list the installed distributions, then exit"),
            ("--proxy", "URL", "proxy for FFmpeg downloads, e.g. http://127.0.0.1:10808"),
            ("--play", "MODE", "after encoding: off, ffplay or external (default: off)"),
            ("--player", "PATH", "external player executable (default: system file association)"),
        ];

        var sb = new System.Text.StringBuilder();
        sb.Append("usage: LiveArchiveEncoder [--help] [options] [input] [output]\n\n");
        sb.Append("Live-style adaptive archival encoder ");
        sb.Append("(frame/packet impairment is handled by frame_damage.py)\n\n");
        sb.Append("positional arguments:\n");
        sb.Append("  input                   source file; omit to enter the interactive UI\n");
        sb.Append("  output                  destination file\n\n");
        sb.Append("options:\n");
        foreach ((string flag, string meta, string help) in rows)
        {
            string left = meta.Length > 0 ? $"{flag} {meta}" : flag;
            sb.Append("  ").Append(left);
            if (left.Length < 34) sb.Append(' ', 34 - left.Length);
            sb.Append(help).Append('\n');
        }
        return sb.ToString();
    }

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
