using Lae.Core;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The pacing loop, over memory streams.
///
/// <para>The point of testing it here rather than through an encode is that the
/// wall-clock contract is the one thing a real encode cannot answer precisely:
/// "one slot per 1/fps seconds, never more, never fewer" is only visible when the
/// source is instantaneous and the only thing left is the cadence.</para>
/// </summary>
public sealed class FramePumpTests
{
    /// <summary>A source whose frame <c>i</c> is a block filled with <c>i</c>.</summary>
    private static byte[] Script(int frames, int frameSize)
    {
        byte[] bytes = new byte[frames * frameSize];
        for (int i = 0; i < frames; i++) bytes.AsSpan(i * frameSize, frameSize).Fill((byte)i);
        return bytes;
    }

    private static List<int> IndicesOf(MemoryStream sink, int frameSize)
    {
        byte[] all = sink.ToArray();
        var indices = new List<int>(all.Length / frameSize);
        for (int offset = 0; offset + frameSize <= all.Length; offset += frameSize)
        {
            indices.Add(all[offset]);
        }

        return indices;
    }

    [Test]
    public async Task WritesExactlyTheRequestedNumberOfSlots()
    {
        const int frameSize = 8;
        const int target = 10;

        using var source = new MemoryStream(Script(64, frameSize));
        using var sink = new MemoryStream();

        var spec = new PumpSpec { Fps = 200, FrameSize = frameSize, TargetFrames = target };
        PumpStats stats = FramePump.Run(source, sink, spec, null, null, CancellationToken.None);

        await Assert.That(stats.Frames).IsEqualTo(target);
        await Assert.That(sink.Length).IsEqualTo((long)target * frameSize);
    }

    /// <summary>
    /// The buffer depth from the settings has to reach the channel, and one frame
    /// of depth has to be enough to make progress: the producer needs somewhere to
    /// put the picture it just decoded while the consumer is writing the previous
    /// one. A depth of zero was a setting nothing read; a depth of one that
    /// deadlocked would be worse.
    /// </summary>
    [Test]
    public async Task RunsWithASingleFrameOfBuffer()
    {
        const int frameSize = 4;
        const int target = 12;

        using var source = new MemoryStream(Script(64, frameSize));
        using var sink = new MemoryStream();

        var spec = new PumpSpec
        {
            Fps = 500,
            FrameSize = frameSize,
            TargetFrames = target,
            BufferFrames = 1,
        };

        PumpStats stats = FramePump.Run(source, sink, spec, null, null, CancellationToken.None);

        await Assert.That(stats.Frames).IsEqualTo(target);
        await Assert.That(sink.Length).IsEqualTo((long)target * frameSize);
    }

    /// <summary>
    /// The pump may repeat a frame, never rewind. A slot that showed frame 5 and
    /// then frame 3 would be a decoder writing out of order or a channel handing
    /// out an older buffer, and both would produce a visibly broken file.
    /// </summary>
    [Test]
    public async Task NeverGoesBackwardsInTheSource()
    {
        const int frameSize = 4;

        using var source = new MemoryStream(Script(40, frameSize));
        using var sink = new MemoryStream();

        var spec = new PumpSpec { Fps = 500, FrameSize = frameSize, TargetFrames = 20 };
        FramePump.Run(source, sink, spec, null, null, CancellationToken.None);

        List<int> shown = IndicesOf(sink, frameSize);
        await Assert.That(shown.Count).IsEqualTo(20);

        for (int i = 1; i < shown.Count; i++)
        {
            await Assert.That(shown[i]).IsGreaterThanOrEqualTo(shown[i - 1]);
        }
    }

    /// <summary>
    /// The cadence itself. Forty slots at 100 fps is 400 ms of wall clock; if the
    /// pump lands far outside that, the output file is the right length in frames
    /// and the wrong length in seconds, and every duration in the archive is off.
    ///
    /// <para>The window is generous on purpose (a loaded CI machine overshoots),
    /// but tight enough to catch a sleep granularity problem: without the 1 ms
    /// timer a 10 ms slot overshoots by ~40%.</para>
    /// </summary>
    [Test]
    public async Task HoldsTheRequestedCadence()
    {
        const int frameSize = 4;
        const int target = 40;

        using var source = new MemoryStream(Script(200, frameSize));
        using var sink = new MemoryStream();

        var spec = new PumpSpec { Fps = 100, FrameSize = frameSize, TargetFrames = target };
        PumpStats stats = FramePump.Run(source, sink, spec, null, null, CancellationToken.None);

        await Assert.That(stats.Seconds).IsBetween(0.36, 0.48)
            .Because($"{stats.Frames} slots at 100 fps is 0.40 s, not {stats.Seconds:F3} s");
    }

