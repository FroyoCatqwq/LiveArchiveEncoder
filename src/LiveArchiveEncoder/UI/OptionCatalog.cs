using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// Every command-line option this program accepts, written down once.
///
/// <para>
/// v7.9.8.6: this exists because there used to be two lists -- the parser's
/// <c>switch</c> and a hand-maintained table inside <see cref="Completion"/> -- and
/// they drifted. By v7.9.8.5 the parser took 112 spellings while the generated
/// shell scripts knew 69: <c>--era-aspect</c>, <c>--h264</c>, <c>--pix-fmt</c>,
/// <c>--update</c>, the whole colour block and both NNEDI3 switches completed
/// nothing at all, in every shell. A test walks the parser source and fails if an
/// option is missing here, so the two cannot separate silently again.
/// </para>
///
/// <para>
/// This table is deliberately about <em>completion metadata</em> only. The parser
/// still owns validation and error text; duplicating those rules here would create
/// exactly the second source of truth this file was written to remove.
/// </para>
/// </summary>
public static class OptionCatalog
{
    public enum Kind
    {
        /// <summary>Bare switch: <c>--quiet</c>.</summary>
        Flag,

        /// <summary>Takes a value; <see cref="Spec.Choices"/> may suggest some.</summary>
        Value,

        /// <summary>Takes a path, so the shell should offer files.</summary>
        File,
    }

    /// <summary>
    /// One accepted spelling. <paramref name="Choices"/> empty means "free text"
    /// (numbers, timecodes, codec names): the shell then leaves completion alone
    /// instead of offering an empty list.
    /// </summary>
    public readonly record struct Spec(string Name, Kind OptionKind, string[] Choices);

    /// <summary>Placeholder replaced with the encoder names FFmpeg reports.</summary>
    public const string EncodersMarker = "@ENCODERS";

    private static string[] FfmpegIds() =>
        [.. FFmpeg.FfmpegCatalog.Available.Select(d => d.Id).Distinct()];

