using System.Globalization;
using Lae.Core.Settings;

namespace Lae.Core;

/// <summary>
/// The two FFmpeg invocations a single-pass encode needs, worked out ahead of
/// time and without touching anything.
///
/// <para>This is the piece v7 never had. Its <c>RealtimeEncoder.Execute</c> was
/// 465 lines that probed, decided, spawned, paced, monitored and cleaned up in
/// one block, so the decisions could only be tested by running a real encode.
/// Planning is a pure function over <see cref="EncodeRequest"/>,
/// <see cref="SourceInfo"/> and the chosen encoder, which means the argument
/// layout can be asserted line by line against v7's -- the whole point of the
/// comparison matrix, moved earlier and made cheap.</para>
///
/// <para>Two commands rather than one: the decoder reads the source and writes
/// raw frames to a pipe, the encoder reads the pipe and paces its output. That
/// split is what makes the run realtime -- the pacing has to happen between the
/// two, not inside one FFmpeg.</para>
/// </summary>
public sealed record EncodePlan
{
    /// <summary>The format frames travel in on the pipe.</summary>
    public required string TransportPixelFormat { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required double Fps { get; init; }

    public required bool HasAudio { get; init; }

    /// <summary>Output container extension, without the dot.</summary>
    public required string Container { get; init; }

    /// <summary>The muxer name for <c>-f</c>.</summary>
    public required string Muxer { get; init; }

    /// <summary>How much of the source this run covers.</summary>
    public required double SpanSeconds { get; init; }

    public required IReadOnlyList<string> DecoderArgs { get; init; }

    public required IReadOnlyList<string> EncoderArgs { get; init; }

    /// <summary>Choices worth telling the user about, e.g. a rounded frame size.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Reasons this plan cannot run. Empty means it can.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool IsRunnable => Problems.Count == 0;

    /// <summary>Bytes per raw frame on the pipe.</summary>
    public int FrameSize => PixelFormatUtil.RawFrameSize(Width, Height, TransportPixelFormat);

    /// <summary>Frames this run should produce; the pacing loop's target.</summary>
    public long TargetFrames => (long)Math.Round(SpanSeconds * Fps);

    public List<string> DecodeCommand(string ffmpeg) => [ffmpeg, .. DecoderArgs];

    public List<string> EncodeCommand(string ffmpeg) => [ffmpeg, .. EncoderArgs];

    /// <summary>
    /// Works out how to encode. Never throws for user input: everything that is
    /// wrong comes back in <see cref="Problems"/>, because a CLI has to report a
    /// bad request as exit 2 with a sentence, not as a stack trace.
    /// </summary>
    public static EncodePlan Create(EncodeRequest request, SourceInfo source, EncoderInfo encoder)
    {
        var problems = new List<string>();
        var notes = new List<string>();

        problems.AddRange(request.Validate());

        // Nothing here is wired yet. Refusing is the honest answer: a run that
        // silently ignored a subtitle request would produce a file that looks
        // finished and is missing the thing the user asked for.
        if (request.Encode.Subtitles.Any)
            problems.Add("subtitles are not wired into the v8 pipeline yet; drop the subtitle options for now");
        if (request.Encode.Recompress.Enabled)
            problems.Add("the recompress chain is not wired into the v8 pipeline yet; drop --recompress for now");

        string? pickProblem = source.CheckPick(request.VideoStream, "video", "--video-stream")
                           ?? source.CheckPick(request.AudioStream, "audio", "--audio-stream");
        if (pickProblem is not null) problems.Add(pickProblem);

        double? span = request.SpanSeconds(source.DurationSeconds);
        if (span is null) problems.Add("the trim range leaves nothing to encode");

        // The transport format is the source's own when the encoder takes it;
        // otherwise the closest thing both can agree on.
        string? requested = PixelFormatCatalog.Normalise(request.Encode.PixFmt);
        string transport = requested is { Length: > 0 }
            ? requested
            : encoder.Caps is { } caps
                ? PixelFormatUtil.ChooseTransportPixFmt(source.PixelFormat, caps)
                : PixelFormatCatalog.Normalise(source.PixelFormat) is { Length: > 0 } own ? own : "yuv420p";

        if (requested is { Length: > 0 } && !requested.Equals(transport, StringComparison.Ordinal))
            notes.Add($"the requested pixel format '{requested}' was not usable; encoding as '{transport}'");

        // Some formats need even, or 4-aligned, dimensions. Rounding here rather
        // than letting FFmpeg round later keeps the frame size the pacing loop
        // counts bytes with identical to the one on the wire.
        (int width, int height, bool exact) =
            PixelFormatUtil.NormalizeTransportDimensions(request.Encode.Width, request.Encode.Height, transport);
        if (!exact)
        {
            notes.Add($"frame size {request.Encode.Width}x{request.Encode.Height} is not legal for "
                    + $"{transport}; using {width}x{height}");
        }

        // RoQ is the one encoder with a hard frame-size rule. Its odd sizes are a
        // warning rather than a refusal -- FFmpeg will encode them, Quake may not
        // play them -- so it goes in the notes where it is loud but not fatal.
        if (encoder.Name == "roqvideo")
        {
            try
            {
                if (EncoderMath.ValidateRoqDimensions(width, height) is { } roqWarning) notes.Add(roqWarning);
            }
            catch (ArgumentException exc)
            {
                problems.Add(exc.Message);
            }
        }

        if (encoder.Name == "amv" && !EncoderMath.UsesWholeFps(encoder.Name))
            problems.Add("internal error: amv must use whole frame rates");

        (string container, string muxer) = ResolveContainer(request.Encode.Container, encoder, problems);

        // Building the chain can refuse: nnedi without its weights, an unknown
        // scale algorithm, a filter this build does not have. Those are requests
        // a user can make, so they belong in Problems -- a planner that throws
        // would hand the CLI an exception where it promised a verdict.
        string filters = "";
        try
        {
            filters = ArgsBuilder.BuildFilters(
                width, height, request.Encode.Fps, request.Encode.FitMode, request.Encode.CropAnchor,
                request.Encode.PadColor, request.Encode.ScaleAlgo, transport,
                request.Encode.Deinterlace.Filter, request.Encode.Deinterlace.FieldMode,
                request.Encode.Deinterlace.Parity, request.Encode.Deinterlace.MarkedOnly,
                request.NnediWeights);
        }
        catch (ArgumentException exc)
        {
            problems.Add(exc.Message);
        }

        VideoFilter filter = VideoFilter.Simple(filters);
        bool hasAudio = source.HasAudio && request.Encode.AudioEncoder.HasText;

        List<string> decoder = BuildDecoderArgs(
            request, source, filter, transport, span, problems);

        List<string> encoderArgsOut = BuildEncoderArgs(
            request, source, encoder, transport, width, height, hasAudio, muxer, problems);

        return new EncodePlan
        {
            TransportPixelFormat = transport,
            Width = width,
            Height = height,
            Fps = request.Encode.Fps,
            HasAudio = hasAudio,
            Container = container,
            Muxer = muxer,
            SpanSeconds = span ?? 0,
            DecoderArgs = decoder,
            EncoderArgs = encoderArgsOut,
            Notes = notes,
            Problems = problems,
        };
    }

    /// <summary>
    /// Which container, and therefore which muxer.
    ///
    /// <para>An explicit request must be one the encoder can actually be written
    /// into; anything else is refused with the legal list. Silently swapping in
    /// the default would produce a file whose extension contradicts its contents,
    /// which is the sort of thing discovered a year later.</para>
    /// </summary>
    private static (string Container, string Muxer) ResolveContainer(
        string? requested, EncoderInfo encoder, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return (encoder.Extension, encoder.FormatName);

        string wanted = requested.Trim().TrimStart('.').ToLowerInvariant();

        if (!Constants.ContainerChoicesByEncoder.TryGetValue(encoder.Name, out var choices))
            return (encoder.Extension, encoder.FormatName);

        foreach ((string ext, string format) in choices)
        {
            if (ext == wanted) return (ext, format);
        }

        problems.Add(
            $"'{encoder.Name}' cannot write '{wanted}'; it can write: "
            + string.Join(", ", choices.Select(c => c.Ext)));

        return (encoder.Extension, encoder.FormatName);
    }

    private static List<string> BuildDecoderArgs(
        EncodeRequest request, SourceInfo source, VideoFilter filter,
        string transport, double? span, List<string> problems)
    {
        EncodeDefaults e = request.Encode;
        var cmd = new List<string> { "-hide_banner", "-loglevel", "error" };

        // Dither belongs to the conversion the decoder performs -- the only place
        // a colour reduction happens -- and it is a global option, so it goes
        // before -i.
        cmd.AddRange(ArgsBuilder.DitherArgs(e.Dither, transport));

        if (!string.Equals(e.Hwaccel, "off", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(e.Hwaccel, "auto", StringComparison.OrdinalIgnoreCase))
                problems.Add("--hwaccel auto needs device probing, which is not wired yet; name a method or use off");
            else
                cmd.AddRange(["-hwaccel", e.Hwaccel]);
        }

        if (request.StartSeconds > 0)
            cmd.AddRange(["-ss", ArgsBuilder.FloatArg(request.StartSeconds)]);

        cmd.AddRange(["-i", request.SourcePath]);

        if (span is not null) cmd.AddRange(["-t", ArgsBuilder.FloatArg(span.Value)]);

        // A filter graph carries its own -map, and -vf with -filter_complex is an
        // argument error rather than a merge.
        if (filter.IsGraph)
        {
            cmd.Add("-an");
            cmd.AddRange(filter.Args());
        }
        else
        {
            cmd.AddRange(["-map", VideoMap(request.VideoStream), "-an"]);
            cmd.AddRange(filter.Args());
        }

        cmd.AddRange(["-pix_fmt", transport, "-f", "rawvideo", "pipe:1"]);
        return cmd;
    }

    private static List<string> BuildEncoderArgs(
        EncodeRequest request, SourceInfo source, EncoderInfo encoder, string transport,
        int width, int height, bool hasAudio, string muxer, List<string> problems)
    {
        EncodeDefaults e = request.Encode;

        var cmd = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-threads", Math.Max(1, e.Threads).ToString(CultureInfo.InvariantCulture),
            "-f", "rawvideo",
            "-pix_fmt", transport,
            "-s:v", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"),
            "-framerate", ArgsBuilder.FpsArg(e.Fps),
            "-i", "pipe:0",
        };

        if (hasAudio)
        {
            // The audio track is read from the source a second time, because the
            // decoder pipe carries video only.
            if (request.StartSeconds > 0)
                cmd.AddRange(["-ss", ArgsBuilder.FloatArg(request.StartSeconds)]);
            cmd.AddRange(["-i", request.SourcePath]);
        }

        // Input 0 is the pipe, which already carries the selected video stream;
        // selecting it again would index the pipe's single stream and match
        // nothing.
        cmd.AddRange(["-map", "0:v:0"]);
        if (hasAudio) cmd.AddRange(["-map", AudioMap(1, request.AudioStream)]);

        cmd.AddRange(["-c:v", encoder.Name]);
        cmd.AddRange(ArgsBuilder.VideoQualityArgs(encoder.Name, e.Quality, e.LimitKbps));

        // Pin the format on the encoder side too when the user named one: the
        // pipe already carries it, but without this the encoder may hand the
        // muxer its own default.
        if (e.PixFmt.HasText) cmd.AddRange(["-pix_fmt", transport]);

        ColorOverride? colors = ColorOverride.Parse(e.Color.Range, e.Color.Space, e.Color.Primaries, e.Color.Transfer);
        if (encoder.Name == "libx264")
        {
            cmd.AddRange(ArgsBuilder.X264Args(e.X264.Preset, e.X264.Tune, e.X264.Profile, e.X264.Level));
            cmd.AddRange(ArgsBuilder.X264ColorArgs(colors));
        }

        cmd.AddRange(["-r", ArgsBuilder.FpsArg(e.Fps), "-fps_mode", "cfr"]);

        StreamInfo stream = new()
        {
            Width = source.Width,
            Height = source.Height,
            PixFmt = source.PixelFormat,
            ColorRange = source.ColorRange,
            ColorSpace = source.ColorSpace,
            ColorPrimaries = source.ColorPrimaries,
            ColorTransfer = source.ColorTransfer,
        };
        cmd.AddRange(ArgsBuilder.ColorArgs(stream, colors));

        if (hasAudio && e.AudioEncoder is { } audioEncoder)
        {
            cmd.AddRange(["-c:a", audioEncoder]);

            // No rate control on these: FFmpeg ignores -b:a, so emitting it only
            // misleads whoever reads the command.
            if (!EncoderMath.AudioIgnoresBitrate(audioEncoder))
                cmd.AddRange(["-b:a", $"{e.AudioKbps}k"]);

            // The AMV muxer wants exactly one ADPCM frame per video frame.
            if (encoder.Name == "amv")
            {
                cmd.AddRange(["-block_size:a",
                    EncoderMath.AmvAudioBlockSize(e.Fps).ToString(CultureInfo.InvariantCulture)]);
            }

            int channels = AudioCatalog.ClampChannels(audioEncoder, e.AudioChannels);
            cmd.AddRange(["-ac", Math.Max(1, channels).ToString(CultureInfo.InvariantCulture)]);

            int rate = e.AudioRate ?? source.AudioRate;
            if (rate > 0) cmd.AddRange(["-ar", rate.ToString(CultureInfo.InvariantCulture)]);
        }

        cmd.AddRange(["-f", muxer, request.DestinationPath]);
        return cmd;
    }

    /// <summary><c>-map 0:v</c> or an absolute stream index, which is what the user types.</summary>
    private static string VideoMap(int? absoluteIndex)
        => absoluteIndex is { } index ? $"0:{index}" : "0:v";

    private static string AudioMap(int input, int? absoluteIndex)
        => absoluteIndex is { } index ? $"{input}:{index}" : $"{input}:a";
}
