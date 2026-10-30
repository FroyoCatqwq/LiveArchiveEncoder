using Lae.Core;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// Burning subtitles: what the filter says, and -- the part that is easy to get
/// wrong -- when it says it.
///
/// <para>The clock is why this is tested at all. The decoder seeks with
/// <c>-ss</c>, which rebases the output to zero, while the subtitle file keeps
/// the original times; without a shift every caption is late by exactly the
/// start offset, and a 20-minute excerpt taken from 18:00 has its captions
/// arriving after the video has ended. Both directions of the shift are asserted
/// here because leaving the return leg out is a valid-looking edit that retimes
/// the whole encode.</para>
/// </summary>
public sealed class SubtitleChainTests
{
    private static SubtitleDefaults Burn(params string[] files) => new()
    {
        Mode = "burn",
        Files = files.Length > 0 ? files : ["/subs/a.srt"],
    };

    [Test]
    public async Task NothingAskedMeansNothingBurned()
    {
        SubtitleBurn burn = SubtitleChain.Build(new SubtitleDefaults(), 0, null);

        await Assert.That(burn.Filter.Length).IsEqualTo(0);
        await Assert.That(burn.Problems.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ABurnOfOneFileNamesThatFile()
    {
        SubtitleBurn burn = SubtitleChain.Build(Burn("/subs/a.srt"), 0, null);

        await Assert.That(burn.Problems.Count).IsEqualTo(0);
        await Assert.That(burn.Filter.StartsWith("subtitles=filename=", StringComparison.Ordinal)).IsTrue();
        await Assert.That(burn.Filter.Contains("a.srt", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A trimmed run pushes the timeline forward before libass sees it and back
    /// afterwards. The second half is not optional: without it the encoder is
    /// handed frames whose timestamps begin at the start offset, which the
    /// <c>-r</c>/<c>fps_mode cfr</c> pair then retimes.
    /// </summary>
    [Test]
    public async Task ATrimmedRunShiftsTheClockForwardAndBack()
    {
        SubtitleBurn burn = SubtitleChain.Build(Burn("/subs/a.srt"), 12, null);

        int forward = burn.Filter.IndexOf("setpts=PTS+12.000000/TB", StringComparison.Ordinal);
        int burnAt = burn.Filter.IndexOf("subtitles=", StringComparison.Ordinal);
        int back = burn.Filter.IndexOf("setpts=PTS-12.000000/TB", StringComparison.Ordinal);

        await Assert.That(forward).IsGreaterThan(-1);
        await Assert.That(burnAt).IsGreaterThan(forward);
        await Assert.That(back).IsGreaterThan(burnAt);
    }

    [Test]
    public async Task AnUntrimmedRunNeedsNoShift()
    {
        SubtitleBurn burn = SubtitleChain.Build(Burn("/subs/a.srt"), 0, null);

        await Assert.That(burn.Filter.Contains("setpts", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task TheFontsFolderIsHandedToLibass()
    {
        SubtitleBurn burn = SubtitleChain.Build(Burn("/subs/a.srt"), 0, "/tools/fonts");

        await Assert.That(burn.Filter.Contains("fontsdir=", StringComparison.Ordinal)).IsTrue();
        await Assert.That(burn.Filter.Contains("tools", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AFallbackFamilyIsForcedOverWhatTheFileAsksFor()
    {
        SubtitleDefaults settings = Burn("/subs/a.srt") with { FallbackFont = "SimHei" };
        SubtitleBurn burn = SubtitleChain.Build(settings, 0, null);

        // Quoted, so the graph parser leaves the commas inside the style alone.
        await Assert.That(burn.Filter.Contains("force_style='FontName=SimHei'", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// An embedded track is an input, not a file: it would need a second input
    /// and a map on the encoder side. Refusing is the honest answer -- a file
    /// that looks finished and has no captions is not.
    /// </summary>
    /// <summary>
    /// The host rewrites "burn track 3" into "burn this file" before the plan
    /// exists, so reaching the chain with a stream index means a caller skipped
    /// that step. The refusal names the flag that does it.
    /// </summary>
    [Test]
    public async Task AnEmbeddedTrackIsRefusedWithTheWayForward()
    {
        SubtitleDefaults settings = new() { Mode = "burn", Stream = 3 };
        SubtitleBurn burn = SubtitleChain.Build(settings, 0, null);

        await Assert.That(burn.Problems.Count).IsEqualTo(1);
        await Assert.That(burn.Problems[0].Contains("--subtitle-track", StringComparison.Ordinal)).IsTrue();
        await Assert.That(burn.Filter.Length).IsEqualTo(0);
    }

    /// <summary>
    /// Copy mode draws nothing, so the chain has nothing to build -- but a copy
    /// request with no track named is a request that would produce a file with no
    /// subtitles on it, and that is worth saying out loud.
    /// </summary>
    [Test]
    public async Task CopyModeDrawsNothingAndNeedsATrackNamed()
    {
        SubtitleBurn named = SubtitleChain.Build(
            new SubtitleDefaults { Mode = "copy", CopyTracks = [2] }, 0, null);

        await Assert.That(named.Problems.Count).IsEqualTo(0);
        await Assert.That(named.Filter.Length).IsEqualTo(0);

        SubtitleBurn unnamed = SubtitleChain.Build(new SubtitleDefaults { Mode = "copy" }, 0, null);

        await Assert.That(unnamed.Problems.Count).IsEqualTo(1);
        await Assert.That(unnamed.Problems[0].Contains("--subtitle-copy", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AnUnknownBoxShapeIsRefused()
    {
        SubtitleDefaults settings = Burn("/subs/a.srt") with { BoxAspect = "5:4" };
        SubtitleBurn burn = SubtitleChain.Build(settings, 0, null);

        await Assert.That(burn.Problems.Count).IsEqualTo(1);
        await Assert.That(burn.Problems[0].Contains("4:3", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ABurnedBoxIsAShapeLibassIsGiven()
    {
        SubtitleDefaults settings = Burn("/subs/a.srt") with { BoxAspect = "4:3" };
        SubtitleBurn burn = SubtitleChain.Build(settings, 0, null);

        await Assert.That(burn.BoxAspect).IsEqualTo((4, 3));
    }

    /// <summary>
    /// The whole-frame case stays a plain <c>-vf</c>: a filtergraph that crops,
    /// burns and overlays the same frame back is several times the work for a
    /// picture that comes out identical.
    /// </summary>
    [Test]
    public async Task TheWholeFrameStaysASimpleChain()
    {
        SubtitleBurn burn = SubtitleChain.Build(Burn("/subs/a.srt"), 0, null);
        VideoFilter filter = SubtitleChain.Apply("scale=320:240", burn, "0:v", 320, 240, "yuv420p");

        await Assert.That(filter.IsGraph).IsFalse();
        await Assert.That(filter.Chain.StartsWith("scale=320:240,subtitles=", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ABurnedBoxBuildsTheCropBurnOverlayGraph()
    {
        SubtitleBurn burn = SubtitleChain.Build(Burn("/subs/a.srt") with { BoxAspect = "4:3" }, 0, null);
        VideoFilter filter = SubtitleChain.Apply("scale=320:240", burn, "0:v", 320, 240, "yuv420p");

        await Assert.That(filter.IsGraph).IsTrue();
        await Assert.That(filter.Graph!.Contains("split=2", StringComparison.Ordinal)).IsTrue();
        await Assert.That(filter.Graph.Contains("overlay=", StringComparison.Ordinal)).IsTrue();
        await Assert.That(filter.Args().Contains("-map")).IsTrue();
    }

    /// <summary>
    /// A Windows path inside a filtergraph needs its separators doubled, its drive
    /// colon escaped and the whole value quoted -- measured, not assumed.
    /// </summary>
    [Test]
    public async Task APathIsEscapedForTheFiltergraph()
    {
        SubtitleBurn burn = SubtitleChain.Build(Burn(@"C:\subs\a.srt"), 0, null);

        await Assert.That(burn.Filter.Contains(@"'C\:\\subs\\a.srt'", StringComparison.Ordinal)).IsTrue();
    }
}
