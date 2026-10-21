using System.Globalization;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Encoding;

/// <summary>How a subtitle track reaches the output (v7.9 goal 2).</summary>
public enum SubtitleMode
{
    /// <summary>Render the text into the picture (needs the libass subtitles filter).</summary>
    Burn,

    /// <summary>Mux the track as a real subtitle stream, where the container has one.</summary>
    Copy,

    /// <summary>No subtitles.</summary>
    Off,
}

/// <summary>What the user asked for.</summary>
/// <param name="Files">External subtitle files (sidecar .ass/.srt, e.g. clip.sc.ass).</param>
/// <param name="Stream">Absolute index of an embedded subtitle track.</param>
/// <param name="Mode">Burn into the picture, carry as a stream, or none.</param>
/// <param name="Codec">Explicit subtitle codec for <see cref="SubtitleMode.Copy"/>.</param>
public sealed record SubtitleSettings(
    IReadOnlyList<string> Files, int? Stream, SubtitleMode Mode, string? Codec)
{
    public bool Active => Mode != SubtitleMode.Off && (Files.Count > 0 || Stream is not null);
}

/// <summary>
/// The resolved plan: which files to burn, or which track to carry and how.
/// Disposing it removes the staging directory an embedded track was written to.
/// </summary>
public sealed class SubtitlePlan : IDisposable
{
    public SubtitleMode Mode { get; init; } = SubtitleMode.Off;

    /// <summary>Files to render into the picture, in order.</summary>
    public IReadOnlyList<string> BurnFiles { get; init; } = [];

    /// <summary>File to open as an extra input in copy mode (a sidecar, or the
    /// source itself when the pick is an embedded track).</summary>
    public string? CopyFile { get; init; }

    /// <summary>Subtitle stream to map out of <see cref="CopyFile"/>; null means
    /// "its only subtitle stream" (the sidecar case).</summary>
    public int? CopyStream { get; init; }

    public string? CopyCodec { get; init; }

    /// <summary>Directory holding an extracted track; removed on dispose.</summary>
    public string? StageDir { get; init; }

    public bool Active => Mode != SubtitleMode.Off;

    /// <summary>One-line report for the verbose banner, or null.</summary>
    public string Describe()
    {
        string what = BurnFiles.Count > 0
            ? string.Join(", ", BurnFiles.Select(f => Path.GetFileName(f)))
            : CopyStream is int i ? $"stream {i}" : (CopyFile is null ? "?" : Path.GetFileName(CopyFile));
        return Mode == SubtitleMode.Burn
            ? $"burning {what} into the picture"
            : $"carrying {what} as {CopyCodec}";
    }

