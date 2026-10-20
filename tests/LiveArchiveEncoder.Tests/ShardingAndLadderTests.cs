using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Encoding;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.7 #1: a sharded run failed with "shard 6: end time 00:03:34.333 exceeds
/// the source duration (00:03:34.280)". The slot grid is coarser than the media
/// timeline -- ceil(span*fps) slots cover more time than the source has -- so
/// the last boundary landed past EOF. Every case here is about that boundary.
/// </summary>
public sealed class ShardPlannerTests
{
    [Test]
    public async Task TheReportedFailureNoLongerOvershoots()
    {
        // 214.280 s at 15 fps = 3214.2 slots -> 3215 -> 214.333 s, i.e. 53 ms
        // past the end of the source.
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 214.280, 15.0, 7);

        await Assert.That(ranges).Count().IsEqualTo(7);
        await Assert.That(ranges[^1].End).IsEqualTo(214.280).Within(1e-9);
        await Assert.That(ranges.All(r => r.End <= 214.280 + 1e-9)).IsTrue();
    }

    [Test]
    public async Task BoundariesStayInsideAnOffsetRange()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(30.0, 100.0, 12.5, 4);

        await Assert.That(ranges[0].Start).IsEqualTo(30.0).Within(1e-9);
        await Assert.That(ranges[^1].End).IsEqualTo(130.0).Within(1e-9);
        await Assert.That(ranges.All(r => r.End <= 130.0 + 1e-9)).IsTrue();
    }

    [Test]
    public async Task ShardsAreContiguousAndCoverEverything()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 97.531, 15.0, 6);

        for (int i = 1; i < ranges.Count; i++)
        {
            await Assert.That(ranges[i].Start).IsEqualTo(ranges[i - 1].End).Within(1e-9);
        }
        await Assert.That(ranges.Sum(r => r.End - r.Start)).IsEqualTo(97.531).Within(1e-6);
    }

    [Test]
    public async Task SlotCountsAddUpToTheWholeRange()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 200.0, 15.0, 7);
        int expected = (int)Math.Ceiling(200.0 * 15.0 - 1e-9);

        await Assert.That(ranges.Sum(r => r.Slots)).IsEqualTo(expected);
    }

    [Test]
    public async Task NeverMoreShardsThanSlots()
    {
        // 2 s at 3 fps is 6 slots; asking for 10 shards must not create holes.
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 2.0, 3.0, 10);

        await Assert.That(ranges).Count().IsEqualTo(6);
        await Assert.That(ranges.All(r => r.End - r.Start > 0)).IsTrue();
    }

    [Test]
    public async Task ASingleShardIsTheWholeRange()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(5.0, 61.7, 15.0, 1);

        await Assert.That(ranges).Count().IsEqualTo(1);
        await Assert.That(ranges[0].Start).IsEqualTo(5.0).Within(1e-9);
        await Assert.That(ranges[0].End).IsEqualTo(66.7).Within(1e-9);
    }

    [Test]
    public async Task AnExactMultipleOfSlotsStillEndsAtTheRangeEnd()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 10.0, 15.0, 5);

        await Assert.That(ranges[^1].End).IsEqualTo(10.0).Within(1e-9);
        await Assert.That(ranges).Count().IsEqualTo(5);
    }

    [Test]
    public async Task NonsenseInputsAreRejected()
    {
        await Assert.That(() => ShardPlanner.Plan(0.0, 0.0, 15.0, 4)).Throws<ArgumentException>();
        await Assert.That(() => ShardPlanner.Plan(0.0, 10.0, 0.0, 4)).Throws<ArgumentException>();
        await Assert.That(() => ShardPlanner.Plan(0.0, double.NaN, 15.0, 4)).Throws<ArgumentException>();
    }
}

