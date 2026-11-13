using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.10: the opt-in tear, and the -q:v / -b:v precedence that made
/// <c>--era dvd</c> encode at 2.1 Mbps while its preset said 6000.
/// </summary>
public sealed class V79910Tests
{
    [Test]
    public async Task HorizontalTearSplitsTheFrameHalfWayDown()
    {
        string chain = ArgsBuilder.TearingChain("horizontal");
        await Assert.That(chain).StartsWith("tblend=all_expr=");
        await Assert.That(chain.Contains("H*0.5", StringComparison.Ordinal)).IsTrue();
        // A and B are "this frame" and "the previous frame"; the tear is the
        // top half taking the older one.
        await Assert.That(chain.Contains(",B,A)", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task RandomTearMovesTheSplitEveryFrame()
    {
        string chain = ArgsBuilder.TearingChain("random");
        await Assert.That(chain.Contains("mod(N*37", StringComparison.Ordinal)).IsTrue();
        // The comma inside an FFmpeg expression must be escaped or the filter
        // parser splits the graph there.
        await Assert.That(chain.Contains("\\,", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task NoModeMeansNoTear()
    {
        await Assert.That(ArgsBuilder.TearingChain(null)).IsEqualTo("");
        await Assert.That(ArgsBuilder.TearingChain("")).IsEqualTo("");
        await Assert.That(ArgsBuilder.TearingChain("off")).IsEqualTo("");
        await Assert.That(ArgsBuilder.TearingChain("bogus")).IsEqualTo("");
    }

    [Test]
    public async Task APinnedBitrateOutranksTheQuantiser()
    {
        // -q:v wins over -b:v inside FFmpeg, so emitting it would silently
        // discard the bitrate a disc preset asked for.
        await Assert.That(ArgsBuilder.PinsBitrate("-maxrate 8000k -bufsize 1835k")).IsTrue();
        await Assert.That(ArgsBuilder.PinsBitrate("-b:v 6000k")).IsTrue();
        await Assert.That(ArgsBuilder.PinsBitrate("-q:v 11")).IsFalse();
        await Assert.That(ArgsBuilder.PinsBitrate("")).IsFalse();
        await Assert.That(ArgsBuilder.PinsBitrate(null)).IsFalse();

        List<string> qv = ArgsBuilder.VideoQualityArgs("mpeg2video", 10, 6000);
        await Assert.That(qv[0]).IsEqualTo("-q:v");

        List<string> bitrate = ArgsBuilder.VideoQualityArgs("mpeg2video", 10, 6000,
            preferBitrate: true);
        await Assert.That(bitrate[0]).IsEqualTo("-b:v");
        await Assert.That(bitrate[1]).IsEqualTo("6000k");
    }

    [Test]
    public async Task TheFilterChainCarriesTheTearLast()
    {
        string plain = ArgsBuilder.BuildFilters(
            352, 288, 25, "crop", "center", "black", "bicubic", "yuv420p",
            "off", "send_frame", "auto", false);
        await Assert.That(plain.Contains("tblend", StringComparison.Ordinal)).IsFalse();

        string torn = ArgsBuilder.BuildFilters(
            352, 288, 25, "crop", "center", "black", "bicubic", "yuv420p",
            "off", "send_frame", "auto", false, tearing: "horizontal");
        // Last in the chain: tblend needs the finished frame, and everything
        // before it has already produced one in the transport format.
        await Assert.That(torn.EndsWith(",tblend=all_expr='if(lt(Y,H*0.5),B,A)'",
            StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TearingKeysExistInBothLanguages()
    {
        foreach (string key in new[] { "param.tearing", "param.prompt.tearing", "note.tearing" })
        {
            await Assert.That(L.KeysFor(L.English).Contains(key)).IsTrue();
            await Assert.That(L.KeysFor(L.Chinese).Contains(key)).IsTrue();
        }
    }
}