    /// <summary>
    /// A source shorter than the requested span keeps the last frame on screen
    /// for the remaining slots. That is what a live capture does, and it is what
    /// keeps the output exactly as long as <c>-t</c> asked for.
    /// </summary>
    [Test]
    public async Task FillsTheTargetWithTheLastFrameWhenTheSourceEndsEarly()
    {
        const int frameSize = 4;

        using var source = new MemoryStream(Script(3, frameSize));
        using var sink = new MemoryStream();

        var spec = new PumpSpec { Fps = 200, FrameSize = frameSize, TargetFrames = 8 };
        PumpStats stats = FramePump.Run(source, sink, spec, null, null, CancellationToken.None);

        await Assert.That(stats.Frames).IsEqualTo(8);
        await Assert.That(stats.SourceEnded).IsTrue();
        await Assert.That(sink.Length).IsEqualTo(8L * frameSize);

        List<int> shown = IndicesOf(sink, frameSize);
        await Assert.That(shown[^1]).IsEqualTo(2);
    }

    /// <summary>
    /// With no known length the run ends with the source: an indefinite slot count
    /// on a finished source would be an infinite file.
    /// </summary>
    [Test]
    public async Task EndsWithTheSourceWhenNoLengthIsKnown()
    {
        const int frameSize = 4;

        using var source = new MemoryStream(Script(5, frameSize));
        using var sink = new MemoryStream();

        var spec = new PumpSpec { Fps = 200, FrameSize = frameSize, TargetFrames = null };
        PumpStats stats = FramePump.Run(source, sink, spec, null, null, CancellationToken.None);

        await Assert.That(stats.SourceEnded).IsTrue();
        await Assert.That(stats.Frames).IsGreaterThanOrEqualTo(5L);
        await Assert.That(stats.Frames).IsLessThanOrEqualTo(8L);
    }

    [Test]
    public async Task HonoursCancellationWithoutWaitingForTheSpan()
    {
        const int frameSize = 4;

        using var source = new MemoryStream(Script(4000, frameSize));
        using var sink = new MemoryStream();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        var spec = new PumpSpec { Fps = 100, FrameSize = frameSize, TargetFrames = 4000 };

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                FramePump.Run(source, sink, spec, null, null, cancel.Token))
            ;

