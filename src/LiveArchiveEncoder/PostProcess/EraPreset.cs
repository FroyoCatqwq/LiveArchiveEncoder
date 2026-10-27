using System.Globalization;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>Which half of the pipeline a preset speaks for.</summary>
public enum EraKind
{
    /// <summary>
    /// The general state of the web in one year, not one company: what a
    /// recording of that year looked like when it reached a viewer. Ids live
    /// under the <c>generic</c> site.
    /// </summary>
    Era = 0,

    /// <summary>
    /// One site in one year: the bitrate profile that site served -- frame
    /// size, rate, video and audio kbps -- plus the codec it served, which is
    /// the encoder the preset names.
    /// </summary>
    Site = 1,
}

/// <summary>
/// One "site + year" row: what the live capture would have produced, and what
/// the hosting site would then have done to it.
///
/// <para>v7.9.7 rewrote the table as <b>site -&gt; year</b>. Before it there were
/// two flat lists -- five <c>dialup98</c>-style eras and a pile of
/// <c>cctv03</c>-style sites -- where the trailing digits were the only clue to
/// the year and a site with three distinct periods was impossible to express.
/// Now the id is <c>site/year</c> (<c>bilibili/2012</c>), a site owns a row per
/// year it changed shape, and the menu lists sites first, then their years.</para>
///
/// Two layers, because the pipeline has two stages and they model different
/// things:
/// <list type="bullet">
/// <item><description>the <b>live</b> layer -- the encoder, frame size, frame
/// rate and bitrate ceiling of the archival pass;</description></item>
/// <item><description>the <b>second</b> layer -- the damage the site applied
/// on upload, which is what people actually remember about the era.</description></item>
/// </list>
///
/// A preset can also be written by the user (see <c>CustomEra</c>): those live
/// in the settings file next to the built-in table, override a built-in id when
/// they share it, and never need a rebuild to change.
///
/// The numbers are not invented; see <c>docs/era-presets.md</c> for the archived
/// page each row was read off.
/// </summary>
public sealed class EraPreset
{
    /// <summary>Canonical id, <c>site/year</c> for built-ins.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The site this row belongs to -- <c>cctv</c>, <c>bilibili</c>, or
    /// <c>generic</c> for a row that describes the web in general rather than
    /// one company. Used to group the menu.
    /// </summary>
    public string Site { get; init; } = "";

    /// <summary>The year the row describes. 0 for a hand-written preset that only has a range.</summary>
    public int Year { get; init; }

    /// <summary>Era (the web in general) or site (one company).</summary>
    public EraKind Kind { get; init; } = EraKind.Era;

    /// <summary>False for a preset the user wrote; only those can be deleted.</summary>
    public bool BuiltIn { get; init; } = true;

    /// <summary>First year the preset stands for, used only for listing order.</summary>
    public int YearFrom { get; init; }
    public int YearTo { get; init; }

    // ---- live (first pass) ----

    /// <summary>Legacy encoder name as FFmpeg spells it. Null on a bare site preset.</summary>
    public string? Encoder { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }

    /// <summary>
    /// The adaptive frame-rate ladder this period actually ran, highest first.
    ///
    /// <para>Null means "derive one from <see cref="Fps"/>". An explicit
    /// <c>--fps</c> or <c>--fps-levels</c> still wins over all of it: a preset
    /// is a fallback, not an override.</para>
    /// </summary>
    public double[]? FpsLevels { get; init; }

    /// <summary>Bitrate ceiling; the adaptive ladder starts dropping fps above it.</summary>
    public double? LimitKbps { get; init; }

    /// <summary>
    /// The live pass's quality in the MPEG quantiser scale (2..31), for the
    /// legacy codecs a row names: rv10/20, wmv2, flv, mpeg4.
    ///
    /// <para>v7.9.8.2. Until this field existed a preset never touched the
    /// quality at all, so the live pass ran with whatever the previous encoder
    /// left in the settings -- and when the row (or the user) picked H.264, that
    /// leftover MPEG number became a CRF: <c>q:v 9</c> is 375 kbps at
    /// 352x264@15, <c>crf 9</c> is 935 kbps for the same frame, so the 2007 row
    /// (320 kbps) saturated its ladder on the first second and reported a 15 fps
    /// capture that actually ran at the last rung.</para>
    ///
    /// <para>The values are read off the era's own budget, not off a formula:
    /// each one is the coarsest quantiser whose encode still lands inside
    /// <see cref="LimitKbps"/> on the reference clip (see
    /// <c>docs/era-presets.md</c> for the measurement column). q:v moves in
    /// whole steps and the picture barely changes between adjacent ones, so
    /// being one step generous costs little; being four steps generous is what
    /// the ladder notices.</para>
    /// </summary>
    public int LiveQv { get; init; } = 10;

