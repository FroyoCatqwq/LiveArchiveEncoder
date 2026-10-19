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
[TestFixture]
public sealed class ShardPlannerTests
{
    [Test]
    public void TheReportedFailureNoLongerOvershoots()
    {
        // 214.280 s at 15 fps = 3214.2 slots -> 3215 -> 214.333 s, i.e. 53 ms
        // past the end of the source.
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 214.280, 15.0, 7);

        Assert.That(ranges, Has.Count.EqualTo(7));
        Assert.That(ranges[^1].End, Is.EqualTo(214.280).Within(1e-9));
        Assert.That(ranges.All(r => r.End <= 214.280 + 1e-9), Is.True);
    }

    [Test]
    public void BoundariesStayInsideAnOffsetRange()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(30.0, 100.0, 12.5, 4);

        Assert.That(ranges[0].Start, Is.EqualTo(30.0).Within(1e-9));
        Assert.That(ranges[^1].End, Is.EqualTo(130.0).Within(1e-9));
        Assert.That(ranges.All(r => r.End <= 130.0 + 1e-9), Is.True);
    }

    [Test]
    public void ShardsAreContiguousAndCoverEverything()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 97.531, 15.0, 6);

        for (int i = 1; i < ranges.Count; i++)
        {
            Assert.That(ranges[i].Start, Is.EqualTo(ranges[i - 1].End).Within(1e-9));
        }
        Assert.That(ranges.Sum(r => r.End - r.Start), Is.EqualTo(97.531).Within(1e-6));
    }

    [Test]
    public void SlotCountsAddUpToTheWholeRange()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 200.0, 15.0, 7);
        int expected = (int)Math.Ceiling(200.0 * 15.0 - 1e-9);

        Assert.That(ranges.Sum(r => r.Slots), Is.EqualTo(expected));
    }

    [Test]
    public void NeverMoreShardsThanSlots()
    {
        // 2 s at 3 fps is 6 slots; asking for 10 shards must not create holes.
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 2.0, 3.0, 10);

        Assert.That(ranges, Has.Count.EqualTo(6));
        Assert.That(ranges.All(r => r.End - r.Start > 0), Is.True);
    }

    [Test]
    public void ASingleShardIsTheWholeRange()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(5.0, 61.7, 15.0, 1);

        Assert.That(ranges, Has.Count.EqualTo(1));
        Assert.That(ranges[0].Start, Is.EqualTo(5.0).Within(1e-9));
        Assert.That(ranges[0].End, Is.EqualTo(66.7).Within(1e-9));
    }

    [Test]
    public void AnExactMultipleOfSlotsStillEndsAtTheRangeEnd()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 10.0, 15.0, 5);

        Assert.That(ranges[^1].End, Is.EqualTo(10.0).Within(1e-9));
        Assert.That(ranges, Has.Count.EqualTo(5));
    }

    [Test]
    public void NonsenseInputsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ShardPlanner.Plan(0.0, 0.0, 15.0, 4));
        Assert.Throws<ArgumentException>(() => ShardPlanner.Plan(0.0, 10.0, 0.0, 4));
        Assert.Throws<ArgumentException>(() => ShardPlanner.Plan(0.0, double.NaN, 15.0, 4));
    }
}

/// <summary>
/// v7.7 #5/#6: a motion level may not be faster than the base rate, and each
/// rung can carry its own drop/recover bitrate instead of inheriting a multiple
/// of the ceiling.
/// </summary>
[TestFixture]
public sealed class FpsLadderTests
{
    [Test]
    public void DefaultLadderKeepsThev76Thresholds()
    {
        List<FpsRung> ladder = EncoderMath.BuildLadder(15.0, [15.0, 12.0, 10.0, 5.0], [], 225.0, 168.75);

        Assert.That(ladder.Select(r => r.Fps), Is.EqualTo(new[] { 15.0, 12.0, 10.0, 5.0 }));
        // 1.00x / 1.25x / 1.60x of the ceiling, as before.
        Assert.That(ladder[1].DropAboveKbps, Is.EqualTo(225.0).Within(1e-9));
        Assert.That(ladder[2].DropAboveKbps, Is.EqualTo(281.25).Within(1e-9));
        Assert.That(ladder[3].DropAboveKbps, Is.EqualTo(360.0).Within(1e-9));
        // Recovering uses the recover value on every rung.
        Assert.That(ladder.Skip(1).Select(r => r.RiseBelowKbps), Is.All.EqualTo(168.75));
    }

