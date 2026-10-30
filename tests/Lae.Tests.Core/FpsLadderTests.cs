using Lae.Core;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The ladder and the window it reads.
///
/// <para>Both are pure arithmetic, which is exactly why they are tested here
/// rather than through an encode: "the rate sat above the ceiling for two
/// windows and the rung went down by exactly one" is a statement about numbers,
/// and a test that needs FFmpeg to check it is a test nobody runs.</para>
/// </summary>
public sealed class FpsLadderTests
{
    /// <summary>The shipped default: 13, 8, 5, 3 under a 200 kbps ceiling.</summary>
    private static FpsLadder Default() => LadderPolicy.FromDefaults(new EncodeDefaults
    {
        FpsLevels = [13, 8, 5, 3],
        LimitKbps = 200,
        RecoverKbps = 150,
    })!;

    /// <summary>
    /// A reading, one second after the previous one. The ladder holds still for a
    /// fraction of a second after a move, so the tests have to walk a clock --
    /// which is also what the pump does, in programme seconds.
    /// </summary>
    private static Func<double, bool> Reader(FpsLadder ladder)
    {
        double now = 0;
        return kbps => ladder.Observe(kbps, now += 1.0);
    }

    [Test]
    public async Task StartsAtFullRate()
    {
        FpsLadder ladder = Default();

        await Assert.That(ladder.CurrentFps).IsEqualTo(13.0);
        await Assert.That(ladder.Rung).IsEqualTo(0);
    }

    [Test]
    public async Task FallsARungWhenTheCeilingIsExceeded()
    {
        FpsLadder ladder = Default();

        await Assert.That(Reader(ladder)(400)).IsTrue();
        await Assert.That(ladder.CurrentFps).IsEqualTo(8.0);
    }

    /// <summary>
    /// One rung per reading, never straight to the floor. v7.9.8.2 shipped a
    /// version whose quality number crossed scales, the rate came out at 2.9x the
    /// ceiling, and a ladder that jumped would have gone to 3 fps on the first
    /// window for a reason nobody could see.
    /// </summary>
    [Test]
    public async Task NeverFallsMoreThanOneRungPerReading()
    {
        FpsLadder ladder = Default();

        Reader(ladder)(9000);

        await Assert.That(ladder.CurrentFps).IsEqualTo(8.0);
    }

    /// <summary>
    /// And never twice inside the hold. v8's first real run with no hold walked
    /// 13 > 8 > 5 > 3 in a quarter of a second, because one bitrate reading was
    /// offered thirteen times a second.
    /// </summary>
    [Test]
    public async Task CannotMoveTwiceWithinTheHold()
    {
        FpsLadder ladder = Default();

        await Assert.That(ladder.Observe(9000, 0.00)).IsTrue();
        await Assert.That(ladder.Observe(9000, 0.05)).IsFalse();
        await Assert.That(ladder.Observe(9000, 0.10)).IsFalse();
        await Assert.That(ladder.CurrentFps).IsEqualTo(8.0);

        // Past the hold it may step again.
        await Assert.That(ladder.Observe(9000, 0.25)).IsTrue();
        await Assert.That(ladder.CurrentFps).IsEqualTo(5.0);
    }

    [Test]
    public async Task StopsAtTheLowestRung()
    {
        FpsLadder ladder = Default();
        Func<double, bool> read = Reader(ladder);
        for (int i = 0; i < 10; i++) read(9000);

        await Assert.That(ladder.CurrentFps).IsEqualTo(3.0);
    }

    /// <summary>
    /// Climbing back needs a rate well under the ceiling. Sitting exactly at the
    /// ceiling -- which is where a healthy encode sits, since that is what the
    /// ceiling is for -- must not move the ladder in either direction, or it
    /// flaps and the picture pulses.
    /// </summary>
    [Test]
    public async Task DoesNotMoveRightAtTheCeiling()
    {
        FpsLadder ladder = Default();
        Func<double, bool> read = Reader(ladder);
        read(400);

        await Assert.That(read(200)).IsFalse();
        await Assert.That(ladder.CurrentFps).IsEqualTo(8.0);
    }