    /// <summary>
    /// The same picture on x264's scale, for the rows whose period's codec is
    /// H.264 and for any row the user re-encodes with libx264.
    ///
    /// <para>Not <c>LiveQv</c> converted arithmetically: an equal-<em>bitrate</em>
    /// pairing was measured for every geometry in this table, and the round
    /// numbers that came back (crf 28 for 45 kbps, 22 for 300-320 kbps, 19 for
    /// 900 kbps, 18 for 2.5 Mbps) are what is stored here -- each one lands at
    /// 0.76..1.04x of the row's ceiling on the reference clip, where a converted
    /// number was off by up to 0.4x either way.</para>
    /// </summary>
    public int LiveCrf { get; init; } = 21;

    /// <summary>
    /// Seconds the ladder averages the output bitrate over, v7.9.8.2.
    ///
    /// <para>The ladder itself is a measurement, so its window is part of what
    /// the era means: RealServer and Windows Media Services adapted a stream
    /// only after several seconds of sustained trouble, while a 2010s CDN
    /// stepped within one or two. Three seconds on the pre-2006 rows, the tool's
    /// own two from 2006.</para>
    /// </summary>
    public double WindowSeconds { get; init; } = 2.0;

    /// <summary>
    /// Decoded frames the encoder may queue, v7.9.8.2. A 160x120 capture in
    /// 1998 had no reason to hold eight frames of anything; a 2014 720p row
    /// does, because the frames are 20x the size and the encoder is the slow
    /// half of the pair.
    /// </summary>
    public int BufferFrames { get; init; } = 6;

    /// <summary>
    /// The deinterlacer of the period, v7.9.8.2. <c>yadif</c> arrived in 2006
    /// (MPlayer, then FFmpeg); the rows before it therefore say <c>off</c> --
    /// a 2003 WMV capture really did carry its field structure into the file,
    /// and that is what the artefact is supposed to look like.
    /// </summary>
    public string Deinterlace { get; init; } = "off";

    public string DeintMode { get; init; } = "send_frame";
    public string DeintParity { get; init; } = "auto";

    /// <summary>
    /// Only act on frames the source marks as interlaced (v7.9.8.2). True for
    /// every era row: a period preset says how a *broadcast* of that year was
    /// processed, and running yadif over a progressive file would soften it for
    /// no reason -- the flag is what makes the two cases distinguishable.
    /// </summary>
    public bool DeintMarkedOnly { get; init; } = true;

    /// <summary>Scaler kernel, deliberately era-appropriate.</summary>
    public string? ScaleAlgo { get; init; }

    public string? AudioEncoder { get; init; }
    public int? AudioKbps { get; init; }
    public int? AudioChannels { get; init; }
    public int? AudioRate { get; init; }

    // ---- second pass ----

    /// <summary>Frame size / rate and bitrates the site served.</summary>
    public int RcWidth { get; init; } = 320;
    public int RcHeight { get; init; } = 240;
    public double RcFps { get; init; } = 15;
    public int RcVideoKbps { get; init; } = 300;

    /// <summary>Quality for the modern (CRF) rendering of the same profile.</summary>
    public int RcCrf { get; init; } = 28;

    /// <summary>libx264 profile for the modern rendering, e.g. baseline / main / high.</summary>
    public string RcProfile { get; init; } = "baseline";

    public int RcAudioKbps { get; init; } = 32;
    public int RcAudioRate { get; init; } = 22050;
    public int RcAudioChannels { get; init; } = 1;

    /// <summary>
    /// The codec the site itself served. Native rendering uses it; the modern
    /// rendering uses libx264 + aac instead, so the file opens in any current
    /// player.
    /// </summary>
    public string VideoCodec { get; init; } = "libx264";
    public string AudioCodec { get; init; } = "aac";

    /// <summary>Container for the native rendering; the modern one is always mp4.</summary>
    public string RecompressContainer { get; init; } = "mp4";

    /// <summary>
    /// v7.9.7: which H.264 encoder this row means. "libopenh264" for the 2011+
    /// rows, null (i.e. libx264) before that -- and null for any row whose codec
    /// is not H.264 at all. <c>--h264</c> overrides it.
    /// </summary>
    public string? H264Impl { get; init; }

    /// <summary>Localisation key for the one-line description shown in menus.</summary>
    public string? NoteKey { get; init; }

    /// <summary>Free-text description of a user preset (no localisation).</summary>
    public string? NoteText { get; init; }

    /// <summary>True when this preset has a live layer to apply.</summary>
    public bool HasLive => Width is not null && Height is not null;

    /// <summary>True when the preset names a live encoder (site presets do).</summary>
    public bool HasEncoder => Encoder is { Length: > 0 };

    /// <summary>The year to print: the anchor year when known, else the range.</summary>
    public string YearLabel => Year > 0 ? Year.ToString(CultureInfo.InvariantCulture)
        : YearFrom == YearTo ? YearFrom.ToString(CultureInfo.InvariantCulture)
        : $"{YearFrom}-{YearTo}";

