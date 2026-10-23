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
public sealed class AudioLimitTests
{
    // ---- sample-rate floors ------------------------------------------------

    [Test]
    public async Task Mp2StartsAtSixteenKilohertz()
    {
        // FFmpeg: "Specified sample rate 8000 is not supported by the mp2 encoder".
        await Assert.That(AudioCatalog.NearestRate("mp2", 8000)).IsEqualTo(16000);
        await Assert.That(AudioCatalog.NearestRate("mp2", 16000)).IsEqualTo(16000);
        await Assert.That(AudioCatalog.NearestRate("mp2", 44100)).IsEqualTo(44100);
        await Assert.That(AudioCatalog.Validate("mp2", "mpeg", 8000, 1, 64)).Contains("16000");
    }

    [Test]
    public async Task Mp2FixedSharesTheMp2RateList()
    {
        await Assert.That(AudioCatalog.NearestRate("mp2fixed", 11025)).IsEqualTo(16000);
        await Assert.That(AudioCatalog.Validate("mp2fixed", "mpeg", 12000, 1, 64)).IsNotNull();
    }

    [Test]
    public async Task Libmp3lameKeepsItsFullRateRangeOutsideMp4()
    {
        // The codec takes 8 kHz; it is the MP4 muxer that refuses it.
        await Assert.That(AudioCatalog.NearestRate("libmp3lame", 8000)).IsEqualTo(8000);
        await Assert.That(AudioCatalog.NearestRate("libmp3lame", 8000, "avi")).IsEqualTo(8000);
        await Assert.That(AudioCatalog.NearestRate("libmp3lame", 8000, "matroska")).IsEqualTo(8000);
        await Assert.That(AudioCatalog.Validate("libmp3lame", "avi", 8000, 1, 64)).IsNull();
    }

    [Test]
    public async Task AacTakesNinetySixKilohertz()
    {
        await Assert.That(AudioCatalog.NearestRate("aac", 96000)).IsEqualTo(96000);
        await Assert.That(AudioCatalog.Validate("aac", "mp4", 96000, 2, 128)).IsNull();
    }

    // ---- muxer-level rate restrictions -------------------------------------

    [Test]
    public async Task FlvOnlyTakesFourRatesForMp3()
    {
        // "FLV does not support sample rate 16000, choose from (44100, 22050, 11025)".
        await Assert.That(AudioCatalog.RatesFor("libmp3lame", "flv")).IsEquivalentTo(new[] { 11025, 22050, 44100, 48000 });
        await Assert.That(AudioCatalog.NearestRate("libmp3lame", 16000, "flv")).IsEqualTo(11025);
        await Assert.That(AudioCatalog.NearestRate("libmp3lame", 32000, "flv")).IsEqualTo(22050);
        await Assert.That(AudioCatalog.NearestRate("mp3_mf", 8000, "flv")).IsEqualTo(11025);

        string? error = AudioCatalog.Validate("libmp3lame", "flv", 16000, 1, 64);
        await Assert.That(error).IsNotNull();
        await Assert.That(error).Contains("muxer");
    }

    [Test]
    public async Task FlvDoesNotRestrictAac()
    {
        // AAC in FLV carries the real rate in an extension header.
        await Assert.That(AudioCatalog.Validate("aac", "flv", 8000, 1, 32)).IsNull();
        await Assert.That(AudioCatalog.Validate("aac", "flv", 44100, 2, 64)).IsNull();
    }

    [Test]
    public async Task Mp4RefusesMp3BelowSixteenKilohertz()
    {
        // "muxing mp3 at 8000hz is not standard, to mux anyway set strict to -1".
        await Assert.That(AudioCatalog.RatesFor("libmp3lame", "mp4")).IsEquivalentTo(new[] { 16000, 22050, 24000, 32000, 44100, 48000 });
        await Assert.That(AudioCatalog.NearestRate("libmp3lame", 11025, "mp4")).IsEqualTo(16000);
        await Assert.That(AudioCatalog.Validate("libmp3lame", "mp4", 11025, 1, 64)).IsNotNull();
        // ...but the same rate is fine in AVI, MKV, MOV and ASF.
        foreach (string container in new[] { "avi", "matroska", "mov", "asf" })
        {
            await Assert.That(AudioCatalog.Validate("libmp3lame", container, 11025, 1, 64)).IsNull().Because($"{container} should take mp3 at 11025 Hz");
        }
    }

    // ---- bitrate bands -----------------------------------------------------

