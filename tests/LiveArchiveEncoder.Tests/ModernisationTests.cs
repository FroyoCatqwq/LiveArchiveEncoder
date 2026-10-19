using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.8.1 cleanup: the codebase picked up .NET 10 / C# 14 features and retired
/// everything no call site reached. Both are refactors, so both need guards --
/// these cases pin the behaviour that had to survive them.
/// </summary>
[TestFixture]
public sealed class ModernisationTests
{
    // ---- C# 14 extension members ------------------------------------------

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("   ", false)]
    [TestCase("\t\n", false)]
    [TestCase("a", true)]
    [TestCase(" a ", true)]
    public void HasTextIsTheNegationOfIsNullOrWhiteSpace(string? value, bool expected)
    {
        Assert.That(value.HasText, Is.EqualTo(expected));
        Assert.That(value.HasText, Is.EqualTo(!string.IsNullOrWhiteSpace(value)));
    }

    [Test]
    public void OrFallsBackOnlyWhenTheTextHasNothing()
    {
        Assert.That(((string?)null).Or("black"), Is.EqualTo("black"));
        Assert.That(((string?)"  ").Or("black"), Is.EqualTo("black"));
        Assert.That(((string?)"0x00FF00").Or("black"), Is.EqualTo("0x00FF00"));
    }

    [Test]
    public void HasTextNarrowsTheValueForTheCompiler()
    {
        // The [NotNullWhen(true)] on the extension receiver is what makes this
        // compile without a nullable warning; if it is ever dropped, the build
        // says so here rather than in fifty call sites.
        string? maybe = "value";
        if (!maybe.HasText) Assert.Fail("unreachable");
        Assert.That(maybe.Length, Is.EqualTo(5));
    }

    // ---- One generated regex replaces 28 patterns -------------------------

    /// <summary>
    /// The exact name list the old <c>Constants.LegacyEncoderPatterns</c>
    /// accepted (one sample per pattern, including both alternations of the
    /// ones with optional parts). Kept here so the swap to a single
    /// source-generated regex is proven equivalent rather than assumed.
    /// </summary>
    private static readonly string[] LegacyNames =
    [
        "a64multi", "a64multi5", "amv", "asv1", "asv2", "cinepak", "cljr",
        "flashsv", "flashsv2", "flv", "h261", "h263", "h263p", "mjpeg",
        "msmpeg4", "msmpeg4v2", "msrle", "msvideo1", "mpeg1video", "mpeg2video",
        "mpeg4video", "mpeg4", "qtrle", "roqvideo", "rpza", "rv10", "rv20",
        "smc", "snow", "svq1", "wmv1", "wmv2", "zmbv", "libtheora", "libx264",
        "libxvid", "vivo",
    ];

    [Test]
    public void EveryNameTheOldPatternListAcceptedIsStillLegacy()
    {
        foreach (string name in LegacyNames)
        {
            Assert.That(FFmpegDiscovery.LegacyFamily(name, ""), Is.Not.Null,
                $"{name} stopped being recognised as a legacy encoder");
        }
    }

    [TestCase("libx265")]
    [TestCase("h264_nvenc")]
    [TestCase("av1_nvenc")]
    [TestCase("libvpx")]
    [TestCase("libaom-av1")]
    [TestCase("hevc_qsv")]
    [TestCase("rawvideo")]
    [TestCase("png")]
    [TestCase("mpeg2_qsv")]
    public void ModernAndNonArchiveEncodersAreStillRejected(string name)
    {
        Assert.That(FFmpegDiscovery.LegacyFamily(name, ""), Is.Null);
    }

    [Test]
    public void LegacyFamilyKeepsAssigningFamilies()
    {
        Assert.That(FFmpegDiscovery.LegacyFamily("libx264", ""), Is.EqualTo("classic-library"));
        Assert.That(FFmpegDiscovery.LegacyFamily("mpeg4", ""), Is.EqualTo("mpeg-classic"));
        Assert.That(FFmpegDiscovery.LegacyFamily("wmv2", ""), Is.EqualTo("proprietary-classic"));
        Assert.That(FFmpegDiscovery.LegacyFamily("a64multi", "Commodore 64"),
            Is.EqualTo("home-computer"));
        Assert.That(FFmpegDiscovery.LegacyFamily("snow", ""), Is.EqualTo("retro/classic"));
    }

    // ---- Frozen lookup tables ---------------------------------------------

    [Test]
    public void FrozenTablesStillAnswerExactlyAsBefore()
    {
        Assert.That(Constants.DefaultQuality["mjpeg"], Is.EqualTo(6));
        Assert.That(EncoderMath.DefaultQuality("mjpeg"), Is.EqualTo(6));
        Assert.That(EncoderMath.DefaultQuality("never-heard-of-it"), Is.EqualTo(10));

        Assert.That(Constants.FormatExtensions["matroska"], Does.Contain(".mkv"));
        Assert.That(Constants.FormatExtensions.ContainsKey("matroska"), Is.True);
        // Ordinal, as it always was: the call sites lower-case before asking.
        Assert.That(Constants.FormatExtensions.ContainsKey("MATROSKA"), Is.False);

        Assert.That(Constants.NoAudioFormats.Contains("h261"), Is.True);
        Assert.That(Constants.ConcatSafeFormats.Contains("roq"), Is.False);
        Assert.That(Constants.VideoExtensions.Contains(".AVI"), Is.False);
    }

    // ---- ResolveRange: a dead assignment removed --------------------------

    [Test]
    public void AnUnusableSourceDurationIsTreatedAsUnknown()
    {
        // ffprobe answers "N/A" as 0 or NaN for some containers. Both must fall
        // through to "no duration known" instead of throwing, which is what the
        // removed `durationValue = null;` branch used to do.
        Assert.That(Timecode.ResolveRange(0, null, 0).Duration, Is.Null);
        Assert.That(Timecode.ResolveRange(0, null, double.NaN).Duration, Is.Null);
        Assert.That(Timecode.ResolveRange(0, null, -5).Duration, Is.Null);
    }

    [Test]
    public void AKnownSourceDurationStillClampsTheEndTime()
    {
        (double? end, double? duration) = Timecode.ResolveRange(1.0, 9.0, 10.0);
        Assert.That(end, Is.EqualTo(9.0).Within(1e-9));
        Assert.That(duration, Is.EqualTo(8.0).Within(1e-9));

        // No end time: the source duration becomes the end of the range.
        Assert.That(Timecode.ResolveRange(1.0, null, 10.0).Duration, Is.EqualTo(9.0).Within(1e-9));

        // A start at or past the end, or an end past the source, is still an error.
        Assert.That(() => Timecode.ResolveRange(20.0, null, 10.0), Throws.ArgumentException);
        Assert.That(() => Timecode.ResolveRange(1.0, 90.0, 10.0), Throws.ArgumentException);
    }
}
