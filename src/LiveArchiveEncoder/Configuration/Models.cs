namespace LiveArchiveEncoder.Configuration;

/// <summary>What to do with the file once an encode finishes.</summary>
public enum PlayMode
{
    Off = 0,
    /// <summary>ffplay from the active FFmpeg build.</summary>
    Ffplay = 1,
    /// <summary>A user-supplied executable; falls back to the shell association when unset.</summary>
    External = 2,
}

/// <summary>Video stream information reported by ffprobe.</summary>
public sealed class StreamInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string PixFmt { get; init; } = "yuv420p";
    public string? ColorRange { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }
}

/// <summary>
/// One stream of the source, as ffprobe describes it.
///
/// v7.9: a source may carry several video tracks (a VOB with an angle, an MKV
/// with a duplicate encode) and several audio tracks (language tracks), and
/// the user needs to be able to name one. <see cref="Index"/> is the absolute
/// stream index -- the number ffprobe prints and the number the user types.
/// </summary>
/// <param name="Index">Absolute stream index (what ffprobe shows as <c>index=</c>).</param>
/// <param name="Type">codec_type: video, audio, subtitle, ...</param>
/// <param name="Codec">codec_name, e.g. h264 or ac3.</param>
/// <param name="Language">Language tag, when the container carries one.</param>
/// <param name="Title">Stream title, when the container carries one.</param>
/// <param name="Video">Video metadata; null for every other stream type.</param>
public sealed record SourceStream(
    int Index, string Type, string? Codec, string? Language, string? Title, StreamInfo? Video = null);

/// <summary>Capabilities reported by `ffmpeg -h encoder=NAME`.</summary>
public sealed class EncoderCaps
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Flags { get; init; } = "";
    public List<string> SupportedPixFmts { get; init; } = [];
    public List<double> SupportedRates { get; init; } = [];
    public bool HasCrf { get; init; }
    public bool HasQscale { get; init; }
}

/// <summary>A video encoder discovered from `ffmpeg -encoders`.</summary>
public sealed class EncoderInfo
{
    public string Name { get; init; } = "";
    public string Flags { get; init; } = "";
    public string Description { get; init; } = "";
    public string Codec { get; init; } = "";
    public string Family { get; init; } = "";
    public string Extension { get; init; } = "avi";
    public string FormatName { get; init; } = "avi";
    public EncoderCaps? Caps { get; set; }

    /// <summary>
    /// The same encoder writing another container. Everything downstream
    /// (muxer argument, output extension check, audio codec choice, concat
    /// eligibility) reads <see cref="FormatName"/>, so the selection is applied
    /// by swapping in a copy rather than by threading a second value through
    /// every call site.
    /// </summary>
    public EncoderInfo WithContainer(string extension, string formatName) => new()
    {
        Name = Name,
        Flags = Flags,
        Description = Description,
        Codec = Codec,
        Family = Family,
        Extension = extension,
        FormatName = formatName,
        Caps = Caps,
    };
}

/// <summary>
/// A rung of the adaptive FPS ladder as the user configured it.
///
/// <see cref="Fps"/> is the only mandatory part. When a threshold is null the
/// encoder derives it from the run's limit/recover values, which is exactly what
/// every version up to v7.6 did -- so an old settings file keeps behaving the
/// same way after the upgrade.
/// </summary>
public sealed class FpsRungSetting
{
    public double Fps { get; set; }

    /// <summary>Descend onto this rung once the video bitrate reaches this value.</summary>
    public double? DropAboveKbps { get; set; }

    /// <summary>Climb back off this rung once the bitrate falls to this value.</summary>
    public double? RiseBelowKbps { get; set; }
}

/// <summary>
/// A fully resolved ladder rung. Rung 0 is the base rate and is never entered
/// from below, so its drop threshold is infinite.
/// </summary>
public readonly record struct FpsRung(double Fps, double DropAboveKbps, double RiseBelowKbps);

/// <summary>Full user configuration shared by CUI and CLI.</summary>
public sealed class Config
{
    public FileInfo? InputPath { get; set; }
    public FileInfo? OutputPath { get; set; }
    public bool OutputIsAuto { get; set; } = true;
    public double StartTimeSeconds { get; set; }
    public double? EndTimeSeconds { get; set; }
    public EncoderInfo? Encoder { get; set; }

    /// <summary>
    /// Requested container ("avi", "mkv", "mp4", ...). Null means the encoder's
    /// own default. Only containers this encoder can actually write are
    /// accepted -- see <see cref="Constants.ContainerChoicesByEncoder"/>.
    /// </summary>
    public string? Container { get; set; }

    public string? AudioEncoder { get; set; } = "libmp3lame";

    /// <summary>
    /// v7.9: absolute stream index of the video track to encode. Null means
    /// "the first video stream", which is what every version up to v7.8 did.
    /// </summary>
    public int? VideoStream { get; set; }

    /// <summary>
    /// v7.9: absolute stream index of the audio track to keep. Null means
    /// "the first audio stream".
    /// </summary>
    public int? AudioStream { get; set; }
    public double Fps { get; set; } = Constants.DefaultFps;
    public List<double> FpsLevels { get; set; } = [13.0, 8.0, 5.0, 3.0];

    /// <summary>
    /// Bitrate thresholds per ladder rung, keyed by FPS rather than by position,
    /// so editing the ladder never silently shifts a threshold onto another rung.
    /// Empty means "derive everything from Limit/Recover" (the v7.6 behaviour).
    /// </summary>
    public List<FpsRungSetting> FpsLevelKbps { get; set; } = [];

