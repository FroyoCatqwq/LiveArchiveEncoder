using System.Diagnostics;

namespace Lae.Core;

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
    /// How long one frame may sit unpicked in the encoder's input before the run
    /// is declared stuck. One slot is at most a third of a second of programme,
    /// so 30 s means "not slow, wedged".
    /// </summary>
    public TimeSpan WriteDeadline { get; init; } = TimeSpan.FromSeconds(30);
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
    /// Pump until the target slot count is reached or the source runs out.
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
    {        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spec.Fps);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.FrameSize, 1);

        var channel = new FrameChannel(spec.FrameSize);
        var capture = new Capture(source, channel, spec.Fps);
        long start = Stopwatch.GetTimestamp();
        long slotTicks = FineTimer.IntervalTicks(spec.Fps);

        // One buffer, because the consumer is the only thread that touches it:
        // it is filled from the channel and then written out, in that order.
        byte[] frame = new byte[spec.FrameSize];
        long emitted = 0;
        long fresh = 0;
        long repeated = 0;

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
                    while (true)
                    {
                        if (channel.TryTake(frame)) { got = true; break; }
                        if (channel.Completed) break;
                        token.ThrowIfCancellationRequested();
                        capture.ThrowIfFailed();
                        Thread.Sleep(1);
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

                    if (channel.TryTake(frame))
                    {
                        fresh++;
                    }
                    else
                    {
                        // Nothing waiting. With a target to fill the previous
                        // frame is repeated, exactly as a capture card does while
                        // the picture is unchanged; without one there is no
                        // length to fill, so the run is over.
                        if (target is null && channel.Completed) break;
                        repeated++;
                    }
                }

                tick?.Invoke(emitted);
                WriteFrame(sink, frame, spec.FrameSize, spec.WriteDeadline, token);
                emitted++;

                if (progress is not null && emitted % 4 == 0) progress(emitted, spec.TargetFrames);
            }

            capture.Stop();
            capture.ThrowIfFailed();

            return new PumpStats
            {
                Frames = emitted,
                Fresh = fresh,
                Repeated = repeated,
                Seconds = FineTimer.SecondsSince(start),
                SourceEnded = capture.Ended,
            };
        }
        finally
        {
            capture.Stop();
            FineTimer.Disable();
        }
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
        sink.WriteAsync(payload.AsMemory(0, count), token)
            .AsTask()
            .WaitAsync(deadline, token)
            .GetAwaiter()
            .GetResult();
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
