using Lae.Core;
using Lae.Core.Settings;
using static Lae.Tests.Core.TestMessages;

namespace Lae.Tests.Core;

/// <summary>
/// The request a single run carries.
///
/// <para>Two things are asserted here and both were real v7 defects: that a bad
/// request is refused before anything is spawned, and that overriding one field
/// leaves every other field alone. v7 built those overrides by hand twice
/// (sharding and split audio) and dropped a different field each time.</para>
/// </summary>
public sealed class EncodeRequestTests
{
    private static EncodeRequest Sample() => new()
    {
        SourcePath = "/media/in.mkv",
        DestinationPath = "/media/out.avi",
        Encode = new EncodeDefaults { Quality = 8, Fps = 13 },
    };

    [Test]
    public async Task APlainRequestIsAccepted()
    {
        await Assert.That(Sample().Validate(null, En).Count).IsEqualTo(0);
    }

    [Test]
    public async Task WritingOverTheSourceIsRefused()
    {
        var request = Sample() with { DestinationPath = "/media/in.mkv" };

        IReadOnlyList<string> problems = request.Validate(null, En);

        await Assert.That(problems.Count).IsEqualTo(1);
        await Assert.That(problems[0].Contains("input path", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task MissingPathsAreNamed()
    {
        var missingInput = Sample() with { SourcePath = "  " };
        var missingOutput = Sample() with { DestinationPath = "" };

        await Assert.That(missingInput.Validate(null, En).Count).IsEqualTo(1);
        await Assert.That(missingOutput.Validate(null, En).Count).IsEqualTo(1);
    }

    [Test]
    public async Task AnImpossibleTrimRangeIsRefused()
    {
        await Assert.That((Sample() with { StartSeconds = -1 }).Validate(null, En).Count).IsEqualTo(1);

        // end <= start covers both "backwards" and "zero length".
        await Assert.That((Sample() with { StartSeconds = 30, EndSeconds = 30 }).Validate(null, En).Count).IsEqualTo(1);
        await Assert.That((Sample() with { StartSeconds = 30, EndSeconds = 10 }).Validate(null, En).Count).IsEqualTo(1);
        await Assert.That((Sample() with { StartSeconds = 10, EndSeconds = 30 }).Validate(null, En).Count).IsEqualTo(0);
    }

    /// <summary>
    /// Files and an embedded track together would burn one and carry the other,
    /// leaving two subtitle layers and no way to tell which is which.
    /// </summary>
    [Test]
    public async Task SubtitleSourcesAreExclusive()
    {
        EncodeDefaults encode = Sample().Encode with
        {
            Subtitles = new SubtitleDefaults { Files = ["/subs/a.ass"], Stream = 2 },
        };

        await Assert.That((Sample() with { Encode = encode }).Validate(null, En).Count).IsEqualTo(1);
    }

    /// <summary>
    /// Sharding is refused rather than ignored. It was measured and rejected, and
    /// the reason is structural: the wall clock is the pacing, so N shards each
    /// pacing in realtime finish in the same time as one. A settings file that
    /// says 4 must not look honoured by a run that quietly ignored it.
    /// </summary>
    [Test]
    public async Task ShardingIsRefusedWithItsReason()
    {
        EncodeDefaults encode = Sample().Encode with { Parallel = 4 };
        IReadOnlyList<string> problems = (Sample() with { Encode = encode }).Validate(null, En);

        await Assert.That(problems.Count).IsEqualTo(1);
        await Assert.That(problems[0]).Contains("parallel 4");
        await Assert.That(problems[0]).Contains("set parallel to 1");

        // One shard is the default and is not a problem.
        await Assert.That((Sample() with { Encode = Sample().Encode with { Parallel = 1 } }).Validate(null, En).Count)
            .IsEqualTo(0);
    }

    /// <summary>
    /// The override must be a copy, not a rebuild: every field the caller did not
    /// mention has to survive. This is the check that would have caught the
    /// dropped <c>NnediWeights</c> in v7.9.8.6.
    /// </summary>
    [Test]
    public async Task OverridingOneFieldLeavesTheRestAlone()
    {
        EncodeDefaults original = new()
        {
            Encoder = "mpeg4",
            Fps = 25,
            FpsLevels = [25, 12.5],
            Quality = 4,
            LimitKbps = 900,
            Width = 640,
            Deinterlace = new DeinterlaceSettings { Filter = "nnedi", Parity = "tff" },
            Subtitles = new SubtitleDefaults { Mode = "copy", FallbackFont = "SimHei" },
            X264 = new X264Settings { Preset = "veryfast", Profile = "baseline" },
        };

        EncodeDefaults changed = original with { Quality = 20 };

        await Assert.That(changed.Quality).IsEqualTo(20);
        await Assert.That(changed.Encoder).IsEqualTo("mpeg4");
        await Assert.That(changed.Fps).IsEqualTo(25.0);
        await Assert.That(changed.FpsLevels.Count).IsEqualTo(2);
        await Assert.That(changed.LimitKbps).IsEqualTo(900.0);
        await Assert.That(changed.Width).IsEqualTo(640);
        await Assert.That(changed.Deinterlace.Filter).IsEqualTo("nnedi");
        await Assert.That(changed.Deinterlace.Parity).IsEqualTo("tff");
        await Assert.That(changed.Subtitles.Mode).IsEqualTo("copy");
        await Assert.That(changed.Subtitles.FallbackFont).IsEqualTo("SimHei");
        await Assert.That(changed.X264.Preset).IsEqualTo("veryfast");
        await Assert.That(changed.X264.Profile).IsEqualTo("baseline");
    }

    [Test]
    public async Task TheSpanIsDerivedFromTheTrimRange()
    {
        await Assert.That(Sample().SpanSeconds(300)).IsEqualTo(300.0);
        await Assert.That((Sample() with { StartSeconds = 60 }).SpanSeconds(300)).IsEqualTo(240.0);
        await Assert.That((Sample() with { StartSeconds = 60, EndSeconds = 200 }).SpanSeconds(300)).IsEqualTo(140.0);

        // A start past the end of the source is not a negative span; it is
        // "nothing to encode", which the caller reports as such.
        await Assert.That((Sample() with { StartSeconds = 400 }).SpanSeconds(300)).IsNull();
    }
}

