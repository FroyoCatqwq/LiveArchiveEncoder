using System.Collections.Frozen;

namespace LiveArchiveEncoder.Configuration;

/// <summary>
/// All tables and magic values ported verbatim from live_archive_encoder.py v6.3.3.
/// These are the migration baseline: changing any value here changes encoder behaviour.
///
/// v7.8.1: every table here is built once and never mutated, so they are frozen
/// (<see cref="FrozenDictionary{TKey,TValue}"/> / <see cref="FrozenSet{T}"/>).
/// Freeing them costs a little start-up time and buys the fastest possible
/// lookups afterwards, which is the right trade for tables read on every frame.
/// </summary>
public static class Constants
{
    public const int DefaultWidth = 256;
    public const int DefaultHeight = 192;
    public const double DefaultFps = 13.0;

    /// <summary>
    /// The ladder a run starts from, highest first. Kept here because three
    /// places need it and they used to disagree: the CLI default
    /// (<c>--fps-levels 13,8,5,3</c>), the menu's "reset the ladder to the base
    /// rate", and an era preset that names a base rate without naming rungs
    /// (v7.9.6.1). The old menu path derived <c>[base, 8, 5, 3]</c> and silently
    /// lost the 13 rung on any base above 13.
    /// </summary>
    public static readonly double[] DefaultFpsLevels = [DefaultFps, 8.0, 5.0, 3.0];

    public static readonly FrozenSet<string> VideoExtensions =
        new HashSet<string>
        {
            ".avi", ".mov", ".mp4", ".mkv", ".webm", ".wmv", ".mpg", ".mpeg",
            ".m4v", ".flv", ".rm", ".rmvb", ".ts", ".m2ts", ".vob", ".ogv", ".asf",
            ".roq", ".amv",
        }.ToFrozenSet();

    public static readonly string[] ModernPrefixes =
    [
        "av1", "libaom", "librav1e", "libsvtav1", "vp8", "vp9", "libvpx",
        "hevc", "libx265", "libkvazaar", "vvc", "libvvenc", "libxavs2",
        "h264_", "av1_", "hevc_",
    ];

    public static readonly FrozenSet<string> NonArchiveExact =
        new HashSet<string>
        {
            "rawvideo", "wrapped_avframe",
            "ffv1", "ffv1_vulkan", "ffvhuff", "huffyuv", "ljpeg", "jpegls",
            "jpeg2000", "libopenjpeg", "dvvideo", "dnxhd",
            "prores", "prores_aw", "prores_ks", "prores_ks_vulkan", "cfhd",
            "dpx", "exr", "fits", "png", "bmp", "gif", "tiff", "qoi",
            "webp", "libwebp", "libwebp_anim", "libjxl", "libjxl_anim",
            "r10k", "r210", "v210", "speedhq", "utvideo", "magicyuv", "vc2",
            "dxv", "sgi", "targa", "ppm", "pgm", "pam", "xbm", "xwd", "xface",
            "sunrast", "pfm", "phm", "zlib",
        }.ToFrozenSet();

    public static readonly FrozenDictionary<string, (string Ext, string Format)> ContainerByEncoder =
        new Dictionary<string, (string Ext, string Format)>
        {
            ["a64multi"] = ("a64", "a64"),
            ["a64multi5"] = ("a64", "a64"),
            ["amv"] = ("amv", "amv"),
            ["wmv1"] = ("wmv", "asf"),
            ["wmv2"] = ("wmv", "asf"),
            ["rv10"] = ("rm", "rm"),
            ["rv20"] = ("rm", "rm"),
            ["mpeg1video"] = ("mpg", "mpeg"),
            ["mpeg2video"] = ("mpg", "mpeg"),
            ["mpeg4"] = ("avi", "avi"),
            ["libxvid"] = ("avi", "avi"),
            ["libtheora"] = ("ogv", "ogg"),
            ["libx264"] = ("mp4", "mp4"),
            // v7.9.7: the 2010s era presets capture and deliver with OpenH264,
            // so it has to be a first-class live encoder, not just a name the
            // second pass knows.
            ["libopenh264"] = ("mp4", "mp4"),
            ["flv"] = ("flv", "flv"),
            ["flashsv"] = ("flv", "flv"),
            ["flashsv2"] = ("flv", "flv"),
            ["h261"] = ("h261", "h261"),
            ["h263"] = ("h263", "h263"),
            ["h263p"] = ("avi", "avi"),
            ["mjpeg"] = ("avi", "avi"),
            ["qtrle"] = ("mov", "mov"),
            ["rpza"] = ("mov", "mov"),
            ["smc"] = ("mov", "mov"),
            ["svq1"] = ("mov", "mov"),
            ["roqvideo"] = ("roq", "roq"),
        }.ToFrozenDictionary();

