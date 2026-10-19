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
    /// Deliberately not "everything in every container": MPEG-PS in Matroska and
    /// RealVideo in AVI are the kind of pair that muxes and then fails to play.
    /// </summary>
    public static readonly FrozenDictionary<string, (string Ext, string Format)[]> ContainerChoicesByEncoder =
        new Dictionary<string, (string Ext, string Format)[]>
        {
            ["mpeg1video"] = [("mpg", "mpeg"), ("avi", "avi")],
            ["mpeg2video"] = [("mpg", "mpeg"), ("avi", "avi")],
            ["mpeg4"] = [("avi", "avi"), ("mp4", "mp4")],
            ["libxvid"] = [("avi", "avi"), ("mp4", "mp4")],
            ["libx264"] = [("mp4", "mp4"), ("avi", "avi"), ("mkv", "matroska")],
        }.ToFrozenDictionary();

    /// <summary>
    /// How the source frame is made to fill the output frame.
    /// crop = fill and discard the overflow, fit = keep everything and pad,
    /// stretch = fill and distort.
    /// </summary>
    public static readonly string[] FitModes = ["crop", "fit", "stretch"];

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
            ["libx264"] = 28, ["libtheora"] = 7, ["rv10"] = 8, ["rv20"] = 8,
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
            ["ogg"] = ["libvorbis", "vorbis", "libopus", "opus"],
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

    public static readonly FrozenDictionary<string, double> PixelFormatSize =
        new Dictionary<string, double>
        {
            ["yuv420p"] = 3.0 / 2.0, ["yuv422p"] = 2.0, ["yuv444p"] = 3.0,
            ["yuv411p"] = 3.0 / 2.0, ["yuv410p"] = 9.0 / 8.0, ["yuv440p"] = 3.0 / 2.0,
            ["yuvj420p"] = 3.0 / 2.0, ["yuvj422p"] = 2.0, ["yuvj444p"] = 3.0,
            ["yuvj411p"] = 3.0 / 2.0,
            ["yuv420p10le"] = 3.0, ["yuv422p10le"] = 4.0, ["yuv444p10le"] = 6.0,
            ["yuv420p12le"] = 3.0, ["yuv422p12le"] = 4.0, ["yuv444p12le"] = 6.0,
            ["yuv420p16le"] = 3.0, ["yuv422p16le"] = 4.0, ["yuv444p16le"] = 6.0,
            ["gray"] = 1.0, ["gray10le"] = 2.0, ["gray16le"] = 2.0,
            ["rgb24"] = 3.0, ["bgr24"] = 3.0, ["rgba"] = 4.0, ["bgra"] = 4.0,
            ["argb"] = 4.0, ["abgr"] = 4.0, ["rgb0"] = 4.0, ["bgr0"] = 4.0,
            ["rgb555le"] = 2.0, ["rgb555be"] = 2.0, ["rgb565le"] = 2.0, ["rgb565be"] = 2.0,
            ["rgb48le"] = 6.0, ["rgb48be"] = 6.0,
            ["pal8"] = 1.0, ["rgb8"] = 1.0, ["bgr8"] = 1.0,
        }.ToFrozenDictionary();

    public static string[] ScaleAlgorithms => [.. ScaleAlgorithmAliases.Keys];
}
