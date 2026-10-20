using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.8.2: the sample-rate table was incomplete and there was no bitrate table
/// at all. Every number asserted here was probed against the rolling FFmpeg
/// master build the toolchain downloads (tests/out/probe_limits*.py); the
/// failures it fixes are the ones a user reported -- mp2 refusing 16 kbps at
/// 44.1 kHz, libmp3lame refusing 16 kHz inside FLV -- plus the ones found
/// along the way: mp2 starting at 16 kHz rather than 22.05, wmav2 at 24 kbps,
/// and libvorbis scaling its whole band with the rate.
/// </summary>
[TestFixture]
public sealed class AudioLimitTests
{
    // ---- sample-rate floors ------------------------------------------------

    [Test]
    public void Mp2StartsAtSixteenKilohertz()
    {
        // FFmpeg: "Specified sample rate 8000 is not supported by the mp2 encoder".
        Assert.That(AudioCatalog.NearestRate("mp2", 8000), Is.EqualTo(16000));
        Assert.That(AudioCatalog.NearestRate("mp2", 16000), Is.EqualTo(16000));
        Assert.That(AudioCatalog.NearestRate("mp2", 44100), Is.EqualTo(44100));
        Assert.That(AudioCatalog.Validate("mp2", "mpeg", 8000, 1, 64), Does.Contain("16000"));
    }

    [Test]
    public void Mp2FixedSharesTheMp2RateList()
    {
        Assert.That(AudioCatalog.NearestRate("mp2fixed", 11025), Is.EqualTo(16000));
        Assert.That(AudioCatalog.Validate("mp2fixed", "mpeg", 12000, 1, 64), Is.Not.Null);
    }

    [Test]
    public void Libmp3lameKeepsItsFullRateRangeOutsideMp4()
    {
        // The codec takes 8 kHz; it is the MP4 muxer that refuses it.
        Assert.That(AudioCatalog.NearestRate("libmp3lame", 8000), Is.EqualTo(8000));
        Assert.That(AudioCatalog.NearestRate("libmp3lame", 8000, "avi"), Is.EqualTo(8000));
        Assert.That(AudioCatalog.NearestRate("libmp3lame", 8000, "matroska"), Is.EqualTo(8000));
        Assert.That(AudioCatalog.Validate("libmp3lame", "avi", 8000, 1, 64), Is.Null);
    }

    [Test]
    public void AacTakesNinetySixKilohertz()
    {
        Assert.That(AudioCatalog.NearestRate("aac", 96000), Is.EqualTo(96000));
        Assert.That(AudioCatalog.Validate("aac", "mp4", 96000, 2, 128), Is.Null);
    }

    // ---- muxer-level rate restrictions -------------------------------------