    /// <summary>
    /// v7.8: every container an encoder may be written to, in preference order.
    /// The first entry is the default, so no encoder changes container unless the
    /// user asks -- mpeg4/libxvid keep AVI, mpeg1/2video keep MPEG-PS, libx264
    /// keeps MP4, but AVI and Matroska become selectable where the combination
    /// actually muxes.
    ///
    /// v7.9: h261 / h263 / h263p join, because the raw .h261/.h263 streams carry
    /// no audio at all while the same codecs in AVI take an mp3 track fine
    /// (verified 2026-10-21 against the BtbN build: all three mux and probe back
    /// with a working audio stream).
    ///
    /// v7.9.5: re-measured against the whole legacy set with
    /// <c>tools/capability_sweep.py</c>, which checks three things per pair --
    /// the muxer accepts it, ffprobe reads the codec back under its own name,
    /// and a decoded frame still has the right colour. That last check matters:
    /// AVI packs an RV20 stream and reads it back as "rawvideo", the rm muxer
    /// writes the RealVideo tag over whatever it was given, and Matroska/ASF
    /// drop the palette of a pal8 codec (msrle: YAVG 123 in AVI, YAVG 16 in
    /// both, measured 2026-10-23). Pairs that only *mux* are deliberately not
    /// offered.
    ///
    /// The gain is Matroska (which holds nearly everything), MP4/MOV for
    /// MPEG-1/2/4, and FLV for libx264. msrle keeps AVI and gains MOV, because
    /// those are the two containers that carry a palette.
    /// </summary>
    public static readonly FrozenDictionary<string, (string Ext, string Format)[]> ContainerChoicesByEncoder =
        new Dictionary<string, (string Ext, string Format)[]>
        {
            ["mpeg1video"] = [("mpg", "mpeg"), ("avi", "avi"), ("mp4", "mp4"), ("mov", "mov"), ("mkv", "matroska")],
            ["mpeg2video"] = [("mpg", "mpeg"), ("avi", "avi"), ("mp4", "mp4"), ("mov", "mov"), ("mkv", "matroska")],
            ["mpeg4"] = [("avi", "avi"), ("mp4", "mp4"), ("mov", "mov"), ("mkv", "matroska")],
            ["libxvid"] = [("avi", "avi"), ("mp4", "mp4"), ("mov", "mov"), ("mkv", "matroska")],
            ["libx264"] = [("mp4", "mp4"), ("avi", "avi"), ("mkv", "matroska"), ("mov", "mov"), ("flv", "flv")],
            ["libopenh264"] = [("mp4", "mp4"), ("mkv", "matroska"), ("mov", "mov"), ("flv", "flv")],
            ["h261"] = [("h261", "h261"), ("avi", "avi"), ("mkv", "matroska")],
            ["h263"] = [("h263", "h263"), ("avi", "avi"), ("mkv", "matroska")],
            ["h263p"] = [("avi", "avi"), ("mkv", "matroska")],
            ["libtheora"] = [("ogv", "ogg"), ("mkv", "matroska"), ("avi", "avi")],
            ["flv"] = [("flv", "flv"), ("avi", "avi"), ("mkv", "matroska")],
            ["mjpeg"] = [("avi", "avi"), ("mkv", "matroska"), ("mov", "mov"), ("mp4", "mp4")],
            ["msrle"] = [("avi", "avi"), ("mov", "mov")],
            ["cinepak"] = [("avi", "avi"), ("mkv", "matroska")],
            ["msmpeg4v2"] = [("avi", "avi"), ("mkv", "matroska")],
            ["msvideo1"] = [("avi", "avi"), ("mkv", "matroska")],
            ["asv1"] = [("avi", "avi"), ("mkv", "matroska")],
            ["asv2"] = [("avi", "avi"), ("mkv", "matroska")],
            ["cljr"] = [("avi", "avi"), ("mkv", "matroska")],
            ["flashsv"] = [("flv", "flv"), ("avi", "avi"), ("mkv", "matroska")],
            ["snow"] = [("avi", "avi"), ("mkv", "matroska")],
            ["zmbv"] = [("avi", "avi"), ("mkv", "matroska")],
        }.ToFrozenDictionary();