    public static readonly IReadOnlyList<Spec> All =
    [
        // ---- reference / one-shot actions ----
        new Spec("-h", Kind.Flag, []),
        new Spec("--help", Kind.Flag, []),
        new Spec("--version", Kind.Flag, []),
        new Spec("--cpu-info", Kind.Flag, []),
        new Spec("--quiet", Kind.Flag, []),
        new Spec("--no-cui", Kind.Flag, []),
        new Spec("--completion", Kind.Value, ["bash", "zsh", "fish", "powershell"]),
        new Spec("--lang", Kind.Value, ["en", "zh"]),

        // ---- input range and pacing ----
        new Spec("--start-time", Kind.Value, []),
        new Spec("--start", Kind.Value, []),
        new Spec("--end-time", Kind.Value, []),
        new Spec("--end", Kind.Value, []),
        new Spec("--limit", Kind.Value, []),
        new Spec("--recover", Kind.Value, []),
        new Spec("--window", Kind.Value, []),
        new Spec("--fps", Kind.Value, []),
        new Spec("--fps-levels", Kind.Value, ["13,8,5,3", "25,15,10,5"]),
        new Spec("--qv", Kind.Value, []),
        new Spec("--threads", Kind.Value, []),
        new Spec("--buffer-frames", Kind.Value, []),
        new Spec("--parallel", Kind.Value, ["1", "2", "4", "8"]),

        // ---- codecs ----
        new Spec("--encoder", Kind.Value, [EncodersMarker]),
        new Spec("--audio-encoder", Kind.Value,
            ["libmp3lame", "aac", "ac3", "mp2", "wmav2", "real_144", "adpcm_ima_amv", "roq_dpcm"]),
        new Spec("--audio-kbps", Kind.Value, []),
        new Spec("--audio-channels", Kind.Value, ["1", "2"]),
        new Spec("--audio-rate", Kind.Value, ["22050", "44100"]),
        new Spec("--audio-mode", Kind.Value, ["inline", "separate", "auto"]),

        // ---- picture ----
        new Spec("--width", Kind.Value, []),
        new Spec("--height", Kind.Value, []),
        new Spec("--container", Kind.Value, ["avi", "mp4", "mkv", "mpg", "mpeg"]),
        new Spec("--fit", Kind.Value, [.. Constants.FitModes]),
        new Spec("--no-crop", Kind.Flag, []),
        new Spec("--crop-anchor", Kind.Value, [.. Constants.CropAnchors]),
        new Spec("--pad-color", Kind.Value, ["black", "white", "gray", "0x808080"]),
        new Spec("--scale-algo", Kind.Value,
            [.. Constants.ScaleAlgorithmAliases.Keys.OrderBy(x => x, StringComparer.Ordinal)]),
        new Spec("--scale-flags", Kind.Value,
            [.. Constants.ScaleAlgorithmAliases.Keys.OrderBy(x => x, StringComparer.Ordinal)]),

        // ---- pixel format and colour (v7.9.6) ----
        new Spec("--pix-fmt", Kind.Value, []),
        new Spec("--pixfmt", Kind.Value, []),
        new Spec("--pixel-format", Kind.Value, []),
        new Spec("--color-range", Kind.Value, ["auto", .. Constants.ColorRanges]),
        new Spec("--colour-range", Kind.Value, ["auto", .. Constants.ColorRanges]),
        new Spec("--colorspace", Kind.Value, ["auto", .. Constants.ColorSpaces]),
        new Spec("--color-space", Kind.Value, ["auto", .. Constants.ColorSpaces]),
        new Spec("--colour-space", Kind.Value, ["auto", .. Constants.ColorSpaces]),
        new Spec("--color-primaries", Kind.Value, ["auto", .. Constants.ColorPrimaries]),
        new Spec("--colour-primaries", Kind.Value, ["auto", .. Constants.ColorPrimaries]),
        new Spec("--color-trc", Kind.Value, ["auto", .. Constants.ColorTransfers]),
        new Spec("--color-transfer", Kind.Value, ["auto", .. Constants.ColorTransfers]),
        new Spec("--colour-trc", Kind.Value, ["auto", .. Constants.ColorTransfers]),
        new Spec("--dither", Kind.Value, [.. Constants.DitherModes]),
        new Spec("--list-pix-fmts", Kind.Flag, []),
        new Spec("--list-pixel-formats", Kind.Flag, []),

        // ---- deinterlacing, hardware decode, x264 knobs ----
        new Spec("--deinterlace", Kind.Value, ["off", .. Constants.DeinterlaceFilters]),
        new Spec("--deint-mode", Kind.Value, ["send_frame", "send_field"]),
        new Spec("--deint-parity", Kind.Value, ["auto", "tff", "bff"]),
        new Spec("--deint-marked-only", Kind.Flag, []),
        new Spec("--hwaccel", Kind.Value,
            ["off", "auto", "cuda", "vaapi", "qsv", "d3d11va", "dxva2", "vdpau", "vulkan"]),
        new Spec("--no-cuda", Kind.Flag, []),
        new Spec("--x264-preset", Kind.Value,
            ["ultrafast", "veryfast", "faster", "fast", "medium", "slow", "slower"]),
        new Spec("--x264-tune", Kind.Value,
            ["film", "animation", "grain", "stillimage", "fastdecode", "zerolatency"]),
        new Spec("--x264-profile", Kind.Value, ["baseline", "main", "high", "high10", "high422", "high444"]),
        new Spec("--x264-level", Kind.Value, ["3.0", "3.1", "4.0", "4.1", "5.0", "5.1"]),

        // ---- stream picks ----
        new Spec("--video-stream", Kind.Value, []),
        new Spec("--audio-stream", Kind.Value, []),
        new Spec("--list-streams", Kind.Flag, []),

        // ---- subtitles (v7.9) ----
        // --subtitle and --subtitle-font take free text (a path, a family name), so
        // the path ones complete as files and the family one as nothing.
        new Spec("--subtitle", Kind.File, []),
        new Spec("--subtitle-stream", Kind.Value, []),
        new Spec("--subtitle-mode", Kind.Value, [.. Constants.SubtitleModes]),
        new Spec("--subtitle-codec", Kind.Value,
            [.. Constants.SubtitleCodecsByContainer.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal)]),
        new Spec("--subtitle-font", Kind.Value, []),
        new Spec("--subtitle-font-dir", Kind.File, []),
        new Spec("--subtitle-aspect", Kind.Value, [.. Constants.SubtitleAspects]),
        new Spec("--font-import", Kind.File, []),
        new Spec("--list-subtitle-fonts", Kind.Flag, []),

