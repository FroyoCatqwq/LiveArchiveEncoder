using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.8: the second pass can be told to use either H.264 implementation, and
/// each one is driven in the only scale it has.
///
/// <para>The defect this pins: naming <c>libopenh264</c> and giving no quality
/// number produced <c>-c:v libopenh264 -pix_fmt yuv420p</c> and nothing else.
/// No bitrate, no mode -- so the encoder ran on its own 2 Mbps target. Measured
/// on 640x480 testsrc2: a pass that asked for 300 kbps produced 2002 kbps, a
/// 6.7x miss that no preset could survive. The user saw "the preset was
/// applied" and got a file seven times too big.</para>
/// </summary>
public sealed class RecompressOpenH264Tests
{
    [Test]
    public async Task OpenH264AlwaysGetsBothABitrateAndAMode()
    {
        SecondPassVideoCodec openh264 = RecompressCodecs.FindVideo("libopenh264")!;

        // No quality number: the catalog's default bitrate has to be there, or
        // the encoder falls back to its built-in target.
        string bare = RecompressCodecs.VideoArgs(openh264, null);
        await Assert.That(bare).Contains("-c:v libopenh264");
        await Assert.That(bare).Contains("-b:v 300k");
        await Assert.That(bare).Contains("-rc_mode bitrate");

        // An explicit bitrate wins.
        await Assert.That(RecompressCodecs.VideoArgs(openh264, null, 700)).Contains("-b:v 700k");

        // A quality number is not a bitrate and must not be mistaken for one;
        // the CLI refuses the combination before this point, and the catalog
        // still produces a controlled command if something calls it directly.
        string withQuality = RecompressCodecs.VideoArgs(openh264, 24);
        await Assert.That(withQuality).Contains("-b:v 300k");
        await Assert.That(withQuality.Contains("-crf", StringComparison.Ordinal)).IsFalse();
        await Assert.That(withQuality.Contains("-q:v", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ThePresetPathPinsTheSitesOwnBitrate()
    {
        SecondPassVideoCodec openh264 = RecompressCodecs.FindVideo("libopenh264")!;

        string atRate = RecompressCodecs.VideoArgsAtBitrate(openh264, 700);
        await Assert.That(atRate).Contains("-b:v 700k");
        await Assert.That(atRate).Contains("-rc_mode bitrate");
        await Assert.That(atRate).Contains("-pix_fmt yuv420p");
    }

    [Test]
    public async Task X264KeepsItsCrfAndTakesNoBitrate()
    {
        SecondPassVideoCodec x264 = RecompressCodecs.FindVideo("libx264")!;

        string args = RecompressCodecs.VideoArgs(x264, 26);
        await Assert.That(args).Contains("-crf 26");
        await Assert.That(args.Contains("-b:v", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task TheEraPresetCarriesTheModeOnBothRenderings()
    {
        EraPreset bilibili = EraPresets.Find("bilibili/2012")!;

        // Native: the site's own bitrate, through OpenH264.
        string native = bilibili.Fragment(native: true);
        await Assert.That(native).Contains("-c:v libopenh264");
        await Assert.That(native).Contains("-rc_mode bitrate");
        await Assert.That(native).Contains("-b:v 700k");

        // Modern: a viewing copy may use either implementation, and the switch
        // must not change which flags the chosen one gets.
        string modernOpen = bilibili.Fragment(native: false);
        await Assert.That(modernOpen).Contains("-rc_mode bitrate");

        string modernX264 = bilibili.Fragment(native: false, h264Impl: "libx264");
        await Assert.That(modernX264).Contains("-c:v libx264");
        await Assert.That(modernX264.Contains("-rc_mode", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task EveryBitrateOnlyCodecCarriesADefault()
    {
        // The rule, not the one encoder: any entry with no quality flag must be
        // able to produce a bitrate on its own. A future codec added without a
        // default would otherwise reintroduce the same silent 2 Mbps.
        foreach (SecondPassVideoCodec codec in RecompressCodecs.Video)
        {
            if (codec.QualityFlag.Length > 0) continue;
            await Assert.That(codec.DefaultKbps).IsGreaterThan(0).Because(codec.Name);
        }
    }
}
