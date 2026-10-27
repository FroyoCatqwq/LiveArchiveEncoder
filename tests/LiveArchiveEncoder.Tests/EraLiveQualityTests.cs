using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.2: an era states its quality, and it states it once per scale.
///
/// <para>The defect this file pins: a preset never touched the live quality at
/// all, so the number left over from the previous encoder was used as-is. A 2007
/// row (352x264@15, 320 kbps) applied after an MPEG-family encoder therefore
/// handed <c>q:v 9</c> to libx264 as <c>-crf 9</c>, which is 935 kbps on the
/// reference clip -- 2.9x the row's ceiling. The ladder reads exactly that number
/// and walked to its last rung within a second.</para>
/// </summary>
public sealed class EraLiveQualityTests
{
    [Test]
    public async Task EveryEncoderIsReadInItsOwnScale()
    {
        await Assert.That(EncoderMath.ScaleOf("mpeg4")).IsEqualTo(EncoderMath.QualityScale.Qv);
        await Assert.That(EncoderMath.ScaleOf("flv")).IsEqualTo(EncoderMath.QualityScale.Qv);
        await Assert.That(EncoderMath.ScaleOf("rv20")).IsEqualTo(EncoderMath.QualityScale.Qv);
        await Assert.That(EncoderMath.ScaleOf("libx264")).IsEqualTo(EncoderMath.QualityScale.Crf);
        await Assert.That(EncoderMath.ScaleOf("libx265")).IsEqualTo(EncoderMath.QualityScale.Crf);
        // No constant-quality mode at all: bitrate is its only dial.
        await Assert.That(EncoderMath.ScaleOf("libopenh264")).IsEqualTo(EncoderMath.QualityScale.Bitrate);
    }

    [Test]
    public async Task ConvertingBetweenScalesKeepsThePicture()
    {
        // Calibrated by equal bitrate: these two pairs measure the same rate at
        // 320x240@15 on the reference clip (2026-10-27).
        await Assert.That(EncoderMath.ConvertQuality("mpeg4", "libx264", 9)).IsEqualTo(18);
        await Assert.That(EncoderMath.ConvertQuality("mpeg4", "libx264", 12)).IsEqualTo(22);
        // And back, without drifting more than one step.
        await Assert.That(EncoderMath.ConvertQuality("libx264", "mpeg4", 18)).IsEqualTo(9);
        // Same scale is the identity, and a bitrate encoder passes the number
        // through untouched -- it has no scale to translate into.
        await Assert.That(EncoderMath.ConvertQuality("mpeg4", "mpeg4", 9)).IsEqualTo(9);
        await Assert.That(EncoderMath.ConvertQuality("libx264", "libopenh264", 22)).IsEqualTo(22);
    }

    [Test]
    public async Task CrfIsPinnedToTheCeilingAndMpegIsNot()
    {
        List<string> x264 = ArgsBuilder.VideoQualityArgs("libx264", 22, 320);
        await Assert.That(string.Join(' ', x264)).IsEqualTo("-crf 22 -maxrate 320k -bufsize 640k");

        // A fixed quantiser plus a tight VBV is a contradictory request: the
        // MPEG-family encoders answer it with "rc buffer underflow" on every
        // frame and overshoot anyway (measured 2026-10-27). They get the
        // quantiser alone.
        List<string> mpeg = ArgsBuilder.VideoQualityArgs("mpeg4", 11, 320);
        await Assert.That(string.Join(' ', mpeg)).IsEqualTo("-q:v 11");

        List<string> openh264 = ArgsBuilder.VideoQualityArgs("libopenh264", 21, 700);
        await Assert.That(string.Join(' ', openh264)).IsEqualTo("-rc_mode bitrate -b:v 700k");
    }

    [Test]
    public async Task OpenH264AlwaysGetsABitrate()
    {
        // Its own default is 1.1 Mbps at 352x264 -- every rung at once. A caller
        // that forgot the ceiling must still leave it pinned.
        List<string> args = ArgsBuilder.VideoQualityArgs("libopenh264", 21, null);
        await Assert.That(string.Join(' ', args)).IsEqualTo("-rc_mode bitrate -b:v 200k");
        await Assert.That(ArgsBuilder.VideoQualityArgs("libopenh264", 21, 0)).Contains("200k");
    }

    [Test]
    public async Task APresetHandsOverTheQualityItsEncoderReads()
    {
        EraPreset youku = EraPresets.Find("youku/2007")!;

        await Assert.That(youku.QualityFor("flv")).IsEqualTo(youku.LiveQv);
        await Assert.That(youku.QualityFor("libx264")).IsEqualTo(youku.LiveCrf);
        // Asking for H.264 on a 2007 row must land on x264's scale, which is
        // three steps coarser than the row's q:v.
        await Assert.That(youku.LiveCrf).IsGreaterThan(youku.LiveQv);
    }

