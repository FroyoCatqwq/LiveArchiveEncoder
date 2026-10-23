using System.Globalization;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>Which half of the pipeline a preset speaks for.</summary>
public enum EraKind
{
    /// <summary>
    /// A whole period: the live layer (encoder, size, rate, ceiling) plus the
    /// damage a site would then have done. What v7.6 shipped.
    /// </summary>
    Era = 0,

    /// <summary>
    /// One site, second pass only (v7.9.5). A site's preset is a bitrate
    /// profile -- resolution, frame rate, video and audio kbps -- plus the
    /// codec that site actually served, which is the encoder the preset names.
    /// </summary>
    Site = 1,
}

/// <summary>
/// One "year" of online video: what the live capture would have produced, and
/// what the hosting site would then have done to it.
///
/// Two layers, because the pipeline has two stages and they model different
/// things:
/// <list type="bullet">
/// <item><description>the <b>live</b> layer -- the encoder, frame size, frame
/// rate and bitrate ceiling of the archival pass;</description></item>
/// <item><description>the <b>second</b> layer -- the damage a video site
/// applied on upload, which is what people actually remember about the
/// era.</description></item>
/// </list>
///
/// v7.9.5.1 gives site presets an <see cref="Encoder"/> too: a 2007 portal
/// transcoded to Sorenson Spark in FLV, a 2014 portal to H.264 in MP4, and
/// reproducing "what Youku did" means using that codec -- the same 320 kbps of
/// H.264 looks nothing like 320 kbps of H.263. The live layer size stays the
/// user's, because a site says nothing about how the recording was captured.
///
/// A preset can also be written by the user (see <c>CustomEra</c>): those live
/// in the settings file next to the built-in table, override a built-in id when
/// they share it, and never need a rebuild to change.
///
/// The numbers are not invented; see <c>docs/era-presets.md</c> for the sources.
/// </summary>
public sealed class EraPreset
{
    public required string Id { get; init; }

    /// <summary>Era (both layers) or site (second pass only).</summary>
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
    /// <para>A base rate on its own is not a preset: 25 fps with the tool's
    /// default 13/8/5/3 ladder means the first step down throws away half the
    /// frame rate, which is not what a 2010 encoder did. The ladder is part of
    /// the period, so it lives here next to <see cref="Fps"/>.</para>
    ///
    /// <para>Null means "derive one from <see cref="Fps"/>" -- the old
    /// behaviour, kept for presets that do not care and for settings files
    /// written before this field existed. An explicit <c>--fps</c> or
    /// <c>--fps-levels</c> still wins over all of it: a preset is a fallback,
    /// not an override.</para>
    /// </summary>
    public double[]? FpsLevels { get; init; }


    /// <summary>Bitrate ceiling; the adaptive ladder starts dropping fps above it.</summary>
    public double? LimitKbps { get; init; }

    /// <summary>
    /// Scaler kernel. Deliberately era-appropriate: the cheap nearest-neighbour
    /// scaler is what a Pentium-era encoder could afford, lanczos only shows up
    /// once CPUs are no longer the constraint.
    /// </summary>
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
    /// rendering uses <c>libx264</c> + <c>aac</c> instead, so the file opens in
    /// any current player.
    /// </summary>
    public string VideoCodec { get; init; } = "libx264";
    public string AudioCodec { get; init; } = "aac";

    /// <summary>Container for the native rendering; the modern one is always mp4.</summary>
    public string RecompressContainer { get; init; } = "mp4";

    /// <summary>Localisation key for the one-line description shown in menus.</summary>
    public string? NoteKey { get; init; }

    /// <summary>Free-text description of a user preset (no localisation).</summary>
    public string? NoteText { get; init; }

    /// <summary>True when this preset has a live layer to apply.</summary>
    public bool HasLive => Width is not null && Height is not null;

    /// <summary>True when the preset names a live encoder (site presets do).</summary>
    public bool HasEncoder => Encoder is { Length: > 0 };

