using Lae.Core;

namespace Lae.Tests.Core;

/// <summary>
/// Reading <c>ffmpeg -progress</c>.
///
/// <para>This parser is the ladder's instrument, so it gets its own tests: the
/// first version of v8 measured the output by asking the file how long it was, and
/// on Windows that answer is served from a directory entry that only refreshes
/// when the writer closes -- the ladder spent a whole encode reading 1017 kbps and
/// then 0 kbps. The lines below are what FFmpeg actually emits.</para>
/// </summary>
public sealed class FfmpegProgressTests
{
    [Test]
    public async Task ReadsTheBytesAndTheProgrammeTime()
    {
        EncoderProgress current = default;
        bool blockEnded = false;

        foreach (string line in new[]
                 {
                     "frame=13", "fps=13.0", "total_size=40960", "out_time_us=1000000",
                     "out_time_ms=1000", "speed=1.01x", "progress=continue",
                 })
        {
            blockEnded = FfmpegProgress.TryRead(line, ref current);
        }

        await Assert.That(blockEnded).IsTrue();
        await Assert.That(current.Bytes).IsEqualTo(40960L);
        await Assert.That(current.MediaSeconds).IsEqualTo(1.0);
    }

    /// <summary>
    /// Some muxers cannot report a size. That is "no reading", not "wrote
    /// nothing": reporting zero would tell the ladder the encode had stopped
    /// producing output, and it would climb back to full rate.
    /// </summary>
    [Test]
    public async Task AnUnknownSizeLeavesThePreviousValue()
    {
        EncoderProgress current = default;
        FfmpegProgress.TryRead("total_size=40960", ref current);
        FfmpegProgress.TryRead("progress=continue", ref current);

        FfmpegProgress.TryRead("total_size=N/A", ref current);

        await Assert.That(current.Bytes).IsEqualTo(40960L);
    }

    [Test]
    public async Task ValueLinesDoNotEndABlock()
    {
        EncoderProgress current = default;

        await Assert.That(FfmpegProgress.TryRead("total_size=1", ref current)).IsFalse();
        await Assert.That(FfmpegProgress.TryRead("out_time_us=1", ref current)).IsFalse();
        await Assert.That(FfmpegProgress.TryRead("bitrate=2.0kbits/s", ref current)).IsFalse();
        await Assert.That(FfmpegProgress.TryRead("progress=end", ref current)).IsTrue();
    }

    /// <summary>Older builds report milliseconds and no microseconds at all.</summary>
    [Test]
    public async Task MillisecondsStandInForMicroseconds()
    {
        EncoderProgress current = default;
        FfmpegProgress.TryRead("out_time_ms=2500", ref current);

        await Assert.That(current.MediaSeconds).IsEqualTo(2.5);
    }

    [Test]
    public async Task GarbageIsIgnoredRatherThanThrown()
    {
        EncoderProgress current = default;

        await Assert.That(FfmpegProgress.TryRead("total_size=", ref current)).IsFalse();
        await Assert.That(FfmpegProgress.TryRead("out_time_us=abc", ref current)).IsFalse();
        await Assert.That(FfmpegProgress.TryRead("", ref current)).IsFalse();
        await Assert.That(current.Bytes).IsEqualTo(0L);
    }
}