    [Test]
    public async Task Mp2BitrateBandDependsOnTheRate()
    {
        // MPEG-2 Layer II (16/22.05/24 kHz) runs 8-160 kbps; MPEG-1
        // (32/44.1/48 kHz) starts at 32. This is the failure the user hit:
        // 16 kbps at 44.1 kHz used to abort inside FFmpeg.
        await Assert.That(AudioCatalog.NearestKbps("mp2", 16, 16000)).IsEqualTo(16);
        await Assert.That(AudioCatalog.NearestKbps("mp2", 16, 22050)).IsEqualTo(16);
        await Assert.That(AudioCatalog.NearestKbps("mp2", 16, 44100)).IsEqualTo(32);
        await Assert.That(AudioCatalog.NearestKbps("mp2", 16, 48000)).IsEqualTo(32);
        await Assert.That(AudioCatalog.Validate("mp2", "mpeg", 44100, 1, 16)).Contains("32");
        await Assert.That(AudioCatalog.Validate("mp2", "mpeg", 16000, 1, 16)).IsNull();
    }

    [Test]
    public async Task Mp2OnlyTakesStandardBitrates()
    {
        // 36 kbps is inside the 32-384 band but is not a Layer II bitrate;
        // 40 kbps is one, at 16 kHz -- but not at 32 kHz, where the nearest
        // standard values are 32 (below) and 48 (above) and the tie goes down.
        await Assert.That(AudioCatalog.NearestKbps("mp2", 36, 44100)).IsEqualTo(32);
        await Assert.That(AudioCatalog.NearestKbps("mp2", 40, 16000)).IsEqualTo(40);
        await Assert.That(AudioCatalog.NearestKbps("mp2", 40, 32000)).IsEqualTo(32);
        await Assert.That(AudioCatalog.Validate("mp2", "mpeg", 44100, 1, 36)).IsNotNull();
        await Assert.That(AudioCatalog.Validate("mp2", "mpeg", 44100, 1, 48)).IsNull();
    }

    [Test]
    public async Task WmaBottomOutAtTwentyFourKbps()
    {
        foreach (string codec in new[] { "wmav1", "wmav2" })
        {
            await Assert.That(AudioCatalog.NearestKbps(codec, 16, 22050)).IsEqualTo(24);
            await Assert.That(AudioCatalog.NearestKbps(codec, 8, 8000)).IsEqualTo(24);
            await Assert.That(AudioCatalog.Validate(codec, "asf", 22050, 1, 16)).Contains("24");
            await Assert.That(AudioCatalog.Validate(codec, "asf", 22050, 1, 24)).IsNull();
        }
    }

    [Test]
    public async Task VorbisBandScalesWithTheRate()
    {
        // Ceiling: 40 kbps at 8 kHz, 80 at 22050, 224 at 44.1/48. Floor: 12 at
        // 11/12 kHz, 16 at 16/22.05/24, 32 at 32 and up. A 128 kbps request at
        // 22050 Hz used to fail with "encoder setup failed".
        await Assert.That(AudioCatalog.NearestKbps("libvorbis", 96, 22050)).IsEqualTo(80);
        await Assert.That(AudioCatalog.NearestKbps("libvorbis", 128, 48000)).IsEqualTo(128);
        await Assert.That(AudioCatalog.NearestKbps("libvorbis", 256, 48000)).IsEqualTo(224);
        await Assert.That(AudioCatalog.NearestKbps("libvorbis", 8, 8000)).IsEqualTo(8);
        await Assert.That(AudioCatalog.NearestKbps("libvorbis", 8, 11025)).IsEqualTo(12);
        await Assert.That(AudioCatalog.NearestKbps("libvorbis", 8, 16000)).IsEqualTo(16);
        await Assert.That(AudioCatalog.NearestKbps("libvorbis", 16, 32000)).IsEqualTo(32);
        await Assert.That(AudioCatalog.Validate("libvorbis", "ogg", 22050, 1, 96)).Contains("80");
        await Assert.That(AudioCatalog.Validate("libvorbis", "ogg", 44100, 1, 96)).IsNull();
    }

    [Test]
    public async Task OpusTopsOutAtTwoFiftySix()
    {
        await Assert.That(AudioCatalog.NearestKbps("libopus", 320, 48000)).IsEqualTo(256);
        await Assert.That(AudioCatalog.NearestKbps("libopus", 8, 8000)).IsEqualTo(8);
        await Assert.That(AudioCatalog.Validate("libopus", "ogg", 48000, 2, 320)).IsNotNull();
    }

