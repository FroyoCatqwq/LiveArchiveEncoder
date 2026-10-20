using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Hardware-decode selection.
///
/// No GPU and no FFmpeg are assumed: the point is the policy -- "off" is the
/// default, and a method that cannot be verified here degrades to software
/// instead of being handed to FFmpeg and failing mid-encode.
/// </summary>
public sealed class HwaccelTests
{
    private static readonly FileInfo NoSource = new("no-such-media.mp4");

    [Test]
    [Arguments("off")]
    [Arguments("OFF")]
    [Arguments("")]
    [Arguments(null)]
    public async Task OffResolvesWithoutTouchingFFmpeg(string? ask) =>
        await Assert.That(Hwaccel.Resolve(ask, "definitely-not-ffmpeg", NoSource, null)).IsEqualTo(Hwaccel.Off);

    [Test]
    public async Task DefaultIsOff()
    {
        // The regression this guards: v7.4 defaulted to CUDA on, and every
        // headless box (all Linux CI, any Windows box without an NVIDIA stack)
        // died on the first frame with "Cannot load libcuda.so.1".
        await Assert.That(Hwaccel.Off).IsEqualTo("off");
        await Assert.That(Hwaccel.Resolve(null, "definitely-not-ffmpeg", NoSource, null)).IsEqualTo(Hwaccel.Off);
    }

    /// <summary>
    /// A method the binary was not built with, or a binary we cannot even run,
    /// must degrade to software -- never throw, never pass something broken to
    /// FFmpeg.
    /// </summary>
    [Test]
    [Arguments("cuda")]
    [Arguments("vaapi")]
    [Arguments("not-a-method")]
    [Arguments("auto")]
    public async Task AnythingUnverifiableFallsBackToSoftware(string ask)
    {
        var notes = new List<string>();

        await Assert.That(Hwaccel.Resolve(ask, "definitely-not-ffmpeg", NoSource, notes.Add)).IsEqualTo(Hwaccel.Off);
        await Assert.That(notes).IsNotEmpty().Because("the fallback has to be visible to the user");
    }

    [Test]
    public async Task PreferenceOrderStartsWithThePlatformsOwnMethod()
    {
        IReadOnlyList<string> order = Hwaccel.PreferenceOrder;

        await Assert.That(order).IsNotEmpty();
        await Assert.That(order).Contains("cuda");
        // Windows leads with cuda, Linux with vaapi; the original expressed
        // that as a conditional constraint, which the fluent API cannot take.
        string expected = OperatingSystem.IsWindows() ? "cuda" : "vaapi";
        await Assert.That(order[0]).IsEqualTo(expected);
        await Assert.That(order.Distinct().Count()).IsEqualTo(order.Count);
    }

    [Test]
    public async Task CompiledIn_NeverThrowsOnAMissingBinary() =>
        await Assert.That(Hwaccel.CompiledIn("definitely-not-ffmpeg")).IsEmpty();
}