    public double Limit { get; set; } = 200.0;
    public double Recover { get; set; } = 150.0;
    public double Window { get; set; } = 2.0;
    public int Qv { get; set; } = 8;
    public int Threads { get; set; } = 16;
    public int BufferFrames { get; set; } = 6;
    public int AudioKbps { get; set; } = 32;
    public int AudioChannels { get; set; } = 1;
    public int? AudioRate { get; set; }
    public int Width { get; set; } = Constants.DefaultWidth;
    public int Height { get; set; } = Constants.DefaultHeight;
    /// <summary>
    /// How the picture is fitted into <see cref="Width"/>x<see cref="Height"/>:
    /// "crop" (fill, discard the overflow), "fit" (keep the whole frame and pad
    /// the remainder -- a 16:9 master inside a 4:3 frame gets top/bottom bars),
    /// or "stretch" (fill and distort).
    /// </summary>
    public string FitMode { get; set; } = "crop";

    /// <summary>Which edge survives a crop: center (default), top, bottom, left, right.</summary>
    public string CropAnchor { get; set; } = "center";

    /// <summary>Bar colour for "fit", e.g. black, white or 0xRRGGBB.</summary>
    public string PadColor { get; set; } = "black";
    public string ScaleAlgo { get; set; } = "neighbor";
    public string Deinterlace { get; set; } = "off";
    public string DeintMode { get; set; } = "send_frame";
    public string DeintParity { get; set; } = "auto";
    public bool DeintMarkedOnly { get; set; }
    /// <summary>
    /// Hardware decode method: "off" (the default), "auto", or any name
    /// <c>ffmpeg -hwaccels</c> reports (cuda, vaapi, d3d11va, qsv, ...).
    /// Off by default because using one requires a working driver and device,
    /// which is verified -- not assumed -- before it is switched on.
    /// </summary>
    public string Hwaccel { get; set; } = "off";
    public string X264Preset { get; set; } = "medium";
    public string X264Tune { get; set; } = "";
    public string X264Profile { get; set; } = "";
    public string X264Level { get; set; } = "";

    /// <summary>Shard count for the parallel pipeline; 1 disables it.</summary>
    public int Parallel { get; set; } = 1;

    /// <summary>UI language, e.g. "en" or "zh".</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Which FFmpeg distribution to use: one of the ids in
    /// <see cref="FFmpeg.FfmpegCatalog"/>, or "path" to mean "whatever is on PATH".
    /// </summary>
    public string FfmpegSource { get; set; } = FFmpeg.FfmpegCatalog.DefaultId;

    /// <summary>Proxy used when downloading an FFmpeg build, e.g. http://127.0.0.1:10808.</summary>
    public string? FfmpegProxy { get; set; }

    public PlayMode Play { get; set; } = PlayMode.Off;

    /// <summary>External player executable; empty means the shell file association.</summary>
    public string? PlayerPath { get; set; }

    /// <summary>
    /// v7.9.1: subtitles, as the interactive shell keeps them. The CLI spells
    /// the same request with --subtitle / --subtitle-stream / --subtitle-mode
    /// / --subtitle-codec; a CLI run never reads these as defaults, it only
    /// writes its own request back.
    /// </summary>
    public SubtitleConfig Subtitles { get; set; } = new();

    /// <summary>
    /// The "upload to a video site" pass. Off by default.
    /// </summary>
    public RecompressSettings Recompress { get; set; } = new();

    /// <summary>
    /// Which era preset was last applied, or null when the settings are custom.
    /// Remembered only so the menu can show it; it is never used to override
    /// explicit settings.
    /// </summary>
    public string? EraPreset { get; set; }
}

/// <summary>
/// v7.9.1: subtitles, as the interactive shell keeps them.
///
/// Mirrors <see cref="Encoding.SubtitleSettings"/> but persists cleanly:
/// the mode is the same string the CLI spells, the file list is editable
/// in place, and an embedded track is a single index. Files and an embedded
/// stream are mutually exclusive -- the encoder refuses both at once -- so
/// the menu clears the other side when one is set.
/// </summary>
public sealed class SubtitleConfig
{
    /// <summary>"burn" (default), "copy", or "off".</summary>
    public string Mode { get; set; } = "burn";

    /// <summary>External sidecar files (.ass/.ssa/.srt/.vtt), in order.</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>Absolute index of an embedded subtitle track; null = none picked.</summary>
    public int? Stream { get; set; }

    /// <summary>Explicit codec for copy mode; null picks one per container.</summary>
    public string? Codec { get; set; }

    /// <summary>Same rule as <see cref="Encoding.SubtitleSettings.Active"/>.</summary>
    public bool Active => Mode != "off" && (Files.Count > 0 || Stream is not null);
}

/// <summary>
/// The second pass: re-encode the live product the way a video site would have.
///
/// The user supplies the middle of an FFmpeg command and the program owns both
/// ends, so a typo cannot overwrite the source. See <see cref="PostProcess.PostArgs"/>.
/// </summary>
public sealed class RecompressSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Everything between <c>-i &lt;live product&gt;</c> and the output path,
    /// e.g. <c>-vf "scale=320:240,fps=15" -c:v libx264 -crf 28 -ac 1 -b:a 32k</c>.
    /// </summary>
    public string Args { get; set; } = "";

    /// <summary>Container extension without the dot.</summary>
    public string Container { get; set; } = "mp4";
}
