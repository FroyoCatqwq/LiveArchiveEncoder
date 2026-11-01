using System.Diagnostics;

namespace Lae.Core;

/// <summary>
/// The bitrate feedback the pump reads, in one place.
///
/// <para><see cref="OutputBytes"/> is a delegate and not a file handle on
/// purpose: the pump is in <c>Lae.Core</c>, which is not allowed to touch a
/// disk. The caller hands in "how many bytes has the encoder written so far"
/// and the pump stays a pure scheduler over two streams -- which is also what
/// makes the ladder testable without FFmpeg, by passing a counter.</para>
/// </summary>
public sealed record LadderControl
{
    public required FpsLadder Ladder { get; init; }

    public required BitrateWindow Window { get; init; }

    /// <summary>
    /// What the encoder has produced so far.
    ///
    /// <para>A delegate and not a file handle on purpose: the pump lives in
    /// <c>Lae.Core</c>, which does not do I/O, and the caller already owns the
    /// child process that knows the answer. It is also what makes the ladder
    /// testable without FFmpeg -- a test hands in a counter.</para>
    /// </summary>
    public required Func<EncoderProgress> Progress { get; init; }

    /// <summary>
    /// Audio bitrate to subtract from the total: the ladder governs the picture,
    /// and a 64 kbps audio track must not be mistaken for motion.
    /// </summary>
    public double AudioKbps { get; init; }

    /// <summary>Called when the rung moves, so the run can say so.</summary>
    public Action<LadderMove>? Moved { get; init; }
}

/// <summary>One rung change, in the terms a report needs.</summary>
/// <param name="FromFps">Frame rate before the move.</param>
/// <param name="ToFps">Frame rate after it.</param>
/// <param name="Kbps">The measured video bitrate that caused it.</param>
/// <param name="CoveredSeconds">
/// How much programme the reading covered. Worth reporting: early in a run this
/// is less than the window, and a number with its span attached is a measurement
/// rather than a claim.
/// </param>
public readonly record struct LadderMove(double FromFps, double ToFps, double Kbps, double CoveredSeconds);

/// <summary>What one pump run needs to know about its slot cadence.</summary>
public sealed record PumpSpec
{
    /// <summary>Output slots per second. The cadence the whole program is named for.</summary>
    public required double Fps { get; init; }

    /// <summary>Bytes per raw frame on the pipe.</summary>
    public required int FrameSize { get; init; }

    /// <summary>
    /// Slots to emit, or null when the source length is unknown and the run ends
    /// with the source.
    /// </summary>
    public long? TargetFrames { get; init; }

    /// <summary>
    /// The adaptive bitrate feedback, or null to hand the encoder every frame the
    /// source produces.
    ///
    /// <para>Null is the honest default for a file-to-file run: the ladder exists
    /// to imitate a server that had to stay under a ceiling, and turning it on
    /// uninvited would silently change the output of every plain encode.</para>
    /// </summary>
    public LadderControl? Ladder { get; init; }

    /// <summary>
    /// Frames the decoder may run ahead of the pump.
    ///
    /// <para>This is <c>BufferFrames</c> from the settings, and it was carried
    /// through three layers without anything reading it until now: the channel's
    /// depth is the only place the number means anything. One frame is the
    /// minimum that works -- the producer needs somewhere to put the picture it
    /// just decoded while the consumer writes the previous one -- and more is a
    /// safety margin for a machine that stalls for a moment. It is not free: a
    /// raw frame at 320x240 is 115 kB, so six of them is most of a megabyte of
    /// memory per run.</para>
    /// </summary>
    public int BufferFrames { get; init; } = 6;

