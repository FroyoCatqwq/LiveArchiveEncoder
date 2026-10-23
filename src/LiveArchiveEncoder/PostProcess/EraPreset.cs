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
    /// One site, recompress layer only (v7.9.5). A site's preset is a bitrate
    /// profile -- resolution, frame rate, video and audio kbps -- and nothing
    /// else, so it never fights the encoder the user picked for the live pass.
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
/// <item><description>the <b>recompress</b> layer -- the damage a video site
/// applied on upload, which is what people actually remember about the
/// era.</description></item>
/// </list>
///
/// v7.9.5 adds <see cref="EraKind.Site"/> presets, which speak only for the
/// second layer: 2005-2009 was a handful of sites with their own transcode
/// ladders, and "make this look like Youku 2007" does not imply a particular
/// live encoder.
///
/// The numbers are not invented; see <c>docs/era-presets.md</c> for the sources.
/// The short version:
/// <list type="bullet">
/// <item><description>RealSystem G2 Production Guide -- "for 28.8 Kbps modems
/// (20 Kbps usable bandwidth), you should use a window no larger than
/// 176x132", and 176x132 / 240x180 / 320x240 map to 20 / 100 / 200 Kbps
/// floors at 10-15 fps;</description></item>
/// <item><description>RealVideo 8 (RV30) targets 100-500 Kbps up to 352x288;
/// RealVideo 10 reaches DVD-like quality near 1 Mbps;</description></item>
/// <item><description>measured Tudou FLV downloads: 320x240 at 295 Kbps /
/// 12 fps and 352x264 at 291 Kbps / 11.8 fps; YouTube in 2007 sat around
/// 320x240, 150 Kbps, 22 kHz mono audio at 32 Kbps;</description></item>
/// <item><description>network side from the MicroComputer magazine scans
/// (W:\\书籍): 56K modems everywhere in 1998-2000, ISDN rollout, ADSL quoted
/// at 8 Mbps down / 1 Mbps up in 2002, and a 2007 retrospective describing
/// dial-up as "under 56 Kbps".</description></item>
/// </list>
/// </summary>
public sealed class EraPreset
{
    public required string Id { get; init; }

    /// <summary>Era (both layers) or site (recompress layer only).</summary>
    public EraKind Kind { get; init; } = EraKind.Era;

    /// <summary>First year the preset stands for, used only for listing order.</summary>
    public int YearFrom { get; init; }
    public int YearTo { get; init; }

    // ---- live (first pass); null on a site preset ----

    /// <summary>Legacy encoder name as FFmpeg spells it.</summary>
    public string? Encoder { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }

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

    // ---- recompress (second pass) ----

    /// <summary>
    /// The middle of an FFmpeg command -- never the input or the output, which
    /// the pipeline owns. See <see cref="PostArgs"/>.
    /// </summary>
    public required string RecompressArgs { get; init; }

    /// <summary>Container extension for the recompressed file, without the dot.</summary>
    public string RecompressContainer { get; init; } = "mp4";

    /// <summary>Localisation key for the one-line description shown in menus.</summary>
    public required string NoteKey { get; init; }

    /// <summary>True when this preset has a live layer to apply.</summary>
    public bool HasLive => Kind == EraKind.Era && Width is not null;

