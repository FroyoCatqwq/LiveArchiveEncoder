using Lae.Core;

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
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                FramePump.Run(source, stuck, spec, null, null, CancellationToken.None));
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