    /// <summary>
    /// How long one frame may sit unpicked in the encoder's input before the run
    /// is declared stuck. One slot is at most a third of a second of programme,
    /// so 30 s means "not slow, wedged".
    /// </summary>
    public TimeSpan WriteDeadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the decoder may take to deliver the first whole frame before the
    /// run is declared wedged.
    ///
    /// <para>The wait for that first frame is the only place in the loop with no
    /// deadline: until a frame arrives the pacing clock has not started, so
    /// <see cref="WriteDeadline"/> has nothing to guard. A 4K source through
    /// <c>nnedi</c>, a <c>-ss</c> that landed inside a long GOP, or a stalled
    /// network read all sit in it, and a cancel is the only way out. Same order of
    /// magnitude as the write deadline for the same reason: not slow, wedged.</para>
    /// </summary>
    public TimeSpan StartDeadline { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>What a pump run did, for the report and for the comparison matrix.</summary>
public sealed record PumpStats
{
    /// <summary>Slots written to the encoder.</summary>
    public required long Frames { get; init; }

    /// <summary>Slots that carried a newly decoded frame.</summary>
    public required long Fresh { get; init; }

    /// <summary>Slots that repeated the previous frame because nothing newer had arrived.</summary>
    public required long Repeated { get; init; }

    /// <summary>Wall clock time the pump ran for.</summary>
    public required double Seconds { get; init; }

    /// <summary>True when the source ended before the target slot count was reached.</summary>
    public required bool SourceEnded { get; init; }

    /// <summary>Decoded frames thrown away because the ladder was throttling or the consumer lagged.</summary>
    public long Dropped { get; init; }

    /// <summary>
    /// Slots written after the source had run out, repeating the last picture.
    ///
    /// <para>Separate from <see cref="Repeated"/> because the two mean different
    /// things to the person reading the report: <c>Repeated</c> is what a still
    /// picture looks like, and this is a truncated source -- the file says eleven
    /// minutes and the last ninety seconds are one frozen frame. v8 reported both
    /// as an ordinary success, which is the v7 complaint the rewrite was meant to
    /// answer.</para>
    /// </summary>
    public long FrozenAtEnd { get; init; }

    /// <summary>The rung the run finished on; 0 means the ladder never moved.</summary>
    public int FinalRung { get; init; }

    /// <summary>Frame rate left after adaptation, when a ladder was in use.</summary>
    public double MotionFps { get; init; }

    /// <summary>
    /// The last bitrate the ladder measured, in kbps, or 0 when no ladder ran.
    ///
    /// <para>Worth reporting separately from the file's average: the average says
    /// what the whole file came to, this says where the encoder sat when the run
    /// ended, and the two differ by exactly as much as the ladder moved.</para>
    /// </summary>
    public double MeasuredKbps { get; init; }

    /// <summary>Frames per second actually achieved, against the requested cadence.</summary>
    public double EffectiveFps => Seconds > 0 ? Frames / Seconds : 0;
}

/// <summary>
/// Paces raw frames from a decoder pipe into an encoder pipe, one slot per
/// <c>1/fps</c> seconds, on the wall clock.
///
/// <para>This is the piece that makes the re-encode "live": the point of the
/// whole program is not to transcode a file as fast as the CPU allows, but to
/// hand an encoder the same frame sequence a capture card would have handed it
/// in 2004, so the encoder's rate control behaves the way the archive wants to
/// document. Take the pacing out and the bitrate curve, the frame duplication and
/// the whole feel of the output change.</para>
///
/// <para>Two loops, not one. The producer blocks on the decoder pipe and is
/// paced to one frame per slot; the consumer ticks on the slot deadline and
/// writes the newest frame it has, repeating the previous one when the producer
/// has nothing new. That split is what keeps the cadence exact when decoding is
/// slower than realtime -- a single loop that read and wrote in step would
/// produce fewer slots than the requested duration and a short file.</para>
///
/// <para>Every deadline here is recomputed from the previous deadline rather than
/// from "now", so a slow slot does not accumulate drift; when a slot is already
/// overdue the deadline is reset instead of being chased, which is what a live
/// capture does when the machine cannot keep up.</para>
/// </summary>
public static class FramePump
{
    /// <summary>
    /// How long the pacing loop waits at the end for the producer to say whether
    /// the source ran out. Long enough for a thread that has already read its
    /// end-of-stream to publish the fact; short enough that a live source, whose
    /// producer is blocked in a read that will not return, costs nothing
    /// noticeable.
    /// </summary>
    private static readonly TimeSpan EndGrace = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Pump until the target slot count is reached, or -- when the length is
    /// unknown -- until the source runs out.
    ///
    /// <para>A known-length run that loses its source early does <b>not</b> stop:
    /// it pads the rest of the timeline with the last picture, because a file
    /// whose duration follows the timeline is the thing being archived. What it
    /// must not do is call that an ordinary success, so the padding is counted in
    /// <see cref="PumpStats.FrozenAtEnd"/> and the run reports it.</para>
    ///
    /// <para><paramref name="tick"/> runs once per slot before the frame is
    /// written: it is where the caller checks that both children are still alive,
    /// because a loop that only looks at its own counters will happily fill the
    /// rest of the duration with the last frame and report success.</para>
    /// </summary>
    public static PumpStats Run(
        Stream source,
        Stream sink,
        PumpSpec spec,
        Action<long>? tick,
        Action<long, long?>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spec.Fps);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.FrameSize, 1);

        // A depth of zero is not "no buffering", it is a decoder that has nowhere
        // to put the frame it just read. Clamped rather than refused because the
        // number arrives from a settings file, and a run that works beats a run
        // that explains why it will not start.
        var channel = new FrameChannel(spec.FrameSize, Math.Max(1, spec.BufferFrames));
        var capture = new Capture(source, channel, spec.Fps);
        long start = Stopwatch.GetTimestamp();
        long slotTicks = FineTimer.IntervalTicks(spec.Fps);

        // One buffer, because the consumer is the only thread that touches it:
        // it is filled from the channel and then written out, in that order.
        byte[] frame = new byte[spec.FrameSize];
        long emitted = 0;
        long fresh = 0;
        long repeated = 0;
        long frozenAtEnd = 0;
        long discarded;

        // The cadence counter. At full rate it adds 1.0 per slot and always
        // passes the threshold, so a run with no ladder behaves exactly as
        // before; at 8 of 13 fps it passes on eight slots out of thirteen and
        // the rest repeat the picture. This is how the throttle is expressed:
        // not by writing fewer slots -- that would shorten the file -- but by
        // handing the encoder a repeated frame, which costs almost nothing to
        // encode and keeps the timeline intact.
        double cadence = 1.0;
        EncoderProgress produced = default;
        double measured = 0;
        LadderControl? control = spec.Ladder;

        try
        {
            // Armed inside the try so the finally below always pairs with it;
            // enabling earlier leaked one hold on the 1 ms period whenever
            // anything between threw.
            FineTimer.Enable();
            capture.Start();

            long? target = spec.TargetFrames;

            // Deadlines are absolute -- baseline + k * interval -- never "now plus
            // one interval". Relative deadlines accumulate every overshoot: a
            // sleeping thread wakes a millisecond or two late, that lateness is
            // added to the next slot, and a five-second encode takes six. That is
            // not theory: v8's first real encode ran 5.04 s of source in 5.88 s.
            //
            // The clock starts when the first frame arrives, not when the call was
            // made: a source that takes a second to answer must not spend a
            // second's worth of slots showing the encoder nothing.
            long baseline = 0;
            long slot = 0;
            bool started = false;

            while (target is null || emitted < target.Value)
            {
                token.ThrowIfCancellationRequested();

                if (!started)
                {
                    bool got = false;
                    long giveUp = Stopwatch.GetTimestamp()
                                + (long)(spec.StartDeadline.TotalSeconds * Stopwatch.Frequency);
                    int spins = 0;

                    while (true)
                    {
                        if (channel.TryTake(frame)) { got = true; break; }
                        if (channel.Completed) break;
                        token.ThrowIfCancellationRequested();
                        capture.ThrowIfFailed();

                        // Timed, and backed off. Spinning at 1000 Hz for half a
                        // minute is 30 000 wasted wake-ups, and without the
                        // deadline it is not half a minute but forever.
                        if (Stopwatch.GetTimestamp() >= giveUp)
                        {
                            throw new InvalidOperationException(
                                $"the decoder produced no frame within {spec.StartDeadline.TotalSeconds:F0}s");
                        }

                        Thread.Sleep(spins++ < 100 ? 1 : 5);
                    }

                    // The source produced nothing at all.
                    if (!got) break;

                    started = true;
                    baseline = Stopwatch.GetTimestamp();
                    fresh++;
                }
                else
                {
                    slot++;
                    FineTimer.WaitUntil(baseline + (slot * slotTicks));

                    // Ask the ladder whether this slot may carry a new picture.
                    // Everything the throttle does lives in these four lines.
                    bool wantsFresh = true;
                    if (control is not null)
                    {
                        cadence += control.Ladder.CurrentFps / spec.Fps;
                        wantsFresh = cadence >= 1.0;
                        if (wantsFresh) cadence -= 1.0;
                    }

                    if (wantsFresh && control is not null)
                    {
                        // Throttled: take the newest and drop the rest, so the
                        // encoder never receives a picture older than the one it
                        // already has while the queue fills behind it. The count
                        // of what was skipped comes from the channel's own tally
                        // at the end -- counting it here as well double-counted
                        // every skipped frame.
                        if (channel.TryTakeNewest(frame, out _)) { fresh++; }
                        else
                        {
                            if (target is null && channel.Completed) break;
                            repeated++;
                            if (channel.Completed) frozenAtEnd++;
                        }
                    }
                    else if (wantsFresh)
                    {
                        if (channel.TryTake(frame)) fresh++;
                        else
                        {
                            if (target is null && channel.Completed) break;
                            repeated++;
                            if (channel.Completed) frozenAtEnd++;
                        }
                    }
                    else
                    {
                        // Throttled and not this slot's turn: repeat. The frame
                        // still in the buffer is the previous one by construction.
                        repeated++;
                        if (channel.Completed) frozenAtEnd++;
                    }
                }

                tick?.Invoke(emitted);
                WriteFrame(sink, frame, spec.FrameSize, spec.WriteDeadline, token);
                emitted++;

                if (control is not null)
                {
                    (produced, measured) = ObserveBitrate(control, produced);
                }

                if (progress is not null && emitted % 4 == 0) progress(emitted, spec.TargetFrames);
            }

            double elapsed = FineTimer.SecondsSince(start);
            capture.Stop();

            // "The source ended" has to be an answer, not a race. The producer
            // learns about the end of the stream on its own thread, so a run that
            // reaches its target in the same millisecond the source runs out can
            // read the flag before the producer has set it -- and then the report
            // says the source was still going when it was not. Measured on a
            // loaded two-core box: the same test passed alone and failed with
            // three other suites running.
            //
            // The grace is short on purpose. A live source never ends, so the
            // producer stays blocked in its read and this costs the grace once
            // per run; the channel already knows it is complete when there is
            // nothing to wait for, in which case there is nothing to wait for.
            if (!channel.Completed) capture.WaitForEnd(EndGrace);

            capture.ThrowIfFailed();
            discarded = channel.Dropped;

            return new PumpStats
            {
                Frames = emitted,
                Fresh = fresh,
                Repeated = repeated,

                // Measured before the wait above: the grace is teardown, not
                // pacing, and folding it into the run's wall clock would make the
                // reported rate depend on how long the source took to admit it had
                // finished.
                Seconds = elapsed,
                SourceEnded = capture.Ended,
                Dropped = discarded,
                FrozenAtEnd = frozenAtEnd,
                FinalRung = control?.Ladder.Rung ?? 0,
                MotionFps = control?.Ladder.CurrentFps ?? spec.Fps,
                MeasuredKbps = measured,
            };
        }
        finally
        {
            capture.Stop();
            FineTimer.Disable();
        }
    }