    /// <summary>
    /// Containers that store an unknown VfW codec as a BITMAPINFOHEADER with no
    /// palette in it. A pal8-only codec written to one of these decodes to a
    /// black picture -- measured on msrle (2026-10-23): the same clip is YAVG
    /// 123 in AVI and YAVG 16 in both Matroska and ASF.
    ///
    /// AVI and MOV are safe (the palette lives in the stream header). No entry
    /// in <see cref="ContainerChoicesByEncoder"/> pairs a pal8-only encoder with
    /// one of these; <c>PaletteContainerTests</c> keeps it that way.
    /// </summary>
    public static readonly FrozenSet<string> PaletteLossyFormats =
        new HashSet<string> { "matroska", "asf" }.ToFrozenSet();

    /// <summary>
    /// Containers that store no frame rate at all: the file is a bare bitstream,
    /// and a player assumes a default (ffprobe guesses 25 fps for .h261/.h263 and
    /// reports nothing for .a64). Measured 2026-10-23 -- a 15 fps h261 written to
    /// .h261 reads back as 25/1, while the same encode in AVI keeps 15.
    ///
    /// The picture is unaffected, so this is a note rather than an error; the
    /// note names AVI as the container that keeps the rate.
    /// </summary>
    public static readonly FrozenSet<string> RateLessFormats =
        new HashSet<string> { "h261", "h263", "a64" }.ToFrozenSet();

    /// <summary>
    /// How the source frame is made to fill the output frame.
    /// crop = fill and discard the overflow, fit = keep everything and pad,
    /// stretch = fill and distort.
    /// </summary>
    public static readonly string[] FitModes = ["crop", "fit", "stretch"];

    // ---------------------------------------------------------------- v7.9 #2
    // Subtitles. "以 ffmpeg -encoders 为准": every codec named below is one of
    // the S-class encoders a current build actually prints, and the copy path
    // re-checks the choice against the discovered set before using it.

    /// <summary>
    /// Subtitle codec each container carries, in preference order. Null (absent)
    /// means the container has no subtitle stream concept at all -- the archive
    /// formats (rm, flv, asf, roq, amv, ...) are in that group, which is exactly
    /// why burning the text into the picture is the default.
    /// </summary>
    public static readonly FrozenDictionary<string, string[]> SubtitleCodecsByContainer =
        new Dictionary<string, string[]>
        {
            ["matroska"] = ["ass", "ssa", "srt", "subrip", "webvtt", "text"],
            ["mp4"] = ["mov_text"],
            ["avi"] = ["xsub"],
            ["mpeg"] = ["dvdsub"],
            ["mpegts"] = ["dvbsub"],
            ["webm"] = ["webvtt"],
        }.ToFrozenDictionary();