/// <summary>
/// v7.7 #5/#6: a motion level may not be faster than the base rate, and each
/// rung can carry its own drop/recover bitrate instead of inheriting a multiple
/// of the ceiling.
/// </summary>
public sealed class FpsLadderTests
{
    [Test]
    public async Task DefaultLadderKeepsThev76Thresholds()
    {
        List<FpsRung> ladder = EncoderMath.BuildLadder(15.0, [15.0, 12.0, 10.0, 5.0], [], 225.0, 168.75);

        await Assert.That(ladder.Select(r => r.Fps)).IsEquivalentTo(new[] { 15.0, 12.0, 10.0, 5.0 });
        // 1.00x / 1.25x / 1.60x of the ceiling, as before.
        await Assert.That(ladder[1].DropAboveKbps).IsEqualTo(225.0).Within(1e-9);
        await Assert.That(ladder[2].DropAboveKbps).IsEqualTo(281.25).Within(1e-9);
        await Assert.That(ladder[3].DropAboveKbps).IsEqualTo(360.0).Within(1e-9);
        // Recovering uses the recover value on every rung.
        foreach (double rise in ladder.Skip(1).Select(r => r.RiseBelowKbps))
        {
            await Assert.That(rise).IsEqualTo(168.75);
        }
    }

    [Test]
    public async Task TheBaseRungIsNeverEnteredFromBelow()
    {
        List<FpsRung> ladder = EncoderMath.BuildLadder(15.0, null, [], 225.0, 168.75);

        await Assert.That(ladder[0].DropAboveKbps).IsEqualTo(double.PositiveInfinity);
    }