    /// <summary>
    /// Apply the live layer of this preset. The second pass is a separate stage
    /// with its own on/off switch, so picking an era does not silently turn it
    /// on.
    ///
    /// Returns false when the preset has no frame size (a bare site preset).
    /// </summary>
    public bool ApplyLive(Configuration.Config c, (int W, int H)? size = null)
    {
        if (!HasLive) return false;

        (int width, int height) = size ?? (Width!.Value, Height!.Value);
        c.Width = width;
        c.Height = height;
        if (Fps is { } fps) c.Fps = fps;
        if (FpsLevels is { Length: > 1 } levels)
        {
            c.FpsLevels = EncoderMath.SelectLevels(c.Fps, levels);
            c.FpsLevelKbps = EncoderMath.PruneRungs(c.FpsLevelKbps, c.FpsLevels);
        }
        else if (Fps is not null)
        {
            c.FpsLevels = EncoderMath.SelectLevels(c.Fps, Configuration.Constants.DefaultFpsLevels);
            c.FpsLevelKbps = EncoderMath.PruneRungs(c.FpsLevelKbps, c.FpsLevels);
        }
        if (LimitKbps is { } limit)
        {
            c.Limit = limit;
            c.Recover = limit * 0.75;
        }
        c.Window = WindowSeconds;
        c.BufferFrames = BufferFrames;
        if (Deinterlace is { Length: > 0 } deint)
        {
            c.Deinterlace = deint;
            c.DeintMode = DeintMode;
            c.DeintParity = DeintParity;
            c.DeintMarkedOnly = DeintMarkedOnly;
        }
        if (ScaleAlgo is { Length: > 0 } scale) c.ScaleAlgo = scale;
        if (AudioKbps is { } kbps) c.AudioKbps = kbps;
        if (AudioChannels is { } channels) c.AudioChannels = channels;
        c.AudioRate = AudioRate;
        if (AudioEncoder is not null) c.AudioEncoder = AudioEncoder;
        // v7.9.8.2: the quality belongs to the encoder, so it is written here
        // rather than in the settings it inherits -- and it is written for
        // whichever encoder is in effect, not for the one this row names. The
        // caller applies the encoder first; a preset with no live layer (a bare
        // site row) leaves the user's number alone.
        c.Qv = QualityFor(c.Encoder?.Name);
        return true;
    }

    /// <summary>
    /// This row's live quality, in the scale <paramref name="encoderName"/>
    /// reads. OpenH264 gets the CRF number: it cannot use it, but it is the same
    /// picture the CRF-capable H.264 encoder of that row would produce, and the
    /// ceiling is what actually pins the bitrate.
    /// </summary>
    public int QualityFor(string? encoderName) =>
        EncoderMath.ScaleOf(encoderName) == EncoderMath.QualityScale.Qv ? LiveQv : LiveCrf;

    /// <summary>
    /// The quality pair a ceiling implies, for a row that does not state one --
    /// a custom preset written before v7.9.8.2, which has no live quality in the
    /// file. Interpolated from the built-in table, so a hand-written 500 kbps
    /// row lands where the built-in rows of that rate land.
    /// </summary>
    public static (int Qv, int Crf) QualityBand(double? kbps) => kbps switch
    {
        null => (11, 22),
        <= 60 => (12, 28),
        <= 130 => (11, 23),
        <= 450 => (11, 22),
        <= 850 => (10, 21),
        <= 1000 => (9, 19),
        <= 1600 => (9, 23),
        <= 2000 => (9, 21),
        _ => (8, 18),
    };