    /// <summary>
    /// Bitmap subtitle formats. libass renders <em>text</em>; a DVD/DVB
    /// subpicture is a picture, and turning it into text needs OCR, which FFmpeg
    /// does not do. So these can be carried (copy) but not burned.
    /// </summary>
    public static readonly FrozenSet<string> BitmapSubtitleCodecs =
        new[] { "dvdsub", "dvbsub", "dvb_subtitle", "dvd_subtitle", "xsub" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Sidecar subtitle files this tool recognises, e.g. clip.sc.ass.</summary>
    public static readonly string[] SubtitleFileExtensions = [".ass", ".ssa", ".srt", ".vtt"];

    /// <summary>How a subtitle track reaches the output.</summary>
    public static readonly string[] SubtitleModes = ["burn", "copy", "off"];

    /// <summary>Which part survives when <c>crop</c> has to discard an edge.</summary>
    public static readonly string[] CropAnchors = ["center", "top", "bottom", "left", "right"];

    /// <summary>
    /// v7.8: preset frame sizes, grouped by aspect ratio. Handy because a 16:9
    /// master stored as 4:3 (or the reverse) needs the right pair of numbers --
    /// 320x180 rather than 320x240 -- before any padding or cropping is applied.
    /// </summary>
    public static readonly (int W, int H, string Label)[] ResolutionPresets =
    [
        (160, 120, "4:3"), (256, 192, "4:3"), (320, 240, "4:3"),
        (384, 288, "4:3"), (512, 384, "4:3"), (640, 480, "4:3"),
        (160, 90, "16:9"), (256, 144, "16:9"), (320, 180, "16:9"), (384, 216, "16:9"),
        (512, 288, "16:9"), (640, 360, "16:9"), (854, 480, "16:9"), (1280, 720, "16:9"),
    ];

    public static readonly FrozenDictionary<string, int> DefaultQuality =
        new Dictionary<string, int>
        {
            ["libx264"] = 28, ["libopenh264"] = 26, ["libtheora"] = 7, ["rv10"] = 8, ["rv20"] = 8,
            ["wmv1"] = 10, ["wmv2"] = 10, ["mpeg1video"] = 8, ["mpeg2video"] = 8,
            ["mpeg4"] = 8, ["libxvid"] = 8, ["cinepak"] = 10, ["cljr"] = 10,
            ["h261"] = 10, ["h263"] = 10, ["h263p"] = 10, ["mjpeg"] = 6,
            ["flv"] = 10, ["flashsv"] = 10, ["flashsv2"] = 10, ["qtrle"] = 10,
            ["rpza"] = 10, ["smc"] = 10, ["svq1"] = 10, ["roqvideo"] = 10,
            ["zmbv"] = 10, ["amv"] = 10, ["asv1"] = 10, ["asv2"] = 10,
            ["a64multi"] = 10, ["a64multi5"] = 10, ["msmpeg4v2"] = 10,
            ["msmpeg4"] = 10, ["msvideo1"] = 10, ["msrle"] = 10, ["snow"] = 8,
        }.ToFrozenDictionary();

    public static readonly FrozenDictionary<string, string[]> DefaultAudioByContainer =
        new Dictionary<string, string[]>
        {
            ["asf"] = ["wmav2", "wmav1", "libmp3lame"],
            ["mpeg"] = ["mp2", "mp3_mf", "libmp3lame"],
            ["avi"] = ["libmp3lame", "mp3_mf", "mp2", "aac"],
            // v7.8.2: the native vorbis/opus encoders are experimental in
            // FFmpeg and need -strict, which this tool never passes, so the
            // lib-prefixed pair is what the preference list offers.
            ["ogg"] = ["libvorbis", "libopus"],
            ["rm"] = ["ac3", "aac"],
            ["mp4"] = ["aac", "libopus", "libmp3lame"],
            ["flv"] = ["aac", "libmp3lame", "mp3_mf"],
            ["mov"] = ["aac", "libmp3lame"],
            ["amv"] = ["adpcm_ima_amv", "adpcm_ima_wav"],
            ["roq"] = ["roq_dpcm"],
        }.ToFrozenDictionary();

    public static readonly FrozenSet<string> NoAudioFormats =
        new HashSet<string> { "a64", "h261", "h263" }.ToFrozenSet();

    /// <summary>
    /// Muxers whose shards can be rejoined with <c>-f concat -c copy</c> without
    /// losing data. Measured on two adjacent 5s shards (2026-10-17):
    ///
    ///   avi / mp4 / flv / asf / mpeg -- frame count preserved, duration within
    ///   0.03s of the sum, one of them byte-identical in size.
    ///   rm                           -- duration drifts (+0.031s over 10s) and
    ///                                  the container is rewritten, not copied.
    ///   roq                          -- catastrophic: the second shard is
    ///                                  dropped entirely (250 frames -> 125,
    ///                                  990 KB -> 222 KB). RoQ has a single file
    ///                                  header that concat reuses.
    ///
    /// Only formats actually verified are listed; untested ones stay out so the
    /// parallel path never silently damages an archive.
    ///
    /// v7.9.2 note: the list still gates *which user-facing containers may be
    /// sharded*, but shards are no longer written in the target container --
    /// they are staged in Matroska and remuxed after the join (see
    /// ParallelEncoder). The two-shard flv result above stopped holding at five
    /// shards on a long rawvideo source, which is exactly the kind of thing a
    /// 5-second sample cannot show.
    /// </summary>
    public static readonly FrozenSet<string> ConcatSafeFormats =
        new HashSet<string> { "avi", "mp4", "flv", "asf", "mpeg" }.ToFrozenSet();

    /// <summary>
    /// Shortest shard worth the two extra FFmpeg process launches. Below this the
    /// spawn cost dominates and sharding would be slower than a single pass.
    /// </summary>
    public const double MinShardSeconds = 4.0;

    /// <summary>
    /// Upper bound for <c>--parallel</c>. Beyond this the FFmpeg process launches
    /// cost more than the encoding they overlap, and the concat step gets slower.
    /// </summary>
    public const int MaxParallelShards = 8;

    public static readonly FrozenDictionary<string, IReadOnlySet<string>> FormatExtensions =
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["a64"] = new HashSet<string> { ".a64" }.ToFrozenSet(),
            ["amv"] = new HashSet<string> { ".amv" }.ToFrozenSet(),
            ["asf"] = new HashSet<string> { ".asf", ".wmv" }.ToFrozenSet(),
            ["avi"] = new HashSet<string> { ".avi" }.ToFrozenSet(),
            ["flv"] = new HashSet<string> { ".flv" }.ToFrozenSet(),
            ["h261"] = new HashSet<string> { ".h261" }.ToFrozenSet(),
            ["h263"] = new HashSet<string> { ".h263" }.ToFrozenSet(),
            ["mpeg"] = new HashSet<string> { ".mpg", ".mpeg", ".mpe", ".vob" }.ToFrozenSet(),
            ["mp4"] = new HashSet<string> { ".mp4", ".m4v" }.ToFrozenSet(),
            ["matroska"] = new HashSet<string> { ".mkv" }.ToFrozenSet(),
            ["mov"] = new HashSet<string> { ".mov", ".qt" }.ToFrozenSet(),
            ["ogg"] = new HashSet<string> { ".ogg", ".ogv" }.ToFrozenSet(),
            ["rm"] = new HashSet<string> { ".rm", ".rmvb" }.ToFrozenSet(),
            ["roq"] = new HashSet<string> { ".roq" }.ToFrozenSet(),
        }.ToFrozenDictionary();

