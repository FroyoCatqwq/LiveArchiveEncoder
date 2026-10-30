using Lae.Core;
using Lae.Media;

namespace Lae.Encoding;

/// <summary>What a completed single-pass encode leaves behind.</summary>
public sealed record LiveRun
{
    public required PumpStats Pump { get; init; }

    public required string OutputPath { get; init; }

    public required long OutputBytes { get; init; }

    public required double SpanSeconds { get; init; }

    public required bool HasAudio { get; init; }

    /// <summary>Whole-file bitrate over the encoded span.</summary>
    public double AverageKbps => SpanSeconds > 0 ? OutputBytes * 8.0 / SpanSeconds / 1000.0 : 0;
}

/// <summary>
/// Runs one <see cref="EncodePlan"/>: two FFmpeg children, raw frames pumped
/// between them on the wall clock.
///
/// <para>The whole design is "decide, then execute". Every decision -- encoder,
/// container, filter chain, frame size, both command lines -- was made by
/// <see cref="EncodePlan"/> before this type is reached, so what is left here is
/// process lifetime and failure reporting. v7's counterpart decided and executed
/// in the same 465-line block, which is why a wrong argument could only be found
/// by running a real encode and looking at the result.</para>
///
/// <para>Failure policy, in one place: kill both children, remove the part file,
/// rethrow. A caller never has to clean up after a failed run, and a half-written
/// output never survives it.</para>
/// </summary>
public static class LiveEncode
{
    /// <summary>
    /// Encode <paramref name="plan"/> to its destination.
    /// <paramref name="progress"/> is called with (frames written, target or null);
    /// <paramref name="warn"/> receives sentences that are worth printing but do
    /// not stop the run.
    /// </summary>
    public static LiveRun Run(
        EncodePlan plan,
        string ffmpeg,
        Action<long, long?>? progress,
        Action<string>? warn,
        CancellationToken token)
    {
        if (!plan.IsRunnable)
            throw new InvalidOperationException("the plan has problems; nothing was started");

        string finalPath = Path.GetFullPath(plan.DestinationPath);
        string partPath = PartFile.PathFor(finalPath);
        string? folder = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // A part file from a run that was killed from outside -- the terminal
        // closed, the machine rebooted, CI cancelled the job -- is ours and is
        // disposable. Clearing it here means a later run never starts by arguing
        // with a corpse. The encoder also passes -y, so both halves of this are
        // covered.
        PartFile.Discard(partPath);

        using ChildProcess decoder = ChildProcess.Start(
            plan.DecodeCommand(ffmpeg), new ChildSpec { ReadOutput = true });

        ChildProcess? encoder = null;
        try
        {
            // A failed encoder start used to leave the decoder reading a network
            // source for the rest of the session: disposing a Process does not
            // stop a child, and the cleanup below is not reached from here.
            try
            {
                encoder = ChildProcess.Start(
                    plan.WritingTo(partPath).EncodeCommand(ffmpeg),
                    new ChildSpec { WriteInput = true, ReadOutput = true });
            }
            catch (Exception exc) when (exc is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                decoder.KillTree();
                throw new InvalidOperationException($"could not start the encoder: {exc.Message}", exc);
            }

            // The encoder's stdout carries -progress reports instead of frames;
            // the frames go in through stdin and out to the file.
            using var progressTap = new ProgressTap(encoder.Output!);

            PumpStats stats = Pump(plan, progressTap, decoder, encoder, progress, warn, token);

            // Closing the input is what tells the encoder the picture is over; it
            // then flushes its last packets and exits.
            encoder.Input?.Dispose();

            double span = plan.SpanSeconds;
            TimeSpan drain = TimeSpan.FromSeconds(Math.Max(60.0, span * 2.0 + 60.0));

            encoder.WaitOrCancel(drain,
                $"the encoder did not finish within {drain.TotalSeconds:F0}s", token);

            // The decoder is only a source for the pump. Once the requested slot
            // count is out, stop it rather than racing a second end-of-pipe.
            decoder.KillTree();

            if (encoder.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"the encoder exited with code {encoder.ExitCode}; {Diagnostics(encoder)}");
            }

            if (stats.SourceEnded && plan.TargetFrames > stats.Frames && warn is not null)
            {
                warn($"the source ended after {stats.Frames} of {plan.TargetFrames} slots");
            }

            long bytes = new FileInfo(partPath).Length;
            PartFile.Promote(partPath, finalPath);

            return new LiveRun
            {
                Pump = stats,
                OutputPath = finalPath,
                OutputBytes = bytes,
                SpanSeconds = span,
                HasAudio = plan.HasAudio,
            };
        }
        catch
        {
            encoder?.KillTree();
            decoder.KillTree();

            if (PartFile.Discard(partPath) is { } leftover) warn?.Invoke(leftover);
            throw;
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    /// <summary>
    /// The pump, with the two things the generic loop cannot know: what "the run
    /// is still alive" means for these two children, and what the muxer has
    /// produced.
    /// </summary>
    private static PumpStats Pump(
        EncodePlan plan, ProgressTap progressTap, ChildProcess decoder, ChildProcess encoder,
        Action<long, long?>? progress, Action<string>? warn, CancellationToken token)
    {
        FpsLadder? ladder = plan.BuildLadder();

        var spec = new PumpSpec
        {
            Fps = plan.Fps,
            FrameSize = plan.FrameSize,
            TargetFrames = plan.TargetFrames,
            Ladder = ladder is null ? null : new LadderControl
            {
                Ladder = ladder,
                Window = new BitrateWindow(plan.Encode.WindowSeconds),
                Progress = () => progressTap.Current,
                AudioKbps = plan.HasAudio ? plan.Encode.AudioKbps : 0,
                Moved = move => warn?.Invoke(
                    $"video {move.Kbps:F0} kbps over {move.CoveredSeconds:G4}s; "
                    + $"motion fps {move.FromFps:G4} -> {move.ToFps:G4}"),
            },
        };

        return FramePump.Run(
            decoder.Output!, encoder.Input!, spec,
            tick: frames =>
            {
                // Checked every slot. v7 waited for the capture thread to notice a
                // closed pipe, and then could not tell "the decoder is done" from
                // "the decoder died", so a dropped network source produced a
                // frozen tail and a successful exit code.
                if (decoder.HasExited && decoder.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"the decoder died after {frames} frames (code {decoder.ExitCode}); "
                        + Diagnostics(decoder));
                }

                if (encoder.HasExited)
                {
                    throw new InvalidOperationException(
                        $"the encoder stopped accepting frames at slot {frames} (code {encoder.ExitCode}); "
                        + Diagnostics(encoder));
                }
            },
            progress: progress,
            token);
    }

    private static string Diagnostics(ChildProcess child)
        => child.Diagnostics() is { Length: > 0 } text ? text : "no FFmpeg diagnostic";
}
