using Lae.Core;

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
    private static FpsLadder Default() => new([13, 8, 5, 3], [200, 200, 200, 200]);

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

        await Assert.That(ladder.Observe(400)).IsTrue();
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

        ladder.Observe(9000);

        await Assert.That(ladder.CurrentFps).IsEqualTo(8.0);
    }

    [Test]
    public async Task StopsAtTheLowestRung()
    {
        FpsLadder ladder = Default();
        for (int i = 0; i < 10; i++) ladder.Observe(9000);

        await Assert.That(ladder.CurrentFps).IsEqualTo(3.0);
    }

    /// <summary>
    /// Climbing back needs a rate well under the ceiling. With the default 75%
    /// floor, sitting exactly at the ceiling -- which is where a healthy encode
    /// sits -- must not move the ladder in either direction, or it flaps.
    /// </summary>
    [Test]
    public async Task DoesNotMoveRightAtTheCeiling()
    {
        FpsLadder ladder = Default();
        ladder.Observe(400);

        await Assert.That(ladder.Observe(200)).IsFalse();
        await Assert.That(ladder.CurrentFps).IsEqualTo(8.0);
    }

    [Test]
    public async Task ClimbsWhenTheRateFallsWellBelow()
    {
        FpsLadder ladder = Default();
        ladder.Observe(400);

        await Assert.That(ladder.Observe(50)).IsTrue();
        await Assert.That(ladder.CurrentFps).IsEqualTo(13.0);
    }

    [Test]
    public async Task ResetReturnsToFullRate()
    {
        FpsLadder ladder = Default();
        ladder.Observe(400);
        ladder.Reset();

        await Assert.That(ladder.CurrentFps).IsEqualTo(13.0);
    }
}

public sealed class BitrateWindowTests
{
    [Test]
    public async Task ReportsTheRateOverTheWindow()
    {
        var window = new BitrateWindow(2.0);

        // 25 KB every 0.5 s is 400 kbps: 75 KB over the 1.5 s it covers.
        for (int i = 1; i <= 3; i++) window.Add(i * 0.5, 25_000);

        await Assert.That(window.Kbps(1.5)).IsBetween(380.0, 420.0);
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

        await Assert.That(window.Kbps(5.0)).IsLessThan(200.0);
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
