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
    /// <summary>
    /// v7.9.5.1: extra directory libass looks in, on top of the program's own
    /// <c>fonts</c> directory. Null means "the program's directory only".
    /// </summary>
    public string? FontDir { get; init; }

    /// <summary>
    /// v7.9.5.1: family forced over the one the file asks for. Used when the
    /// file names a font nobody has, and for SRT/VTT, which name none.
    /// </summary>
    public string? FallbackFont { get; init; }

    /// <summary>
    /// v7.9.8.3: the shape the text is laid out in -- <c>4:3</c>, <c>16:9</c> or
    /// null/"source" for "the whole picture". See
    /// <see cref="SubtitleTrack.BurnInto"/>.
    /// </summary>
    public string? BoxAspect { get; init; }

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

    /// <summary>v7.9.5.1: extra font directory for the burn filter.</summary>
    public string? FontDir { get; init; }

    /// <summary>v7.9.5.1: family forced over the file's own choice.</summary>
    public string? FallbackFont { get; init; }

    /// <summary>
    /// v7.9.8.3: the box the text is laid out in, as a ratio, or null for "the
    /// whole picture". Resolved against the delivered frame at burn time --
    /// the frame size is not known when the plan is made (a shard planner may
    /// still adjust it).
    /// </summary>
    public (int Num, int Den)? BoxAspect { get; init; }

    /// <summary>Directory holding an extracted track; removed on dispose.</summary>
    public string? StageDir { get; init; }

    public bool Active => Mode != SubtitleMode.Off;

    /// <summary>One-line report for the verbose banner, or null.</summary>
    public string Describe()
    {
        string what = BurnFiles.Count > 0
            ? string.Join(", ", BurnFiles.Select(f => Path.GetFileName(f)))
            : CopyStream is int i ? $"stream {i}" : (CopyFile is null ? "?" : Path.GetFileName(CopyFile));
        string text = Mode == SubtitleMode.Burn
            ? $"burning {what} into the picture"
            : $"carrying {what} as {CopyCodec}";
        if (Mode == SubtitleMode.Burn && FallbackFont.HasText)
            text += $" (font forced to {FallbackFont})";
        return text;
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
            return new SubtitlePlan
            {
                Mode = SubtitleMode.Burn,
                BurnFiles = settings.Files,
                FontDir = settings.FontDir,
                FallbackFont = settings.FallbackFont,
                BoxAspect = Constants.ParseAspect(settings.BoxAspect),
            };
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
            FontDir = settings.FontDir,
            FallbackFont = settings.FallbackFont,
            BoxAspect = Constants.ParseAspect(settings.BoxAspect),
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
    ///
    /// v7.9.5.1: <paramref name="fontDir"/> points libass at the fonts a release
    /// actually needs, and <paramref name="fallbackFont"/> forces a family over
    /// the one the file names (or gives SRT/VTT a family they never had).
    /// </summary>
    public static string AppendBurn(
        string vf, IEnumerable<string> files, string? fontDir = null, string? fallbackFont = null)
    {
        string? dir = ResolveFontDirectory(fontDir);
        string? forced = fallbackFont.HasText ? EscapeFilterValue(fallbackFont.Trim()) : null;

        var parts = new List<string>();
        foreach (string file in files)
        {
            // Without fontsdir an ASS file names fonts the machine may not have
            // (a Chinese release asking for "Microsoft YaHei" on Linux renders
            // boxes); the resolver already knows where this tool's fonts live.
            var options = new System.Text.StringBuilder($"subtitles={EscapeFilterPath(file)}");
            if (dir is not null) options.Append(":fontsdir=").Append(EscapeFilterPath(dir));
            if (forced is not null)
                options.Append(":force_style='FontName=").Append(forced).Append('\'');
            parts.Add(options.ToString());
        }
        return parts.Count == 0 ? vf : (vf.Length > 0 ? $"{vf}," : "") + string.Join(",", parts);
    }

    /// <summary>
    /// Add the burn step to a finished filter chain.
    ///
    /// <para>Without a declared box this is the linear chain every earlier
    /// version produced: the text is rendered onto the scaled and padded frame,
    /// last, so it is neither scaled nor cropped with the picture.</para>
    ///
    /// <para>v7.9.8.4: <paramref name="startSeconds"/> puts the burn filter back
    /// on the source's clock. A run that starts at 00:22:45 hands FFmpeg an
    /// input <c>-ss</c>, which rebases the picture to 00:00:00, while the
    /// subtitle file still counts from the beginning of the source -- without
    /// this the whole track lands 22 minutes late and no line is ever shown.
    /// The offset is added in front of the filter and taken back after it, so
    /// everything else in the graph (and <c>-t</c>) keeps the timestamps it
    /// expects.</para>
    ///
    /// <para>With a box (<c>--subtitle-aspect</c>) the text cannot live in that
    /// chain: libass paints into whatever frame it is given, so a 4:3 script on a
    /// 16:9 picture would either spread across the extra width or -- for a 16:9
    /// script on a 4:3 picture -- run off the edge. Instead the finished frame is
    /// cropped to the box, the text is rendered there, and the piece is overlaid
    /// back at the same offset. Same size, same place, no scaling: the pixels
    /// outside the box are untouched, and inside it the layout is exactly what
    /// the script would get on a frame of its own shape.</para>
    /// </summary>
    public static VideoFilter BurnInto(
        string chain, SubtitlePlan plan, int frameW, int frameH,
        string transportFmt, string input, double startSeconds = 0)
    {
        string burn = Shifted(
            AppendBurn("", plan.BurnFiles, plan.FontDir, plan.FallbackFont), startSeconds);
        if (plan.BoxAspect is not { } aspect)
            return VideoFilter.Simple(chain.Length == 0 ? burn : $"{chain},{burn}");

        SubtitleBox box = SubtitleBox.Contain(frameW, frameH, aspect.Num, aspect.Den);
        return VideoFilter.Boxed(chain, burn, input, box, frameW, frameH, transportFmt);
    }

    /// <summary>
    /// Move the burn filters onto the source's clock and put the segment's back
    /// afterwards -- see <see cref="BurnInto"/>. Zero returns the string it was
    /// given, so a run from 00:00:00 builds exactly the command it always did.
    /// </summary>
    public static string Shifted(string burn, double seconds)
    {
        if (seconds <= 0 || burn.Length == 0) return burn;
        string offset = ArgsBuilder.FloatArg(seconds);
        return $"setpts=PTS+{offset}/TB,{burn},setpts=PTS-{offset}/TB";
    }

    /// <summary>
    /// The directory handed to <c>fontsdir</c>. The program's own directory wins
    /// when it holds fonts -- those are the ones that travel with the tool -- and
    /// the user's extra directory is the fallback. Null means "let fontconfig
    /// answer", which is the old behaviour.
    /// </summary>
    public static string? ResolveFontDirectory(string? extraDir)
    {
        string program = FontResolver.FontDirectory;
        if (HasFontFiles(program)) return program;
        if (extraDir.HasText && HasFontFiles(extraDir)) return extraDir;
        return Directory.Exists(program) ? program : null;
    }

    private static bool HasFontFiles(string dir)
    {
        try
        {
            return dir.HasText && Directory.Exists(dir)
                && Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                    .Any(f => SubtitleFonts.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Make a value safe inside a filter argument. Only the characters FFmpeg's
    /// filter parser treats specially are touched; a family name is plain text.
    /// </summary>
    private static string EscapeFilterValue(string value) => Core.FilterArgs.EscapeValue(value);

    /// <summary>
    /// Make a path safe inside a filter argument (quoted, separators doubled,
    /// drive colon escaped) -- see <see cref="Core.FilterArgs.EscapePath"/>.
    /// </summary>
    public static string EscapeFilterPath(string path) => Core.FilterArgs.EscapePath(path);

    /// <summary>
    /// The extra FFmpeg input carrying the track in copy mode, seeked to the
    /// segment like the audio input is -- a subtitle file holds the whole
    /// timeline, so without this the events would land at the wrong moment.
    ///
    /// No <c>-t</c> here: the encoder already bounds the whole output with the
    /// segment duration, and a second copy of it per input is one more thing to
    /// keep in step. (v7.9.3.1 dropped a duration argument that was never read.)
    /// </summary>
    public static List<string> CopyInputArgs(
        SubtitlePlan plan, string sourcePath, double startSeconds)
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

        CaptureResult p;
        try
        {
            p = ProcessRunner.Run(cmd, Probe.SourceTimeoutSeconds);
        }
        catch
        {
            // A probe that times out (a remote source, a large file) must not
            // leave the staging directory behind: the caller only gets to
            // dispose a plan that was actually returned.
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            throw;
        }
        if (p.ExitCode != 0 || !File.Exists(target) || new FileInfo(target).Length == 0)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            return (null, null);
        }
        return (target, dir);
    }
}