    [Test]
    public async Task UserThresholdsReplaceTheDerivedOnes()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 128.0, RiseBelowKbps = 115.0 },
            new FpsRungSetting { Fps = 5.0, DropAboveKbps = 140.0 },
        ];

        List<FpsRung> ladder = EncoderMath.BuildLadder(
            15.0, [15.0, 12.0, 10.0, 5.0], settings, 225.0, 168.75);

        await Assert.That(ladder[1].DropAboveKbps).IsEqualTo(128.0);
        await Assert.That(ladder[1].RiseBelowKbps).IsEqualTo(115.0);
        // Untouched rung keeps the derived threshold...
        await Assert.That(ladder[2].DropAboveKbps).IsEqualTo(281.25).Within(1e-9);
        // ...and the explicit one overrides it.
        await Assert.That(ladder[3].DropAboveKbps).IsEqualTo(140.0);
        await Assert.That(ladder[3].RiseBelowKbps).IsEqualTo(168.75).Within(1e-9);
    }

    [Test]
    public async Task TargetLevelFollowsTheCustomThresholds()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 128.0, RiseBelowKbps = 115.0 },
            new FpsRungSetting { Fps = 5.0, DropAboveKbps = 140.0, RiseBelowKbps = 130.0 },
        ];
        List<FpsRung> ladder = EncoderMath.BuildLadder(
            15.0, [15.0, 12.0, 5.0], settings, 225.0, 168.75);

        await Assert.That(EncoderMath.TargetLevel(120.0, ladder)).IsEqualTo(0);
        await Assert.That(EncoderMath.TargetLevel(128.0, ladder)).IsEqualTo(1);
        await Assert.That(EncoderMath.TargetLevel(139.0, ladder)).IsEqualTo(1);
        await Assert.That(EncoderMath.TargetLevel(140.0, ladder)).IsEqualTo(2);
    }

    [Test]
    public async Task ANonMonotonicLadderStillReachesItsDeepestRung()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 200.0 },
            new FpsRungSetting { Fps = 5.0, DropAboveKbps = 140.0 },
        ];
        List<FpsRung> ladder = EncoderMath.BuildLadder(15.0, [15.0, 12.0, 5.0], settings, 225.0, 168.75);

        await Assert.That(EncoderMath.TargetLevel(210.0, ladder)).IsEqualTo(2);
    }

    [Test]
    public async Task ALadderOfOneRungNeverThrottles()
    {
        List<FpsRung> ladder = EncoderMath.BuildLadder(15.0, [15.0], [], 225.0, 168.75);

        await Assert.That(EncoderMath.TargetLevel(9999.0, ladder)).IsEqualTo(0);
    }

    [Test]
    public async Task LevelsAboveTheBaseRateAreDropped()
    {
        // The safety net behind the CUI/CLI validation: SelectLevels never
        // returns a rung the controller could not reach anyway.
        await Assert.That(EncoderMath.SelectLevels(10.0, [25.0, 15.0, 8.0])).IsEquivalentTo(new[] { 10.0, 8.0 });
    }

    // ---- parsing the "FPS[:drop[:rise]]" spelling ----

    [Test]
    public async Task ParsesBareFrameRates()
    {
        (List<double> fps, List<FpsRungSetting> settings, string? error) =
            EncoderMath.ParseLevelSpec("13,8,5,3");

        await Assert.That(error).IsNull();
        await Assert.That(fps).IsEquivalentTo(new[] { 13.0, 8.0, 5.0, 3.0 });
        await Assert.That(settings.All(s => s.DropAboveKbps is null && s.RiseBelowKbps is null)).IsTrue();
    }

    [Test]
    public async Task ParsesDropAndRiseThresholds()
    {
        (List<double> fps, List<FpsRungSetting> settings, string? error) =
            EncoderMath.ParseLevelSpec("12:128,5:140:130");

        await Assert.That(error).IsNull();
        await Assert.That(fps).IsEquivalentTo(new[] { 12.0, 5.0 });
        await Assert.That(settings[0].DropAboveKbps).IsEqualTo(128.0);
        await Assert.That(settings[0].RiseBelowKbps).IsNull();
        await Assert.That(settings[1].DropAboveKbps).IsEqualTo(140.0);
        await Assert.That(settings[1].RiseBelowKbps).IsEqualTo(130.0);
    }

    [Test]
    public async Task SemicolonsAndSpacesAreAccepted()
    {
        (List<double> fps, _, string? error) = EncoderMath.ParseLevelSpec("12 : 128 ; 5:140");

        await Assert.That(error).IsNull();
        await Assert.That(fps).IsEquivalentTo(new[] { 12.0, 5.0 });
    }

    [Test]
    public async Task BadSpecsAreRejected()
    {
        await Assert.That(EncoderMath.ParseLevelSpec("12:128:115:1").Error).IsNotNull();
        await Assert.That(EncoderMath.ParseLevelSpec("abc").Error).IsNotNull();
        await Assert.That(EncoderMath.ParseLevelSpec("0").Error).IsNotNull();
        await Assert.That(EncoderMath.ParseLevelSpec("-5").Error).IsNotNull();
        await Assert.That(EncoderMath.ParseLevelSpec("12:0").Error).IsNotNull();
        // Recovering must be cheaper than dropping, or the ladder oscillates.
        await Assert.That(EncoderMath.ParseLevelSpec("12:128:130").Error).IsNotNull();
    }

    // ---- rendering / pruning ----

    [Test]
    public async Task FormatLadderRoundTrips()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 128.0 },
            new FpsRungSetting { Fps = 5.0, DropAboveKbps = 140.0, RiseBelowKbps = 130.0 },
        ];

        string text = EncoderMath.FormatLadder(15.0, [15.0, 12.0, 5.0], settings);
        await Assert.That(text).IsEqualTo("15,12:128,5:140:130");

        (List<double> fps, List<FpsRungSetting> parsed, string? error) = EncoderMath.ParseLevelSpec(text);
        await Assert.That(error).IsNull();
        await Assert.That(fps).IsEquivalentTo(new[] { 15.0, 12.0, 5.0 });
        await Assert.That(parsed[1].DropAboveKbps).IsEqualTo(128.0);
    }

    [Test]
    public async Task FormatLadderOmitsDerivedThresholds()
    {
        await Assert.That(EncoderMath.FormatLadder(15.0, [15.0, 12.0, 5.0], [])).IsEqualTo("15,12,5");
    }

    [Test]
    public async Task PruningDropsThresholdsForRungsThatAreGone()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 25.0, DropAboveKbps = 300.0 },
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 128.0 },
        ];

        List<FpsRungSetting> kept = EncoderMath.PruneRungs(settings, [15.0, 12.0, 5.0]);

        await Assert.That(kept).Count().IsEqualTo(1);
        await Assert.That(kept[0].Fps).IsEqualTo(12.0);
    }
}
