using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.Update;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.11.1: MJPEG has no rate control. The quantiser fixes the frame size,
/// so the bitrate ladder has to be calibrated from that size rather than from a
/// limit the encoder physically cannot reach.
/// </summary>
public sealed class IntraQualityTests
{
    private static readonly double[] Levels = [13.0, 8.0, 5.0, 3.0];

    [Test]
    public async Task OnlyMjpegIsTreatedAsIntraOnly()
    {
        await Assert.That(IntraQuality.IsIntraOnly("mjpeg")).IsTrue();
        await Assert.That(IntraQuality.IsIntraOnly("MJPEG")).IsTrue();
        await Assert.That(IntraQuality.IsIntraOnly("mpeg4")).IsFalse();
        await Assert.That(IntraQuality.IsIntraOnly(null)).IsFalse();
    }

    [Test]
    public async Task KbpsFollowsFrameSizeTimesFps()
    {
        // Measured 2026-11-07, 256x192 yuvj420p, q:v 6: 6616 B/frame.
        double kbps = IntraQuality.KbpsFor(6616, 13);
        await Assert.That(kbps).IsGreaterThan(687);
        await Assert.That(kbps).IsLessThan(689);
        await Assert.That(IntraQuality.KbpsFor(6616, 3)).IsLessThan(160);
    }

    [Test]
    public async Task RungForPicksTheShallowestReachableRung()
    {
        // 6616 B/frame -> 688 / 423 / 265 / 159 kbps at 13/8/5/3 fps.
        await Assert.That(IntraQuality.RungFor(6616, Levels, 500)).IsEqualTo(1);
        await Assert.That(IntraQuality.RungFor(6616, Levels, 300)).IsEqualTo(2);
        await Assert.That(IntraQuality.RungFor(6616, Levels, 200)).IsEqualTo(3);
        // No rung can fit: dropping frames would only stutter the picture.
        await Assert.That(IntraQuality.RungFor(6616, Levels, 100)).IsEqualTo(0);
        // Already under the limit: stay on the base rate.
        await Assert.That(IntraQuality.RungFor(1000, Levels, 200)).IsEqualTo(0);
    }

    [Test]
    public async Task AudioAlignmentReachesBothTheEncoderAndTheSplitMux()
    {
        await Assert.That(string.Join(' ', ArgsBuilder.AudioAlignArgs()))
            .IsEqualTo("-af asetpts=PTS-STARTPTS");
        var mux = AudioTrack.BuildMuxCommand("ffmpeg", "v.avi", "a.mp3", 3.0, "avi", "out.avi");
        await Assert.That(mux.Contains("-shortest")).IsTrue();
        await Assert.That(mux.Contains("-t")).IsTrue();
    }

    [Test]
    public async Task FivePartReleaseTagsStillCompareCorrectly()
    {
        VersionTag current = VersionTag.Parse("v7.9.8.11")!;
        VersionTag next = VersionTag.Parse("v7.9.8.11.1")!;
        await Assert.That(next.IsNewerThan(current)).IsTrue();
        await Assert.That(next.ToString()).IsEqualTo("7.9.8.11.1");
        // A trailing zero is not a new release -- see VersionTag.GetHashCode.
        await Assert.That(VersionTag.Parse("v7.9.8.11.0")!.IsNewerThan(current)).IsFalse();
    }
}