    /// <summary>
    /// One bitrate reading: how much the muxer produced since the last slot,
    /// folded into the window over the programme time it covers, and -- once the
    /// window means something -- offered to the ladder.
    ///
    /// <para>Measured against the muxer's own <c>out_time</c>, not the wall clock.
    /// The wall clock includes every moment this machine was busy elsewhere, and
    /// dividing bytes by it reports a rate lower than the file's: on a loaded box
    /// the ladder would never descend, and the output would quietly be over its
    /// ceiling for the whole run.</para>
    /// </summary>
    private static (EncoderProgress Progress, double Kbps) ObserveBitrate(
        LadderControl control, EncoderProgress previous)
    {
        EncoderProgress now = control.Progress();
        double media = now.MediaSeconds;

        // Nothing written yet, or the muxer cannot report: no reading rather than
        // a division by zero that would look like an infinite bitrate.
        if (media <= previous.MediaSeconds) return (now, control.Window.Kbps());

        long delta = Math.Max(0, now.Bytes - previous.Bytes);
        control.Window.Add(media, (int)Math.Min(delta, int.MaxValue));

        // A rate needs bytes and time, nothing more. An earlier version waited
        // for the window to fill before reading at all, which sounded careful and
        // cost a third of a short clip: on six seconds of source the ladder did
        // not look at anything until 2 s had gone by, so v8 spent those two
        // seconds at full rate while v7 was already throttling, and the file came
        // out 38% larger than v7's. The window is a smoothing device, not a
        // warm-up: with a span denominator a half-second reading is a true
        // half-second rate.
        if (control.Window.CoveredSeconds > 0)
        {
            double video = Math.Max(0.0, control.Window.Kbps() - control.AudioKbps);
            double before = control.Ladder.CurrentFps;
            if (control.Ladder.Observe(video, media)) // the rung moved
            {
                control.Moved?.Invoke(new LadderMove(
                    before, control.Ladder.CurrentFps, video, control.Window.CoveredSeconds));
            }

            return (now, video);
        }

        return (now, control.Window.Kbps());
    }