    [Test]
    public async Task ClimbsWhenTheRateFallsWellBelow()
    {
        FpsLadder ladder = Default();
        Func<double, bool> read = Reader(ladder);
        read(400);

        await Assert.That(read(50)).IsTrue();
        await Assert.That(ladder.CurrentFps).IsEqualTo(13.0);
    }

    [Test]
    public async Task ResetReturnsToFullRate()
    {
        FpsLadder ladder = Default();
        Reader(ladder)(400);
        ladder.Reset();

        await Assert.That(ladder.CurrentFps).IsEqualTo(13.0);
    }
}

/// <summary>
/// Turning settings into rungs. This is where v8 has to stay identical to v7:
/// the same ceiling has to produce the same thresholds, or the comparison matrix
/// stops comparing like with like.
/// </summary>
public sealed class LadderPolicyTests
{
    [Test]
    public async Task DerivedThresholdsAreV7sMultipliers()
    {
        FpsLadder ladder = LadderPolicy.FromDefaults(new EncodeDefaults
        {
            FpsLevels = [13, 8, 5, 3],
            LimitKbps = 200,
            RecoverKbps = 150,
        })!;

        double[] drops = [.. ladder.Rungs.Select(r => r.DropAboveKbps)];

        // Rung 0 can never be entered, so it is named infinite on purpose.
        await Assert.That(double.IsPositiveInfinity(drops[0])).IsTrue();
        await Assert.That(drops[1]).IsEqualTo(200.0);
        await Assert.That(drops[2]).IsEqualTo(250.0);
        await Assert.That(drops[3]).IsEqualTo(320.0);
        await Assert.That(ladder.Rungs[3].RiseBelowKbps).IsEqualTo(150.0);
    }

    /// <summary>A rung's threshold is keyed by its frame rate, not by position.</summary>
    [Test]
    public async Task ConfiguredThresholdsWinOverTheDerivedOnes()
    {
        FpsLadder ladder = LadderPolicy.FromDefaults(new EncodeDefaults
        {
            FpsLevels = [13, 8, 5],
            LimitKbps = 200,
            RecoverKbps = 150,
            FpsThresholds =
            [
                new FpsThreshold(8, 180, 120),
                new FpsThreshold(5, 400, null),
            ],
        })!;

        await Assert.That(ladder.Rungs[1].DropAboveKbps).IsEqualTo(180.0);
        await Assert.That(ladder.Rungs[1].RiseBelowKbps).IsEqualTo(120.0);
        await Assert.That(ladder.Rungs[2].DropAboveKbps).IsEqualTo(400.0);
        await Assert.That(ladder.Rungs[2].RiseBelowKbps).IsEqualTo(150.0);
    }

    /// <summary>
    /// A ladder with nothing to choose between is not a ladder. Returning null
    /// rather than a one-rung object keeps the pump free of a throttle that can
    /// never fire.
    /// </summary>
    [Test]
    public async Task NoLadderWithoutTwoRungsOrACeiling()
    {
        await Assert.That(LadderPolicy.FromDefaults(new EncodeDefaults
        {
            FpsLevels = [13], LimitKbps = 200,
        })).IsNull();

        await Assert.That(LadderPolicy.FromDefaults(new EncodeDefaults
        {
            FpsLevels = [13, 8], LimitKbps = 0,
        })).IsNull();
    }

    /// <summary>An unconfigured ladder must not be able to leave the top rung.</summary>
    [Test]
    public async Task TheTopRungIsNeverEnteredFromAbove()
    {
        FpsLadder ladder = LadderPolicy.FromDefaults(new EncodeDefaults
        {
            FpsLevels = [13, 8],
            LimitKbps = 200,
        })!;

        await Assert.That(ladder.Observe(1e9, 1.0)).IsTrue();      // down to 8
        await Assert.That(ladder.Observe(1e9, 9.0)).IsFalse();     // nowhere left to go
        await Assert.That(ladder.CurrentFps).IsEqualTo(8.0);
    }

