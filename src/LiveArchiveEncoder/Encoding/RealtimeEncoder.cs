using System.Diagnostics;
using System.Globalization;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Frames;
using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.RateControl;

namespace LiveArchiveEncoder.Encoding;

/// <summary>
/// The realtime adaptive encoder shared by CUI and CLI.
///
/// The wall-clock pacing of the original Python controller is preserved: the
/// capture thread feeds frames at the source cadence and the main loop emits one
/// output slot every 1/fps seconds. The optimisations here target allocation
/// count, pipe throughput and timer precision, not the pacing semantics.
/// </summary>
public static class RealtimeEncoder
{    /// <summary>Mutable flags shared with the capture thread (a local cannot be volatile).</summary>
    private sealed class CaptureState
    {
        public volatile bool Stop;
        public volatile bool Done;
    }

    /// <summary>
    /// Entry point. Dispatches to the sharded pipeline when the caller asked for
    /// more than one shard and the container can survive the join; otherwise this
    /// is a plain single pass.
    /// </summary>
    public static EncodeResult Run(RealtimeOptions o, CancellationToken cancellationToken = default)
        => o.Parallel > 1 ? ParallelEncoder.Run(o, cancellationToken) : RunSingle(o, cancellationToken);

    /// <summary>Single-pass encode: one decoder process, one encoder process.</summary>
    public static EncodeResult RunSingle(RealtimeOptions o, CancellationToken cancellationToken = default)
    {
        var dst = new FileInfo(Path.GetFullPath(o.DestinationPath));

        // v7.9: an HTTP(S) source has no FileInfo and no existence check -- it is
        // the probe below that decides whether the URL is any good.
        bool urlSource = Remote.HttpSource.IsUrl(o.SourcePath);
        var src = urlSource ? null : new FileInfo(Path.GetFullPath(o.SourcePath));

        // "Input not found" is the wrong message for an SMB source that simply
        // is not reachable as a path yet -- tell the user how to make it one.
        if (src is not null && !src.Exists)
        {
            string? hint = Remote.Smb.UnreachableHint(o.SourcePath);
            throw new FileNotFoundException(hint is null
                ? $"Input not found: {src.FullName}"
                : $"Input not found: {src.FullName}\n{hint}");
        }
        if (!urlSource && string.Equals(src!.FullName, dst.FullName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must differ from input");

        // One string for "where the frames come from": the resolved local path,
        // or the URL itself. Every consumer below (probe, decoder, audio track)
        // used to read src.FullName.
        string sourcePath = src?.FullName ?? o.SourcePath;

        Validation.ValidateOutputExtension(dst, o.Encoder);

        string? roqDimensionWarning = o.Encoder.Name == "roqvideo"
            ? EncoderMath.ValidateRoqDimensions(o.Width, o.Height)
            : null;

        (int? audioRate, int audioChannels, int audioKbps) = Validation.EffectiveAudioSettings(
            o.Encoder, o.AudioEncoder, o.AudioRate, o.AudioChannels, o.AudioKbps);

        var config = new Config
        {
            // A URL source has no local path to remember in the config.
            InputPath = urlSource ? null : src,
            OutputPath = dst,
            StartTimeSeconds = o.StartSeconds,
            EndTimeSeconds = o.EndSeconds,
            Encoder = o.Encoder,
            AudioEncoder = o.AudioEncoder,
            Fps = o.Fps,
            FpsLevels = o.FpsLevels,
            Limit = o.LimitKbps,
            Recover = o.RecoverKbps,
            Window = o.WindowSeconds,
            Qv = o.Quality,
            Threads = o.Threads,
            BufferFrames = o.BufferFrames,
            AudioKbps = audioKbps,
            AudioChannels = audioChannels,
            AudioRate = audioRate,
            Width = o.Width,
            Height = o.Height,
            FitMode = o.FitMode,
            CropAnchor = o.CropAnchor,
            PadColor = o.PadColor,
            ScaleAlgo = o.ScaleAlgo,
            Deinterlace = o.Deinterlace,
            DeintMode = o.DeintMode,
            DeintParity = o.DeintParity,
            DeintMarkedOnly = o.DeintMarkedOnly,
            PixFmt = o.PixFmt,
            ColorRange = o.ColorRange,
            ColorSpace = o.ColorSpace,
            ColorPrimaries = o.ColorPrimaries,
            ColorTransfer = o.ColorTransfer,
            Dither = o.Dither,
            Hwaccel = o.Hwaccel,
            X264Preset = string.IsNullOrEmpty(o.X264Preset) ? "medium" : o.X264Preset,
            X264Tune = o.X264Tune ?? "",
            X264Profile = o.X264Profile ?? "",
            X264Level = o.X264Level ?? "",
        };
        Validation.ValidateCommonConfig(config);

        // One ffprobe run instead of three (stream / duration / audio presence).
        // v7.9: the stream picks are validated here, against this same probe,
        // and Video metadata is re-pointed at the selected video stream.
        SourceInfo source = StreamSelection.Resolve(
            Probe.Source(o.FFprobe, sourcePath), o.VideoStream, o.AudioStream);
        StreamInfo stream = source.Video;
        (double? resolvedEnd, double? duration) =
            Timecode.ResolveRange(o.StartSeconds, o.EndSeconds, source.Duration);
        bool hasAudio = source.HasAudio
            && o.AudioEncoder is not null
            && !Constants.NoAudioFormats.Contains(o.Encoder.FormatName);

        // The AMV container literally only holds two streams. Without audio the
        // muxer aborts with "AMV files only support 2 streams" after the whole
        // encode has already run, so fail up front with something actionable.
        if (!hasAudio && Constants.FormatsRequiringAudio.Contains(o.Encoder.FormatName))
        {
            throw new ArgumentException(
                $"encoder '{o.Encoder.Name}' writes the '{o.Encoder.FormatName}' container, which "
                + "requires exactly one video and one audio stream, but this source has no usable "
                + "audio track (or audio was disabled). Choose a different encoder, or pick a "
                + "source with audio.");
        }

        // v7.9.5: the profile is cached per FFmpeg binary, so a direct call to
        // the encoder (tests, API, a future front end) pays the same one-time
        // cost the CLI and the shell do instead of probing per run.
        FFmpeg.Capabilities profile = FFmpeg.BuildProfileStore.For(o.FFmpeg);
        o.Encoder.Caps ??= profile.EncoderCaps(o.Encoder);
        EncoderCaps caps = o.Encoder.Caps;

        HashSet<string> availableFilters = o.AvailableFilters ?? profile.Filters;
        HashSet<string> availableScaleAlgorithms =
            o.AvailableScaleAlgorithms ?? profile.ScaleAlgorithms;

        if (!availableFilters.Contains("scale"))
            throw new ArgumentException("Selected FFmpeg build does not provide the scale filter");
        if (o.Deinterlace != "off" && !availableFilters.Contains(o.Deinterlace))
            throw new ArgumentException($"FFmpeg filter '{o.Deinterlace}' is not available in this build");

        string scaleAlgo = EncoderMath.CanonicalScaleAlgo(o.ScaleAlgo);
        if (!availableScaleAlgorithms.Contains(scaleAlgo))
            throw new ArgumentException($"Scale algorithm '{scaleAlgo}' is not exposed by this FFmpeg build");

        // v7.9.6: an explicit --pix-fmt is honoured or refused here, before any
        // process starts. The automatic choice is unchanged.
        string transportFmt = PixelFormatUtil.ChooseTransportPixFmt(stream.PixFmt, caps, o.PixFmt);
        // A palette format cannot survive a container that stores the codec
        // header without it -- measured on msrle: YAVG 123 in AVI, YAVG 16 in
        // Matroska and ASF (2026-10-23). v7.9.4 checked this only inside the
        // shard planner, so a plain single pass still produced a black file.
        if (PixelFormatUtil.PaletteContainerConflict(transportFmt, o.Encoder.FormatName) is { } paletteConflict)
            throw new ArgumentException(paletteConflict);

        (int width, int height, bool sizeExact) =
            EncoderMath.NormalizeDimensions(o.Encoder.Name, o.Width, o.Height);
        (width, height, bool transportExact) =
            PixelFormatUtil.NormalizeTransportDimensions(width, height, transportFmt);
        if (o.Encoder.Name == "roqvideo")
            roqDimensionWarning = EncoderMath.ValidateRoqDimensions(width, height);

        if (o.Verbose)
        {
            if (!sizeExact)
                Console.WriteLine(L.T("note.sizeadjusted", o.Encoder.Name, width, height));
            else if (!transportExact)
                Console.WriteLine(L.T("note.transportadjusted", transportFmt, width, height));
            if (roqDimensionWarning is not null) Console.WriteLine($"WARNING: {roqDimensionWarning}");
        }

        string vf = ArgsBuilder.BuildFilters(
            width, height, o.Fps, o.FitMode, o.CropAnchor, o.PadColor, scaleAlgo,
            transportFmt, o.Deinterlace, o.DeintMode, o.DeintParity, o.DeintMarkedOnly);

        // v7.9 #2: subtitles are validated here, before anything expensive, and
        // rendered last so the text is neither scaled nor cropped with the
        // picture. The plan owns a staging directory when it extracts a track.
        SubtitlePlan? subs = o.Subtitles?.Active == true
            ? SubtitleTrack.Plan(
                source, o.Subtitles!, o.Encoder.FormatName, availableFilters,
                o.AvailableSubtitleEncoders ?? profile.SubtitleEncoders,
                o.FFmpeg, sourcePath)
            : null;
        if (subs is { Mode: SubtitleMode.Burn })
            vf = SubtitleTrack.AppendBurn(vf, subs.BurnFiles, subs.FontDir, subs.FallbackFont);
        int frameSize = PixelFormatUtil.RawFrameSize(width, height, transportFmt);
        int? targetSlots = duration is null ? null : (int)Math.Ceiling(duration.Value * o.Fps - 1e-9);
        string muxer = o.Encoder.FormatName;
        dst.Directory?.Create();

        // Writing to a share that turns out to be read-only wastes the whole
        // real-time encode before it fails, so check once, up front.
        if (dst.Directory is { Exists: true } outDir && !Remote.Smb.CanWriteDirectory(outDir.FullName))
        {
            throw new UnauthorizedAccessException(
                $"cannot write to {outDir.FullName}; check the share permissions");
        }

        // One trial for the whole run: video, filters, muxer and -- when the
        // source has audio -- the audio encoder on the same command line.
        // v7.9.5: this used to be two processes racing each other, and the
        // audio one only ever proved that `-c:a` opens.
        void RunVideoPreflight(string hwaccel)
        {
            Preflight.Video(
                ffmpeg: o.FFmpeg, sourcePath: sourcePath, encoder: o.Encoder, fps: o.Fps, quality: o.Quality,
                threads: o.Threads, vf: vf, muxer: muxer,
                stream: stream,
                videoStream: o.VideoStream,
                x264Preset: o.Encoder.Name == "libx264" ? o.X264Preset : null,
                x264Tune: o.Encoder.Name == "libx264" ? o.X264Tune : null,
                x264Profile: o.Encoder.Name == "libx264" ? o.X264Profile : null,
                x264Level: o.Encoder.Name == "libx264" ? o.X264Level : null,
                hwaccel: hwaccel, startSeconds: o.StartSeconds,
                audio: hasAudio && o.AudioEncoder is not null
                    ? new AudioTrial(
                        o.AudioEncoder, audioChannels, audioRate,
                        o.Encoder.Name == "amv" ? EncoderMath.AmvAudioBlockSize(o.Fps) : 0,
                        o.AudioKbps)
                    : null,
                pixFmt: o.PixFmt.HasText ? transportFmt : null,
                colors: ColorOverride.Parse(o.ColorRange, o.ColorSpace, o.ColorPrimaries, o.ColorTransfer),
                dither: o.Dither,
                limitKbps: o.LimitKbps,
                transportFmt: transportFmt);
        }

        // A hardware method is only ever used after it has decoded a frame here,
        // so "auto" has already been narrowed to something real by this point.
        // The one thing a probe cannot predict is the encoder/muxer combination
        // of the actual run, so the preflight still gets a software retry: the
        // retry only ever happens in the case that would otherwise have thrown.
        Action<string>? say = o.Verbose ? Console.WriteLine : null;
        string hwaccel = FFmpeg.Hwaccel.Resolve(o.Hwaccel, o.FFmpeg, sourcePath, say);
        o.Hwaccel = hwaccel;

        try
        {
            if (string.Equals(hwaccel, FFmpeg.Hwaccel.Off, StringComparison.OrdinalIgnoreCase))
            {
                RunVideoPreflight(FFmpeg.Hwaccel.Off);
            }
            else
            {
                try
                {
                    RunVideoPreflight(hwaccel);
                }
                catch (InvalidOperationException)
                {
                    o.Hwaccel = FFmpeg.Hwaccel.Off;
                    say?.Invoke(L.T("note.hwaccelfallback", hwaccel));
                    RunVideoPreflight(FFmpeg.Hwaccel.Off);
                }
            }
        }
        catch
        {
            // A staged embedded track has to die here: the caller's finally only
            // runs once Execute has started, and a refused encoder/muxer pair is
            // exactly the case that never gets that far.
            subs?.Dispose();
            throw;
        }
        List<string> decCmd = BuildDecoderCommand(o, sourcePath, vf, transportFmt, duration);
        // The encoder writes a ".part" sibling; Promote puts it in place only
        // once the run has actually succeeded.
        FileInfo part = Core.OutputFile.PartPath(dst);
        List<string> encCmd = BuildEncoderCommand(
            o, part, transportFmt, width, height, hasAudio, audioRate, audioChannels, duration, stream, muxer, subs);

        if (o.Verbose)
        {
            Console.WriteLine($"{L.T("run.encodercommand"),-17}: {o.Encoder.Name} -> {muxer} -> {dst.FullName}");
            Console.WriteLine($"{L.T("run.sourcepix"),-17}: {stream.PixFmt}");
            Console.WriteLine($"{L.T("run.pipepix"),-17}: {transportFmt}"
                + (o.PixFmt.HasText ? $" ({L.T("value.requested")})" : ""));
            if (ArgsBuilder.DitherArgs(o.Dither, transportFmt).Count > 0)
                Console.WriteLine($"{L.T("run.dither"),-17}: {o.Dither}");
            ColorOverride colors = ColorOverride.Parse(
                o.ColorRange, o.ColorSpace, o.ColorPrimaries, o.ColorTransfer);
            if (colors.Any)
            {
                Console.WriteLine($"{L.T("run.color"),-17}: {string.Join(" ", ArgsBuilder.ColorArgs(stream, colors))}"
                    + (o.Encoder.Name == "libx264"
                        ? $" {string.Join(" ", ArgsBuilder.X264ColorArgs(colors))}"
                        : ""));
            }
            Console.WriteLine($"{L.T("run.outputsize"),-17}: {width}x{height}");
            Console.WriteLine(
                $"{L.T("run.timerange"),-17}: {Timecode.Format(o.StartSeconds)} - {Timecode.Format(resolvedEnd)}"
                + (duration is not null ? $" ({duration.Value:F3}s)" : ""));
            Console.WriteLine($"{L.T("run.filters"),-17}: {vf}");

            string? smb = Remote.Smb.Describe(sourcePath);
            if (smb is not null) Console.WriteLine(smb);
            string? smbOut = Remote.Smb.DescribeOutput(dst.FullName);
            if (smbOut is not null) Console.WriteLine(smbOut);
        }

        // An embedded track was staged to a temp directory for the filter; it
        // goes away whether the encode worked or not, exactly like the part file.
        try
        {
            EncodeResult result = Execute(
                o, part, decCmd, encCmd, frameSize, targetSlots, duration, hasAudio,
                stream.PixFmt, transportFmt, width, height, scaleAlgo, resolvedEnd, cancellationToken);
            Core.OutputFile.Promote(part, dst);
            return result;
        }
        catch
        {
            Core.OutputFile.Discard(part);
            throw;
        }
        finally
        {
            subs?.Dispose();
        }
    }

    /// <summary>
    /// Entry point. With <see cref="AudioMode.Separate"/> the video is encoded
    /// alone into a staging file while the audio track is encoded in parallel by a
    /// second FFmpeg process; the two are then joined with <c>-c copy</c>.
    /// Any failure falls back to the original single-process pipeline so behaviour
    /// never regresses relative to v6.3.3.
    /// </summary>
    public static EncodeResult RunSplit(RealtimeOptions o, CancellationToken cancellationToken = default)
    {
        if (o.AudioMode == AudioMode.Inline) return Run(o, cancellationToken);

        // Probe once with a throwaway pass over the source to know whether audio
        // is even in play before committing to the split pipeline.
        try
        {
            return RunSplitCore(o, cancellationToken);
        }
        catch (Exception exc) when (exc is InvalidOperationException or IOException
                                        or TimeoutException or ArgumentException)
        {
            if (o.AudioMode == AudioMode.Separate) throw;
            if (o.Verbose)
            {
                Console.WriteLine(L.T("note.splitunavailable", exc.Message));
            }
            return Run(o, cancellationToken);
        }
    }

    private static EncodeResult RunSplitCore(RealtimeOptions o, CancellationToken cancellationToken)
    {
        var dst = new FileInfo(Path.GetFullPath(o.DestinationPath));

        // v7.9: an HTTP(S) source has no FileInfo and no existence check -- it is
        // the probe below that decides whether the URL is any good.
        bool urlSource = Remote.HttpSource.IsUrl(o.SourcePath);
        var src = urlSource ? null : new FileInfo(Path.GetFullPath(o.SourcePath));
        string sourcePath = src?.FullName ?? o.SourcePath;

        // "Input not found" is the wrong message for an SMB source that simply
        // is not reachable as a path yet -- tell the user how to make it one.
        if (src is not null && !src.Exists)
        {
            string? hint = Remote.Smb.UnreachableHint(o.SourcePath);
            throw new FileNotFoundException(hint is null
                ? $"Input not found: {src.FullName}"
                : $"Input not found: {src.FullName}\n{hint}");
        }

        // Reuse the inline preparation to resolve every derived parameter
        // (transport format, size, filters, duration, audio settings) identically.
        // v7.9: stream picks are validated against this probe too.
        SourceInfo source = StreamSelection.Resolve(
            Probe.Source(o.FFprobe, sourcePath), o.VideoStream, o.AudioStream);
        (_, double? duration) = Timecode.ResolveRange(o.StartSeconds, o.EndSeconds, source.Duration);
        bool hasAudio = source.HasAudio
            && o.AudioEncoder is not null
            && !Constants.NoAudioFormats.Contains(o.Encoder.FormatName);

        if (!hasAudio || duration is null) return Run(o, cancellationToken);

        // v7.9 #2: the split pipeline rejoins video and audio with -c copy and
        // would drop a carried subtitle track, so that combination runs inline.
        // Burned subtitles are already in the picture and survive the split.
        if (o.Subtitles is { Active: true, Mode: SubtitleMode.Copy })
        {
            if (o.Verbose)
                Console.WriteLine(L.T("note.subtitleinline"));
            return Run(o, cancellationToken);
        }

        (int? audioRate, int audioChannels, int audioKbps) = Validation.EffectiveAudioSettings(
            o.Encoder, o.AudioEncoder, o.AudioRate, o.AudioChannels, o.AudioKbps);

        // Probe muxability with a 3-frame synthetic clip before spending the whole
        // realtime video pass on it. Legacy muxers can accept the encode and still
        // drop the copied video stream, which is only discoverable by muxing.
        if (!CanSplitMux(o, audioEncoder: o.AudioEncoder!, audioChannels, audioRate, out string muxReason))
        {
            if (o.Verbose)
                Console.WriteLine(L.T("note.splitmux", muxReason));
            return Run(o, cancellationToken);
        }

        string stageDir = Path.Combine(Path.GetTempPath(), "live_archive_v7_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageDir);
        string videoOnly = Path.Combine(stageDir, $"video.{o.Encoder.Extension}");
        string audioOnly = Path.Combine(stageDir, $"audio.{AudioTrack.ContainerFor(o.AudioEncoder!)}");
        FileInfo part = Core.OutputFile.PartPath(dst);

        // The audio pass has to die with the run. If the video stage fails or the
        // user cancels, an un-cancellable task used to keep its FFmpeg working
        // for up to `audioTimeout` seconds, holding the staging directory open --
        // so the cleanup below failed too and both the process and
        // %TEMP%\live_archive_v7_* outlived the command.
        using var audioStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<CaptureResult>? audioTask = null;

        try
        {
            double span = duration.Value;
            int audioTimeout = (int)Math.Max(120.0, span * 2.0 + 120.0);

            // Stage 1 + 2 run concurrently: the audio encoder is not bound by the
            // realtime pacing that governs the video slot loop.
            audioTask = Task.Run(
                () => AudioTrack.Encode(o.FFmpeg, sourcePath, o.AudioEncoder!, audioKbps,
                    audioChannels, audioRate, o.StartSeconds, span, audioOnly, audioTimeout,
                    o.AudioStream, audioStop.Token),
                audioStop.Token);

            var videoOptions = new RealtimeOptions
            {
                FFmpeg = o.FFmpeg,
                FFprobe = o.FFprobe,
                SourcePath = o.SourcePath,
                DestinationPath = videoOnly,
                Encoder = o.Encoder,
                Fps = o.Fps,
                FpsLevels = o.FpsLevels,
                // v7.9.6: this copy used to drop the per-rung thresholds, so a
                // user who pinned "8:128:115" got the derived ones instead --
                // only on this path (--audio-mode auto/separate), which is why
                // no test noticed. Every field the video stage reads is now
                // copied explicitly.
                FpsLevelKbps = o.FpsLevelKbps,
                Quality = o.Quality,
                Threads = o.Threads,
                LimitKbps = o.LimitKbps,
                RecoverKbps = o.RecoverKbps,
                WindowSeconds = o.WindowSeconds,
                BufferFrames = o.BufferFrames,
                AudioEncoder = null,
                AudioKbps = audioKbps,
                AudioChannels = audioChannels,
                AudioRate = audioRate,
                // The video stage re-enters Run(), which probes the source
                // again: it has to make the same stream pick.
                VideoStream = o.VideoStream,
                AudioStream = o.AudioStream,
                Subtitles = o.Subtitles,
                AvailableSubtitleEncoders = o.AvailableSubtitleEncoders,
                Hwaccel = o.Hwaccel,
                Width = o.Width,
                Height = o.Height,
                FitMode = o.FitMode,
                CropAnchor = o.CropAnchor,
                PadColor = o.PadColor,
                ScaleAlgo = o.ScaleAlgo,
                PixFmt = o.PixFmt,
                ColorRange = o.ColorRange,
                ColorSpace = o.ColorSpace,
                ColorPrimaries = o.ColorPrimaries,
                ColorTransfer = o.ColorTransfer,
                Dither = o.Dither,
                Deinterlace = o.Deinterlace,
                DeintMode = o.DeintMode,
                DeintParity = o.DeintParity,
                DeintMarkedOnly = o.DeintMarkedOnly,
                X264Preset = o.X264Preset,
                X264Tune = o.X264Tune,
                X264Profile = o.X264Profile,
                X264Level = o.X264Level,
                StartSeconds = o.StartSeconds,
                EndSeconds = o.EndSeconds,
                AvailableFilters = o.AvailableFilters,
                AvailableScaleAlgorithms = o.AvailableScaleAlgorithms,
                Verbose = o.Verbose,
                AudioMode = AudioMode.Inline,
            };

            EncodeResult video = Run(videoOptions, cancellationToken);

            CaptureResult audio = audioTask.GetAwaiter().GetResult();
            if (audio.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"audio encode failed (code {audio.ExitCode}): "
                    + (audio.Stderr.Trim() is { Length: > 0 } d ? d : "no FFmpeg diagnostic"));
            }
            if (!new FileInfo(audioOnly).HasContent)
                throw new InvalidOperationException("audio encode produced no usable output");

            // Stage 3: join. Both streams are copied, so this is a remux, not a re-encode.
            CaptureResult mux = AudioTrack.Mux(
                o.FFmpeg, videoOnly, audioOnly, span, o.Encoder.FormatName, part.FullName, audioTimeout);
            if (mux.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"audio mux failed (code {mux.ExitCode}): "
                    + (mux.Stderr.Trim() is { Length: > 0 } d ? d : "no FFmpeg diagnostic"));
            }

            // File size alone is not proof of a correct mux: the RoQ muxer, for
            // example, happily writes an audio-only file with rc=0 and silently
            // drops the copied video stream. Verify the streams actually landed
            // -- on the part file, before it replaces anything.
            VerifyMuxedOutput(o.FFprobe, part, expectedAudio: true, expectedDuration: span);
            Core.OutputFile.Promote(part, dst);

            // The video-only pass measures the video bitrate directly; the final
            // file supplies the combined bitrate. This is more accurate than the
            // inline path, which only ever estimates video = total - audio_kbps.
            long videoBytes = new FileInfo(videoOnly).Length;
            double avgVideoKbps = videoBytes * 8.0 / span / 1000.0;
            double avgTotalKbps = dst.Length * 8.0 / span / 1000.0;

            return new EncodeResult
            {
                Duration = span,
                StartTimeSeconds = o.StartSeconds,
                EndTimeSeconds = duration.HasValue ? o.StartSeconds + span : null,
                SourcePixFmt = video.SourcePixFmt,
                TransportPixFmt = video.TransportPixFmt,
                OutputWidth = video.OutputWidth,
                OutputHeight = video.OutputHeight,
                AvgVideoKbps = avgVideoKbps,
                AvgTotalKbps = avgTotalKbps,
                // This path never runs without an audio track (see the
                // `!hasAudio` early return above).
                HasAudio = true,
                Fresh = video.Fresh,
                Repeated = video.Repeated,
                Deinterlace = o.Deinterlace,
                ScaleAlgo = video.ScaleAlgo,
                Slots = video.Slots,
                ElapsedSeconds = video.ElapsedSeconds,
            };
        }
        catch
        {
            Core.OutputFile.Discard(part);
            throw;
        }
        finally
        {
            // Stop the audio pass before deleting the directory it writes into,
            // and observe its outcome so a kill does not surface as an unobserved
            // task exception on the finalizer thread.
            audioStop.Cancel();
            if (audioTask is not null)
            {
                try { audioTask.Wait(5000); } catch { /* cancelled: already handled */ }
            }
            try { Directory.Delete(stageDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static List<string> BuildDecoderCommand(
        RealtimeOptions o, string sourcePath, string vf, string transportFmt, double? duration)
    {
        var cmd = new List<string> { o.FFmpeg, "-hide_banner", "-loglevel", "error" };
        // v7.9.6: swscale dither applies while the decoder converts the source
        // into the transport format -- which is the only place a colour
        // reduction happens. It is a global option, so it goes before -i.
        cmd.AddRange(ArgsBuilder.DitherArgs(o.Dither, transportFmt));
        // Network resilience, per source: the reconnect family for a URL, the
        // rw_timeout for a share. On a local file these are noise, so both
        // helpers return nothing.
        cmd.AddRange(Remote.HttpSource.ReadArgs(sourcePath));
        cmd.AddRange(Remote.Smb.RemoteReadArgs(sourcePath));
        if (!string.Equals(o.Hwaccel, FFmpeg.Hwaccel.Off, StringComparison.OrdinalIgnoreCase))
            cmd.AddRange(["-hwaccel", o.Hwaccel]);
        // Input seeking works over HTTP on any Range-capable server, which is
        // what archive.org and every CDN are; a server without it will fail
        // here rather than silently produce the wrong segment.
        if (o.StartSeconds > 0) cmd.AddRange(["-ss", ArgsBuilder.FloatArg(o.StartSeconds)]);
        cmd.AddRange(["-i", sourcePath]);
        if (duration is not null) cmd.AddRange(["-t", ArgsBuilder.FloatArg(duration.Value)]);
        cmd.AddRange([
            "-map", StreamSelection.VideoMap(o.VideoStream), "-an", "-vf", vf, "-pix_fmt", transportFmt, "-f", "rawvideo", "pipe:1",
        ]);
        return cmd;
    }

    private static List<string> BuildEncoderCommand(
        RealtimeOptions o, FileInfo dst, string transportFmt, int width, int height,
        bool hasAudio, int? audioRate, int audioChannels, double? duration,
        StreamInfo stream, string muxer, SubtitlePlan? subs)
    {
        var cmd = new List<string>
        {
            o.FFmpeg, "-hide_banner", "-loglevel", "error",
            "-threads", Math.Max(1, o.Threads).ToString(),
            "-f", "rawvideo",
            "-pix_fmt", transportFmt,
            "-s:v", $"{width}x{height}",
            "-framerate", ArgsBuilder.FpsArg(o.Fps),
            "-i", "pipe:0",
        };
        ColorOverride colors = ColorOverride.Parse(
            o.ColorRange, o.ColorSpace, o.ColorPrimaries, o.ColorTransfer);
        // Input 0 is the pipe; every further input is numbered from here.
        int nextInput = 1;

        if (hasAudio)
        {
            // The audio track is read from the source a second time -- a second
            // network read when the source is a URL, so it gets the same
            // resilience options as the decoder's read.
            cmd.AddRange(Remote.HttpSource.ReadArgs(o.SourcePath));
            cmd.AddRange(Remote.Smb.RemoteReadArgs(o.SourcePath));
            if (o.StartSeconds > 0) cmd.AddRange(["-ss", ArgsBuilder.FloatArg(o.StartSeconds)]);
            cmd.AddRange(["-i", o.SourcePath]);
            nextInput++;
        }

        // v7.9 #2: a carried subtitle track is its own input. A sidecar file is
        // read directly; an embedded track means reading the source once more.
        int subtitleInput = -1;
        if (subs is { Mode: SubtitleMode.Copy })
        {
            cmd.AddRange(SubtitleTrack.CopyInputArgs(subs, o.SourcePath, o.StartSeconds));
            subtitleInput = nextInput++;
        }

        // Input 0 is the raw video pipe coming from the decoder, which already
        // carries the *selected* video stream -- the pick is applied there
        // (BuildDecoderCommand), not here. Selecting it a second time would
        // index the pipe's single stream and match nothing.
        cmd.AddRange(["-map", "0:v:0"]);
        // Input 1 is the source again, read once more for the audio track.
        if (hasAudio) cmd.AddRange(["-map", StreamSelection.AudioMap(1, o.AudioStream)]);
        // Burned subtitles are already in the picture and need no map here.
        if (subtitleInput >= 0) cmd.AddRange(["-map", SubtitleTrack.CopyMap(subtitleInput, subs!)]);

        cmd.AddRange(["-c:v", o.Encoder.Name]);
        cmd.AddRange(ArgsBuilder.VideoQualityArgs(o.Encoder.Name, o.Quality, o.LimitKbps));
        // v7.9.6: pin the format on the encoder side too when the user named
        // one. The pipe already carries --pix_fmt, but without this the encoder
        // is free to hand the muxer its own default (measured: libx264 given
        // grey 4:0:0 writes yuvj420p), which is the kind of silent substitution
        // this flag exists to prevent.
        if (o.PixFmt.HasText) cmd.AddRange(["-pix_fmt", transportFmt]);
        if (o.Encoder.Name == "libx264")
        {
            cmd.AddRange(ArgsBuilder.X264Args(o.X264Preset, o.X264Tune, o.X264Profile, o.X264Level));
            cmd.AddRange(ArgsBuilder.X264ColorArgs(colors));
        }
        cmd.AddRange(["-r", ArgsBuilder.FpsArg(o.Fps), "-fps_mode", "cfr"]);
        cmd.AddRange(ArgsBuilder.ColorArgs(stream, colors));

        if (hasAudio)
        {
            if (o.AudioEncoder is null) throw new InvalidOperationException("internal error: audio encoder vanished");
            cmd.AddRange(["-c:a", o.AudioEncoder]);

            // No rate control on these: FFmpeg silently ignores -b:a, so emitting
            // it only misleads whoever reads the command.
            if (!EncoderMath.AudioIgnoresBitrate(o.AudioEncoder))
                cmd.AddRange(["-b:a", $"{o.AudioKbps}k"]);

            // The AMV muxer wants exactly one ADPCM frame per video frame.
            if (o.Encoder.Name == "amv")
            {
                cmd.AddRange(["-block_size:a",
                    EncoderMath.AmvAudioBlockSize(o.Fps).ToString(CultureInfo.InvariantCulture)]);
            }

            cmd.AddRange(["-ac", Math.Max(1, audioChannels).ToString()]);
            if (audioRate is not null && audioRate.Value != 0)
                cmd.AddRange(["-ar", audioRate.Value.ToString()]);
        }

        // v7.9 #2: the carried track is transcoded into whatever the container
        // can hold (ass in Matroska, mov_text in MP4, xsub in AVI, ...).
        if (subs is { Mode: SubtitleMode.Copy }) cmd.AddRange(SubtitleTrack.CopyCodecArgs(subs));

        // The segment duration bounds both streams. There is no -shortest because
        // the raw-video producer must not race the audio input into a premature
        // encoder shutdown.
        if (duration is not null) cmd.AddRange(["-t", ArgsBuilder.FloatArg(duration.Value)]);

        // -rw_timeout is the only write-side resilience option FFmpeg has; the
        // reconnect family is decode-only and would be rejected here.
        cmd.AddRange(Remote.Smb.RemoteWriteArgs(dst.FullName));
        cmd.AddRange(["-flush_packets", "1", "-progress", "pipe:2", "-f", muxer, "-y", dst.FullName]);
        return cmd;
    }

    private static EncodeResult Execute(
        RealtimeOptions o,
        FileInfo dst,
        List<string> decCmd,
        List<string> encCmd,
        int frameSize,
        int? targetSlots,
        double? duration,
        bool hasAudio,
        string sourcePixFmt,
        string transportFmt,
        int width,
        int height,
        string scaleAlgo,
        double? resolvedEnd,
        CancellationToken cancellationToken)
    {
        HighResolutionTimer.Enable();

        var monitor = new OutputMonitor(o.WindowSeconds);
        var pool = new FrameBufferPool(o.BufferFrames, frameSize);
        var captureState = new CaptureState();
        var captureErrors = new List<string>();
        var decoderDiagnostics = new List<string>();
        Lock diagnosticsLock = new();
        long capturedSequence = 0;

        var decoderStart = new ProcessStartInfo
        {
            FileName = decCmd[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (int i = 1; i < decCmd.Count; i++) decoderStart.ArgumentList.Add(decCmd[i]);

        var encoderStart = new ProcessStartInfo
        {
            FileName = encCmd[0],
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (int i = 1; i < encCmd.Count; i++) encoderStart.ArgumentList.Add(encCmd[i]);

        using var decoder = Process.Start(decoderStart)
            ?? throw new InvalidOperationException("Failed to start decoder FFmpeg");
        using var encoderProc = Process.Start(encoderStart)
            ?? throw new InvalidOperationException("Failed to start encoder FFmpeg");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task decoderStderrTask = PipeReaders.DiagnosticsReaderAsync(
            decoder.StandardError.BaseStream,
            line =>
            {
                lock (diagnosticsLock)
                {
                    decoderDiagnostics.Add(line);
                    if (decoderDiagnostics.Count > 64) decoderDiagnostics.RemoveAt(0);
                }
            },
            cts.Token);

        Task progressTask = PipeReaders.ProgressReaderAsync(
            encoderProc.StandardError.BaseStream, monitor, cts.Token);

        void CaptureWorker()
        {
            try
            {
                Stream stdout = decoder.StandardOutput.BaseStream;
                long frequency = Stopwatch.Frequency;
                double interval = 1.0 / o.Fps;
                long nextDeadline = Stopwatch.GetTimestamp();

                while (!captureState.Stop)
                {
                    if (!pool.TryRent(out int index))
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    byte[] buffer = pool[index];
                    int totalRead = 0;
                    while (totalRead < frameSize)
                    {
                        int read = stdout.Read(buffer, totalRead, frameSize - totalRead);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (totalRead != frameSize)
                    {
                        pool.Release(index);
                        break;
                    }

                    capturedSequence++;
                    pool.Publish(index, capturedSequence);

                    // Python resets the deadline when the slot is already overdue
                    // instead of trying to catch up; keep that behaviour.
                    nextDeadline += (long)(interval * frequency);
                    long nowTicks = Stopwatch.GetTimestamp();
                    if (nextDeadline > nowTicks) HighResolutionTimer.WaitUntil(nextDeadline);
                    else nextDeadline = nowTicks;
                }
            }
            catch (Exception ex)
            {
                lock (diagnosticsLock) captureErrors.Add(ex.Message);
            }
            finally
            {
                captureState.Done = true;
            }
        }

        var captureThread = new Thread(CaptureWorker) { IsBackground = true, Name = "frame-capture" };

        // v7.7: a rung carries its own bitrate thresholds, so the controller no
        // longer has to guess them from the ceiling alone.
        List<FpsRung> ladder = EncoderMath.BuildLadder(
            o.Fps, o.FpsLevels, o.FpsLevelKbps, o.LimitKbps, o.RecoverKbps);
        List<double> levels = [.. ladder.Select(r => r.Fps)];
        int currentLevel = 0;
        double effectiveFps = levels[0];
        double cadence = 1.0;
        double slotInterval = 1.0 / o.Fps;
        int slots = 0;
        int fresh = 0;
        int repeated = 0;
        double lastStatus = 0.0;
        double lastProgress = -1.0;
        double? highSince = null;
        double? lowSince = null;

        long start = Stopwatch.GetTimestamp();
        long slotIntervalTicks = (long)(slotInterval * Stopwatch.Frequency);
        int previousIndex = -1;
        long? previousSequence = null;

        try
        {
            captureThread.Start();
            Stream encoderStdin = encoderProc.StandardInput.BaseStream;
            long nextSlot = Stopwatch.GetTimestamp();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (targetSlots is not null && slots >= targetSlots) break;

                // A decoder that died has to be reported even when the capture
                // thread has already noticed the closed pipe: that thread sets
                // Done on its way out, so the old `&& !captureState.Done` guard
                // made this check unreachable in exactly the case it exists for.
                // A mid-stream failure (dropped HTTP source, bad sector) then
                // let the loop fill the rest of the requested duration with the
                // last decoded frame and report success on a frozen tail. This
                // loop only kills the decoder after it ends, so a non-zero code
                // here is always the decoder's own doing.
                decoder.Refresh();
                if (decoder.HasExited && decoder.ExitCode != 0)
                {
                    List<string> tail = TailLines(decoderDiagnostics, 8);
                    throw new InvalidOperationException(
                        $"decoder exited before the capture finished (code {decoder.ExitCode}); "
                        + (tail.Count > 0 ? string.Join(" | ", tail) : "no decoder diagnostic"));
                }

                if (targetSlots is null && captureState.Done && pool.ReadyCount == 0) break;

                // Do not consume an output slot until the first decoded frame is
                // available, otherwise short files lose their first slot purely
                // because the capture thread had not produced one yet.
                if (previousIndex < 0 && pool.ReadyCount == 0)
                {
                    if (captureState.Done) break;
                    Thread.Sleep(Math.Max(1, (int)(Math.Min(0.01, slotInterval) * 1000)));
                    continue;
                }

                long nowSlotTicks = Stopwatch.GetTimestamp();
                nextSlot += slotIntervalTicks;
                if (nextSlot > nowSlotTicks) HighResolutionTimer.WaitUntil(nextSlot);
                else nextSlot = nowSlotTicks;

                encoderProc.Refresh();
                if (encoderProc.HasExited)
                {
                    throw new InvalidOperationException(
                        $"encoder exited before final frame (code {encoderProc.ExitCode}); "
                        + JoinDiagnostics(monitor.TailDiagnostics(8)));
                }

                slots++;
                // The hysteresis constants are expressed in seconds of
                // *programme*, which on the wall clock is exactly what the
                // realtime pacing produces -- so they are compared against
                // monotonic time directly.
                double now = HighResolutionTimer.MonotonicSeconds();
                double totalRate = monitor.RateKbps();
                double videoRate = Math.Max(0.0, totalRate - (hasAudio ? o.AudioKbps : 0.0));

                if (videoRate >= o.LimitKbps)
                {
                    lowSince = null;
                    highSince ??= now;
                    int target = EncoderMath.TargetLevel(videoRate, ladder);
                    if (target > currentLevel)
                    {
                        double hold = videoRate >= o.LimitKbps * 1.25 ? 0.18 : 0.30;
                        if (now - highSince.Value >= hold) currentLevel++;
                    }
                    else if (target < currentLevel && videoRate < o.LimitKbps * 1.10)
                    {
                        if (now - highSince.Value >= 0.80) currentLevel--;
                    }
                }
                else if (currentLevel > 0 && videoRate <= ladder[currentLevel].RiseBelowKbps)
                {
                    highSince = null;
                    lowSince ??= now;
                    if (now - lowSince.Value >= 0.75) currentLevel--;
                }
                else
                {
                    highSince = null;
                    lowSince = null;
                }

                currentLevel = Math.Clamp(currentLevel, 0, levels.Count - 1);
                double newEffective = levels[currentLevel];
                if (Math.Abs(newEffective - effectiveFps) > 1e-9 && o.Verbose)
                {
                    string direction = newEffective < effectiveFps ? "DOWN" : "UP";
                    Console.WriteLine(
                        $"\n[{slots,7}] video={videoRate,6:F1} kbps "
                        + $"motion FPS {effectiveFps:G6} -> {newEffective:G6} ({direction})");
                }
                effectiveFps = newEffective;

                cadence += effectiveFps / o.Fps;
                bool emitFresh = previousIndex < 0 || cadence >= 1.0;

                byte[] payload;
                if (emitFresh)
                {
                    if (pool.TryTakeLatest(out int latestIndex, out long latestSequence))
                    {
                        if (latestSequence != previousSequence)
                        {
                            if (previousIndex >= 0) pool.Release(previousIndex);
                            previousIndex = latestIndex;
                            previousSequence = latestSequence;
                            fresh++;
                        }
                        else if (previousIndex < 0)
                        {
                            // The first frame is always accepted so the stream has a
                            // valid starting picture/keyframe.
                            previousIndex = latestIndex;
                            previousSequence = latestSequence;
                            fresh++;
                        }
                        else
                        {
                            pool.Release(latestIndex);
                            repeated++;
                        }
                        payload = pool[previousIndex];
                    }
                    else if (previousIndex >= 0)
                    {
                        payload = pool[previousIndex];
                        repeated++;
                    }
                    else
                    {
                        continue;
                    }
                    cadence -= 1.0;
                }
                else
                {
                    if (previousIndex < 0) continue;
                    payload = pool[previousIndex];
                    repeated++;
                }

                try
                {
                    encoderStdin.Write(payload, 0, frameSize);
                    encoderStdin.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    encoderProc.Refresh();
                    string rc = encoderProc.HasExited ? encoderProc.ExitCode.ToString() : "running";
                    throw new InvalidOperationException(
                        $"encoder stdin closed at output slot {slots} (returncode={rc}); "
                        + JoinDiagnostics(monitor.TailDiagnostics(8)), ex);
                }

                double elapsed = HighResolutionTimer.SecondsSince(start);
                if (o.Progress is not null && elapsed - lastProgress >= 0.2)
                {
                    lastProgress = elapsed;
                    o.Progress(slots, targetSlots);
                }
                if (o.Verbose && elapsed - lastStatus >= 0.5)
                {
                    string state = currentLevel == 0 ? "LIVE" : "THROTTLED";
                    Console.Write(
                        $"\r[{elapsed,7:F1}s] video={videoRate,6:F1} kbps {state,-9} "
                        + $"motion={effectiveFps:G6}fps slots={slots,7} new={fresh,7} repeat={repeated,7}");
                    lastStatus = elapsed;
                }
            }

            try
            {
                encoderStdin.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                encoderProc.Refresh();
                string rc = encoderProc.HasExited ? encoderProc.ExitCode.ToString() : "running";
                throw new InvalidOperationException(
                    $"encoder stdin closed before final frame (returncode={rc}); "
                    + JoinDiagnostics(monitor.TailDiagnostics(8)), ex);
            }

            // The raw decoder is only a live source for the controller. Once the
            // requested cadence has been emitted, stop it explicitly instead of
            // waiting for a second independent EOF race on a pipe.
            captureState.Stop = true;
            decoder.Refresh();
            if (!decoder.HasExited)
            {
                try { decoder.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }

            double encoderTimeout = Math.Max(60.0, (duration ?? 60.0) * 2.0 + 60.0);
            int encRc;
            if (!encoderProc.WaitForExit((int)(encoderTimeout * 1000)))
            {
                try { encoderProc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                encoderProc.WaitForExit(5000);
                throw new InvalidOperationException($"encoder did not terminate within {encoderTimeout:F0}s");
            }
            encRc = encoderProc.ExitCode;

            double decoderTimeout = Math.Max(10.0, (duration ?? 30.0) * 0.25 + 10.0);
            if (!decoder.WaitForExit((int)(decoderTimeout * 1000)))
            {
                try { decoder.Kill(entireProcessTree: true); } catch { /* already gone */ }
                decoder.WaitForExit(5000);
                throw new InvalidOperationException(
                    $"decoder did not terminate within {decoderTimeout:F0}s after stop request");
            }

            captureThread.Join(5000);
            cts.Cancel();
            try { Task.WaitAll([progressTask, decoderStderrTask], 3000); } catch { /* diagnostics only */ }

            // Non-zero decoder codes after our deliberate stop are expected; the
            // pipe was intentionally stopped after the output slots were produced.
            if (encRc != 0)
                throw new InvalidOperationException(
                    $"encoder exited with code {encRc}; {JoinDiagnostics(monitor.TailDiagnostics(10))}");

            if (!dst.HasContent)
                throw new InvalidOperationException("FFmpeg reported success but output file is empty");

            lock (diagnosticsLock)
            {
                if (captureErrors.Count > 0)
                {
                    throw new InvalidOperationException(
                        "frame capture failed: " + string.Join(" | ", captureErrors.TakeLast(5)));
                }
            }

            double measuredDuration = duration ?? Math.Max(0.001, slots / o.Fps);
            long totalBytes = monitor.TotalBytes();
            double avgTotalKbps = totalBytes * 8.0 / measuredDuration / 1000.0;
            double avgVideoKbps = Math.Max(0.0, avgTotalKbps - (hasAudio ? o.AudioKbps : 0.0));

            return new EncodeResult
            {
                Duration = measuredDuration,
                StartTimeSeconds = o.StartSeconds,
                EndTimeSeconds = resolvedEnd,
                SourcePixFmt = sourcePixFmt,
                TransportPixFmt = transportFmt,
                OutputWidth = width,
                OutputHeight = height,
                AvgVideoKbps = avgVideoKbps,
                AvgTotalKbps = avgTotalKbps,
                HasAudio = hasAudio,
                Fresh = fresh,
                Repeated = repeated,
                Deinterlace = o.Deinterlace,
                ScaleAlgo = scaleAlgo,
                Slots = slots,
                ElapsedSeconds = HighResolutionTimer.SecondsSince(start),
            };
        }
        finally
        {
            captureState.Stop = true;
            cts.Cancel();
            foreach (Process proc in new[] { decoder, encoderProc })
            {
                try
                {
                    proc.Refresh();
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch { /* already gone */ }
            }
            _ = HighResolutionTimer.Disable();
        }
    }

    /// <summary>
    /// Cheap feasibility probe for the split pipeline: encode 3 synthetic frames and
    /// half a second of synthetic audio, mux them, and check the video survived.
    /// Costs ~1-2 s and saves a full realtime pass when the muxer cannot do it.
    /// </summary>
    private static bool CanSplitMux(
        RealtimeOptions o, string audioEncoder, int audioChannels, int? audioRate, out string reason)
    {
        reason = "";
        string dir = Path.Combine(Path.GetTempPath(), "live_archive_probe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string videoPath = Path.Combine(dir, $"v.{o.Encoder.Extension}");
            string audioPath = Path.Combine(dir, $"a.{AudioTrack.ContainerFor(audioEncoder)}");
            string muxPath = Path.Combine(dir, $"m.{o.Encoder.Extension}");

            var videoCmd = new List<string>
            {
                o.FFmpeg, "-hide_banner", "-loglevel", "error",
                "-f", "lavfi",
                "-i", $"testsrc2=size={o.Width}x{o.Height}:rate={ArgsBuilder.FpsArg(o.Fps)}:duration=0.3",
                "-frames:v", "3",
                "-c:v", o.Encoder.Name,
            };
            videoCmd.AddRange(ArgsBuilder.VideoQualityArgs(o.Encoder.Name, o.Quality, o.LimitKbps));
            videoCmd.AddRange(["-f", o.Encoder.FormatName, "-y", videoPath]);
            CaptureResult video = ProcessRunner.Run(videoCmd, 60);
            if (video.ExitCode != 0)
            {
                reason = $"probe video encode failed: {(video.Stderr.Trim() is { Length: > 0 } d ? d : "no diagnostic")}";
                return false;
            }

            var audioCmd = new List<string>
            {
                o.FFmpeg, "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=0.5",
                "-c:a", audioEncoder,
                "-b:a", $"{o.AudioKbps}k",
                "-ac", Math.Max(1, audioChannels).ToString(CultureInfo.InvariantCulture),
            };
            if (audioRate is not null && audioRate.Value != 0)
                audioCmd.AddRange(["-ar", audioRate.Value.ToString(CultureInfo.InvariantCulture)]);
            audioCmd.AddRange(["-f", AudioTrack.ContainerFor(audioEncoder), "-y", audioPath]);
            CaptureResult audio = ProcessRunner.Run(audioCmd, 60);
            if (audio.ExitCode != 0)
            {
                reason = $"probe audio encode failed: {(audio.Stderr.Trim() is { Length: > 0 } d ? d : "no diagnostic")}";
                return false;
            }

            CaptureResult mux = AudioTrack.Mux(
                o.FFmpeg, videoPath, audioPath, 0.3, o.Encoder.FormatName, muxPath, 60);
            if (mux.ExitCode != 0)
            {
                reason = $"probe mux failed: {(mux.Stderr.Trim() is { Length: > 0 } d ? d : "no diagnostic")}";
                return false;
            }

            try
            {
                // Stream presence only: a 3-frame clip is too short for a
                // meaningful duration comparison, and some muxers (RealMedia)
                // report odd durations for tiny files.
                VerifyMuxedOutput(o.FFprobe, new FileInfo(muxPath), expectedAudio: true, checkDuration: false);
            }
            catch (InvalidOperationException exc)
            {
                reason = exc.Message;
                return false;
            }

            // Stream presence is not enough. The RealMedia muxer yields a file that
            // ffprobe lists as having a video stream while every rv20 slice header is
            // destroyed by the copy; that only surfaces when actually decoding.
            CaptureResult decode = ProcessRunner.Run(
                [o.FFmpeg, "-v", "error", "-i", muxPath, "-f", "null", "-"], 60);
            if (decode.ExitCode != 0 || decode.Stderr.Trim().Length > 0)
            {
                reason = "muxed output does not decode cleanly";
                return false;
            }
            return true;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Confirm the muxed file really carries both streams for roughly the expected
    /// span. Several legacy muxers (RoQ in particular) return exit code 0 while
    /// dropping the copied video stream, so the split pipeline must not trust the
    /// exit code. Raises so the caller can fall back to the inline pipeline.
    /// </summary>
    private static void VerifyMuxedOutput(
        string ffprobe, FileInfo dst, bool expectedAudio, double expectedDuration = 0,
        bool checkDuration = true)
    {
        static double? ProbeDuration(string ffprobePath, string file, string streamSelector)
        {
            CaptureResult p = ProcessRunner.Run([
                ffprobePath, "-v", "error", "-select_streams", streamSelector,
                "-show_entries", "stream=codec_name,duration",
                "-of", "csv=p=0", file,
            ], 30);
            if (p.ExitCode != 0 || p.Stdout.Trim().Length == 0) return null;

            string[] parts = p.Stdout.Trim().Split(',');
            if (parts.Length < 2) return null;
            return double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : null;
        }

        double? videoDuration = ProbeDuration(ffprobe, dst.FullName, "v:0");
        if (videoDuration is null)
            throw new InvalidOperationException("muxed output has no video stream");

        if (expectedAudio && ProbeDuration(ffprobe, dst.FullName, "a:0") is null)
            throw new InvalidOperationException("muxed output has no audio stream");

        // Allow generous slack: containers round durations and some codecs pad the
        // last frame, but a stream that lost most of its payload is a real failure.
        if (checkDuration && videoDuration.Value < expectedDuration * 0.5)
        {
            throw new InvalidOperationException(
                $"muxed video stream is {videoDuration.Value:F3}s, far short of {expectedDuration:F3}s");
        }
    }

    private static string JoinDiagnostics(List<string> diagnostics) =>
        diagnostics.Count > 0 ? string.Join(" | ", diagnostics) : "no FFmpeg diagnostic";

    private static List<string> TailLines(List<string> values, int count)
    {
        if (count <= 0) return [];
        return values.Count <= count ? [.. values] : [.. values.Skip(values.Count - count)];
    }
}