        // ---- settings files (v7.9.1) ----
        new Spec("--save-config", Kind.File, []),
        new Spec("--emit-args", Kind.Flag, []),

        // ---- toolchain ----
        new Spec("--ffmpeg", Kind.File, []),
        new Spec("--ffprobe", Kind.File, []),
        new Spec("--ffmpeg-source", Kind.Value, ["path", .. FfmpegIds()]),
        new Spec("--ffmpeg-download", Kind.Value, FfmpegIds()),
        new Spec("--ffmpeg-archive", Kind.File, []),
        new Spec("--ffmpeg-delete", Kind.Value, FfmpegIds()),
        new Spec("--ffmpeg-list", Kind.Flag, []),
        new Spec("--list-filters", Kind.Flag, []),
        new Spec("--proxy", Kind.Value,
            ["http://127.0.0.1:10808", "http://127.0.0.1:7890", "socks5://127.0.0.1:10808"]),
        new Spec("--gh-proxy", Kind.Value, [FFmpeg.GitHubProxy.DefaultPrefix, FFmpeg.GitHubProxy.Off]),
        new Spec("--no-gh-proxy", Kind.Flag, []),
        new Spec("--nnedi-update", Kind.Flag, []),
        new Spec("--nnedi-status", Kind.Flag, []),
        new Spec("--nnedi-weights", Kind.File, []),

        // ---- self-update (v7.9.8.5) ----
        new Spec("--update", Kind.Flag, []),
        new Spec("--update-check", Kind.Flag, []),
        new Spec("--update-feed", Kind.Value, []),
        new Spec("--no-update-check", Kind.Flag, []),

        // ---- networking measurement (v7.9.6.3) ----
        new Spec("--net-test", Kind.Value, []),
        new Spec("--iperf3", Kind.Value, []),

        // ---- playback ----
        new Spec("--play", Kind.Value, ["off", "ffplay", "external"]),
        new Spec("--player", Kind.File, []),

        // ---- post-processing (v7.6 / v7.9.5 / v7.9.8.3) ----
        new Spec("--recompress", Kind.Flag, []),
        new Spec("--no-recompress", Kind.Flag, []),
        new Spec("--recompress-only", Kind.Flag, []),
        new Spec("--recompress-args", Kind.File, []),
        new Spec("--recompress-container", Kind.Value, ["mp4", "mkv", "avi", "flv"]),
        new Spec("--recompress-codec", Kind.Value, []),
        new Spec("--recompress-quality", Kind.Value, []),
        new Spec("--recompress-audio-codec", Kind.Value, []),
        new Spec("--recompress-audio-kbps", Kind.Value, []),

        // ---- era presets (v7.9.5 / v7.9.7) ----
        new Spec("--era", Kind.Value, [.. PostProcess.EraPresets.All.Select(p => p.Id)]),
        new Spec("--era-aspect", Kind.Value, [.. PostProcess.EraAspects.Labels]),
        new Spec("--era-modern", Kind.Flag, []),
        new Spec("--era-native", Kind.Flag, []),
        new Spec("--h264", Kind.Value, ["libx264", "libopenh264"]),
        new Spec("--h264-impl", Kind.Value, ["libx264", "libopenh264"]),
    ];

    /// <summary>Options that take no value.</summary>
    public static IEnumerable<string> Flags =>
        All.Where(o => o.OptionKind == Kind.Flag).Select(o => o.Name);

    /// <summary>Options that take a value, in declaration order.</summary>
    public static IEnumerable<Spec> ValueOptions =>
        All.Where(o => o.OptionKind != Kind.Flag);
}