    public static readonly double[] AmvFps = [10.0, 14.0, 15.0, 18.0, 21.0, 25.0, 30.0];

    /// <summary>adpcm_ima_amv only accepts 22050 Hz mono s16.</summary>
    public const int AmvAudioSampleRate = 22050;

    /// <summary>
    /// AMV is far stricter than every other legacy target. Each rule below was
    /// reproduced against this FFmpeg build (2026-10-17):
    ///
    ///   * the container accepts <b>exactly two</b> streams, so a video-only AMV
    ///     is impossible -- FFmpeg aborts with "AMV files only support 2 streams";
    ///   * the audio track must be adpcm_ima_amv at 22050 Hz mono and its block
    ///     size must equal 22050 / fps, or the muxer rejects the header with
    ///     "Audio sample rate not a multiple of the frame size";
    ///   * width and height should be multiples of 16 -- FFmpeg only warns and
    ///     then emits a file most hardware decoders refuse.
    ///
    /// v6.3.3 got none of this right, so `--encoder amv` produced a zero-byte
    /// file. This is a deliberate, documented deviation from the Python baseline.
    /// </summary>
    public const int AmvDimensionMultiple = 16;

    /// <summary>Containers that cannot be written without an audio stream.</summary>
    public static readonly FrozenSet<string> FormatsRequiringAudio =
        new HashSet<string> { "amv" }.ToFrozenSet();

