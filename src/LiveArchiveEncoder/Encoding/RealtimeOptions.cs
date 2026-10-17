using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.Encoding;

/// <summary>All inputs of a single realtime encode call.</summary>
public sealed class RealtimeOptions
{
    public required string FFmpeg { get; init; }
    public required string FFprobe { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required EncoderInfo Encoder { get; init; }
    public double Fps { get; init; } = Constants.DefaultFps;
    public List<double> FpsLevels { get; init; } = [13.0, 8.0, 5.0, 3.0];
    public int Quality { get; init; } = 8;
    public int Threads { get; init; } = 16;
    public double LimitKbps { get; init; } = 200.0;
    public double RecoverKbps { get; init; } = 150.0;
    public double WindowSeconds { get; init; } = 2.0;
    public int BufferFrames { get; init; } = 6;
    public string? AudioEncoder { get; init; } = "libmp3lame";
    public int AudioKbps { get; init; } = 32;
    public int AudioChannels { get; init; } = 1;
    public int? AudioRate { get; init; }
    public bool HwaccelCuda { get; init; } = true;
    public int Width { get; init; } = Constants.DefaultWidth;
    public int Height { get; init; } = Constants.DefaultHeight;
    public bool Crop43 { get; init; } = true;
    public string ScaleAlgo { get; init; } = "neighbor";
    public string Deinterlace { get; init; } = "off";
    public string DeintMode { get; init; } = "send_frame";
    public string DeintParity { get; init; } = "auto";
    public bool DeintMarkedOnly { get; init; }
    public string? X264Preset { get; init; } = "medium";
    public string? X264Tune { get; init; }
    public string? X264Profile { get; init; }
    public string? X264Level { get; init; }
    public double StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public HashSet<string>? AvailableFilters { get; init; }
    public HashSet<string>? AvailableScaleAlgorithms { get; init; }
    public bool Verbose { get; init; } = true;

    /// <summary>
    /// Audio routing. <see cref="AudioMode.Inline"/> is the default and reproduces
    /// v6.3.3 exactly. <see cref="AudioMode.Separate"/> encodes the track in its own
    /// process and remuxes it afterwards; <see cref="AudioMode.Auto"/> does the same
    /// but falls back to the single-process pipeline when the muxer rejects the split.
    /// </summary>
    public AudioMode AudioMode { get; init; } = AudioMode.Inline;

    /// <summary>
    /// Drop the deliberate wall-clock pacing and run as fast as the decode/encode
    /// pipeline allows (measured ~58x on a 20s 320x240 source).
    ///
    /// This is NOT a free lunch: the output slot loop and the capture thread are
    /// both pinned to 1/fps on purpose, and the fresh/repeat mix plus the
    /// adaptive-FPS hysteresis are wall-clock driven. Turbo trades the "live
    /// archive" simulation for throughput, so it stays opt-in.
    /// </summary>
    public bool Turbo { get; init; }

    /// <summary>
    /// Split the time range into this many shards, encode them concurrently and
    /// join the results. 1 disables it. Only containers in
    /// <see cref="Constants.ConcatSafeFormats"/> are eligible; anything else
    /// silently falls back to a single shard because the join would corrupt it.
    /// </summary>
    public int Parallel { get; init; } = 1;
}
