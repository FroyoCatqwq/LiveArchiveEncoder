using System.Globalization;
using Lae.Core.Localization;
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

    /// <summary>
    /// Where the encoder writes. It also ends the encoder argument list, because
    /// a dry run exists to be pasted into a shell and tried; keeping the path in
    /// two places is why <see cref="WritingTo"/> exists rather than a caller
    /// editing the list by hand.
    /// </summary>
    public required string DestinationPath { get; init; }

    /// <summary>How much of the source this run covers.</summary>
    public required double SpanSeconds { get; init; }

    /// <summary>
    /// The parameters the plan was made from, carried whole.
    ///
    /// <para>Not a copy of the handful the arguments needed: the run also needs
    /// the bitrate ceiling, the ladder and the window, and those are settings
    /// the argument list never spells. Carrying the record means a field added
    /// to the settings cannot be quietly missing here -- the same reason
    /// <see cref="EncodeRequest"/> carries it instead of flattening it.</para>
    /// </summary>
    public EncodeDefaults Encode { get; init; } = new();

    /// <summary>
    /// The adaptive ladder this run should use, or null when there is nothing to
    /// adapt -- one rung, or no bitrate ceiling asked for.
    /// </summary>
    public FpsLadder? BuildLadder() => LadderPolicy.FromDefaults(Encode);

    public required IReadOnlyList<string> DecoderArgs { get; init; }

    public required IReadOnlyList<string> EncoderArgs { get; init; }

    /// <summary>Choices worth telling the user about, e.g. a rounded frame size.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Subtitle tracks carried into the output as streams of their own. Empty for
    /// every run that does not ask for them, which is every run that burns.
    /// </summary>
    public IReadOnlyList<SubtitleCarry> SubtitleTracks { get; init; } = [];

    public bool HasSubtitleTracks => SubtitleTracks.Count > 0;

    /// <summary>Reasons this plan cannot run. Empty means it can.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool IsRunnable => Problems.Count == 0;

    /// <summary>Bytes per raw frame on the pipe.</summary>
    public int FrameSize => PixelFormatUtil.RawFrameSize(Width, Height, TransportPixelFormat);

    /// <summary>
    /// Frames this run should produce; the pacing loop's target.
    ///
    /// <para>Rounded up, not to nearest. A constant-frame-rate trim of 2.5 s at
    /// 13 fps is ended by the frame that starts at 2.46 s, so the encoder emits
    /// 33 frames; asking the pump for 32 would hand it a file one frame short of
    /// what <c>-t</c> would have produced.</para>
    /// </summary>
    public long TargetFrames => (long)Math.Ceiling(SpanSeconds * Fps - 1e-9);

    public List<string> DecodeCommand(string ffmpeg) => [ffmpeg, .. DecoderArgs];

    public List<string> EncodeCommand(string ffmpeg) => [ffmpeg, .. EncoderArgs];

    /// <summary>
    /// The same plan, writing somewhere else.
    ///
    /// <para>An encode writes to <c>name.part.ext</c> and is renamed only once it
    /// has succeeded, so the run needs a copy of the plan aimed at that sibling.
    /// Rebuilding the plan would redo every decision; replacing the last argument
    /// is exact because the encoder command line ends with its output path by
    /// construction.</para>
    /// </summary>
    public EncodePlan WritingTo(string destination)
    {
        if (EncoderArgs.Count == 0)
            throw new InvalidOperationException("this plan has no encoder arguments to retarget");

        string[] retargeted = [.. EncoderArgs];
        retargeted[^1] = destination;

        return this with { DestinationPath = destination, EncoderArgs = retargeted };
    }

    /// <summary>
    /// The same plan, decoding through a different hardware method.
    ///
    /// <para>Exists for the one decision that can only be made by trying: whether
    /// a hardware decoder works on this machine. The preflight asks, fails, and
    /// rewrites the plan rather than making the caller rebuild every other
    /// decision from the request. <c>off</c> removes the option entirely instead
    /// of writing <c>-hwaccel off</c>, because FFmpeg treats an absent option and
    /// an explicit "off" differently in the argument echo the user reads.</para>
    /// </summary>
    public EncodePlan WithHwaccel(string method)
    {
        List<string> args = [.. DecoderArgs];
        bool off = string.Equals(method, "off", StringComparison.OrdinalIgnoreCase);

        int at = args.IndexOf("-hwaccel");
        if (at >= 0)
        {
            if (off) args.RemoveRange(at, 2);
            else args[at + 1] = method;
        }
        else if (!off)
        {
            // Before -i, which is where a decoder-level option belongs; if there
            // is no -i (a plan cannot exist without one) the front is the sane
            // guess rather than an exception.
            int input = args.IndexOf("-i");
            args.InsertRange(input >= 0 ? input : 0, ["-hwaccel", method]);
        }

        return this with { DecoderArgs = args, Encode = Encode with { Hwaccel = method } };
    }

    /// <summary>Frames an encode has to reach before a trial is worth running.</summary>
    public const int TrialFrames = 3;

    /// <summary>
    /// The same plan, cut down to a few frames and aimed at a throwaway file.
    ///
    /// <para>This is the preflight: run the real command shape over three frames
    /// so a combination FFmpeg refuses -- an encoder that will not take this
    /// pixel format, a muxer that will not hold this codec, a filter this build
    /// does not have -- costs one sentence and a second, instead of a live
    /// capture that dies after it has already recorded nothing. v7 did this with
    /// a hand-built trial command; here the trial is the plan itself, which is
    /// why no second argument builder exists to drift out of step.</para>
    ///
    /// <para>Both sides are capped. The decoder stops after
    /// <see cref="TrialFrames"/> frames, and the encoder's <c>-t</c> is rewritten
    /// to the same span -- without that, a run whose second input carries audio
    /// would sit reading the source for the whole requested duration, because a
    /// video pipe ending early does not end an output that still has an audio
    /// stream to fill.</para>
    /// </summary>
    public EncodePlan ForTrial(string destination, int frames = TrialFrames)
    {
        if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames), frames, "a trial needs frames");

        double span = frames / Fps;

        List<string> decoder = [.. DecoderArgs];
        int raw = PairAt(decoder, "-f", "rawvideo");
        if (raw < 0)
            throw new InvalidOperationException("this plan's decoder does not write rawvideo; cannot trial it");

        decoder.InsertRange(raw, ["-frames:v", frames.ToString(CultureInfo.InvariantCulture)]);

        // Retarget first: WritingTo rewrites the output path at the end of the
        // encoder list, so building the trial's list from the original would put
        // the real destination straight back (found by the test that checks the
        // last argument).
        EncodePlan retargeted = WritingTo(destination);

        List<string> encoder = StripTimedLimit([.. retargeted.EncoderArgs]);
        int format = PairAt(encoder, "-f", Muxer);
        if (format < 0)
            throw new InvalidOperationException("this plan's encoder does not name the muxer; cannot trial it");

        encoder.InsertRange(format, ["-t", ArgsBuilder.FloatArg(span)]);

        return retargeted with
        {
            DecoderArgs = decoder,
            EncoderArgs = encoder,
            SpanSeconds = span,
        };
    }

    /// <summary>Removes every <c>-t</c> pair, so the trial can state its own.</summary>
    private static List<string> StripTimedLimit(List<string> args)
    {
        for (int i = args.Count - 2; i >= 0; i--)
        {
            if (args[i] == "-t") args.RemoveRange(i, 2);
        }

        return args;
    }

    /// <summary>Index of an option and its value, or -1.</summary>
    private static int PairAt(List<string> args, string name, string value)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == name && args[i + 1] == value) return i;
        }

        return -1;
    }

    /// <summary>
    /// Works out how to encode. Never throws for user input: everything that is
    /// wrong comes back in <see cref="Problems"/>, because a CLI has to report a
    /// bad request as exit 2 with a sentence, not as a stack trace.
    ///
    /// <para><paramref name="messages"/> is the language those sentences come back
    /// in. A parameter rather than a field on the request, because the same request
    /// has to be able to answer in either language within one process -- which is
    /// what the tests and <c>lae repl</c> both do -- and resolving here rather than
    /// at the printing end is what keeps <see cref="Problems"/> a list of finished
    /// sentences for every consumer: the report, the history row, the note line.</para>
    /// </summary>
    public static EncodePlan Create(
        EncodeRequest request, SourceInfo source, EncoderInfo encoder, IMessages messages)
    {
        var problems = new List<string>();
        var notes = new List<string>();

        // The container comes first, because it decides the output's extension
        // when the caller did not name a path. Resolving it here rather than in
        // the CLI is what stops an MP4 being written into a file called
        // clip.avi: only the plan knows which encoder was found in this FFmpeg
        // build, and therefore which container is actually in use.
        (string container, string muxer) = ResolveContainer(request.Encode.Container, encoder, problems, messages);
        string destination = request.DestinationPath ?? EncodeRequest.DeriveDestination(request.SourcePath, container);

        // A named output has to agree with the container the plan settled on.
        //
        // <para>This is the same failure <see cref="ResolveContainer"/> refuses one
        // paragraph up, arriving through the other door: `--encoder flv --out
        // clip.avi` wrote an FLV stream into a file called .avi, and the run
        // reported success. Some players sniff and cope, some trust the extension
        // and refuse, and either way the archive has a file that lies about
        // itself. Found by running the built binary against a real source and
        // reading the command line it printed.</para>
        if (request.DestinationPath is { } named)
        {
            string extension = Path.GetExtension(named).TrimStart('.').ToLowerInvariant();
            if (extension.Length > 0 && !SameFormatFamily(container, extension))
            {
                problems.Add(messages.Format("plan.error.extensionMismatch", new
                {
                    name = Path.GetFileName(named),
                    given = extension,
                    encoder = encoder.Name,
                    container,
                }));
            }
        }

        problems.AddRange(request.Validate(destination, messages));

        // Carried subtitle tracks need the container before they can be checked:
        // Matroska keeps a PGS track as it is, MP4 can only hold text, and an AVI
        // has no subtitle muxer at all. Deciding that here -- rather than letting
        // the muxer refuse two seconds into the run -- is the whole point of
        // planning before spawning.
        IReadOnlyList<SubtitleCarry> carries = [];
        if (request.Encode.Subtitles.Mode == "copy" && request.Encode.Subtitles.CopyTracks.Count > 0)
        {
            (IReadOnlyList<SubtitleCarry> resolved, IReadOnlyList<string> carryProblems) =
                SubtitleCopy.Resolve(container, source, request.Encode.Subtitles.CopyTracks, messages);

            problems.AddRange(carryProblems);
            carries = resolved;

            if (resolved.Count > 0)
            {
                notes.Add(messages.Format(
                    resolved.Count == 1 ? "plan.note.carryOne" : "plan.note.carryMany", new
                    {
                        tracks = string.Join(", ", resolved.Select(t => t.Index)),
                        codec = resolved[0].Codec,
                        container,
                    }));
            }
        }

        // Subtitles are burned on the decoder side, into the frames that go down
        // the pipe: the encoder then needs no knowledge of them, and a run with
        // captions differs from one without only in what the frames contain.
        // Whatever cannot be honoured -- an embedded track, a copy instead of a
        // burn -- comes back as a problem rather than a silently plainer file.
        SubtitleBurn burn = SubtitleChain.Build(
            request.Encode.Subtitles, Math.Max(0, request.StartSeconds), request.FontsDirectory, messages);
        problems.AddRange(burn.Problems);
        notes.AddRange(burn.Notes);

        // The recompress chain is not part of this plan: it runs after the
        // recording exists, reads it, and never touches it. It is planned by
        // RecompressPlan against this plan's destination, and it is checked
        // before anything is spawned so a bad chain costs a sentence rather than
        // a full-length capture followed by a refusal.

        string? pickProblem = source.CheckPick(request.VideoStream, "video", "--video-stream", messages)
                           ?? source.CheckPick(request.AudioStream, "audio", "--audio-stream", messages);
        if (pickProblem is not null) problems.Add(pickProblem);

        double? span = request.SpanSeconds(source.DurationSeconds);
        if (span is null)
        {
            // Two different situations, two different sentences. "Leaves nothing
            // to encode" is right for --from 10 --to 5 and useless for a source
            // whose container reports no duration at all, which is the case the
            // user cannot fix by adjusting the trim.
            problems.Add(messages[source.DurationSeconds > 0
                ? "plan.error.trimEmpty"
                : "plan.error.noDuration"]);
        }

        // The transport format is the source's own when the encoder takes it;
        // otherwise the closest thing both can agree on.
        string? requested = PixelFormatCatalog.Normalise(request.Encode.PixFmt);
        string transport;
        try
        {
            transport = requested is { Length: > 0 }
                ? requested
                : encoder.Caps is { } caps
                    ? PixelFormatUtil.ChooseTransportPixFmt(source.PixelFormat, caps)
                    : PixelFormatCatalog.Normalise(source.PixelFormat) is { Length: > 0 } own ? own : "yuv420p";
        }
        catch (RefusalException exc)
        {
            // A refusal from one of the argument builders: it carries a key, so it
            // comes back in the caller's language instead of as the English the
            // sentence was written in.
            problems.Add(exc.Resolve(messages));
            transport = "yuv420p";
        }
        catch (ArgumentException exc)
        {
            // An encoder that lists pixel formats none of which this build has ever
            // seen -- an old build, an exotic codec -- reaches here. Collecting it
            // rather than letting it fly keeps this method's promise: a bad request
            // exits 2 with a sentence, not 1 with a stack trace.
            problems.Add(exc.Message);
            transport = "yuv420p";
        }

        if (requested is { Length: > 0 } && !requested.Equals(transport, StringComparison.Ordinal))
        {
            notes.Add(messages.Format(
                "plan.note.pixFmtFallback", new { requested, transport }));
        }

        // Some formats need even, or 4-aligned, dimensions. Rounding here rather
        // than letting FFmpeg round later keeps the frame size the pacing loop
        // counts bytes with identical to the one on the wire.
        (int width, int height, bool exact) =
            PixelFormatUtil.NormalizeTransportDimensions(request.Encode.Width, request.Encode.Height, transport);
        if (!exact)
        {
            notes.Add(messages.Format("plan.note.sizeRounded", new
            {
                width = request.Encode.Width,
                height = request.Encode.Height,
                transport,
                usedWidth = width,
                usedHeight = height,
            }));
        }

        // RoQ is the one encoder with a hard frame-size rule. Its odd sizes are a
        // warning rather than a refusal -- FFmpeg will encode them, Quake may not
        // play them -- so it goes in the notes where it is loud but not fatal.
        if (encoder.Name == "roqvideo")
        {
            try
            {
                if (EncoderMath.ValidateRoqDimensions(width, height, messages) is { } roqWarning)
                    notes.Add(roqWarning);
            }
            catch (RefusalException exc)
            {
                problems.Add(exc.Resolve(messages));
            }
        }

        if (encoder.Name == "amv" && !EncoderMath.UsesWholeFps(encoder.Name))
            problems.Add(messages["plan.error.amvWholeFps"]);

        // The ladder climbs back below the recovery point, so a recovery rate at
        // or above the ceiling leaves it no gap to sit in: it would drop a rung,
        // find itself under the rise threshold, and climb straight back for the
        // rest of the run. Clamping is better than refusing -- the encode is
        // still worth doing -- but it must be said out loud, because the number
        // the user typed is not the number in use.
        if (request.Encode.LimitKbps > 0 && request.Encode.RecoverKbps >= request.Encode.LimitKbps)
        {
            notes.Add(messages.Format("plan.note.recoverClamped", new
            {
                recover = ArgsBuilder.FloatArg(request.Encode.RecoverKbps),
                limit = ArgsBuilder.FloatArg(request.Encode.LimitKbps),
                used = ArgsBuilder.FloatArg(LadderPolicy.RecoverKbps(request.Encode)),
            }));
        }

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
        catch (RefusalException exc)
        {
            problems.Add(exc.Resolve(messages));
        }
        catch (ArgumentException exc)
        {
            problems.Add(exc.Message);
        }

        VideoFilter filter = SubtitleChain.Apply(
            filters, burn, VideoMap(request.VideoStream), width, height, transport);
        bool hasAudio = source.HasAudio && request.Encode.AudioEncoder.HasText;

        List<string> decoder = BuildDecoderArgs(
            request, filter, transport, span);

        List<string> encoderArgsOut = BuildEncoderArgs(
            request, source, encoder, transport, width, height, hasAudio, carries, muxer, span ?? 0, destination, problems);

        return new EncodePlan
        {
            TransportPixelFormat = transport,
            Width = width,
            Height = height,
            Fps = request.Encode.Fps,
            HasAudio = hasAudio,
            Container = container,
            Muxer = muxer,
            DestinationPath = destination,
            SpanSeconds = span ?? 0,
            Encode = request.Encode,
            DecoderArgs = decoder,
            EncoderArgs = encoderArgsOut,
            Notes = notes,
            Problems = problems,
            SubtitleTracks = carries,
        };
    }

    /// <summary>
    /// Whether a file name's extension is the same format as the container the plan
    /// settled on.
    ///
    /// <para>Not string equality: <c>mp4</c> and <c>m4v</c> are one muxer, and so are
    /// <c>mov</c>/<c>qt</c>, <c>mkv</c> (whose format is called matroska) and
    /// <c>mpg</c>/<c>mpeg</c>/<c>vob</c>. Comparing the two names directly refused
    /// <c>--out clip.m4v</c> with the sentence "libx264 writes mp4; rename it to
    /// .mp4" -- advice that contradicts the program's own table of legal
    /// extensions, and that a user cannot be expected to argue with.</para>
    /// </summary>
    private static bool SameFormatFamily(string container, string extension)
    {
        string left = "." + container;
        string right = "." + extension;
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;

        foreach (IReadOnlySet<string> family in Constants.FormatExtensions.Values)
        {
            if (family.Contains(left) && family.Contains(right)) return true;
        }

        return false;
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
        string? requested, EncoderInfo encoder, List<string> problems, IMessages messages)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return (encoder.Extension, encoder.FormatName);

        string wanted = requested.Trim().TrimStart('.').ToLowerInvariant();

        if (!Constants.ContainerChoicesByEncoder.TryGetValue(encoder.Name, out var choices))
        {
            // An encoder with no table entry writes exactly one container -- RV10
            // only in .rm, RoQ only in .roq, QuickTime's own codecs only in .mov.
            // Returning its default silently would mean `--container avi` produced
            // a .mov and said nothing, which is the failure this whole method is
            // written against; the request still loses, but it loses out loud.
            if (!string.Equals(wanted, encoder.Extension, StringComparison.Ordinal))
            {
                problems.Add(messages.Format("plan.error.containerOnlyOne", new
                {
                    encoder = encoder.Name,
                    extension = encoder.Extension,
                    wanted,
                }));
            }

            return (encoder.Extension, encoder.FormatName);
        }

        foreach ((string ext, string format) in choices)
        {
            if (ext == wanted) return (ext, format);
        }

        problems.Add(messages.Format("plan.error.containerChoices", new
        {
            encoder = encoder.Name,
            wanted,
            choices = string.Join(", ", choices.Select(c => c.Ext)),
        }));

        return (encoder.Extension, encoder.FormatName);
    }

    private static List<string> BuildDecoderArgs(
        EncodeRequest request, VideoFilter filter,
        string transport, double? span)
    {
        EncodeDefaults e = request.Encode;
        var cmd = new List<string> { "-hide_banner", "-loglevel", "error" };

        // Dither belongs to the conversion the decoder performs -- the only place
        // a colour reduction happens -- and it is a global option, so it goes
        // before -i.
        cmd.AddRange(ArgsBuilder.DitherArgs(e.Dither, transport));

        // "auto" is passed through rather than refused: FFmpeg already means "use
        // a hardware decoder if this machine has one, otherwise read in software"
        // by it, and the preflight is what turns that into a decided answer --
        // it runs three frames with the option, and falls back to "off" when the
        // hardware path fails. Refusing here would have made the user supply a
        // method name they have no way of knowing.
        if (!string.Equals(e.Hwaccel, "off", StringComparison.OrdinalIgnoreCase))
            cmd.AddRange(["-hwaccel", e.Hwaccel]);

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
        int width, int height, bool hasAudio, IReadOnlyList<SubtitleCarry> carries,
        string muxer, double span, string destination,
        List<string> problems)
    {
        EncodeDefaults e = request.Encode;

        // One second input serves both jobs. The decoder pipe carries video only,
        // so audio and any carried subtitle track are read from the source again --
        // and a run that carries subtitles needs that input whether or not there
        // is an audio track.
        bool secondInput = hasAudio || carries.Count > 0;

        var cmd = new List<string>
        {
            "-hide_banner", "-loglevel", "error",

            // Overwrite without asking. The output is our own part file, so a
            // question never has a useful answer -- and without this, one crash
            // that leaves a part file behind makes every later run of the same
            // name fail with an "already exists" complaint from inside FFmpeg,
            // reported as a mysterious exit code -17. Found by running twice on a
            // build server where an earlier attempt had been killed from outside.
            "-y",

            // The rate ladder steers by what the muxer has actually written, and
            // this is where that number comes from. It goes on stdout, which the
            // encoder has free -- frames arrive on stdin and leave through the
            // file -- and it is a global option, so it belongs before the inputs.
            "-progress", "pipe:1",

            "-threads", Math.Max(1, e.Threads).ToString(CultureInfo.InvariantCulture),
            "-f", "rawvideo",
            "-pix_fmt", transport,
            "-s:v", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"),
            "-framerate", ArgsBuilder.FpsArg(e.Fps),
            "-i", "pipe:0",
        };

        if (secondInput)
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

        // Carried subtitle tracks come out of the same second input, by absolute
        // stream index. One -c:s covers them all: SubtitleCopy refuses a set that
        // would need two, so the shared value is the honest answer.
        foreach (SubtitleCarry track in carries)
            cmd.AddRange(["-map", $"{1}:{track.Index}"]);
        if (carries.Count > 0) cmd.AddRange(["-c:s", carries[0].Codec]);

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

        // Cap the output at the span the run covers.
        //
        // The video side stops on its own: the pump stops writing and the pipe
        // ends. The second input does not -- it is a read of the source that
        // started at -ss and would otherwise run to the end of the file, so a
        // 1 s excerpt came out as a 3 s file whose picture was 1 s of it and
        // whose container duration followed the second input. Found by the
        // end-to-end comparison of a trimmed run against its own source, not by
        // reading it.
        if (secondInput && span > 0) cmd.AddRange(["-t", ArgsBuilder.FloatArg(span)]);

        // Write every packet out as it is produced.
        //
        // A muxer is allowed to hold packets and write them in a lump; the AVI
        // muxer does exactly that, up to a couple of seconds at a time. That is
        // invisible in the finished file and fatal to the ladder, which reads
        // `-progress`'s total_size: the counter sits still for a second, reports
        // 0 kbps, then jumps and reports 1000. Measured on a 14 s clip: the
        // alternation drove the frame rate up and down four times.
        cmd.AddRange(["-flush_packets", "1"]);

        cmd.AddRange(["-f", muxer, destination]);
        return cmd;
    }

    /// <summary><c>-map 0:v</c> or an absolute stream index, which is what the user types.</summary>
    private static string VideoMap(int? absoluteIndex)
        => absoluteIndex is { } index ? $"0:{index}" : "0:v";

    private static string AudioMap(int input, int? absoluteIndex)
        => absoluteIndex is { } index ? $"{input}:{index}" : $"{input}:a";
}