        // 120 ms at 100 fps is about twelve slots; anything near the full four
        // thousand means the token was only looked at between spans, not slots.
        await Assert.That(sink.Length).IsLessThan(200L * frameSize);
    }

    /// <summary>
    /// A consumer that never drains must not hold the run forever: 30 s of no
    /// progress means the encoder is wedged, and reporting that beats hanging.
    /// </summary>
    [Test]
    public async Task FailsWhenTheEncoderStopsDraining()
    {
        const int frameSize = 4;

        using var source = new MemoryStream(Script(100, frameSize));
        using var stuck = new NeverDrains();

        var spec = new PumpSpec
        {
            Fps = 200,
            FrameSize = frameSize,
            TargetFrames = 100,
            WriteDeadline = TimeSpan.FromMilliseconds(80),
        };

        try
        {
            // Wrapped on purpose: "TimeoutException" tells the user nothing about
            // who was stuck, so the pump says which side stopped draining and how
            // long it waited, and keeps the timeout as the inner exception for
            // anyone reading a log.
            InvalidOperationException failure =
                (await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    FramePump.Run(source, stuck, spec, null, null, CancellationToken.None)))!;

            await Assert.That(failure.Message.Contains("stopped accepting frames", StringComparison.Ordinal)).IsTrue();
            await Assert.That(failure.InnerException is TimeoutException).IsTrue();
        }
        finally
        {
            stuck.Release();
        }
    }

    /// <summary>A sink that accepts nothing until it is told to; stands in for a wedged encoder.</summary>
    private sealed class NeverDrains : Stream
    {
        private readonly ManualResetEventSlim _open = new(false);

        public void Release() => _open.Set();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _open.Wait();

        public override void Write(ReadOnlySpan<byte> buffer) => _open.Wait();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => Task.Run(() => _open.Wait(token), token);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
            => new(Task.Run(() => _open.Wait(token), token));

        protected override void Dispose(bool disposing)
        {
            if (disposing) _open.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// What the throttle does to the pipe.
///
/// <para>The behaviour worth pinning is not "the ladder moved" -- that is
/// arithmetic, tested on its own -- but "a throttled run still writes every slot
/// and spends most of them on the frame it already has". Those are different
/// claims: a pump that stopped writing would produce a short file, and a pump
/// that kept writing fresh frames would not lower the bitrate at all.</para>
/// </summary>
public sealed class FramePumpLadderTests
{
    private static byte[] Script(int frames, int frameSize)
    {
        byte[] bytes = new byte[frames * frameSize];
        for (int i = 0; i < frames; i++) bytes.AsSpan(i * frameSize, frameSize).Fill((byte)i);
        return bytes;
    }

    /// <summary>
    /// The muxer, as the pump sees it: 40 kB written per report, and a tenth of
    /// a second of programme per report. The rate is deliberately absurd (about
    /// 3 Mbps), because the test is about what happens when the ceiling is
    /// blown, not about how it is blown.
    /// </summary>
    private sealed class ByteCounter
    {
        private long _bytes;
        private double _media;

        public EncoderProgress Next()
        {
            _bytes += 40_000;
            _media += 0.1;
            return new EncoderProgress(_bytes, _media);
        }
    }

    [Test]
    public async Task WithNoLadderEverySlotCarriesWhatTheSourceProduced()
    {
        const int frameSize = 4;
        const int fps = 40;
        using var source = new MemoryStream(Script(200, frameSize));
        using var sink = new MemoryStream();

        var spec = new PumpSpec { Fps = fps, FrameSize = frameSize, TargetFrames = 20 };
        PumpStats stats = FramePump.Run(source, sink, spec, null, null, CancellationToken.None);

        // No ladder means no throttle: whatever the producer managed to hand over
        // is what goes in, and the rung never moves. How many of the twenty slots
        // caught a fresh frame depends on how the two threads were scheduled, so
        // that is not a number to pin.
        await Assert.That(stats.Fresh + stats.Repeated).IsEqualTo(20L);
        await Assert.That(stats.FinalRung).IsEqualTo(0);
        await Assert.That(stats.MotionFps).IsEqualTo(40.0);
        await Assert.That(stats.Dropped).IsEqualTo(0L);
    }

    /// <summary>
    /// Above the ceiling the pump keeps writing slots but stops taking new
    /// pictures: the file stays the same length in seconds, the bitrate falls.
    /// </summary>
    [Test]
    public async Task ABitrateAboveTheCeilingTradesFreshFramesForRepeats()
    {
        const int frameSize = 4;
        const int fps = 40;
        const int target = 40;

        using var source = new MemoryStream(Script(400, frameSize));
        using var sink = new MemoryStream();

        var counter = new ByteCounter();
        var moves = new List<LadderMove>();

        FpsLadder ladder = LadderPolicy.FromDefaults(new EncodeDefaults
        {
            Fps = fps,
            FpsLevels = [40, 10],
            LimitKbps = 200,
            RecoverKbps = 150,
        })!;

        var spec = new PumpSpec
        {
            Fps = fps,
            FrameSize = frameSize,
            TargetFrames = target,
            Ladder = new LadderControl
            {
                Ladder = ladder,
                Window = new BitrateWindow(0.2),
                Progress = counter.Next,
                Moved = moves.Add,
            },
        };

        PumpStats stats = FramePump.Run(source, sink, spec, null, null, CancellationToken.None);

        // The duration is untouched -- that is the whole point of throttling by
        // repeating rather than by writing fewer slots.
        await Assert.That(stats.Frames).IsEqualTo(target);
        await Assert.That(sink.Length).IsEqualTo((long)target * frameSize);

        await Assert.That(stats.FinalRung).IsEqualTo(1);
        await Assert.That(stats.MotionFps).IsEqualTo(10.0);
        await Assert.That(moves.Count).IsEqualTo(1);
        await Assert.That(moves[0].ToFps).IsEqualTo(10.0);

        // One fresh frame in four after the drop, against forty slots.
        await Assert.That(stats.Fresh).IsLessThan(26L);
        await Assert.That(stats.Repeated).IsGreaterThan(14L);
    }

    /// <summary>
    /// Frames the ladder refuses to show have to leave the queue. A pump that
    /// left them there would feed the encoder a picture from a quarter of a
    /// second ago for the rest of the run.
    /// </summary>
    [Test]
    public async Task SkippedFramesAreDroppedRatherThanQueued()
    {
        const int frameSize = 4;
        const int fps = 40;

        using var source = new MemoryStream(Script(400, frameSize));
        using var sink = new MemoryStream();

        var counter = new ByteCounter();

        FpsLadder ladder = LadderPolicy.FromDefaults(new EncodeDefaults
        {
            Fps = fps,
            FpsLevels = [40, 10],
            LimitKbps = 200,
        })!;

        var spec = new PumpSpec
        {
            Fps = fps,
            FrameSize = frameSize,
            TargetFrames = 40,
            Ladder = new LadderControl
            {
                Ladder = ladder,
                Window = new BitrateWindow(0.2),
                Progress = counter.Next,
            },
        };

        PumpStats stats = FramePump.Run(source, sink, spec, null, null, CancellationToken.None);

        await Assert.That(stats.Dropped).IsGreaterThan(0L);

        // A skipped frame can only be counted once. The producer publishes one
        // frame per slot, so the tally is the frames that were not taken -- which
        // can never exceed the slots themselves. Counting the discards inside
        // TryTakeNewest *and* reading the channel's total afterwards doubled it,
        // and a report that claims more dropped frames than the file has is a
        // report nobody can use to compare two runs.
        await Assert.That(stats.Dropped).IsLessThan(stats.Frames);

        // And the picture still moves forward: a run that kept its oldest frame
        // would show block 0 for three quarters of the file.
        byte[] all = sink.ToArray();
        await Assert.That(all[^frameSize]).IsGreaterThan((byte)0);
    }
}
