using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.8.1 cleanup: the codebase picked up .NET 10 / C# 14 features and retired
/// everything no call site reached. Both are refactors, so both need guards --
/// these cases pin the behaviour that had to survive them.
/// </summary>
public sealed class ModernisationTests
{
    // ---- C# 14 extension members ------------------------------------------

    [Test]
    [Arguments(null, false)]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments("\t\n", false)]
    [Arguments("a", true)]
    [Arguments(" a ", true)]
    public async Task HasTextIsTheNegationOfIsNullOrWhiteSpace(string? value, bool expected)
    {
        await Assert.That(value.HasText).IsEqualTo(expected);
        await Assert.That(value.HasText).IsEqualTo(!string.IsNullOrWhiteSpace(value));
    }

    [Test]
    public async Task OrFallsBackOnlyWhenTheTextHasNothing()
    {
        await Assert.That(((string?)null).Or("black")).IsEqualTo("black");
        await Assert.That(((string?)"  ").Or("black")).IsEqualTo("black");
        await Assert.That(((string?)"0x00FF00").Or("black")).IsEqualTo("0x00FF00");
    }

    [Test]
    public async Task HasTextNarrowsTheValueForTheCompiler()
    {
        // The [NotNullWhen(true)] on the extension receiver is what makes this
        // compile without a nullable warning; if it is ever dropped, the build
        // says so here rather than in fifty call sites.
        string? maybe = "value";
        if (!maybe.HasText) Fail.Test("unreachable");
        await Assert.That(maybe.Length).IsEqualTo(5);
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
    public async Task EveryNameTheOldPatternListAcceptedIsStillLegacy()
    {
        foreach (string name in LegacyNames)
        {
            await Assert.That(FFmpegDiscovery.LegacyFamily(name, "")).IsNotNull().Because($"{name} stopped being recognised as a legacy encoder");
        }
    }

    [Test]
    [Arguments("libx265")]
    [Arguments("h264_nvenc")]
    [Arguments("av1_nvenc")]
    [Arguments("libvpx")]
    [Arguments("libaom-av1")]
    [Arguments("hevc_qsv")]
    [Arguments("rawvideo")]
    [Arguments("png")]
    [Arguments("mpeg2_qsv")]
    public async Task ModernAndNonArchiveEncodersAreStillRejected(string name)
    {
        await Assert.That(FFmpegDiscovery.LegacyFamily(name, "")).IsNull();
    }

    [Test]
    public async Task LegacyFamilyKeepsAssigningFamilies()
    {
        await Assert.That(FFmpegDiscovery.LegacyFamily("libx264", "")).IsEqualTo("classic-library");
        await Assert.That(FFmpegDiscovery.LegacyFamily("mpeg4", "")).IsEqualTo("mpeg-classic");
        await Assert.That(FFmpegDiscovery.LegacyFamily("wmv2", "")).IsEqualTo("proprietary-classic");
        await Assert.That(FFmpegDiscovery.LegacyFamily("a64multi", "Commodore 64")).IsEqualTo("home-computer");
        await Assert.That(FFmpegDiscovery.LegacyFamily("snow", "")).IsEqualTo("retro/classic");
    }

    // ---- Frozen lookup tables ---------------------------------------------

    [Test]
    public async Task FrozenTablesStillAnswerExactlyAsBefore()
    {
        await Assert.That(Constants.DefaultQuality["mjpeg"]).IsEqualTo(6);
        await Assert.That(EncoderMath.DefaultQuality("mjpeg")).IsEqualTo(6);
        await Assert.That(EncoderMath.DefaultQuality("never-heard-of-it")).IsEqualTo(10);

        await Assert.That(Constants.FormatExtensions["matroska"]).Contains(".mkv");
        await Assert.That(Constants.FormatExtensions.ContainsKey("matroska")).IsTrue();
        // Ordinal, as it always was: the call sites lower-case before asking.
        await Assert.That(Constants.FormatExtensions.ContainsKey("MATROSKA")).IsFalse();

        await Assert.That(Constants.NoAudioFormats.Contains("h261")).IsTrue();
        await Assert.That(Constants.ConcatSafeFormats.Contains("roq")).IsFalse();
        await Assert.That(Constants.VideoExtensions.Contains(".AVI")).IsFalse();
    }

    // ---- ResolveRange: a dead assignment removed --------------------------

    [Test]
    public async Task AnUnusableSourceDurationIsTreatedAsUnknown()
    {
        // ffprobe answers "N/A" as 0 or NaN for some containers. Both must fall
        // through to "no duration known" instead of throwing, which is what the
        // removed `durationValue = null;` branch used to do.
        await Assert.That(Timecode.ResolveRange(0, null, 0).Duration).IsNull();
        await Assert.That(Timecode.ResolveRange(0, null, double.NaN).Duration).IsNull();
        await Assert.That(Timecode.ResolveRange(0, null, -5).Duration).IsNull();
    }

    [Test]
    public async Task AKnownSourceDurationStillClampsTheEndTime()
    {
        (double? end, double? duration) = Timecode.ResolveRange(1.0, 9.0, 10.0);
        // TUnit's Within() rides on the non-nullable double assertion, so a
        // nullable operand is compared as a distance instead.
        await Assert.That(Math.Abs(end!.Value - 9.0)).IsLessThan(1e-9);
        await Assert.That(Math.Abs(duration!.Value - 8.0)).IsLessThan(1e-9);

        // No end time: the source duration becomes the end of the range.
        await Assert.That(Math.Abs(Timecode.ResolveRange(1.0, null, 10.0).Duration!.Value - 9.0)).IsLessThan(1e-9);

        // A start at or past the end, or an end past the source, is still an error.
        await Assert.That(() => Timecode.ResolveRange(20.0, null, 10.0)).Throws<ArgumentException>();
        await Assert.That(() => Timecode.ResolveRange(1.0, 90.0, 10.0)).Throws<ArgumentException>();
    }
}