    [Test]
    public void FlvOnlyTakesFourRatesForMp3()
    {
        // "FLV does not support sample rate 16000, choose from (44100, 22050, 11025)".
        Assert.That(AudioCatalog.RatesFor("libmp3lame", "flv"),
            Is.EqualTo(new[] { 11025, 22050, 44100, 48000 }));
        Assert.That(AudioCatalog.NearestRate("libmp3lame", 16000, "flv"), Is.EqualTo(11025));
        Assert.That(AudioCatalog.NearestRate("libmp3lame", 32000, "flv"), Is.EqualTo(22050));
        Assert.That(AudioCatalog.NearestRate("mp3_mf", 8000, "flv"), Is.EqualTo(11025));

        string? error = AudioCatalog.Validate("libmp3lame", "flv", 16000, 1, 64);
        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("muxer"));
    }

    [Test]
    public void FlvDoesNotRestrictAac()
    {
        // AAC in FLV carries the real rate in an extension header.
        Assert.That(AudioCatalog.Validate("aac", "flv", 8000, 1, 32), Is.Null);
        Assert.That(AudioCatalog.Validate("aac", "flv", 44100, 2, 64), Is.Null);
    }

    [Test]
    public void Mp4RefusesMp3BelowSixteenKilohertz()
    {
        // "muxing mp3 at 8000hz is not standard, to mux anyway set strict to -1".
        Assert.That(AudioCatalog.RatesFor("libmp3lame", "mp4"),
            Is.EqualTo(new[] { 16000, 22050, 24000, 32000, 44100, 48000 }));
        Assert.That(AudioCatalog.NearestRate("libmp3lame", 11025, "mp4"), Is.EqualTo(16000));
        Assert.That(AudioCatalog.Validate("libmp3lame", "mp4", 11025, 1, 64), Is.Not.Null);
        // ...but the same rate is fine in AVI, MKV, MOV and ASF.
        foreach (string container in new[] { "avi", "matroska", "mov", "asf" })
        {
            Assert.That(AudioCatalog.Validate("libmp3lame", container, 11025, 1, 64), Is.Null,
                $"{container} should take mp3 at 11025 Hz");
        }
    }

    // ---- bitrate bands -----------------------------------------------------

    [Test]
    public void Mp2BitrateBandDependsOnTheRate()
    {
        // MPEG-2 Layer II (16/22.05/24 kHz) runs 8-160 kbps; MPEG-1
        // (32/44.1/48 kHz) starts at 32. This is the failure the user hit:
        // 16 kbps at 44.1 kHz used to abort inside FFmpeg.
        Assert.That(AudioCatalog.NearestKbps("mp2", 16, 16000), Is.EqualTo(16));
        Assert.That(AudioCatalog.NearestKbps("mp2", 16, 22050), Is.EqualTo(16));
        Assert.That(AudioCatalog.NearestKbps("mp2", 16, 44100), Is.EqualTo(32));
        Assert.That(AudioCatalog.NearestKbps("mp2", 16, 48000), Is.EqualTo(32));
        Assert.That(AudioCatalog.Validate("mp2", "mpeg", 44100, 1, 16), Does.Contain("32"));
        Assert.That(AudioCatalog.Validate("mp2", "mpeg", 16000, 1, 16), Is.Null);
    }

    [Test]
    public void Mp2OnlyTakesStandardBitrates()
    {
        // 36 kbps is inside the 32-384 band but is not a Layer II bitrate;
        // 40 kbps is one, at 16 kHz -- but not at 32 kHz, where the nearest
        // standard values are 32 (below) and 48 (above) and the tie goes down.
        Assert.That(AudioCatalog.NearestKbps("mp2", 36, 44100), Is.EqualTo(32));
        Assert.That(AudioCatalog.NearestKbps("mp2", 40, 16000), Is.EqualTo(40));
        Assert.That(AudioCatalog.NearestKbps("mp2", 40, 32000), Is.EqualTo(32));
        Assert.That(AudioCatalog.Validate("mp2", "mpeg", 44100, 1, 36), Is.Not.Null);
        Assert.That(AudioCatalog.Validate("mp2", "mpeg", 44100, 1, 48), Is.Null);
    }

    [Test]
    public void WmaBottomOutAtTwentyFourKbps()
    {
        foreach (string codec in new[] { "wmav1", "wmav2" })
        {
            Assert.That(AudioCatalog.NearestKbps(codec, 16, 22050), Is.EqualTo(24));
            Assert.That(AudioCatalog.NearestKbps(codec, 8, 8000), Is.EqualTo(24));
            Assert.That(AudioCatalog.Validate(codec, "asf", 22050, 1, 16), Does.Contain("24"));
            Assert.That(AudioCatalog.Validate(codec, "asf", 22050, 1, 24), Is.Null);
        }
    }

    [Test]
    public void VorbisBandScalesWithTheRate()
    {
        // Ceiling: 40 kbps at 8 kHz, 80 at 22050, 224 at 44.1/48. Floor: 12 at
        // 11/12 kHz, 16 at 16/22.05/24, 32 at 32 and up. A 128 kbps request at
        // 22050 Hz used to fail with "encoder setup failed".
        Assert.That(AudioCatalog.NearestKbps("libvorbis", 96, 22050), Is.EqualTo(80));
        Assert.That(AudioCatalog.NearestKbps("libvorbis", 128, 48000), Is.EqualTo(128));
        Assert.That(AudioCatalog.NearestKbps("libvorbis", 256, 48000), Is.EqualTo(224));
        Assert.That(AudioCatalog.NearestKbps("libvorbis", 8, 8000), Is.EqualTo(8));
        Assert.That(AudioCatalog.NearestKbps("libvorbis", 8, 11025), Is.EqualTo(12));
        Assert.That(AudioCatalog.NearestKbps("libvorbis", 8, 16000), Is.EqualTo(16));
        Assert.That(AudioCatalog.NearestKbps("libvorbis", 16, 32000), Is.EqualTo(32));
        Assert.That(AudioCatalog.Validate("libvorbis", "ogg", 22050, 1, 96), Does.Contain("80"));
        Assert.That(AudioCatalog.Validate("libvorbis", "ogg", 44100, 1, 96), Is.Null);
    }

    [Test]
    public void OpusTopsOutAtTwoFiftySix()
    {
        Assert.That(AudioCatalog.NearestKbps("libopus", 320, 48000), Is.EqualTo(256));
        Assert.That(AudioCatalog.NearestKbps("libopus", 8, 8000), Is.EqualTo(8));
        Assert.That(AudioCatalog.Validate("libopus", "ogg", 48000, 2, 320), Is.Not.Null);
    }

    [Test]
    public void LameAndAacKeepTheFreeRange()
    {
        // LAME takes any bitrate at any rate it supports, and so does the
        // native AAC encoder (quality suffers, nothing aborts).
        foreach (int rate in new[] { 8000, 22050, 44100, 48000 })
        {
            Assert.That(AudioCatalog.NearestKbps("libmp3lame", 8, rate), Is.EqualTo(8));
            Assert.That(AudioCatalog.NearestKbps("libmp3lame", 640, rate), Is.EqualTo(640));
            Assert.That(AudioCatalog.Validate("libmp3lame", "avi", rate, 1, 8), Is.Null);
            Assert.That(AudioCatalog.Validate("aac", "mp4", rate, 1, 640), Is.Null);
        }
    }

    // ---- correction as a whole ---------------------------------------------

    [Test]
    public void EffectiveCorrectsRateAndBitrateTogether()
    {
        // FLV narrows the rate first, and the band then applies to the snapped
        // rate -- the order matters for mp2.
        (int? rate, int channels, int kbps) =
            AudioCatalog.Effective("libmp3lame", 16000, 2, 32, "flv");
        Assert.That(rate, Is.EqualTo(11025));
        Assert.That(channels, Is.EqualTo(2));
        Assert.That(kbps, Is.EqualTo(32));

        (int? mpegRate, _, int mpegKbps) =
            AudioCatalog.Effective("mp2", 8000, 1, 16, "mpeg");
        Assert.That(mpegRate, Is.EqualTo(16000));
        Assert.That(mpegKbps, Is.EqualTo(16));

        (int? wmaRate, _, int wmaKbps) =
            AudioCatalog.Effective("wmav2", 22050, 2, 16, "asf");
        Assert.That(wmaRate, Is.EqualTo(22050));
        Assert.That(wmaKbps, Is.EqualTo(24));
    }

    [Test]
    public void EveryEraPresetSurvivesTheTables()
    {
        // The presets are written for a period, not for a codec; the correction
        // pass is what keeps them alive across containers.
        string[] containers = ["rm", "mpeg", "avi", "mp4", "flv", "asf", "ogg", "matroska"];
        foreach (EraPreset era in EraPresets.All)
        {
            foreach (string container in containers)
            {
                List<string> codecs = AudioCatalog.Compatible(container,
                    ["ac3", "aac", "libmp3lame", "mp2", "wmav2", "libvorbis", "libopus"]);
                if (codecs.Count == 0) continue;
                string codec = codecs[0];
                (int? rate, _, int kbps) = AudioCatalog.Effective(
                    codec, era.AudioRate, era.AudioChannels, era.AudioKbps, container);
                Assert.That(AudioCatalog.Validate(codec, container, rate, era.AudioChannels, kbps),
                    Is.Null, $"{era.Id} + {container} + {codec} should be correctable");
            }
        }
    }

    // ---- experimental encoders ---------------------------------------------

    [Test]
    public void TheNativeVorbisAndOpusEncodersAreRefused()
    {
        // Both need -strict, which this tool never passes: offering them would
        // only guarantee a failed encode.
        Assert.That(AudioCatalog.Validate("vorbis", "ogg", 44100, 2, 64), Does.Contain("strict"));
        Assert.That(AudioCatalog.Validate("opus", "ogg", 48000, 2, 64), Does.Contain("strict"));
        Assert.That(AudioCatalog.Validate("libvorbis", "ogg", 44100, 2, 64), Is.Null);
        Assert.That(AudioCatalog.Validate("libopus", "ogg", 48000, 2, 64), Is.Null);
    }

    [Test]
    public void TheOggPickerOffersTheWorkingPairOnly()
    {
        List<string> ogg = AudioCatalog.Compatible("ogg",
            ["libvorbis", "vorbis", "libopus", "opus", "flac"]);
        Assert.That(ogg, Is.EqualTo(new[] { "libvorbis", "libopus", "flac" }));
    }

    [Test]
    public void UnknownCodecsStillPassThrough()
    {
        // A codec this build has and this table does not model must still run,
        // whatever the numbers.
        Assert.That(AudioCatalog.Validate("libfdk_aac", "mp4", 8000, 1, 8), Is.Null);
        Assert.That(AudioCatalog.Validate("libfdk_aac", "flv", 16000, 1, 8), Is.Null);
        Assert.That(AudioCatalog.NearestKbps("libfdk_aac", 4), Is.EqualTo(4));
        Assert.That(AudioCatalog.KbpsBand("libfdk_aac", 44100),
            Is.EqualTo((8, 640, System.Array.Empty<int>())));
    }
}