    [Test]
    public async Task EveryBuiltInRowStatesBothScales()
    {
        foreach (EraPreset preset in EraPresets.WithLiveLayer)
        {
            await Assert.That(preset.LiveQv).IsBetween(2, 31).Because(preset.Id);
            await Assert.That(preset.LiveCrf).IsBetween(0, 51).Because(preset.Id);
            // A CRF coarser than the quantiser is the whole point of the fix;
            // equal numbers mean somebody copied the wrong column.
            await Assert.That(preset.LiveCrf).IsGreaterThan(preset.LiveQv).Because(preset.Id);
        }
    }

    [Test]
    public async Task ApplyingALiveLayerWritesTheQualityForTheEncoderInEffect()
    {
        var c = new Config();
        EraPreset youku = EraPresets.Find("youku/2007")!;

        c.Encoder = new EncoderInfo { Name = "libx264" };
        await Assert.That(youku.ApplyLive(c)).IsTrue();
        await Assert.That(c.Qv).IsEqualTo(youku.LiveCrf);

        c.Encoder = new EncoderInfo { Name = "flv" };
        await Assert.That(youku.ApplyLive(c)).IsTrue();
        await Assert.That(c.Qv).IsEqualTo(youku.LiveQv);
    }

    [Test]
    public async Task TheCapturePipelineFollowsTheYear()
    {
        // 1998-2003: a long averaging window, a small frame queue, and no
        // deinterlacer -- yadif did not exist.
        (double w98, int b98, string d98) = EraPresets.LivePipeline(1998);
        await Assert.That(w98).IsEqualTo(3.0);
        await Assert.That(b98).IsEqualTo(4);
        await Assert.That(d98).IsEqualTo("off");

        (double w06, int b06, string d06) = EraPresets.LivePipeline(2006);
        await Assert.That(w06).IsEqualTo(2.0);
        await Assert.That(b06).IsEqualTo(6);
        await Assert.That(d06).IsEqualTo("yadif");

        (double w14, int b14, string d14) = EraPresets.LivePipeline(2014);
        await Assert.That(w14).IsEqualTo(2.0);
        await Assert.That(b14).IsEqualTo(8);
        await Assert.That(d14).IsEqualTo("yadif");
    }

    [Test]
    public async Task ARowCarriesItsPipelineIntoTheConfig()
    {
        var c = new Config();
        EraPreset cctv = EraPresets.Find("cctv/2003")!;
        await Assert.That(cctv.ApplyLive(c)).IsTrue();
        await Assert.That(c.Window).IsEqualTo(3.0);
        await Assert.That(c.BufferFrames).IsEqualTo(4);
        await Assert.That(c.Deinterlace).IsEqualTo("off");

        var c2 = new Config();
        EraPreset bilibili = EraPresets.Find("bilibili/2014")!;
        await Assert.That(bilibili.ApplyLive(c2)).IsTrue();
        await Assert.That(c2.Window).IsEqualTo(2.0);
        await Assert.That(c2.BufferFrames).IsEqualTo(8);
        await Assert.That(c2.Deinterlace).IsEqualTo("yadif");
        // Only frames the source marks as interlaced: a progressive file has to
        // pass through untouched.
        await Assert.That(c2.DeintMarkedOnly).IsTrue();
    }

    [Test]
    public async Task ACustomRowKeepsItsPipelineAndOldFilesGetTheYear()
    {
        var stored = new CustomEraConfig
        {
            Id = "mine/2009",
            Kind = "era",
            Year = 2009,
            YearFrom = 2009,
            YearTo = 2009,
            Encoder = "flv",
            Width = 352,
            Height = 264,
            Fps = 15,
            LimitKbps = 320,
            LiveQv = 12,
            LiveCrf = 23,
            WindowSeconds = 2.5,
            BufferFrames = 5,
            Deinterlace = "bwdif",
        };

        EraPreset round = EraConfigMap.FromConfig(stored)!;
        await Assert.That(round.LiveQv).IsEqualTo(12);
        await Assert.That(round.LiveCrf).IsEqualTo(23);
        await Assert.That(round.WindowSeconds).IsEqualTo(2.5);
        await Assert.That(round.BufferFrames).IsEqualTo(5);
        await Assert.That(round.Deinterlace).IsEqualTo("bwdif");

        CustomEraConfig again = EraConfigMap.ToConfig(round);
        await Assert.That(again.LiveQv).IsEqualTo(12);
        await Assert.That(again.LiveCrf).IsEqualTo(23);
        await Assert.That(again.WindowSeconds).IsEqualTo(2.5);

        // A file from before v7.9.8.2 states none of this; the year it claims
        // supplies them rather than a modern default.
        stored.LiveQv = null;
        stored.LiveCrf = null;
        stored.WindowSeconds = null;
        stored.BufferFrames = null;
        stored.Deinterlace = null;
        EraPreset old = EraConfigMap.FromConfig(stored)!;
        await Assert.That(old.WindowSeconds).IsEqualTo(2.0);
        await Assert.That(old.BufferFrames).IsEqualTo(6);
        await Assert.That(old.Deinterlace).IsEqualTo("yadif");
        await Assert.That(old.LiveCrf).IsGreaterThan(old.LiveQv);
    }
}
