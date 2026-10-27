using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.7: the H.264 encoder is a knob, not an assumption. The 2010s portals did
/// not run x264, so their rows name OpenH264; everything before them stays on
/// x264, and a row that is not H.264 at all is untouched by the switch.
/// </summary>
public sealed class EraH264Tests
{
    [Test]
    public async Task TheH264YearsDefaultToOpenH264()
    {
        foreach (EraPreset preset in EraPresets.All.Where(p => p.BuiltIn))
        {
            if (preset.Year >= 2010 && preset.VideoCodec == "libx264")
            {
                await Assert.That(preset.H264Impl).IsEqualTo("libopenh264").Because(preset.Id);
                // The live pass names it too, or the capture and the delivery
                // would describe two different encoders.
                await Assert.That(preset.Encoder).IsEqualTo("libopenh264").Because(preset.Id);
            }
            else if (preset.Year < 2010 && preset.VideoCodec == "libx264")
            {
                await Assert.That(preset.H264Impl).IsNull().Because(preset.Id);
            }
        }
    }

    [Test]
    public async Task NonH264RowsAreLeftAlone()
    {
        // A 2007 FLV row, a 2003 WMV row and a 2015 VP9 row must not pick up an
        // H.264 implementation just because the switch exists.
        foreach (string id in new[] { "youku/2007", "cctv/2003", "youtube/2015" })
        {
            EraPreset preset = EraPresets.Find(id)!;
            await Assert.That(preset.H264Impl).IsNull().Because(id);
            string args = preset.Fragment(native: true, h264Impl: "libopenh264");
            await Assert.That(args.Contains("openh264", StringComparison.OrdinalIgnoreCase))
                .IsFalse().Because(id);
        }
    }

    [Test]
    public async Task OpenH264IsDrivenByBitrateAndX264ByCrf()
    {
        EraPreset bilibili = EraPresets.Find("bilibili/2012")!;

        string openh264 = bilibili.Fragment(native: true);
        await Assert.That(openh264).Contains("-c:v libopenh264");
        await Assert.That(openh264).Contains("-b:v 700k");
        // No quality knob, so no CRF leaking through.
        await Assert.That(openh264.Contains("-crf", StringComparison.Ordinal)).IsFalse();

        string x264 = bilibili.Fragment(native: true, h264Impl: "libx264");
        await Assert.That(x264).Contains("-c:v libx264");

        // The native path pins the site's bitrate for x264 too: a 2012 artefact
        // is a bitrate, not "whatever CRF 24 gives".
        await Assert.That(x264.Contains("-b:v 700k", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TheModernCopyFollowsTheSameEncoder()
    {
        EraPreset bilibili = EraPresets.Find("bilibili/2012")!;

        // A viewing copy is H.264 + AAC in MP4 either way, but which H.264 is a
        // property of the run, not of the mode.
        string openh264 = bilibili.Fragment(native: false);
        await Assert.That(openh264).Contains("-c:v libopenh264");
        await Assert.That(openh264).Contains("-c:a aac");

        string x264 = bilibili.Fragment(native: false, h264Impl: "libx264");
        await Assert.That(x264).Contains("-c:v libx264");
        await Assert.That(x264).Contains("-crf");
    }

    [Test]
    public async Task EffectiveH264PrefersTheExplicitChoice()
    {
        EraPreset bilibili = EraPresets.Find("bilibili/2012")!;
        await Assert.That(bilibili.EffectiveH264(null)).IsEqualTo("libopenh264");
        await Assert.That(bilibili.EffectiveH264("libx264")).IsEqualTo("libx264");

        EraPreset youku = EraPresets.Find("youku/2007")!;
        await Assert.That(youku.EffectiveH264(null)).IsEqualTo("libx264");
    }

    [Test]
    public async Task OpenH264IsInTheSecondPassCatalogue()
    {
        SecondPassVideoCodec? codec = RecompressCodecs.FindVideo("libopenh264");
        await Assert.That(codec).IsNotNull();
        await Assert.That(codec!.Containers).Contains("mp4");
        await Assert.That(codec.QualityFlag).IsEmpty();
    }
}
