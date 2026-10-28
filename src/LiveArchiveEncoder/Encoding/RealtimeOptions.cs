using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.Encoding;

/// <summary>
/// All inputs of a single realtime encode call.
///
/// A record, not a class, because the two callers that re-enter the encoder
/// (the shard pipeline and the split audio/video pipeline) have to hand down a
/// copy with a few fields changed. v7.9.8.6: those copies used to be written out
/// by hand, and twice in a row a field was forgotten -- first the per-rung
/// bitrates, then <see cref="NnediWeights"/> and <see cref="Parallel"/>, so
/// <c>--deinterlace nnedi --parallel 4</c> failed claiming the weights file was
/// missing. <c>with</c> copies every property, including the ones added later.
/// </summary>
public sealed record RealtimeOptions
{
    public required string FFmpeg { get; init; }
    public required string FFprobe { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required EncoderInfo Encoder { get; init; }
    public double Fps { get; init; } = Constants.DefaultFps;
    public List<double> FpsLevels { get; init; } = [13.0, 8.0, 5.0, 3.0];

    /// <summary>
    /// Per-rung bitrate thresholds, keyed by FPS. Empty derives them from
    /// <see cref="LimitKbps"/>/<see cref="RecoverKbps"/>, which is the v7.6 rule.
    /// </summary>
    public List<FpsRungSetting> FpsLevelKbps { get; init; } = [];
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

    /// <summary>
    /// v7.9: absolute stream index of the video track to encode; null is the
    /// first video stream (the v7.8 and earlier behaviour). See
    /// <see cref="StreamSelection"/>.
    /// </summary>
    public int? VideoStream { get; init; }

    /// <summary>v7.9: absolute stream index of the audio track to keep; null is the first.</summary>
    public int? AudioStream { get; init; }

    /// <summary>
    /// v7.9: subtitles -- an external sidecar file and/or an embedded track,
    /// burned into the picture or carried as a stream. Null means none.
    /// </summary>
    public SubtitleSettings? Subtitles { get; init; }

    /// <summary>
    /// Subtitle encoders this FFmpeg build can write, when they were already
    /// discovered. Null means "detect them if subtitles are in play" -- shards
    /// inherit the set so N of them do not each spawn an <c>ffmpeg -encoders</c>.
    /// </summary>
    public HashSet<string>? AvailableSubtitleEncoders { get; init; }
    /// <summary>
    /// Hardware decode method: "off" (the default since v7.5), "auto", or any
    /// name <c>ffmpeg -hwaccels</c> prints (cuda, vaapi, d3d11va, qsv, ...).
    ///
    /// Off by default because using one needs a real driver and a real device,
    /// which is verified by decoding a frame before the method is accepted --
    /// <c>ffmpeg -hwaccels</c> only says what the binary was compiled against.
    /// Most machines this runs on (including every Linux CI box) are headless,
    /// so the software path has to be the one that works out of the box.
    ///
    /// Mutable on purpose: <see cref="Hwaccel.Resolve"/> narrows "auto" to a
    /// concrete method, and a preflight failure falls back to "off".
    /// </summary>
    public string Hwaccel { get; set; } = "off";
    public int Width { get; init; } = Constants.DefaultWidth;
    public int Height { get; init; } = Constants.DefaultHeight;
    /// <summary>crop (fill, discard the edge), fit (pad) or stretch (distort).</summary>
    public string FitMode { get; init; } = "crop";
    public string CropAnchor { get; init; } = "center";
    public string PadColor { get; init; } = "black";
    public string ScaleAlgo { get; init; } = "neighbor";

    /// <summary>
    /// v7.9.6: the format frames are encoded in. Null = automatic (keep the
    /// source's own format when the encoder accepts it). See
    /// <see cref="Core.PixelFormatUtil.ChooseTransportPixFmt"/>.
    /// </summary>
    public string? PixFmt { get; init; }

    /// <summary>
    /// v7.9.6: colour tags written onto the output, overriding what the source
    /// declared. Null keeps the source's value; these never convert the picture.
    /// </summary>
    public string? ColorRange { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }

    /// <summary>
    /// v7.9.6: swscale dither for low-colour output. "auto" emits no flag.
    /// </summary>
    public string Dither { get; init; } = "auto";

    public string Deinterlace { get; init; } = "off";
    public string DeintMode { get; init; } = "send_frame";
    public string DeintParity { get; init; } = "auto";
    public bool DeintMarkedOnly { get; init; }

    /// <summary>
    /// v7.9.8.3: the NNEDI3 weights file, when <see cref="Deinterlace"/> is
    /// <c>nnedi</c>. Resolved by the caller (the CLI or the shell) so this layer
    /// never has to decide where the file lives; null here plus
    /// <c>nnedi</c> selected is a refusal with instructions, not a silent
    /// fallback to yadif.
    /// </summary>
    public string? NnediWeights { get; init; }
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
    /// v7.9.6.3: an iperf3 server to measure the *link* against, when the user
    /// supplied one. Null means the link is measured by reading the source,
    /// which is the measurement that matters for a share.
    /// </summary>
    public string? Iperf3Host { get; init; }

    /// <summary>
    /// Progress sink for the shard pipeline: called with (slots emitted so far,
    /// target slot count or null) roughly every 0.2s. Only the parallel pipeline
    /// sets it -- a single pass already prints its own status line, and seven
    /// shards talking over each other would be unreadable.
    /// </summary>
    public Action<int, int?>? Progress { get; init; }

    /// <summary>
    /// Audio routing. <see cref="AudioMode.Inline"/> is the default and reproduces
    /// v6.3.3 exactly. <see cref="AudioMode.Separate"/> encodes the track in its own
    /// process and remuxes it afterwards; <see cref="AudioMode.Auto"/> does the same
    /// but falls back to the single-process pipeline when the muxer rejects the split.
    /// </summary>
    public AudioMode AudioMode { get; init; } = AudioMode.Inline;

    /// <summary>
    /// Split the time range into this many shards, encode them concurrently and
    /// join the results. 1 disables it. Only containers in
    /// <see cref="Constants.ConcatSafeFormats"/> are eligible; anything else
    /// silently falls back to a single shard because the join would corrupt it.
    /// </summary>
    public int Parallel { get; init; } = 1;
}