    /// <summary>
    /// Apply just the live layer of this preset. The recompress layer is a
    /// separate stage with its own on/off switch, so picking an era does not
    /// silently turn it on.
    ///
    /// Returns false for a site preset, which has no live layer at all.
    /// </summary>
    public bool ApplyLive(Configuration.Config c)
    {
        if (!HasLive) return false;

        c.Width = Width!.Value;
        c.Height = Height ?? c.Height;
        if (Fps is { } fps) c.Fps = fps;
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
}

/// <summary>
/// The built-in eras and sites. Read-only by design: users add or override
/// entries in their own config file, they never edit this table, so an upgrade
/// can add a row without fighting a local modification.
/// </summary>
public static class EraPresets
{
    public static readonly IReadOnlyList<EraPreset> All =
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
            LimitKbps = 45,
            ScaleAlgo = "neighbor",
            AudioEncoder = null,           // chosen per container
            AudioKbps = 16,
            AudioChannels = 1,
            AudioRate = 16000,
            // 20 Kbps usable on a 28.8K modem; speech-grade mono audio.
            RecompressArgs = "-vf scale=160:120:flags=neighbor,fps=10 "
                           + "-c:v libx264 -profile:v baseline -preset veryfast -crf 34 "
                           + "-ac 1 -ar 16000 -b:a 16k",
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
            LimitKbps = 225,
            ScaleAlgo = "bicubic",
            AudioKbps = 24,
            AudioChannels = 1,
            AudioRate = 22050,
            RecompressArgs = "-vf scale=320:240:flags=bicubic,fps=15 "
                           + "-c:v libx264 -profile:v baseline -preset veryfast -crf 31 "
                           + "-ac 1 -ar 22050 -b:a 24k",
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
            LimitKbps = 300,
            ScaleAlgo = "bicubic",
            AudioKbps = 32,
            AudioChannels = 1,
            AudioRate = 22050,
            RecompressArgs = "-vf scale=352:264:flags=bicubic,fps=15 "
                           + "-c:v libx264 -profile:v baseline -preset veryfast -crf 28 "
                           + "-ac 1 -ar 22050 -b:a 32k",
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
            LimitKbps = 900,
            ScaleAlgo = "lanczos",
            AudioKbps = 64,
            AudioChannels = 1,
            AudioRate = 44100,
            RecompressArgs = "-vf scale=640:480:flags=lanczos,fps=25 "
                           + "-c:v libx264 -profile:v main -preset veryfast -crf 25 "
                           + "-ac 1 -ar 44100 -b:a 64k",
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
            LimitKbps = 2500,
            ScaleAlgo = "lanczos",
            AudioKbps = 96,
            AudioChannels = 2,
            AudioRate = 44100,
            RecompressArgs = "-vf scale=1280:720:flags=lanczos,fps=30 "
                           + "-c:v libx264 -profile:v high -preset veryfast -crf 23 "
                           + "-ac 2 -ar 44100 -b:a 96k",
            NoteKey = "era.hd14",
        },

        // ---- v7.9.5: the sites themselves, recompress layer only ----
        //
        // 2005-2009 China was not one transcode ladder but six: every portal ran
        // its own FLV settings, which is why the same upload looked different on
        // Sina, Sohu, Tencent, Youku and Tudou. These presets reproduce the
        // *second* pass at each site's nominal bitrate and never touch the live
        // encoder, so "what Youku would have done to my recording" is one flag.
        Site("sina06", 2005, 2008, 320, 240, 12, 200, 32, 22050, 1, "era.sina06"),
        Site("sohu06", 2006, 2009, 320, 240, 15, 240, 32, 22050, 1, "era.sohu06"),
        Site("qq06", 2006, 2009, 320, 240, 15, 260, 32, 22050, 1, "era.qq06"),
        Site("tudou06", 2005, 2009, 352, 264, 12, 295, 32, 22050, 1, "era.tudou06"),
        Site("youku07", 2007, 2010, 352, 264, 15, 320, 32, 22050, 1, "era.youku07"),
        // YouTube's 2007 standard stream: 320x240, ~150-200 Kbps video, and the
        // 22.05 kHz mono track at 64 Kbps everybody remembers.
        Site("youtube07", 2006, 2008, 320, 240, 15, 200, 64, 22050, 1, "era.youtube07"),
    ];

    /// <summary>
    /// A site preset: the numbers are the site's transcode ladder, the fragment
    /// is derived from them so the two can never drift apart.
    ///
    /// The recompressed file stays MP4/H.264 on purpose (see docs/era-presets.md):
    /// the point is the bitrate and the resolution, and the result has to open in
    /// a player that is not from 2007. `-b:v` rather than `-crf` because a site
    /// targeted a bitrate, not a quality level.
    /// </summary>
    private static EraPreset Site(
        string id, int from, int to, int width, int height, double fps,
        int videoKbps, int audioKbps, int audioRate, int channels, string noteKey) => new()
    {
        Id = id,
        Kind = EraKind.Site,
        YearFrom = from,
        YearTo = to,
        RecompressArgs = string.Create(CultureInfo.InvariantCulture,
            $"-vf scale={width}:{height}:flags=bicubic,fps={fps:G6} "
            + $"-c:v libx264 -profile:v baseline -preset veryfast "
            + $"-b:v {videoKbps}k -maxrate {videoKbps * 12 / 10}k -bufsize {videoKbps * 2}k "
            + $"-ac {channels} -ar {audioRate} -c:a aac -b:a {audioKbps}k"),
        RecompressContainer = "mp4",
        NoteKey = noteKey,
    };

    public static EraPreset? Find(string? id)
    {
        if (!id.HasText) return null;
        return All.FirstOrDefault(
            p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Only the presets that can reshape the live pass.</summary>
    public static IReadOnlyList<EraPreset> WithLiveLayer =>
        [.. All.Where(p => p.HasLive)];

    /// <summary>Only the site presets (recompress layer alone).</summary>
    public static IReadOnlyList<EraPreset> Sites => [.. All.Where(p => p.Kind == EraKind.Site)];

    public static string Ids => string.Join(", ", All.Select(p => p.Id));
}
