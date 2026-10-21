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
    /// Text watermark burned in after the live pass and before the recompress
    /// pass. Off by default.
    /// </summary>
    public WatermarkSettings Watermark { get; set; } = new();

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
/// A static text watermark.
///
/// Deliberately narrow in v7.6: text, placement, size and colour. Movement,
/// outlines, background boxes and timed appearance are v8's job and were left
/// out rather than added as switches that do nothing -- the stage itself is
/// separate from the recompress stage precisely so those can be added without
/// touching anything else.
/// </summary>
public sealed class WatermarkSettings
{
    public bool Enabled { get; set; }

    /// <summary>The drawn text. Empty is rejected before FFmpeg is started.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// top-left, top-right, bottom-left, bottom-right or center.
    /// A video site of the era alternated corners between clips; that animation
    /// is not implemented yet, so this is a fixed corner.
    /// </summary>
    public string Position { get; set; } = "top-right";

    /// <summary>
    /// Point size. 0 means "scale it to the frame": height/15, floored at 8, so
    /// a 160x120 clip gets a period-appropriate 8 px mark instead of a 16 px one
    /// that would cover a sixth of the picture.
    /// </summary>
    public int FontSize { get; set; }

    /// <summary>Edge inset in pixels. 0 means height/40, floored at 4.</summary>
    public int Margin { get; set; }

    /// <summary>Any FFmpeg colour name or 0xRRGGBB value.</summary>
    public string FontColor { get; set; } = "white";

    /// <summary>
    /// Explicit font file. Null makes the program pick a system font that can
    /// actually render the text; on a Chinese Windows box that is Microsoft
    /// YaHei, because Arial cannot draw CJK and drawtext fails without a font
    /// that can.
    /// </summary>
    public string? FontFile { get; set; }

    public static IReadOnlyList<string> Positions =>
        ["top-left", "top-right", "bottom-left", "bottom-right", "center"];

    public static string CanonicalPosition(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        return Positions.Contains(v) ? v : "top-right";
    }
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

    /// <summary>
    /// Keep the watermarked intermediate file instead of deleting it when the
    /// recompress pass consumes it.
    /// </summary>
    public bool KeepIntermediate { get; set; }
}