    [Test]
    public void TheBaseRungIsNeverEnteredFromBelow()
    {
        List<FpsRung> ladder = EncoderMath.BuildLadder(15.0, null, [], 225.0, 168.75);

        Assert.That(ladder[0].DropAboveKbps, Is.EqualTo(double.PositiveInfinity));
    }

    [Test]
    public void UserThresholdsReplaceTheDerivedOnes()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 128.0, RiseBelowKbps = 115.0 },
            new FpsRungSetting { Fps = 5.0, DropAboveKbps = 140.0 },
        ];

        List<FpsRung> ladder = EncoderMath.BuildLadder(
            15.0, [15.0, 12.0, 10.0, 5.0], settings, 225.0, 168.75);

        Assert.That(ladder[1].DropAboveKbps, Is.EqualTo(128.0));
        Assert.That(ladder[1].RiseBelowKbps, Is.EqualTo(115.0));
        // Untouched rung keeps the derived threshold...
        Assert.That(ladder[2].DropAboveKbps, Is.EqualTo(281.25).Within(1e-9));
        // ...and the explicit one overrides it.
        Assert.That(ladder[3].DropAboveKbps, Is.EqualTo(140.0));
        Assert.That(ladder[3].RiseBelowKbps, Is.EqualTo(168.75).Within(1e-9));
    }

    [Test]
    public void TargetLevelFollowsTheCustomThresholds()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 128.0, RiseBelowKbps = 115.0 },
            new FpsRungSetting { Fps = 5.0, DropAboveKbps = 140.0, RiseBelowKbps = 130.0 },
        ];
        List<FpsRung> ladder = EncoderMath.BuildLadder(
            15.0, [15.0, 12.0, 5.0], settings, 225.0, 168.75);

        Assert.That(EncoderMath.TargetLevel(120.0, ladder), Is.EqualTo(0));
        Assert.That(EncoderMath.TargetLevel(128.0, ladder), Is.EqualTo(1));
        Assert.That(EncoderMath.TargetLevel(139.0, ladder), Is.EqualTo(1));
        Assert.That(EncoderMath.TargetLevel(140.0, ladder), Is.EqualTo(2));
    }

    [Test]
    public void ANonMonotonicLadderStillReachesItsDeepestRung()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 200.0 },
            new FpsRungSetting { Fps = 5.0, DropAboveKbps = 140.0 },
        ];
        List<FpsRung> ladder = EncoderMath.BuildLadder(15.0, [15.0, 12.0, 5.0], settings, 225.0, 168.75);

        Assert.That(EncoderMath.TargetLevel(210.0, ladder), Is.EqualTo(2));
    }

    [Test]
    public void ALadderOfOneRungNeverThrottles()
    {
        List<FpsRung> ladder = EncoderMath.BuildLadder(15.0, [15.0], [], 225.0, 168.75);

        Assert.That(EncoderMath.TargetLevel(9999.0, ladder), Is.EqualTo(0));
    }

    [Test]
    public void LevelsAboveTheBaseRateAreDropped()
    {
        // The safety net behind the CUI/CLI validation: SelectLevels never
        // returns a rung the controller could not reach anyway.
        Assert.That(EncoderMath.SelectLevels(10.0, [25.0, 15.0, 8.0]),
            Is.EqualTo(new[] { 10.0, 8.0 }));
    }

    // ---- parsing the "FPS[:drop[:rise]]" spelling ----

    [Test]
    public void ParsesBareFrameRates()
    {
        (List<double> fps, List<FpsRungSetting> settings, string? error) =
            EncoderMath.ParseLevelSpec("13,8,5,3");

        Assert.That(error, Is.Null);
        Assert.That(fps, Is.EqualTo(new[] { 13.0, 8.0, 5.0, 3.0 }));
        Assert.That(settings.All(s => s.DropAboveKbps is null && s.RiseBelowKbps is null), Is.True);
    }

    [Test]
    public void ParsesDropAndRiseThresholds()
    {
        (List<double> fps, List<FpsRungSetting> settings, string? error) =
            EncoderMath.ParseLevelSpec("12:128,5:140:130");

        Assert.That(error, Is.Null);
        Assert.That(fps, Is.EqualTo(new[] { 12.0, 5.0 }));
        Assert.That(settings[0].DropAboveKbps, Is.EqualTo(128.0));
        Assert.That(settings[0].RiseBelowKbps, Is.Null);
        Assert.That(settings[1].DropAboveKbps, Is.EqualTo(140.0));
        Assert.That(settings[1].RiseBelowKbps, Is.EqualTo(130.0));
    }

    [Test]
    public void SemicolonsAndSpacesAreAccepted()
    {
        (List<double> fps, _, string? error) = EncoderMath.ParseLevelSpec("12 : 128 ; 5:140");

        Assert.That(error, Is.Null);
        Assert.That(fps, Is.EqualTo(new[] { 12.0, 5.0 }));
    }

    [Test]
    public void BadSpecsAreRejected()
    {
        Assert.That(EncoderMath.ParseLevelSpec("12:128:115:1").Error, Is.Not.Null);
        Assert.That(EncoderMath.ParseLevelSpec("abc").Error, Is.Not.Null);
        Assert.That(EncoderMath.ParseLevelSpec("0").Error, Is.Not.Null);
        Assert.That(EncoderMath.ParseLevelSpec("-5").Error, Is.Not.Null);
        Assert.That(EncoderMath.ParseLevelSpec("12:0").Error, Is.Not.Null);
        // Recovering must be cheaper than dropping, or the ladder oscillates.
        Assert.That(EncoderMath.ParseLevelSpec("12:128:130").Error, Is.Not.Null);
    }

    // ---- rendering / pruning ----

    [Test]
    public void FormatLadderRoundTrips()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 128.0 },
            new FpsRungSetting { Fps = 5.0, DropAboveKbps = 140.0, RiseBelowKbps = 130.0 },
        ];

        string text = EncoderMath.FormatLadder(15.0, [15.0, 12.0, 5.0], settings);
        Assert.That(text, Is.EqualTo("15,12:128,5:140:130"));

        (List<double> fps, List<FpsRungSetting> parsed, string? error) = EncoderMath.ParseLevelSpec(text);
        Assert.That(error, Is.Null);
        Assert.That(fps, Is.EqualTo(new[] { 15.0, 12.0, 5.0 }));
        Assert.That(parsed[1].DropAboveKbps, Is.EqualTo(128.0));
    }

    [Test]
    public void FormatLadderOmitsDerivedThresholds()
    {
        Assert.That(EncoderMath.FormatLadder(15.0, [15.0, 12.0, 5.0], []), Is.EqualTo("15,12,5"));
    }

    [Test]
    public void PruningDropsThresholdsForRungsThatAreGone()
    {
        List<FpsRungSetting> settings =
        [
            new FpsRungSetting { Fps = 25.0, DropAboveKbps = 300.0 },
            new FpsRungSetting { Fps = 12.0, DropAboveKbps = 128.0 },
        ];

        List<FpsRungSetting> kept = EncoderMath.PruneRungs(settings, [15.0, 12.0, 5.0]);

        Assert.That(kept, Has.Count.EqualTo(1));
        Assert.That(kept[0].Fps, Is.EqualTo(12.0));
    }
}
