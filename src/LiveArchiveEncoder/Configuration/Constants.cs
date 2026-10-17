namespace LiveArchiveEncoder.Configuration;

/// <summary>
/// All tables and magic values ported verbatim from live_archive_encoder.py v6.3.3.
/// These are the migration baseline: changing any value here changes encoder behaviour.
/// </summary>
public static class Constants
{
    public const int DefaultWidth = 256;
    public const int DefaultHeight = 192;
    public const double DefaultFps = 13.0;
    public const string DefaultFpsLevels = "13,8,5,3";

    public const string BtbnGplReleaseUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/"
        + "ffmpeg-master-latest-win64-gpl.zip";

    public static readonly HashSet<string> VideoExtensions =
        [".avi", ".mov", ".mp4", ".mkv", ".webm", ".wmv", ".mpg", ".mpeg",
         ".m4v", ".flv", ".rm", ".rmvb", ".ts", ".m2ts", ".vob", ".ogv", ".asf",
         ".roq", ".amv"];

    public static readonly string[] LegacyEncoderPatterns =
    [
        @"^a64multi(?:5)?$", @"^amv$", @"^asv[12]$", @"^cinepak$", @"^cljr$",
        @"^flashsv2?$", @"^flv$", @"^h261$", @"^h263(?:p)?$", @"^mjpeg$",
        @"^msmpeg4(?:v2)?$", @"^msrle$", @"^msvideo1$", @"^mpeg[124]video$",
        @"^mpeg4$", @"^qtrle$", @"^roqvideo$", @"^rpza$", @"^rv(?:10|20)$",
        @"^smc$", @"^snow$", @"^svq1$", @"^wmv[12]$", @"^zmbv$", @"^libtheora$",
        @"^libx264$", @"^libxvid$", @"^vivo$",
    ];

    public static readonly string[] ModernPrefixes =
    [
        "av1", "libaom", "librav1e", "libsvtav1", "vp8", "vp9", "libvpx",
        "hevc", "libx265", "libkvazaar", "vvc", "libvvenc", "libxavs2",
        "h264_", "av1_", "hevc_",
    ];

    public static readonly HashSet<string> NonArchiveExact =
    [
        "rawvideo", "wrapped_avframe",
        "ffv1", "ffv1_vulkan", "ffvhuff", "huffyuv", "ljpeg", "jpegls",
        "jpeg2000", "libopenjpeg", "dvvideo", "dnxhd",
        "prores", "prores_aw", "prores_ks", "prores_ks_vulkan", "cfhd",
        "dpx", "exr", "fits", "png", "bmp", "gif", "tiff", "qoi",
        "webp", "libwebp", "libwebp_anim", "libjxl", "libjxl_anim",
        "r10k", "r210", "v210", "speedhq", "utvideo", "magicyuv", "vc2",
        "dxv", "sgi", "targa", "ppm", "pgm", "pam", "xbm", "xwd", "xface",
        "sunrast", "pfm", "phm", "zlib",
    ];

    public static readonly Dictionary<string, (string Ext, string Format)> ContainerByEncoder = new()
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
    };

    public static readonly Dictionary<string, int> DefaultQuality = new()
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
    };

    public static readonly Dictionary<string, string[]> DefaultAudioByContainer = new()
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
    };

    public static readonly HashSet<string> NoAudioFormats = ["a64", "h261", "h263"];

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
    public static readonly HashSet<string> ConcatSafeFormats =
        ["avi", "mp4", "flv", "asf", "mpeg"];

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

    public static readonly Dictionary<string, HashSet<string>> FormatExtensions = new()
    {
        ["a64"] = [".a64"],
        ["amv"] = [".amv"],
        ["asf"] = [".asf", ".wmv"],
        ["avi"] = [".avi"],
        ["flv"] = [".flv"],
        ["h261"] = [".h261"],
        ["h263"] = [".h263"],
        ["mpeg"] = [".mpg", ".mpeg", ".mpe", ".vob"],
        ["mp4"] = [".mp4", ".m4v"],
        ["mov"] = [".mov", ".qt"],
        ["ogg"] = [".ogg", ".ogv"],
        ["rm"] = [".rm", ".rmvb"],
        ["roq"] = [".roq"],
    };

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
    public static readonly HashSet<string> FormatsRequiringAudio = ["amv"];

    public static readonly double[] Mpeg1RealFps =
        [24000.0 / 1001.0, 24.0, 25.0, 30000.0 / 1001.0, 30.0, 50.0, 60000.0 / 1001.0, 60.0];

    public static readonly Dictionary<string, (int W, int H)[]> SpecialSizes = new()
    {
        ["h261"] = [(176, 144), (352, 288)],
        ["h263"] = [(128, 96), (176, 144), (352, 288), (704, 576), (1408, 1152)],
    };

    public static readonly Dictionary<string, string> ScaleAlgorithmAliases = new()
    {
        ["neighbor"] = "neighbor", ["nearest"] = "neighbor", ["point"] = "neighbor",
        ["bilinear"] = "bilinear", ["bicubic"] = "bicubic", ["cubic"] = "bicubic",
        ["lanczos"] = "lanczos", ["spline"] = "spline", ["sinc"] = "sinc",
        ["area"] = "area", ["gauss"] = "gauss",
    };

    public static readonly string[] DeinterlaceFilters = ["bwdif", "yadif", "w3fdif"];

    public static readonly Dictionary<string, double> PixelFormatSize = new()
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
    };

    public static string[] ScaleAlgorithms => [.. ScaleAlgorithmAliases.Keys];
}
