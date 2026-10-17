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
}

/// <summary>Full user configuration shared by CUI and CLI.</summary>
public sealed class Config
{
    public FileInfo? InputPath { get; set; }
    public FileInfo? OutputPath { get; set; }
    public bool OutputIsAuto { get; set; } = true;
    public double StartTimeSeconds { get; set; }
    public double? EndTimeSeconds { get; set; }
    public EncoderInfo? Encoder { get; set; }
    public string? AudioEncoder { get; set; } = "libmp3lame";
    public double Fps { get; set; } = Constants.DefaultFps;
    public List<double> FpsLevels { get; set; } = [13.0, 8.0, 5.0, 3.0];
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
    public bool Crop43 { get; set; } = true;
    public string ScaleAlgo { get; set; } = "neighbor";
    public string Deinterlace { get; set; } = "off";
    public string DeintMode { get; set; } = "send_frame";
    public string DeintParity { get; set; } = "auto";
    public bool DeintMarkedOnly { get; set; }
    public bool Cuda { get; set; } = true;
    public string X264Preset { get; set; } = "medium";
    public string X264Tune { get; set; } = "";
    public string X264Profile { get; set; } = "";
    public string X264Level { get; set; } = "";

    /// <summary>
    /// Drop the 1/fps realtime pacing. Off by default: it is much faster but the
    /// fresh/repeat mix and the adaptive ladder change, so the archive no longer
    /// matches what a live recording would have produced.
    /// </summary>
    public bool Turbo { get; set; }

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
}