    /// <summary>
    /// Hand one frame to the encoder, with a deadline.
    ///
    /// <para>A synchronous write blocks for as long as the encoder refuses to
    /// drain its input -- a stalled network target and a muxer waiting on its own
    /// buffer are both real -- and this loop would only notice a cancel at its
    /// top. v7 learned that the hard way: Ctrl+C did nothing, the cleanup never
    /// ran, and two FFmpeg children were left behind.</para>
    /// </summary>
    private static void WriteFrame(
        Stream sink, byte[] payload, int count, TimeSpan deadline, CancellationToken token)
    {
        // Held in a local so the timeout path can observe it. Without that, a write
        // that outlives its deadline finishes as a faulted task nobody awaits: the
        // run reports "the encoder stopped accepting frames", which is the right
        // sentence, while the actual reason stays in an unobserved exception.
        Task write = sink.WriteAsync(payload.AsMemory(0, count), token).AsTask();

        try
        {
            write.WaitAsync(deadline, token).GetAwaiter().GetResult();
        }
        catch (TimeoutException exc)
        {
            _ = write.ContinueWith(
                finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Told apart from a cancel on purpose: a cancelled run is the user's
            // decision and exits 130, while a wedged encoder is a failure that has
            // to say how long it waited. Without this the report is the word
            // "TimeoutException", which says nothing about who was stuck.
            throw new InvalidOperationException(
                $"the encoder stopped accepting frames: no room in its input for {deadline.TotalSeconds:F0}s", exc);
        }

        sink.Flush();
    }

    /// <summary>The producer side: mostly-blocked read, one frame per slot.</summary>
    private sealed class Capture
    {
        private readonly Stream _source;
        private readonly FrameChannel _channel;
        private readonly long _intervalTicks;
        private readonly Thread _thread;
        private volatile bool _stop;
        private Exception? _failure;
        private volatile bool _ended;

        internal Capture(Stream source, FrameChannel channel, double fps)
        {
            _source = source;
            _channel = channel;
            _intervalTicks = FineTimer.IntervalTicks(fps);
            _thread = new Thread(Loop) { IsBackground = true, Name = "frame-capture" };
        }

        /// <summary>True when the producer saw the end of the source.</summary>
        internal bool Ended => _ended;

        internal void Start() => _thread.Start();

        internal void Stop() => _stop = true;

        /// <summary>
        /// Wait for the producer to finish, up to <paramref name="grace"/>. False
        /// means it is still blocked in a read, which for a live source is the
        /// normal answer and not a problem: the thread is a background one and
        /// goes with the process.
        /// </summary>
        internal bool WaitForEnd(TimeSpan grace) => _thread.Join(grace);

        /// <summary>Rethrow whatever the producer died of, in the caller's thread.</summary>
        internal void ThrowIfFailed()
        {
            if (_failure is { } failure)
            {
                throw new InvalidOperationException($"frame capture failed: {failure.Message}", failure);
            }
        }

        private void Loop()
        {
            byte[] frame = new byte[_channel.FrameSize];
            long next = Stopwatch.GetTimestamp();

            try
            {
                while (!_stop)
                {
                    if (!ReadFrame(frame))
                    {
                        _ended = true;
                        break;
                    }

                    _channel.Publish(frame);

                    // Reset the deadline when the slot is already overdue instead
                    // of trying to catch up: a decoder that fell behind cannot
                    // gain that time back by running flat out.
                    next += _intervalTicks;
                    long now = Stopwatch.GetTimestamp();
                    if (next > now) FineTimer.WaitUntil(next);
                    else next = now;
                }
            }
            catch (ObjectDisposedException)
            {
                // The decoder was killed under us after the run finished. That is
                // the normal way a live source ends.
            }
            catch (Exception exc)
            {
                _failure = exc;
            }
            finally
            {
                _channel.Complete();
            }
        }

        /// <summary>Fill one whole frame; false at a clean end of stream.</summary>
        private bool ReadFrame(byte[] frame)
        {
            int filled = 0;
            while (filled < frame.Length)
            {
                int read = _source.Read(frame, filled, frame.Length - filled);
                if (read == 0) return false;
                filled += read;
            }

            return true;
        }
    }
}