    /// <summary>
    /// Apply the live layer of this preset. The second pass is a separate stage
    /// with its own on/off switch, so picking an era does not silently turn it
    /// on.
    ///
    /// Returns false when the preset has no frame size (a site preset): the
    /// caller still gets <see cref="Encoder"/> through <see cref="ApplyEncoder"/>.
    /// </summary>
    public bool ApplyLive(Configuration.Config c)
    {
        if (!HasLive) return false;

        c.Width = Width!.Value;
        c.Height = Height ?? c.Height;
        if (Fps is { } fps) c.Fps = fps;
        // v7.9.6.1: the ladder follows the base rate. Without this, a 25 fps era
        // kept the 13/8/5/3 rungs meant for the 13 fps default -- the first step
        // down would have thrown away half the frame rate, which no period
        // encoder did. Pinned thresholds are pruned to the rungs that survive.
        if (FpsLevels is { Length: > 1 } levels)
        {
            c.FpsLevels = EncoderMath.SelectLevels(c.Fps, levels);
            c.FpsLevelKbps = EncoderMath.PruneRungs(c.FpsLevelKbps, c.FpsLevels);
        }
        else if (Fps is not null)
        {
            // No ladder in this preset (a hand-written one, or a file from
            // before the field existed): keep the tool's default rungs and let
            // the base rate decide which of them survive.
            c.FpsLevels = EncoderMath.SelectLevels(c.Fps, Configuration.Constants.DefaultFpsLevels);
            c.FpsLevelKbps = EncoderMath.PruneRungs(c.FpsLevelKbps, c.FpsLevels);
        }
        if (LimitKbps is { } limit)
        {
            c.Limit = limit;
            c.Recover = limit * 0.75;
        }
        if (ScaleAlgo is { Length: > 0 } scale) c.ScaleAlgo = scale;
        if (AudioKbps is { } kbps) c.AudioKbps = kbps;
        if (AudioChannels is { } channels) c.AudioChannels = channels;
        c.AudioRate = AudioRate;
        if (AudioEncoder is not null) c.AudioEncoder = AudioEncoder;
        return true;
    }

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
    /// The middle of the FFmpeg command for the second pass.
    ///
    /// <paramref name="native"/> true renders with the site's own codec (the
    /// artefacts people remember); false renders the same resolution/bitrate
    /// with libx264 in MP4, which is what every version before v7.9.5.1 did.
    /// </summary>
    public string Fragment(bool native)
    {
        string video = native
            ? NativeVideoArgs()
            : $"-c:v libx264 -profile:v {RcProfile} -preset veryfast -crf {RcCrf.ToString(CultureInfo.InvariantCulture)}";

        var parts = new List<string>
        {
            $"-vf scale={RcWidth}:{RcHeight}:flags=bicubic,fps={RcFps.ToString("G6", CultureInfo.InvariantCulture)}",
            video,
        };

        // A site targeted a bitrate, so the native rendering caps it. The modern
        // rendering is CRF-based and needs no cap: it is a viewing copy.
        if (native && Kind == EraKind.Site)
            parts.Add($"-maxrate {RcVideoKbps * 12 / 10}k -bufsize {RcVideoKbps * 2}k");

        parts.Add(native
            ? RecompressCodecs.AudioArgs(AudioCodec, RcAudioKbps, RcAudioRate, RcAudioChannels)
            : RecompressCodecs.AudioArgs("aac", RcAudioKbps, RcAudioRate, RcAudioChannels));
        return string.Join(' ', parts);
    }

    private string NativeVideoArgs()
    {
        SecondPassVideoCodec? info = RecompressCodecs.FindVideo(VideoCodec);
        return info is null
            ? RecompressCodecs.VideoArgs(
                RecompressCodecs.FindVideo("libx264")!, RcCrf, RcVideoKbps)
            : RecompressCodecs.VideoArgsAtBitrate(info, RcVideoKbps);
    }

    /// <summary>
    /// The fragment the CLI and the menu use when the preset is applied. Kept as
    /// a property so existing call sites (and the settings file) see one string.
    /// </summary>
    public string RecompressArgs => Fragment(native: true);

    /// <summary>Same profile, rendered with libx264/aac so any player opens it.</summary>
    public string ModernRecompressArgs => Fragment(native: false);

    /// <summary>Container the native rendering lands in.</summary>
    public string NativeContainer => RecompressContainer;

    /// <summary>
    /// Container the modern rendering lands in. Constant by design: a "viewing
    /// copy" is an MP4 with libx264 + AAC, and that is the whole point of it.
    /// </summary>
    public const string ModernContainer = "mp4";

    public string ContainerFor(bool native) => native ? NativeContainer : ModernContainer;

    public string Note => NoteKey is { Length: > 0 } key ? Localization.L.T(key) : (NoteText ?? "");

