namespace LiveArchiveEncoder.PostProcess;

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

    /// <summary>First year the preset stands for, used only for listing order.</summary>
    public int YearFrom { get; init; }
    public int YearTo { get; init; }

    // ---- live (first pass) ----

    /// <summary>Legacy encoder name as FFmpeg spells it.</summary>
    public required string Encoder { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }

    /// <summary>Bitrate ceiling; the adaptive ladder starts dropping fps above it.</summary>
    public double LimitKbps { get; init; }

    /// <summary>
    /// Scaler kernel. Deliberately era-appropriate: the cheap nearest-neighbour
    /// scaler is what a Pentium-era encoder could afford, lanczos only shows up
    /// once CPUs are no longer the constraint.
    /// </summary>
    public string ScaleAlgo { get; init; } = "neighbor";

    public string? AudioEncoder { get; init; }
    public int AudioKbps { get; init; } = 32;
    public int AudioChannels { get; init; } = 1;
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

    /// <summary>
    /// Apply just the live layer of this preset. The recompress layer is a
    /// separate stage with its own on/off switch, so picking an era does not
    /// silently turn it on.
    /// </summary>
    public void ApplyLive(Configuration.Config c)
    {
        c.Width = Width;
        c.Height = Height;
        c.Fps = Fps;
        c.Limit = LimitKbps;
        c.Recover = LimitKbps * 0.75;
        c.ScaleAlgo = ScaleAlgo;
        c.AudioKbps = AudioKbps;
        c.AudioChannels = AudioChannels;
        c.AudioRate = AudioRate;
        if (AudioEncoder is not null) c.AudioEncoder = AudioEncoder;
    }
}

/// <summary>
/// The built-in eras. Read-only by design: users add or override entries in
/// their own config file, they never edit this table, so an upgrade can add a
/// row without fighting a local modification.
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
    ];

    public static EraPreset? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return All.FirstOrDefault(
            p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static string Ids => string.Join(", ", All.Select(p => p.Id));
}