    /// <summary>
    /// The holds are part of the ladder, not of the pump: a run that throttled
    /// once per slot would fall to the floor before the second second of output
    /// existed to justify it.
    /// </summary>
    [Test]
    public async Task AHoldSeparatesTwoMoves()
    {
        FpsLadder ladder = LadderPolicy.FromDefaults(new EncodeDefaults
        {
            FpsLevels = [13, 8, 5],
            LimitKbps = 200,
        })!;

        await Assert.That(ladder.DropHoldSeconds).IsBetween(0.1, 0.4);
        await Assert.That(ladder.RiseHoldSeconds).IsBetween(0.5, 1.0);
    }

    /// <summary>
    /// A recovery rate at or above the ceiling leaves the ladder no gap: it drops
    /// a rung, finds itself under the rise threshold, climbs straight back, and
    /// the picture pulses for the whole run. Clamped to three quarters of the
    /// ceiling, which is where v7's 150-of-200 sat.
    /// </summary>
    [Test]
    public async Task RecoveryIsPulledBelowTheCeiling()
    {
        EncodeDefaults tight = new() { LimitKbps = 60, RecoverKbps = 150 };

        await Assert.That(LadderPolicy.RecoverKbps(tight)).IsEqualTo(45.0);

        FpsLadder ladder = LadderPolicy.FromDefaults(tight with { FpsLevels = [13, 8] })!;
        await Assert.That(ladder.Rungs[1].RiseBelowKbps).IsEqualTo(45.0);
    }

    [Test]
    public async Task ASensibleRecoveryRateIsLeftAlone()
    {
        EncodeDefaults sane = new() { LimitKbps = 200, RecoverKbps = 150 };

        await Assert.That(LadderPolicy.RecoverKbps(sane)).IsEqualTo(150.0);
    }
}

public sealed class BitrateWindowTests
{
    /// <summary>
    /// 25 KB per half second is 400 kbps. The first sample is only the origin, so
    /// the two that follow supply both the bytes and the span they took.
    /// </summary>
    [Test]
    public async Task ReportsTheRateOverTheCoveredSpan()
    {
        var window = new BitrateWindow(2.0);

        for (int i = 0; i <= 3; i++) window.Add(i * 0.5, 25_000);

        await Assert.That(window.CoveredSeconds).IsEqualTo(1.5);
        await Assert.That(window.Kbps()).IsBetween(380.0, 420.0);
    }

    /// <summary>
    /// Old samples have to leave, or a burst at the start of a run would keep the
    /// ladder down for the rest of it.
    /// </summary>
    [Test]
    public async Task ForgetsSamplesOlderThanTheWindow()
    {
        var window = new BitrateWindow(1.0);
        window.Add(0.0, 200_000);
        window.Add(5.0, 1_000);

        await Assert.That(window.Kbps()).IsLessThan(200.0);
    }

    /// <summary>
    /// The denominator is programme time, not arrivals. FFmpeg reports twice a
    /// second; a report covering half a second that arrives a second late must
    /// still be divided by the half second it describes, or the rate reads low and
    /// the ladder never descends.
    /// </summary>
    [Test]
    public async Task SparseReportsDoNotChangeTheRate()
    {
        var window = new BitrateWindow(4.0);

        window.Add(0.0, 0);
        window.Add(0.5, 25_000);
        window.Add(1.0, 25_000);

        // Same 25 KB per half second as above, but sampled coarsely.
        await Assert.That(window.Kbps()).IsBetween(380.0, 420.0);
    }

    [Test]
    public async Task OneSampleIsNotYetARate()
    {
        var window = new BitrateWindow(2.0);
        window.Add(1.0, 900_000);

        await Assert.That(window.Kbps()).IsEqualTo(0.0);
        await Assert.That(window.CoveredSeconds).IsEqualTo(0.0);
    }

    [Test]
    public async Task CountsEveryFrameForTheReport()
    {
        var window = new BitrateWindow(1.0);
        window.Add(0.0, 10);
        window.Add(1.0, 10);

        await Assert.That(window.Frames).IsEqualTo(2);
    }
}