    /// <summary>
    /// Every value the two layers hold, in one line, for the report and the
    /// menu. Kept next to the fields so a new field cannot be forgotten here.
    /// </summary>
    public string Summary()
    {
        string ladder = FpsLevels is { Length: > 1 } levels
            ? " [" + string.Join(",", levels.Select(x => x.ToString("G6", CultureInfo.InvariantCulture))) + "]"
            : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{RcWidth}x{RcHeight}@{RcFps:G6} {RcVideoKbps}k {VideoCodec}/{AudioCodec} .{RecompressContainer}{ladder}");
    }
}

/// <summary>
/// The built-in eras and sites, plus whatever the user wrote in the settings
/// file. Read-only by design for the built-in rows: an upgrade can add one
/// without fighting a local modification, and a user who wants different
/// numbers writes a custom preset with the same id, which wins.
/// </summary>
public static class EraPresets
{
    private static readonly IReadOnlyList<EraPreset> BuiltIn =
    [
        new EraPreset
        {
            Id = "dialup98",
            YearFrom = 1997, YearTo = 2000,
            // RealVideo G2. 176x132 is the largest window the G2 production guide
            // allows for a 28.8K modem; 160x120 keeps the 4:3 ratio the encoder
            // family expects while staying a power-of-two-ish size.
            Encoder = "rv20",
            Width = 160, Height = 120,
            Fps = 10,
            // RealVideo G2 on a 28.8K modem ran the ladder 10 -> 8 -> 5 -> 3.
            // Below 5 fps the encoder stops pretending to be video, which is
            // exactly what a 20 Kbps stream looked like; 3 stays because the
            // original controller allowed a slideshow rather than a stall.
            FpsLevels = [10, 8, 5, 3],
            LimitKbps = 45,
            ScaleAlgo = "neighbor",
            AudioEncoder = null,           // chosen per container
            AudioKbps = 16,
            AudioChannels = 1,
            AudioRate = 16000,
            RcWidth = 160, RcHeight = 120, RcFps = 10,
            RcVideoKbps = 45, RcCrf = 34, RcProfile = "baseline",
            RcAudioKbps = 16, RcAudioRate = 16000, RcAudioChannels = 1,
            // 20 Kbps usable on a 28.8K modem; speech-grade mono audio.
            NoteKey = "era.dialup98",
        },
        new EraPreset
        {
            Id = "broadband03",
            YearFrom = 2001, YearTo = 2004,
            // Early ADSL: 8 Mbps down in principle, 512 Kbps in practice.
            // RealVideo 8 territory (100-500 Kbps, up to 352x288).
            Encoder = "rv20",
            Width = 320, Height = 240,
            Fps = 15,
            // Early-ADSL RealVideo: 15 fps base stepping 15 -> 12 -> 10 -> 8 -> 5.
            // The 12 and 8 rungs matter: RV8/9 kept 12 fps usable at 150 Kbps,
            // while a 15-to-8 jump would have looked like a broken player.
            FpsLevels = [15, 12, 10, 8, 5],
            LimitKbps = 225,
            ScaleAlgo = "bicubic",
            AudioKbps = 24,
            AudioChannels = 1,
            AudioRate = 22050,
            RcWidth = 320, RcHeight = 240, RcFps = 15,
            RcVideoKbps = 225, RcCrf = 31, RcProfile = "baseline",
            RcAudioKbps = 24, RcAudioRate = 22050, RcAudioChannels = 1,
            NoteKey = "era.broadband03",
        },
        new EraPreset
        {
            Id = "flv07",
            YearFrom = 2005, YearTo = 2008,
            // Sorenson Spark (H.263) in an FLV: what Tudou/Youku/YouTube served.
            // Measured Tudou files sit at 320x240 / ~295 Kbps / 12 fps and at
            // 352x264 / ~291 Kbps / 11.8 fps; the larger size is used here so the
            // eras stay monotonically bigger than the 320x240 one before it.
            Encoder = "flv",
            Width = 352, Height = 264,
            Fps = 15,
            // Sorenson Spark in FLV: 15 fps base, 15 -> 12 -> 10 -> 8 -> 5.
            // Measured Tudou files sit at 11.8 fps, i.e. the 12 rung was the
            // one the portals actually used when the uplink was short.
            FpsLevels = [15, 12, 10, 8, 5],
            LimitKbps = 300,
            ScaleAlgo = "bicubic",
            AudioKbps = 32,
            AudioChannels = 1,
            AudioRate = 22050,
            RcWidth = 352, RcHeight = 264, RcFps = 15,
            RcVideoKbps = 300, RcCrf = 28, RcProfile = "baseline",
            RcAudioKbps = 32, RcAudioRate = 22050, RcAudioChannels = 1,
            VideoCodec = "libx264",       // an era is not one site; keep it playable
            AudioCodec = "aac",
            NoteKey = "era.flv07",
        },
        new EraPreset
        {
            Id = "mp4_10",
            YearFrom = 2009, YearTo = 2012,
            // H.264 in MP4 takes over; 480p becomes the default web size.
            Encoder = "libx264",
            Width = 640, Height = 480,
            Fps = 25,
            // 480p H.264: 25 fps base, dropping 25 -> 20 -> 15 -> 10. No 5 rung:
            // by this period the ladder stopped before slideshow territory, and
            // the encoder would rather spend bits than frames.
            FpsLevels = [25, 20, 15, 10],
            LimitKbps = 900,
            ScaleAlgo = "lanczos",
            AudioKbps = 64,
            AudioChannels = 1,
            AudioRate = 44100,
            RcWidth = 640, RcHeight = 480, RcFps = 25,
            RcVideoKbps = 900, RcCrf = 25, RcProfile = "main",
            RcAudioKbps = 64, RcAudioRate = 44100, RcAudioChannels = 1,
            NoteKey = "era.mp4_10",
        },
        new EraPreset
        {
            Id = "hd14",
            YearFrom = 2013, YearTo = 2016,
            // Broadband is no longer the constraint; 720p is the web default and
            // stereo audio survives the upload.
            Encoder = "libx264",
            Width = 1280, Height = 720,
            Fps = 30,
            // 720p: 30 fps base, 30 -> 25 -> 20 -> 15. The steps are small on
            // purpose -- dropping a 720p stream to 15 fps was already a visible
            // defect, so no 10/5 rungs.
            FpsLevels = [30, 25, 20, 15],
            LimitKbps = 2500,
            ScaleAlgo = "lanczos",
            AudioKbps = 96,
            AudioChannels = 2,
            AudioRate = 44100,
            RcWidth = 1280, RcHeight = 720, RcFps = 30,
            RcVideoKbps = 2500, RcCrf = 23, RcProfile = "high",
            RcAudioKbps = 96, RcAudioRate = 44100, RcAudioChannels = 2,
            NoteKey = "era.hd14",
        },

        // ---- the 2005-2009 portals: Sorenson Spark in FLV ----
        //
        // 2005-2009 China was not one transcode ladder but a dozen: every portal
        // ran its own FLV settings, which is why the same upload looked
        // different on Sina, Sohu, Tencent, Youku, Tudou, 56 and Ku6. These are
        // the *second* pass only -- the live size stays yours -- but they do
        // name the codec, because that is half of what "looks like 2007" means.
        Site("sina06", 2005, 2008, 320, 240, 12, 200, 32, 22050, 1, "flv", "libmp3lame", "flv", "era.sina06"),
        Site("sohu06", 2006, 2009, 320, 240, 15, 240, 32, 22050, 1, "flv", "libmp3lame", "flv", "era.sohu06"),
        // Tencent's player was the one that pushed a higher frame rate in that
        // period: 25 fps, not 15, at the same 320x240 the rest of the portals
        // served. Same bitrate, twice the frames -- visibly smoother, visibly
        // blockier per frame, which is exactly how QQ播客 looked.
        Site("qq06", 2006, 2009, 320, 240, 25, 260, 32, 22050, 1, "flv", "libmp3lame", "flv", "era.qq06"),
        Site("tudou06", 2005, 2009, 352, 264, 12, 295, 32, 22050, 1, "flv", "libmp3lame", "flv", "era.tudou06"),
        Site("youku07", 2007, 2010, 352, 264, 15, 320, 32, 22050, 1, "flv", "libmp3lame", "flv", "era.youku07"),
        Site("ku6_07", 2007, 2010, 320, 240, 15, 240, 32, 22050, 1, "flv", "libmp3lame", "flv", "era.ku6"),
        Site("56_07", 2006, 2010, 320, 240, 15, 220, 32, 22050, 1, "flv", "libmp3lame", "flv", "era.56"),
        // 央视网 was the odd one out: 4:3, 25 fps (PAL telecine), and the
        // bitrate of a broadcaster rather than a portal.
        Site("cctv06", 2006, 2009, 384, 288, 25, 350, 48, 22050, 1, "flv", "libmp3lame", "flv", "era.cctv06"),
        Site("acfun08", 2008, 2011, 320, 240, 15, 260, 32, 22050, 1, "flv", "libmp3lame", "flv", "era.acfun08"),
        // Bilibili started as an FLV site too; 384x288@24 is the anime size of
        // the period (4:3 source, 24 fps telecine).
        Site("bilibili09", 2009, 2012, 384, 288, 24, 320, 32, 22050, 1, "flv", "libmp3lame", "flv", "era.bilibili09"),
        // YouTube's 2007 standard stream: 320x240, ~150-200 Kbps video, and the
        // 22.05 kHz mono track at 64 Kbps everybody remembers.
        Site("youtube07", 2006, 2008, 320, 240, 15, 200, 64, 22050, 1, "flv", "libmp3lame", "flv", "era.youtube07"),

        // ---- the 2010s: H.264 in MP4, 360p then 720p ----
        Site("bilibili11", 2011, 2013, 640, 360, 30, 700, 96, 44100, 2, "libx264", "aac", "mp4", "era.bilibili11"),
        Site("youku11", 2011, 2013, 640, 360, 25, 800, 96, 44100, 2, "libx264", "aac", "mp4", "era.youku11"),
        Site("iqiyi11", 2011, 2014, 640, 360, 25, 800, 96, 44100, 2, "libx264", "aac", "mp4", "era.iqiyi11"),
        Site("cctv12", 2010, 2013, 640, 360, 25, 700, 96, 44100, 2, "libx264", "aac", "mp4", "era.cctv12"),
        Site("youtube11", 2011, 2013, 640, 360, 30, 700, 96, 44100, 2, "libx264", "aac", "mp4", "era.youtube11"),
        Site("bilibili14", 2014, 2016, 1280, 720, 30, 1800, 128, 44100, 2, "libx264", "aac", "mp4", "era.bilibili14"),
        // YouTube moved 720p to VP9 + Opus around 2014-2016, which is the one
        // period-correct case where the "modern" codec is not the site's codec.
        Site("youtube15", 2014, 2016, 1280, 720, 30, 1500, 96, 48000, 2, "libvpx-vp9", "libopus", "webm", "era.youtube15"),
    ];