    public void Dispose()
    {
        if (StageDir is null) return;
        try { Directory.Delete(StageDir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Subtitles: read them, burn them, or carry them (v7.9 goal 2).
///
/// "以 ffmpeg -encoders 为准" -- the copy path never assumes a codec name, it
/// picks the first one the container prefers that the binary in use actually
/// lists under <c>S</c> in <c>ffmpeg -encoders</c>.
///
/// Two things are deliberately refused rather than done badly:
///   * burning a bitmap subtitle (dvdsub/dvbsub): libass renders text, and
///     turning a subpicture into text is OCR, which FFmpeg does not do;
///   * carrying a subtitle in a container that has none (rm, flv, asf, ...),
///     which is also why burning is the default.
/// </summary>
public static class SubtitleTrack
{
    /// <summary>Parse <c>--subtitle-mode</c>; the parser already rejected anything else.</summary>
    public static SubtitleMode ParseMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "burn" => SubtitleMode.Burn,
        "copy" => SubtitleMode.Copy,
        _ => SubtitleMode.Off,
    };

    /// <summary>The filter that renders text, and the build flag behind it.</summary>
    public const string BurnFilter = "subtitles";

    /// <summary>
    /// Resolve the request, or throw <see cref="ArgumentException"/> with the
    /// reason. Returns null when nothing was asked for.
    /// </summary>
    public static SubtitlePlan? Plan(
        SourceInfo source, SubtitleSettings settings, string formatName,
        HashSet<string> availableFilters, HashSet<string> subtitleEncoders,
        string ffmpeg, string sourcePath)
    {
        if (!settings.Active) return null;

        if (settings.Files.Count > 0 && settings.Stream is not null)
            throw new ArgumentException(
                "give either --subtitle (an external file) or --subtitle-stream "
                + "(a track inside the source), not both");

        if (settings.Mode == SubtitleMode.Burn)
            return BurnPlan(source, settings, availableFilters, ffmpeg, sourcePath);
        return CopyPlan(source, settings, formatName, subtitleEncoders);
    }

    private static SubtitlePlan BurnPlan(
        SourceInfo source, SubtitleSettings settings, HashSet<string> availableFilters,
        string ffmpeg, string sourcePath)
    {
        if (!availableFilters.Contains(BurnFilter))
        {
            throw new ArgumentException(
                $"this FFmpeg build has no '{BurnFilter}' filter (it needs libass), "
                + "so subtitles cannot be burned into the picture");
        }

        if (settings.Files.Count > 0)
        {
            foreach (string file in settings.Files)
            {
                if (!File.Exists(file))
                    throw new ArgumentException($"subtitle file not found: {file}");
                RejectBitmap(CodecForFile(file), $"file '{Path.GetFileName(file)}'");
            }
            return new SubtitlePlan { Mode = SubtitleMode.Burn, BurnFiles = settings.Files };
        }

        int index = settings.Stream!.Value;
        if (!source.Streams.Any(s => s.Index == index))
            throw new ArgumentException(
                $"--subtitle-stream {index} does not name a stream in this source."
                + $"\n{StreamSelection.Describe(source)}");
        SourceStream stream = source.Streams.First(s => s.Index == index);
        if (stream.Type != "subtitle")
        {
            throw new ArgumentException(
                $"--subtitle-stream {index} is a {stream.Type} stream, not a subtitle."
                + $"\n{StreamSelection.Describe(source)}");
        }
        RejectBitmap(stream.Codec, $"subtitle stream {index} ({stream.Codec})");

        // The subtitles filter reads a file, not a stream, so the track is
        // staged first -- which is also the only shape that works for a URL
        // source, where the filter cannot open the origin itself.
        (string? staged, string? stageDir) = Stage(ffmpeg, sourcePath, index, stream.Codec);
        if (staged is null)
        {
            throw new ArgumentException(
                $"could not extract subtitle stream {index} ({stream.Codec}) from the source; "
                + "use --subtitle-mode copy to carry it instead, or --subtitle with a file");
        }
        return new SubtitlePlan
        {
            Mode = SubtitleMode.Burn,
            BurnFiles = [staged],
            StageDir = stageDir,
        };
    }

    private static SubtitlePlan CopyPlan(
        SourceInfo source, SubtitleSettings settings, string formatName,
        HashSet<string> subtitleEncoders)
    {
        if (!Constants.SubtitleCodecsByContainer.TryGetValue(formatName, out string[]? preferred))
        {
            throw new ArgumentException(
                $"the '{formatName}' container cannot carry a subtitle stream; "
                + "use --subtitle-mode burn instead");
        }

        string? codec = settings.Codec;
        if (codec is { Length: > 0 })
        {
            if (!subtitleEncoders.Contains(codec))
            {
                throw new ArgumentException(
                    $"this FFmpeg build cannot write subtitle codec '{codec}'; "
                    + $"it has: {string.Join(", ", subtitleEncoders.OrderBy(x => x, StringComparer.Ordinal))}");
            }
        }
        else
        {
            codec = preferred.FirstOrDefault(subtitleEncoders.Contains)
                ?? throw new ArgumentException(
                    $"this FFmpeg build has no subtitle encoder the '{formatName}' container "
                    + $"can hold ({string.Join(", ", preferred)}); use --subtitle-mode burn instead");
        }

        if (settings.Files.Count > 0)
        {
            // Only one sidecar can become "the" subtitle stream; two language
            // tracks need --subtitle-mode burn.
            string file = settings.Files[0];
            if (!File.Exists(file))
                throw new ArgumentException($"subtitle file not found: {file}");
            if (settings.Files.Count > 1)
                throw new ArgumentException(
                    "--subtitle-mode copy takes one subtitle file; use --subtitle-mode burn "
                    + "to put several tracks into the picture");
            return new SubtitlePlan
            {
                Mode = SubtitleMode.Copy, CopyFile = file, CopyStream = null, CopyCodec = codec,
            };
        }

        int index = settings.Stream!.Value;
        if (!source.Streams.Any(s => s.Index == index && s.Type == "subtitle"))
        {
            throw new ArgumentException(
                $"--subtitle-stream {index} does not name a subtitle stream in this source."
                + $"\n{StreamSelection.Describe(source)}");
        }
        return new SubtitlePlan
        {
            Mode = SubtitleMode.Copy, CopyFile = null, CopyStream = index, CopyCodec = codec,
        };
    }

    private static void RejectBitmap(string? codec, string what)
    {
        if (codec is { Length: > 0 } && Constants.BitmapSubtitleCodecs.Contains(codec))
        {
            throw new ArgumentException(
                $"{what} is '{codec}', a bitmap subtitle: it can be carried with "
                + "--subtitle-mode copy but not rendered into the picture, because "
                + "turning a subpicture into text needs OCR, which FFmpeg does not do");
        }
    }

    /// <summary>Subtitle codec a sidecar file is written in, from its extension.</summary>
    public static string CodecForFile(string path) =>
        Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
        {
            "ass" or "ssa" => "ass",
            "srt" => "subrip",
            "vtt" => "webvtt",
            _ => "",
        };

    /// <summary>
    /// Append the burn filters to a finished chain.
    ///
    /// They go <em>last</em>, onto the scaled and padded frame: rendering first
    /// and scaling afterwards would blur the text, and cropping afterwards would
    /// cut it in half.
    /// </summary>
    public static string AppendBurn(string vf, IEnumerable<string> files)
    {
        string dir = FontResolver.FontDirectory;
        bool haveDir = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);

        var parts = new List<string>();
        foreach (string file in files)
        {
            // Without fontsdir an ASS file names fonts the machine may not have
            // (a Chinese release asking for "Microsoft YaHei" on Linux renders
            // boxes); the resolver already knows where this tool's fonts live.
            parts.Add(haveDir
                ? $"subtitles={EscapeFilterPath(file)}:fontsdir={EscapeFilterPath(dir)}"
                : $"subtitles={EscapeFilterPath(file)}");
        }
        return parts.Count == 0 ? vf : (vf.Length > 0 ? $"{vf}," : "") + string.Join(",", parts);
    }

    /// <summary>
    /// Make a path safe inside a filter argument.
    ///
    /// FFmpeg parses a filter description on two levels (the graph, then each
    /// option value), and both treat <c>\</c> and <c>:</c> as special, so a
    /// Windows path needs its separators doubled and its drive colon escaped
    /// <em>and</em> the whole thing single-quoted -- measured 2026-10-21:
    /// <c>'C\:\\Users\\x.srt'</c> opens, the unquoted spellings all end up
    /// parsed as the <c>original_size</c> option with a mangled name.
    /// </summary>
    public static string EscapeFilterPath(string path) =>
        "'" + path.Replace("\\", "\\\\", StringComparison.Ordinal)
                  .Replace(":", "\\:", StringComparison.Ordinal)
                  .Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The extra FFmpeg input carrying the track in copy mode, seeked to the
    /// segment like the audio input is -- a subtitle file holds the whole
    /// timeline, so without this the events would land at the wrong moment.
    /// </summary>
    public static List<string> CopyInputArgs(SubtitlePlan plan, string sourcePath, double startSeconds, double? duration)
    {
        string file = plan.CopyFile ?? sourcePath;
        var args = new List<string>();
        args.AddRange(Remote.HttpSource.ReadArgs(file));
        args.AddRange(Remote.Smb.RemoteReadArgs(file));
        if (startSeconds > 0) args.AddRange(["-ss", ArgsBuilder.FloatArg(startSeconds)]);
        args.AddRange(["-i", file]);
        return args;
    }

    /// <summary>Where the copy-mode input lands in the encoder's input list.</summary>
    public static string CopyMap(int inputIndex, SubtitlePlan plan) =>
        plan.CopyStream is int index ? $"{inputIndex}:{index}" : $"{inputIndex}:s:0";

    /// <summary>Codec argument for copy mode.</summary>
    public static List<string> CopyCodecArgs(SubtitlePlan plan) =>
        plan.CopyCodec is { Length: > 0 } codec ? ["-c:s", codec] : ["-c:s", "copy"];

    /// <summary>
    /// Write an embedded subtitle track out as a file the filter can read.
    ///
    /// Text tracks are copied as they are; <c>mov_text</c> and <c>text</c> are
    /// transcoded to SubRip because libass cannot parse their raw form.
    /// </summary>
    public static (string? Path, string? Dir) Stage(
        string ffmpeg, string sourcePath, int index, string? codec)
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae_subs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        bool transcode = codec is "mov_text" or "text";
        string ext = transcode ? "srt" : codec switch
        {
            "subrip" or "srt" => "srt",
            "webvtt" => "vtt",
            _ => "ass",
        };
        string target = Path.Combine(dir, $"subs{index}.{ext}");

        var cmd = new List<string> { ffmpeg, "-hide_banner", "-loglevel", "error" };
        cmd.AddRange(Remote.HttpSource.ReadArgs(sourcePath));
        cmd.AddRange(Remote.Smb.RemoteReadArgs(sourcePath));
        cmd.AddRange([
            "-i", sourcePath, "-map", $"0:{index}", "-vn", "-an",
            "-c:s", transcode ? "srt" : "copy", "-y", target,
        ]);

        CaptureResult p = ProcessRunner.Run(cmd, Probe.SourceTimeoutSeconds);
        if (p.ExitCode != 0 || !File.Exists(target) || new FileInfo(target).Length == 0)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            return (null, null);
        }
        return (target, dir);
    }

    /// <summary>Total subtitle event count is unknown here; this only reports
    /// the mode for the verbose banner.</summary>
    public static string FormatMode(SubtitleMode mode) => mode switch
    {
        SubtitleMode.Burn => "burn",
        SubtitleMode.Copy => "copy",
        _ => "off",
    };
}
