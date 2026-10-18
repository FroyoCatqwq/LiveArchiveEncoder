using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Hardware-decode selection.
///
/// No GPU and no FFmpeg are assumed: the point is the policy -- "off" is the
/// default, and a method that cannot be verified here degrades to software
/// instead of being handed to FFmpeg and failing mid-encode.
/// </summary>
[TestFixture]
public sealed class HwaccelTests
{
    private static readonly FileInfo NoSource = new("no-such-media.mp4");

    [TestCase("off")]
    [TestCase("OFF")]
    [TestCase("")]
    [TestCase(null)]
    public void OffResolvesWithoutTouchingFFmpeg(string? ask) =>
        Assert.That(Hwaccel.Resolve(ask, "definitely-not-ffmpeg", NoSource, null),
            Is.EqualTo(Hwaccel.Off));

    [Test]
    public void DefaultIsOff()
    {
        // The regression this guards: v7.4 defaulted to CUDA on, and every
        // headless box (all Linux CI, any Windows box without an NVIDIA stack)
        // died on the first frame with "Cannot load libcuda.so.1".
        Assert.That(Hwaccel.Off, Is.EqualTo("off"));
        Assert.That(Hwaccel.Resolve(null, "definitely-not-ffmpeg", NoSource, null),
            Is.EqualTo(Hwaccel.Off));
    }

    /// <summary>
    /// A method the binary was not built with, or a binary we cannot even run,
    /// must degrade to software -- never throw, never pass something broken to
    /// FFmpeg.
    /// </summary>
    [TestCase("cuda")]
    [TestCase("vaapi")]
    [TestCase("not-a-method")]
    [TestCase("auto")]
    public void AnythingUnverifiableFallsBackToSoftware(string ask)
    {
        var notes = new List<string>();

        Assert.That(Hwaccel.Resolve(ask, "definitely-not-ffmpeg", NoSource, notes.Add),
            Is.EqualTo(Hwaccel.Off));
        Assert.That(notes, Is.Not.Empty, "the fallback has to be visible to the user");
    }

    [Test]
    public void PreferenceOrderStartsWithThePlatformsOwnMethod()
    {
        IReadOnlyList<string> order = Hwaccel.PreferenceOrder;

        Assert.That(order, Is.Not.Empty);
        Assert.That(order, Does.Contain("cuda"));
        Assert.That(order[0], OperatingSystem.IsWindows()
            ? Is.EqualTo("cuda")
            : Is.EqualTo("vaapi"));
        Assert.That(order.Distinct().Count(), Is.EqualTo(order.Count));
    }

    [Test]
    public void CompiledIn_NeverThrowsOnAMissingBinary() =>
        Assert.That(Hwaccel.CompiledIn("definitely-not-ffmpeg"), Is.Empty);
}