    /// <summary>
    /// Point the live pass at this preset's encoder, if it names one. The
    /// container follows the encoder's default, which is also what the output
    /// extension is rebuilt from.
    /// </summary>
    public bool ApplyEncoder(Configuration.Config c, IReadOnlyList<Configuration.EncoderInfo> encoders)
    {
        if (!HasEncoder) return false;
        Configuration.EncoderInfo? match = encoders.FirstOrDefault(
            e => string.Equals(e.Name, Encoder, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        c.Encoder = match;
        c.Container = null;
        return true;
    }

    /// <summary>
    /// The H.264 encoder this row should use: the caller's explicit choice
    /// first, then the row's own, then libx264. Only meaningful when the row's
    /// codec is H.264.
    /// </summary>
    public string EffectiveH264(string? explicitImpl) =>
        explicitImpl is { Length: > 0 } ? explicitImpl : (H264Impl ?? "libx264");

    /// <summary>
    /// The middle of the FFmpeg command for the second pass.
    ///
    /// <paramref name="native"/> true renders with the site's own codec (the
    /// artefacts people remember); false renders the same resolution/bitrate
    /// with libx264 in MP4, which is the "viewing copy".
    /// </summary>
    public string Fragment(bool native, (int W, int H)? size = null, string? h264Impl = null)
    {
        (int scaleW, int scaleH) = size ?? (RcWidth, RcHeight);
        string h264 = EffectiveH264(h264Impl);
        string video = native ? NativeVideoArgs(h264) : ModernVideoArgs(h264);

        var parts = new List<string>
        {
            $"-vf scale={scaleW}:{scaleH}:flags=bicubic,fps={RcFps.ToString("G6", CultureInfo.InvariantCulture)}",
            video,
        };

        // A site targeted a bitrate, so the native rendering caps it. The modern
        // rendering is CRF-based and needs no cap -- unless the H.264 encoder is
        // OpenH264, which has no constant-quality mode and is already pinned.
        if (native && Kind == EraKind.Site && video.Contains("-b:v", StringComparison.Ordinal) is false)
            parts.Add($"-maxrate {RcVideoKbps * 12 / 10}k -bufsize {RcVideoKbps * 2}k");

        parts.Add(native
            ? RecompressCodecs.AudioArgs(AudioCodec, RcAudioKbps, RcAudioRate, RcAudioChannels)
            : RecompressCodecs.AudioArgs("aac", RcAudioKbps, RcAudioRate, RcAudioChannels));
        return string.Join(' ', parts);
    }

    private string NativeVideoArgs(string h264)
    {
        // The row's own codec, except that "libx264" is a placeholder for "the
        // H.264 encoder of this period" and resolves to whatever was picked.
        string codecName = string.Equals(VideoCodec, "libx264", StringComparison.OrdinalIgnoreCase)
            ? h264 : VideoCodec;
        // A site's ladder is always a bitrate, never a quality number: that is
        // true for the 2003 WMV rows, for FLV, and for OpenH264 (which has no
        // CRF at all), so one path covers every codec the table names.
        SecondPassVideoCodec info = RecompressCodecs.FindVideo(codecName)
            ?? RecompressCodecs.FindVideo("libx264")!;
        return RecompressCodecs.VideoArgsAtBitrate(info, RcVideoKbps);
    }

    private string ModernVideoArgs(string h264)
    {
        // A viewing copy is H.264 in MP4 either way; the difference is that
        // OpenH264 cannot do CRF, so it gets the site's bitrate instead.
        if (string.Equals(h264, "libopenh264", StringComparison.OrdinalIgnoreCase))
        {
            SecondPassVideoCodec info = RecompressCodecs.FindVideo("libopenh264")!;
            return RecompressCodecs.VideoArgsAtBitrate(info, RcVideoKbps);
        }
        return $"-c:v libx264 -profile:v {RcProfile} -preset veryfast -crf {RcCrf.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The fragment the CLI and the menu use when the preset is applied. Kept as
    /// a property so existing call sites (and the settings file) see one string.
    /// </summary>
    public string RecompressArgs => Fragment(native: true);

    /// <summary>Same profile, rendered with libx264/aac so any player opens it.</summary>
    public string ModernRecompressArgs => Fragment(native: false);

    /// <summary>
    /// The fragment at a frame shape resolved by <see cref="EraAspects"/>
    /// (v7.9.6.3): the second pass has to deliver the same shape the live pass
    /// captured, or the artefact contradicts itself.
    /// </summary>
    public string RecompressArgsFor(bool native, (int W, int H)? size, string? h264Impl = null) =>
        Fragment(native, size, h264Impl);

    /// <summary>Container the native rendering lands in.</summary>
    public string NativeContainer => RecompressContainer;

    /// <summary>
    /// Container the modern rendering lands in. Constant by design: a "viewing
    /// copy" is an MP4 with H.264 + AAC, and that is the whole point of it.
    /// </summary>
    public const string ModernContainer = "mp4";

    public string ContainerFor(bool native) => native ? NativeContainer : ModernContainer;

    public string Note => NoteKey is { Length: > 0 } key ? Localization.L.T(key) : (NoteText ?? "");

    /// <summary>
    /// Every value the two layers hold, in one line, for the report and the
    /// menu. Kept next to the fields so a new field cannot be forgotten here.
    /// </summary>
    public string Summary((int W, int H)? size = null)
    {
        string ladder = FpsLevels is { Length: > 1 } levels
            ? " [" + string.Join(",", levels.Select(x => x.ToString("G6", CultureInfo.InvariantCulture))) + "]"
            : "";
        (int w, int h) = size ?? (RcWidth, RcHeight);
        // An H.264 row prints the implementation, because "which H.264" is
        // exactly what changes the look of a 2012 artefact.
        string video = string.Equals(VideoCodec, "libx264", StringComparison.OrdinalIgnoreCase)
            ? ((H264Impl ?? "libx264") == "libopenh264" ? "h264/openh264" : "h264/x264")
            : VideoCodec;
        return string.Create(CultureInfo.InvariantCulture,
            $"{w}x{h}@{RcFps:G6} {RcVideoKbps}k {video}/{AudioCodec} .{RecompressContainer}{ladder}");
    }
}

/// <summary>
/// The built-in <c>site/year</c> rows, plus whatever the user wrote in the
/// settings file. Read-only by design for the built-in rows: an upgrade can add
/// one without fighting a local modification, and a user who wants different
/// numbers writes a custom preset with the same id, which wins.
///
/// <para>Every built-in row is one site in one year. A site that changed shape
/// gets one row per shape (<c>bilibili/2010</c>, <c>bilibili/2012</c>,
/// <c>bilibili/2014</c>), which is what "site -&gt; year" means: the menu lists
/// the sites, then the years each one has.</para>
///
/// <para>The pre-v7.9.7 ids (<c>cctv03</c>, <c>youku07</c>, <c>dialup98</c>, ...)
/// still resolve, through <see cref="Aliases"/>, so a settings file or a shell
/// history entry keeps working -- they are just no longer printed.</para>
/// </summary>
public static class EraPresets
{
    private const string Generic = "generic";

    private static readonly IReadOnlyList<EraPreset> BuiltIn =
    [
        // ---- the web in general, before "a video site" was a business ----
        GenericRow(1998, "rv20", 160, 120, 10, 45, 16, 16000, 1, 12, 28,
            "era.generic.1998", ladder: [10, 8, 5, 3], crf: 34),
        GenericRow(2003, "rv20", 320, 240, 15, 225, 24, 22050, 1, 11, 22,
            "era.generic.2003", ladder: [15, 12, 10, 8, 5], crf: 31),
        GenericRow(2007, "flv", 352, 264, 15, 300, 32, 22050, 1, 11, 22,
            "era.generic.2007", ladder: [15, 12, 10, 8, 5], crf: 28),
        GenericRow(2010, "libx264", 640, 480, 25, 900, 64, 44100, 1, 9, 19,
            "era.generic.2010", ladder: [25, 20, 15, 10], crf: 25, profile: "main"),
        GenericRow(2014, "libx264", 1280, 720, 30, 2500, 96, 44100, 2, 8, 18,
            "era.generic.2014", ladder: [30, 25, 20, 15], crf: 23, profile: "high"),

        // ---- CCTV: Windows Media, then FLV, then its own CDN ----
        Site("cctv", 2003, 320, 240, 15, 250, 32, 22050, 1, "wmv2", "wmav2", "asf",
            11, 22, "era.cctv.2003", liveEncoder: "wmv2"),
        Site("cctv", 2006, 384, 288, 25, 350, 48, 22050, 1, "flv", "libmp3lame", "flv",
            11, 24, "era.cctv.2006"),
        Site("cctv", 2011, 640, 360, 25, 700, 96, 44100, 2, "libx264", "aac", "mp4",
            10, 21, "era.cctv.2011", h264: "libopenh264"),

        // ---- Sina: portal video, then podcast, then H.264 ----
        Site("sina", 2003, 320, 240, 12, 200, 24, 22050, 1, "wmv2", "wmav2", "asf",
            11, 22, "era.sina.2003", liveEncoder: "wmv2"),
        Site("sina", 2006, 320, 240, 12, 200, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 22, "era.sina.2006"),
        Site("sina", 2010, 640, 360, 25, 600, 96, 44100, 2, "libx264", "aac", "mp4",
            10, 22, "era.sina.2010", h264: "libopenh264"),

        // ---- Sohu ----
        Site("sohu", 2003, 320, 240, 15, 220, 32, 22050, 1, "wmv2", "wmav2", "asf",
            11, 22, "era.sohu.2003", liveEncoder: "wmv2"),
        Site("sohu", 2007, 352, 264, 15, 320, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 22, "era.sohu.2007"),
        Site("sohu", 2012, 640, 360, 25, 800, 96, 44100, 2, "libx264", "aac", "mp4",
            10, 20, "era.sohu.2012", h264: "libopenh264"),

        // ---- NetEase: the small-window player of 2003, then a late FLV site ----
        Site("netease", 2003, 176, 144, 12, 120, 24, 22050, 1, "wmv2", "wmav2", "asf",
            11, 22, "era.netease.2003", liveEncoder: "wmv2"),
        Site("netease", 2008, 320, 240, 15, 240, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 22, "era.netease.2008"),

        // ---- Tencent: the player that pushed 25 fps ----
        Site("qq", 2006, 320, 240, 25, 260, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 23, "era.qq.2006"),
        Site("qq", 2010, 640, 360, 25, 700, 96, 44100, 2, "libx264", "aac", "mp4",
            10, 21, "era.qq.2010", h264: "libopenh264"),

        // ---- Tudou / Youku / Ku6 / 56 / AcFun: the FLV years ----
        Site("tudou", 2006, 352, 264, 12, 295, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 22, "era.tudou.2006"),
        Site("tudou", 2009, 352, 264, 15, 320, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 22, "era.tudou.2009"),
        Site("youku", 2007, 352, 264, 15, 320, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 22, "era.youku.2007"),
        Site("youku", 2011, 640, 360, 25, 800, 96, 44100, 2, "libx264", "aac", "mp4",
            10, 20, "era.youku.2011", h264: "libopenh264"),
        Site("ku6", 2007, 320, 240, 15, 240, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 22, "era.ku6.2007"),
        Site("56", 2007, 320, 240, 15, 220, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 22, "era.56.2007"),
        Site("acfun", 2009, 320, 240, 15, 260, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 23, "era.acfun.2009"),

        // ---- Bilibili: FLV at 4:3, then 360p H.264, then 720p ----
        Site("bilibili", 2010, 384, 288, 24, 320, 32, 22050, 1, "flv", "libmp3lame", "flv",
            11, 24, "era.bilibili.2010"),
        Site("bilibili", 2012, 640, 360, 30, 700, 96, 44100, 2, "libx264", "aac", "mp4",
            10, 21, "era.bilibili.2012", h264: "libopenh264"),
        Site("bilibili", 2014, 1280, 720, 30, 1800, 128, 44100, 2, "libx264", "aac", "mp4",
            9, 21, "era.bilibili.2014", h264: "libopenh264"),

        // ---- iQiyi ----
        Site("iqiyi", 2011, 640, 360, 25, 800, 96, 44100, 2, "libx264", "aac", "mp4",
            10, 20, "era.iqiyi.2011", h264: "libopenh264"),

        // ---- YouTube ----
        Site("youtube", 2007, 320, 240, 15, 200, 64, 22050, 1, "flv", "libmp3lame", "flv",
            11, 22, "era.youtube.2007"),
        Site("youtube", 2011, 640, 360, 30, 700, 96, 44100, 2, "libx264", "aac", "mp4",
            10, 21, "era.youtube.2011", h264: "libopenh264"),
        // The one period-correct case where the site's codec is not H.264 at all.
        Site("youtube", 2015, 1280, 720, 30, 1500, 96, 48000, 2, "libvpx-vp9", "libopus", "webm",
            9, 23, "era.youtube.2015"),
    ];

    /// <summary>
    /// The pre-v7.9.7 ids, and what they were renamed to. A settings file or a
    /// shell history entry from before the rewrite keeps working; the menu and
    /// the help text only ever print the new spelling.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dialup98"] = "generic/1998",
            ["broadband03"] = "generic/2003",
            ["flv07"] = "generic/2007",
            ["mp4_10"] = "generic/2010",
            ["hd14"] = "generic/2014",
            // The five generic rows are also reachable by the generic-<year>
            // spelling the CLI help used to suggest.
            ["generic-1998"] = "generic/1998",
            ["generic-2003"] = "generic/2003",
            ["generic-2007"] = "generic/2007",
            ["generic-2010"] = "generic/2010",
            ["generic-2014"] = "generic/2014",

            ["cctv03"] = "cctv/2003",
            ["cctv06"] = "cctv/2006",
            ["cctv12"] = "cctv/2011",
            ["sina03"] = "sina/2003",
            ["sina06"] = "sina/2006",
            ["sohu03"] = "sohu/2003",
            ["sohu06"] = "sohu/2007",
            ["netease03"] = "netease/2003",
            ["qq06"] = "qq/2006",
            ["tudou06"] = "tudou/2006",
            ["youku07"] = "youku/2007",
            ["youku11"] = "youku/2011",
            ["ku6_07"] = "ku6/2007",
            ["56_07"] = "56/2007",
            ["acfun08"] = "acfun/2009",
            ["bilibili09"] = "bilibili/2010",
            ["bilibili11"] = "bilibili/2012",
            ["bilibili14"] = "bilibili/2014",
            ["iqiyi11"] = "iqiyi/2011",
            ["youtube07"] = "youtube/2007",
            ["youtube11"] = "youtube/2011",
            ["youtube15"] = "youtube/2015",
        };

    private static readonly List<EraPreset> CustomRows = [];
    private static readonly Lock Gate = new();

    /// <summary>
    /// The capture pipeline of a year: how long the ladder averages, how deep
    /// the frame queue is, and which deinterlacer existed (v7.9.8.2).
    ///
    /// <para>Three thresholds, each with a reason rather than a taste:</para>
    /// <list type="bullet">
    /// <item><description>2006 -- <c>yadif</c> ships with MPlayer in 2006 and
    /// enters FFmpeg the same year. Every row before it says <c>off</c>,
    /// because there was nothing to call.</description></item>
    /// <item><description>2006 -- streaming servers of the modem and WMV
    /// years adapted after several seconds of sustained trouble; a FLV portal
    /// stepped inside its own two-second buffer.</description></item>
    /// <item><description>2003 / 2009 -- the frame queue grows with the frame:
    /// four at 320x240, six at 384x288, eight once the rows reach 640x360 and
    /// beyond.</description></item>
    /// </list>
    /// </summary>
    public static (double Window, int Buffer, string Deint) LivePipeline(int year) =>
        (year < 2006 ? 3.0 : 2.0,
         year <= 2003 ? 4 : year <= 2009 ? 6 : 8,
         year >= 2006 ? "yadif" : "off");

    /// <summary>
    /// A site row: the site's ladder and the capture settings of that year.
    ///
    /// <para><paramref name="qv"/> and <paramref name="crf"/> are the live
    /// pass's quality in the two scales it can be asked for (v7.9.8.2); see
    /// <see cref="EraPreset.LiveQv"/>.</para>
    /// </summary>
    private static EraPreset Site(
        string site, int year, int width, int height, double fps,
        int videoKbps, int audioKbps, int audioRate, int channels,
        string videoCodec, string audioCodec, string container,
        int qv, int crf, string noteKey,
        string? liveEncoder = null, string? h264 = null) => new()
    {
        Id = $"{site}/{year}",
        Site = site,
        Year = year,
        Kind = EraKind.Site,
        YearFrom = year,
        YearTo = year,
        // The live pass adopts the codec the site ingested. A site that only
        // ever *served* another codec (YouTube's VP9) still took H.264 uploads,
        // so the fallback is H.264 rather than the delivery codec.
        Encoder = liveEncoder ?? (videoCodec is "flv" ? "flv" : videoCodec is "libx264" ? (h264 ?? "libx264") : "libx264"),
        Width = width,
        Height = height,
        Fps = fps,
        FpsLevels = SiteLadder(fps),
        LimitKbps = videoKbps,
        LiveQv = qv,
        LiveCrf = crf,
        ScaleAlgo = year < 2010 ? "bicubic" : "lanczos",
        AudioEncoder = videoCodec switch
        {
            "flv" => "libmp3lame",
            "wmv2" => "wmav2",
            _ => null,
        },
        AudioKbps = audioKbps,
        AudioChannels = channels,
        AudioRate = audioRate,
        RcWidth = width,
        RcHeight = height,
        RcFps = fps,
        RcVideoKbps = videoKbps,
        RcProfile = year < 2010 ? "baseline" : "main",
        RcCrf = year < 2010 ? 28 : 24,
        RcAudioKbps = audioKbps,
        RcAudioRate = audioRate,
        RcAudioChannels = channels,
        VideoCodec = videoCodec,
        AudioCodec = audioCodec,
        RecompressContainer = container,
        H264Impl = h264,
        NoteKey = noteKey,
        WindowSeconds = LivePipeline(year).Window,
        BufferFrames = LivePipeline(year).Buffer,
        Deinterlace = LivePipeline(year).Deint,
    };

    /// <summary>
    /// A row for the web in general: same shape, but the delivery codec stays
    /// H.264 so the result opens anywhere, and no bitrate cap is applied -- a
    /// period is not a company with a transcode ladder.
    /// </summary>
    private static EraPreset GenericRow(
        int year, string liveEncoder, int width, int height, double fps,
        int videoKbps, int audioKbps, int audioRate, int channels,
        int qv, int liveCrf, string noteKey,
        double[] ladder, int crf, string profile = "baseline") => new()
    {
        Id = $"{Generic}/{year}",
        Site = Generic,
        Year = year,
        Kind = EraKind.Era,
        YearFrom = year,
        YearTo = year,
        // Same rule as the site rows: from 2010 the H.264 encoder of the period
        // is OpenH264, so a capture that names H.264 names that one.
        Encoder = liveEncoder == "libx264" && year >= 2010 ? "libopenh264" : liveEncoder,
        Width = width,
        Height = height,
        Fps = fps,
        FpsLevels = ladder,
        LimitKbps = videoKbps,
        LiveQv = qv,
        LiveCrf = liveCrf,
        ScaleAlgo = year < 2004 ? "neighbor" : year < 2010 ? "bicubic" : "lanczos",
        AudioEncoder = liveEncoder is "flv" ? "libmp3lame" : null,
        AudioKbps = audioKbps,
        AudioChannels = channels,
        AudioRate = audioRate,
        RcWidth = width,
        RcHeight = height,
        RcFps = fps,
        RcVideoKbps = videoKbps,
        RcProfile = profile,
        RcCrf = crf,
        RcAudioKbps = audioKbps,
        RcAudioRate = audioRate,
        RcAudioChannels = channels,
        VideoCodec = "libx264",
        AudioCodec = "aac",
        RecompressContainer = "mp4",
        H264Impl = year >= 2010 ? "libopenh264" : null,
        NoteKey = noteKey,
        WindowSeconds = LivePipeline(year).Window,
        BufferFrames = LivePipeline(year).Buffer,
        Deinterlace = LivePipeline(year).Deint,
    };

    /// <summary>
    /// The frame-rate ladder of a site at a given base rate. The steps are the
    /// ones those portals actually used -- 12/10/8 for a 12 fps site,
    /// 12/10/8/5 for 15, 20/15/10 above that.
    /// </summary>
    private static double[] SiteLadder(double fps) => fps switch
    {
        <= 12 => [fps, 10, 8, 5],
        <= 15 => [fps, 12, 10, 8, 5],
        <= 25 => [fps, 20, 15, 10],
        _ => [fps, 25, 20, 15],
    };

    /// <summary>
    /// Install the user's own presets (v7.9.5.1). Called once per process after
    /// the settings file is read; a custom preset with a built-in id replaces
    /// that row in every list.
    /// </summary>
    public static void SetCustom(IEnumerable<EraPreset> custom)
    {
        lock (Gate)
        {
            CustomRows.Clear();
            CustomRows.AddRange(custom.Where(p => p.Id.HasText));
        }
    }

    /// <summary>
    /// Read the custom rows out of a settings snapshot. Split from
    /// <see cref="SetCustom"/> so the CLI can register them without a live
    /// <see cref="Configuration.Config"/> (it never applies the stored settings).
    /// </summary>
    public static void Register(Configuration.UserConfig stored)
    {
        var rows = new List<EraPreset>();
        foreach (Configuration.CustomEraConfig row in stored.CustomEras ?? [])
        {
            EraPreset? preset = Configuration.EraConfigMap.FromConfig(row);
            if (preset is not null) rows.Add(preset);
        }
        SetCustom(rows);
    }

    /// <summary>Built-in rows plus the user's, custom first so it wins on lookup.</summary>
    public static IReadOnlyList<EraPreset> All
    {
        get
        {
            lock (Gate) return [.. CustomRows, .. BuiltIn.Where(b => !CustomRows.Any(c => Same(c.Id, b.Id)))];
        }
    }

    /// <summary>Only the presets that can reshape the live pass.</summary>
    public static IReadOnlyList<EraPreset> WithLiveLayer => [.. All.Where(p => p.HasLive)];

    /// <summary>Only the site presets (one company, one year).</summary>
    public static IReadOnlyList<EraPreset> Sites => [.. All.Where(p => p.Kind == EraKind.Site)];

    /// <summary>
    /// The menu's two levels: every site, with the years it has, oldest site
    /// first. <c>generic</c> leads because it is the only one that is not a
    /// company.
    /// </summary>
    public static IReadOnlyList<(string Site, IReadOnlyList<EraPreset> Rows)> Groups
    {
        get
        {
            var groups = new List<(string Site, IReadOnlyList<EraPreset> Rows)>();
            foreach (IGrouping<string, EraPreset> group in All
                         .GroupBy(p => p.Site.HasText ? p.Site : p.Id))
            {
                List<EraPreset> rows = [.. group.OrderBy(p => p.Year == 0 ? p.YearFrom : p.Year)];
                groups.Add((group.Key, rows));
            }
            return [.. groups
                .OrderBy(g => g.Site == Generic ? 0 : 1)
                .ThenBy(g => g.Rows.Count > 0 ? (g.Rows[0].Year == 0 ? g.Rows[0].YearFrom : g.Rows[0].Year) : 0)
                .ThenBy(g => g.Site, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>The rows of one site, oldest first.</summary>
    public static IReadOnlyList<EraPreset> ForSite(string site) =>
        [.. All.Where(p => Same(p.Site, site)).OrderBy(p => p.Year)];

    public static IReadOnlyList<EraPreset> Custom
    {
        get { lock (Gate) return [.. CustomRows]; }
    }

    /// <summary>
    /// Resolve an id or an alias to the canonical id, or null when neither
    /// names a preset. <c>cctv03</c> answers <c>cctv/2003</c>; <c>cctv/2003</c>
    /// answers itself.
    /// </summary>
    public static string? Canonical(string? id)
    {
        if (!id.HasText) return null;
        string wanted = id.Trim();
        EraPreset? direct = All.FirstOrDefault(p => Same(p.Id, wanted));
        if (direct is not null) return direct.Id;
        return Aliases.TryGetValue(wanted, out string? renamed) ? renamed : null;
    }

    /// <summary>Was this id renamed by the v7.9.7 rewrite?</summary>
    public static bool IsAlias(string? id) =>
        id.HasText && Aliases.ContainsKey(id.Trim());

    public static EraPreset? Find(string? id)
    {
        string? canonical = Canonical(id);
        if (canonical is null) return null;
        return All.FirstOrDefault(p => Same(p.Id, canonical));
    }

    public static string Ids => string.Join(", ", All.Select(p => p.Id));

    /// <summary>The site names, in menu order, for a prompt.</summary>
    public static string SitesList => string.Join(", ", Groups.Select(g => g.Site));

    public static bool IsBuiltInId(string id) =>
        BuiltIn.Any(b => Same(b.Id, id));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