    public static readonly double[] Mpeg1RealFps =
        [24000.0 / 1001.0, 24.0, 25.0, 30000.0 / 1001.0, 30.0, 50.0, 60000.0 / 1001.0, 60.0];

    public static readonly FrozenDictionary<string, (int W, int H)[]> SpecialSizes =
        new Dictionary<string, (int W, int H)[]>
        {
            ["h261"] = [(176, 144), (352, 288)],
            ["h263"] = [(128, 96), (176, 144), (352, 288), (704, 576), (1408, 1152)],
        }.ToFrozenDictionary();

    public static readonly FrozenDictionary<string, string> ScaleAlgorithmAliases =
        new Dictionary<string, string>
        {
            ["neighbor"] = "neighbor", ["nearest"] = "neighbor", ["point"] = "neighbor",
            ["bilinear"] = "bilinear", ["bicubic"] = "bicubic", ["cubic"] = "bicubic",
            ["lanczos"] = "lanczos", ["spline"] = "spline", ["sinc"] = "sinc",
            ["area"] = "area", ["gauss"] = "gauss",
        }.ToFrozenDictionary();

    public static readonly string[] DeinterlaceFilters = ["bwdif", "yadif", "w3fdif"];

    // ------------------------------------------------------------- v7.9.6 #1
    // Colour handling. The pixel-format table itself lives in
    // Core/PixelFormatCatalog.cs: it is measured data with per-format metadata,
    // not a list of magic numbers, and keeping it in two places is how the old
    // yuv440p entry stayed wrong for three releases.

    /// <summary>
    /// Accepted <c>-color_range</c> values. "unspecified" is included because a
    /// source that declares nothing is not an error -- it means "assume the
    /// codec default".
    /// </summary>
    public static readonly string[] ColorRanges = ["tv", "pc", "mpeg", "jpeg", "unspecified"];

    /// <summary>
    /// Accepted <c>-colorspace</c> values. These are FFmpeg's own names, not the
    /// ITU numbers, because a name in the log is searchable and 6 is not.
    /// </summary>
    public static readonly string[] ColorSpaces =
    [
        "bt709", "fcc", "bt470bg", "smpte170m", "smpte240m", "ycgco",
        "bt2020nc", "bt2020c", "smpte2085", "chroma-derived-nc",
        "chroma-derived-c", "ictcp",
    ];

    /// <summary>Accepted <c>-color_primaries</c> values.</summary>
    public static readonly string[] ColorPrimaries =
    [
        "bt709", "bt470m", "bt470bg", "smpte170m", "smpte240m", "film",
        "bt2020", "smpte428", "smpte431", "smpte432", "jedec-p22", "ebu3213",
    ];

    /// <summary>
    /// Accepted <c>-color_trc</c> values. <c>iec61966-2-1</c> is sRGB and
    /// <c>smpte2084</c> is PQ; both keep their FFmpeg spelling so a copy-pasted
    /// command line keeps working.
    /// </summary>
    public static readonly string[] ColorTransfers =
    [
        "bt709", "gamma22", "gamma28", "smpte170m", "smpte240m", "linear",
        "log", "log_sqrt", "iec61966-2-4", "bt1361", "iec61966-2-1",
        "bt2020-10", "bt2020-12", "smpte2084", "smpte428", "arib-std-b67",
    ];

    /// <summary>
    /// Swscale dither modes, used when the picture is reduced to fewer colours
    /// (pal8, rgb565, 8-bit grey). "auto" is swscale's own choice and emits no
    /// flag at all; the rest are passed through <c>-sws_dither</c>.
    ///
    /// <para>This exists because pal8 output without dithering is not "8-bit
    /// colour": it is the same 256 colours snapped from a 24-bit source with a
    /// hard threshold, which bands gradients into visible steps. bayer is the
    /// right default for an archival look, error_diffusion the best quality.</para>
    /// </summary>
    public static readonly string[] DitherModes =
    [
        "auto", "none", "bayer", "ordered", "random", "error_diffusion",
    ];

    /// <summary>Bayer is the default: cheap, stable, no temporal noise.</summary>
    public const string DefaultDither = "bayer";


    public static string[] ScaleAlgorithms => [.. ScaleAlgorithmAliases.Keys];
}
