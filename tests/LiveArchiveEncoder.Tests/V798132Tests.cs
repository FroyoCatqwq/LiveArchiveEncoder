using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>v7.9.8.13.2: regressions for the full-project audit.</summary>
public sealed class V798132Tests
{
    [Test]
    public async Task RoqSizeValidationIsAQueryNotAnException()
    {
        // The CUI config page and Report only print this. Throwing crashed
        // both screens; the encoder preflight is what actually rejects a run.
        string? bad = EncoderMath.ValidateRoqDimensions(250, 190);
        await Assert.That(bad).IsNotNull();
        await Assert.That(bad!).Contains("divisible by 16");
        await Assert.That(EncoderMath.ValidateRoqDimensions(256, 192))
            .Contains("power-of-two");
        await Assert.That(EncoderMath.ValidateRoqDimensions(256, 256)).IsNull();
    }

    [Test]
    public async Task PublishingPathDropsStaleManagedBins()
    {
        string root = Path.GetFullPath(Toolchain.Root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string stale = Path.Combine(root, "nonfree", "bin");
        string keep = Path.Combine(root, "btbn", "bin");
        char sep = OperatingSystem.IsWindows() ? ';' : ':';
        string input = string.Join(sep, ["/usr/bin", stale, keep, "/opt/custom/bin"]);

        string cleaned = ToolchainPath.PruneManagedBins(input, keep, sep);

        // Switching distributions used to stack entries: the oldest one won in
        // a new shell, so `ffmpeg` still ran the build we switched away from.
        await Assert.That(cleaned.Contains(stale)).IsFalse();
        await Assert.That(cleaned.Contains(keep)).IsTrue();
        await Assert.That(cleaned.Contains("/usr/bin")).IsTrue();
        await Assert.That(cleaned.Contains("/opt/custom/bin")).IsTrue();
    }

    [Test]
    public async Task TheDailyFeedSourceHasAWorkingStableFallback()
    {
        FfmpegDist auto = FfmpegCatalog.FindKnown("autobuild", DistPlatform.Windows)!;
        await Assert.That(auto.FeedUrl).IsNotNull();
        // v7.9.8.13.2: StableUrl was empty on every source, which made
        // "no daily -> use stable" dead code. Only the feed-driven source
        // needs one, because the fixed-URL sources ARE the rolling build.
        await Assert.That(auto.StableUrl).IsNotNull();
        await Assert.That(auto.StableUrl!).Contains("AnimMouse/ffmpeg-autobuild/releases/download/");
        await Assert.That(auto.StableUrl!).Contains("win64-nonfree.7z");
    }
}