    private static readonly List<EraPreset> CustomRows = [];
    private static readonly Lock Gate = new();

    /// <summary>
    /// A site preset: the numbers are the site's transcode ladder, the fragment
    /// is derived from them so the two can never drift apart.
    ///
    /// The live encoder is the one that site used (FLV-era portals: flv1;
    /// H.264-era: libx264), because the codec is half of what "looks like
    /// 2007" means. The frame size is left alone: a site says nothing about
    /// how the recording was captured.
    /// </summary>
    private static EraPreset Site(
        string id, int from, int to, int width, int height, double fps,
        int videoKbps, int audioKbps, int audioRate, int channels,
        string videoCodec, string audioCodec, string container, string noteKey) => new()
    {
        Id = id,
        Kind = EraKind.Site,
        YearFrom = from,
        YearTo = to,
        // The live pass adopts the codec the site ingested. A site that only
        // ever *served* another codec (YouTube's VP9) still took H.264 uploads,
        // so the fallback is libx264 rather than the delivery codec.
        Encoder = videoCodec is "flv" or "libx264" ? videoCodec : "libx264",
        RcWidth = width,
        RcHeight = height,
        RcFps = fps,
        RcVideoKbps = videoKbps,
        // 2000s portals ran a baseline profile on a Pentium; 2010s ran main.
        RcProfile = from < 2010 ? "baseline" : "main",
        RcCrf = from < 2010 ? 28 : 24,
        RcAudioKbps = audioKbps,
        RcAudioRate = audioRate,
        RcAudioChannels = channels,
        VideoCodec = videoCodec,
        AudioCodec = audioCodec,
        RecompressContainer = container,
        NoteKey = noteKey,
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

    /// <summary>Only the site presets (second pass alone, encoder named).</summary>
    public static IReadOnlyList<EraPreset> Sites => [.. All.Where(p => p.Kind == EraKind.Site)];

    public static IReadOnlyList<EraPreset> Custom
    {
        get { lock (Gate) return [.. CustomRows]; }
    }

    public static EraPreset? Find(string? id)
    {
        if (!id.HasText) return null;
        return All.FirstOrDefault(p => Same(p.Id, id));
    }

    public static string Ids => string.Join(", ", All.Select(p => p.Id));

    public static bool IsBuiltInId(string id) =>
        BuiltIn.Any(b => Same(b.Id, id));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