    [Test]
    public async Task LameAndAacKeepTheFreeRange()
    {
        // LAME takes any bitrate at any rate it supports, and so does the
        // native AAC encoder (quality suffers, nothing aborts).
        foreach (int rate in new[] { 8000, 22050, 44100, 48000 })
        {
            await Assert.That(AudioCatalog.NearestKbps("libmp3lame", 8, rate)).IsEqualTo(8);
            await Assert.That(AudioCatalog.NearestKbps("libmp3lame", 640, rate)).IsEqualTo(640);
            await Assert.That(AudioCatalog.Validate("libmp3lame", "avi", rate, 1, 8)).IsNull();
            await Assert.That(AudioCatalog.Validate("aac", "mp4", rate, 1, 640)).IsNull();
        }
    }

    // ---- correction as a whole ---------------------------------------------

    [Test]
    public async Task EffectiveCorrectsRateAndBitrateTogether()
    {
        // FLV narrows the rate first, and the band then applies to the snapped
        // rate -- the order matters for mp2.
        (int? rate, int channels, int kbps) =
            AudioCatalog.Effective("libmp3lame", 16000, 2, 32, "flv");
        await Assert.That(rate).IsEqualTo(11025);
        await Assert.That(channels).IsEqualTo(2);
        await Assert.That(kbps).IsEqualTo(32);

        (int? mpegRate, _, int mpegKbps) =
            AudioCatalog.Effective("mp2", 8000, 1, 16, "mpeg");
        await Assert.That(mpegRate).IsEqualTo(16000);
        await Assert.That(mpegKbps).IsEqualTo(16);

        (int? wmaRate, _, int wmaKbps) =
            AudioCatalog.Effective("wmav2", 22050, 2, 16, "asf");
        await Assert.That(wmaRate).IsEqualTo(22050);
        await Assert.That(wmaKbps).IsEqualTo(24);
    }

    [Test]
    public async Task EveryEraPresetSurvivesTheTables()
    {
        // The presets are written for a period, not for a codec; the correction
        // pass is what keeps them alive across containers. Site presets own no
        // live audio settings (v7.9.5), so only the era rows take part.
        string[] containers = ["rm", "mpeg", "avi", "mp4", "flv", "asf", "ogg", "matroska"];
        foreach (EraPreset era in EraPresets.WithLiveLayer)
        {
            foreach (string container in containers)
            {
                List<string> codecs = AudioCatalog.Compatible(container,
                    ["ac3", "aac", "libmp3lame", "mp2", "wmav2", "libvorbis", "libopus"]);
                if (codecs.Count == 0) continue;
                string codec = codecs[0];
                (int? rate, _, int kbps) = AudioCatalog.Effective(
                    codec, era.AudioRate, era.AudioChannels!.Value, era.AudioKbps!.Value, container);
                await Assert.That(AudioCatalog.Validate(codec, container, rate, era.AudioChannels!.Value, kbps)).IsNull().Because($"{era.Id} + {container} + {codec} should be correctable");
            }
        }
    }

    // ---- experimental encoders ---------------------------------------------

    [Test]
    public async Task TheNativeVorbisAndOpusEncodersAreRefused()
    {
        // Both need -strict, which this tool never passes: offering them would
        // only guarantee a failed encode.
        await Assert.That(AudioCatalog.Validate("vorbis", "ogg", 44100, 2, 64)).Contains("strict");
        await Assert.That(AudioCatalog.Validate("opus", "ogg", 48000, 2, 64)).Contains("strict");
        await Assert.That(AudioCatalog.Validate("libvorbis", "ogg", 44100, 2, 64)).IsNull();
        await Assert.That(AudioCatalog.Validate("libopus", "ogg", 48000, 2, 64)).IsNull();
    }

    [Test]
    public async Task TheOggPickerOffersTheWorkingPairOnly()
    {
        List<string> ogg = AudioCatalog.Compatible("ogg",
            ["libvorbis", "vorbis", "libopus", "opus", "flac"]);
        await Assert.That(ogg).IsEquivalentTo(new[] { "libvorbis", "libopus", "flac" });
    }

    [Test]
    public async Task UnknownCodecsStillPassThrough()
    {
        // A codec this build has and this table does not model must still run,
        // whatever the numbers.
        await Assert.That(AudioCatalog.Validate("libfdk_aac", "mp4", 8000, 1, 8)).IsNull();
        await Assert.That(AudioCatalog.Validate("libfdk_aac", "flv", 16000, 1, 8)).IsNull();
        await Assert.That(AudioCatalog.NearestKbps("libfdk_aac", 4)).IsEqualTo(4);
        await Assert.That(AudioCatalog.KbpsBand("libfdk_aac", 44100)).IsEqualTo((8, 640, System.Array.Empty<int>()));
    }
}
