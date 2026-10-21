using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Encoding;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.8 #3: audio encoders were only ever matched by container, never by what
/// the codec accepts. ac3 takes 32/44.1/48 kHz and nothing else, so the
/// dialup98 preset (rv20 -> RealMedia -> ac3 at 16 kHz) aborted inside FFmpeg
/// after the whole encode had run and left a zero-byte file. These cases pin
/// the rate tables, the snapping and the muxer compatibility lists.
/// </summary>
public sealed class AudioCatalogTests
{
    private static readonly string[] Build = ["ac3", "aac", "libmp3lame", "mp2", "wmav2",
        "libvorbis", "opus", "adpcm_ima_amv", "roq_dpcm", "pcm_s16le"];

    [Test]
    public async Task Ac3OnlyAcceptsThreeSampleRates()
    {
        AudioCodecInfo? ac3 = AudioCatalog.Find("ac3");
        await Assert.That(ac3).IsNotNull();
        await Assert.That(ac3!.SampleRates).IsEquivalentTo(new[] { 32000, 44100, 48000 });
    }

    [Test]
    public async Task AnUnsupportedRateIsRejectedWithTheSupportedOnes()
    {
        string? error = AudioCatalog.Validate("ac3", "rm", 16000, 1, 32);
        await Assert.That(error).IsNotNull();
        await Assert.That(error).Contains("16000");
        await Assert.That(error).Contains("32000");
    }

    [Test]
    public async Task NearestRateSnapsToTheClosestSupportedValue()
    {
        await Assert.That(AudioCatalog.NearestRate("ac3", 16000)).IsEqualTo(32000);
        await Assert.That(AudioCatalog.NearestRate("ac3", 47000)).IsEqualTo(48000);
        await Assert.That(AudioCatalog.NearestRate("ac3", 44100)).IsEqualTo(44100);
        // Unconstrained codecs and unknown ones are left alone.
        await Assert.That(AudioCatalog.NearestRate("pcm_s16le", 12345)).IsEqualTo(12345);
        await Assert.That(AudioCatalog.NearestRate("not_a_codec", 16000)).IsEqualTo(16000);
    }

    [Test]
    public async Task EffectiveSettingsSnapInsteadOfFailing()
    {
        (int? rate, int channels, int kbps) = AudioCatalog.Effective("ac3", 16000, 1, 32);
        await Assert.That(rate).IsEqualTo(32000);
        await Assert.That(channels).IsEqualTo(1);
        await Assert.That(kbps).IsEqualTo(32);

        // roq_dpcm keeps its own stricter rule.
        (int? roqRate, int roqChannels, _) = AudioCatalog.Effective("roq_dpcm", 44100, 6, 64);
        await Assert.That(roqRate).IsEqualTo(22050);
        await Assert.That(roqChannels).IsEqualTo(2);
    }

    [Test]
    public async Task ChannelsAreClampedToWhatTheCodecTakes()
    {
        await Assert.That(AudioCatalog.ClampChannels("libmp3lame", 6)).IsEqualTo(2);
        await Assert.That(AudioCatalog.ClampChannels("ac3", 6)).IsEqualTo(6);
        await Assert.That(AudioCatalog.ClampChannels("adpcm_ima_amv", 2)).IsEqualTo(1);
    }

    [Test]
    public async Task CompatibilityIsTheIntersectionOfBuildAndContainer()
    {
        List<string> rm = AudioCatalog.Compatible("rm", Build);
        await Assert.That(rm).IsEquivalentTo(new[] { "ac3", "aac" });

        List<string> ogg = AudioCatalog.Compatible("ogg", Build);
        await Assert.That(ogg).Contains("libvorbis");
        await Assert.That(ogg).DoesNotContain("ac3");

        // A container with no audio stream at all offers nothing.
        await Assert.That(AudioCatalog.Compatible("h261", Build)).IsEmpty();
    }

    [Test]
    public async Task MuxingIsRejectedAcrossContainers()
    {
        await Assert.That(AudioCatalog.SupportsContainer("ac3", "rm")).IsTrue();
        await Assert.That(AudioCatalog.SupportsContainer("ac3", "ogg")).IsFalse();
        await Assert.That(AudioCatalog.Validate("libvorbis", "asf", 44100, 1, 64)).IsNotNull();
    }

    [Test]
    public async Task MatroskaTakesTheWidestSelection()
    {
        List<string> mkv = AudioCatalog.Compatible("matroska", Build);
        await Assert.That(mkv).Contains("aac");
        await Assert.That(mkv).Contains("ac3");
        await Assert.That(mkv).Contains("libvorbis");
    }

    [Test]
    public async Task UnknownCodecsAreNeverRejected()
    {
        // A codec this build has and this table does not model must still run.
        await Assert.That(AudioCatalog.Validate("libfdk_aac", "mp4", 12345, 3, 999)).IsNull();
    }
}

/// <summary>
/// v7.8 #4: containers became a choice for the encoders that can actually write
/// more than one. The default is unchanged, which is what keeps the 35-encoder
/// sweep from moving.
/// </summary>
public sealed class ContainerChoiceTests
{
    private static EncoderInfo Encoder(string name, string ext, string format) => new()
    {
        Name = name, Extension = ext, FormatName = format,
    };

    [Test]
    public async Task DefaultsAreUnchanged()
    {
        await Assert.That(EncoderMath.AllowedContainers(Encoder("mpeg1video", "mpg", "mpeg"))[0].Format).IsEqualTo("mpeg");
        await Assert.That(EncoderMath.AllowedContainers(Encoder("mpeg4", "avi", "avi"))[0].Format).IsEqualTo("avi");
        await Assert.That(EncoderMath.AllowedContainers(Encoder("libx264", "mp4", "mp4"))[0].Format).IsEqualTo("mp4");
    }

    [Test]
    public async Task TheFiveEncodersGainAvi()
    {
        foreach (string name in new[] { "mpeg1video", "mpeg2video", "mpeg4", "libxvid", "libx264" })
        {
            await Assert.That(EncoderMath.AllowedContainers(Encoder(name, "x", "x"))
                    .Select(x => x.Format)).Contains("avi").Because($"{name} should offer AVI");
        }
    }

    [Test]
    public async Task Libx264GainsMatroska()
    {
        await Assert.That(EncoderMath.AllowedContainers(Encoder("libx264", "mp4", "mp4"))
            .Select(x => x.Format)).Contains("matroska");
    }

    /// <summary>
    /// v7.9 #5: h261 / h263 / h263p gain AVI. The raw .h261/.h263 streams carry
    /// no audio, so the point of the AVI option is the mp3 track that comes with
    /// it -- verified against the BtbN build (2026-10-21): all three mux and
    /// probe back with a working audio stream.
    /// </summary>
    [Test]
    public async Task H261H263H263pGainAvi()
    {
        foreach (string name in new[] { "h261", "h263", "h263p" })
        {
            await Assert.That(EncoderMath.AllowedContainers(Encoder(name, "x", "x"))
                    .Select(x => x.Format)).Contains("avi").Because($"{name} should offer AVI");
        }

        // Defaults stay put: the raw streams remain the default for h261/h263,
        // and h263p was already an AVI encoder.
        await Assert.That(EncoderMath.AllowedContainers(Encoder("h261", "h261", "h261"))[0].Format).IsEqualTo("h261");
        await Assert.That(EncoderMath.AllowedContainers(Encoder("h263", "h263", "h263"))[0].Format).IsEqualTo("h263");
        await Assert.That(EncoderMath.AllowedContainers(Encoder("h263p", "avi", "avi"))[0].Format).IsEqualTo("avi");
    }

    [Test]
    public async Task AviLiftsTheNoAudioRuleForH261AndH263()
    {
        // The raw formats are still audio-free ...
        await Assert.That(Constants.NoAudioFormats).Contains("h261");
        await Assert.That(Constants.NoAudioFormats).Contains("h263");
        await Assert.That(EncoderMath.ChooseAudioEncoder("h261", ["libmp3lame"])).IsNull();
        // ... but the AVI container is not, and that is what --container avi
        // switches the encoder to.
        await Assert.That(EncoderMath.ChooseAudioEncoder("avi", ["libmp3lame"])).IsEqualTo("libmp3lame");
        await Assert.That(AudioCatalog.SupportsContainer("libmp3lame", "avi")).IsTrue();
    }

    [Test]
    public async Task ResolveSwapsTheContainerAndKeepsTheEncoder()
    {
        EncoderInfo mpeg1 = Encoder("mpeg1video", "mpg", "mpeg");
        EncoderInfo? avi = EncoderMath.ResolveContainer(mpeg1, "avi");
        await Assert.That(avi).IsNotNull();
        await Assert.That(avi!.FormatName).IsEqualTo("avi");
        await Assert.That(avi.Extension).IsEqualTo("avi");
        await Assert.That(avi.Name).IsEqualTo("mpeg1video");

        // Both the muxer name and the extension are accepted.
        await Assert.That(EncoderMath.ResolveContainer(Encoder("libx264", "mp4", "mp4"), "mkv")!.FormatName).IsEqualTo("matroska");
        await Assert.That(EncoderMath.ResolveContainer(Encoder("libx264", "mp4", "mp4"), "matroska")!.FormatName).IsEqualTo("matroska");
    }

    [Test]
    public async Task AnUnsupportedContainerIsRefusedRatherThanSilentlyReplaced()
    {
        await Assert.That(EncoderMath.ResolveContainer(Encoder("rv20", "rm", "rm"), "mkv")).IsNull();
        await Assert.That(Validation.ValidateContainer(Encoder("rv20", "rm", "rm"), "mkv")).IsNotNull();
        await Assert.That(Validation.ValidateContainer(Encoder("rv20", "rm", "rm"), null)).IsNull();
    }

    [Test]
    public async Task MatroskaFilesAreRecognisedAsSuch()
    {
        await Assert.That(Constants.FormatExtensions["matroska"]).Contains(".mkv");
    }
}

/// <summary>
/// v7.8 #5/#6: 16:9 output and the three ways of mapping a picture onto the
/// output frame -- crop, fit-and-pad, stretch.
/// </summary>
public sealed class FrameFitTests
{
    private const string Fmt = "yuv420p";

    private static string Build(string fit, string anchor = "center", string pad = "black") =>
        ArgsBuilder.BuildFilters(320, 240, 15.0, fit, anchor, pad, "neighbor", Fmt,
            "off", "send_frame", "auto", false);

    [Test]
    public async Task CropFillsTheFrameAndDiscardsTheEdge()
    {
        string vf = Build("crop");
        await Assert.That(vf).StartsWith("crop=min(iw\\,ih*320/240):min(ih\\,iw*240/320):");
        await Assert.That(vf).Contains("scale=320:240:flags=neighbor");
        await Assert.That(vf).EndsWith("format=yuv420p");
        await Assert.That(vf).DoesNotContain("pad=");
    }

    [Test]
    public async Task CropAnchorMovesTheSurvivingEdge()
    {
        await Assert.That(Build("crop", "left")).Contains(":0:(ih-min(ih\\,iw*240/320))/2");
        await Assert.That(Build("crop", "right")).Contains(":(iw-min(iw\\,ih*320/240)):");
        // Either the filter runs before or after the fps stage; both are fine,
        // so the check is an either/or on the plain string.
        string top = Build("crop", "top");
        await Assert.That(
            top.EndsWith(":0,scale=320:240:flags=neighbor,format=yuv420p")
            || top.EndsWith(":0,fps=15:round=down,scale=320:240:flags=neighbor,format=yuv420p")).IsTrue();
        await Assert.That(Build("crop", "bottom")).Contains(":(ih-min(ih\\,iw*240/320)),");
    }

    [Test]
    public async Task FitKeepsTheWholePictureAndPadsIt()
    {
        string vf = Build("fit", "center", "0x808080");
        await Assert.That(vf).Contains("scale=320:240:force_original_aspect_ratio=decrease:flags=neighbor");
        await Assert.That(vf).Contains("pad=320:240:(ow-iw)/2:(oh-ih)/2:0x808080");
        await Assert.That(vf).DoesNotContain("crop=");
    }

    [Test]
    public async Task StretchScalesStraightToTheOutput()
    {
        string vf = Build("stretch");
        await Assert.That(vf).DoesNotContain("crop=");
        await Assert.That(vf).DoesNotContain("pad=");
        await Assert.That(vf).Contains("scale=320:240:flags=neighbor");
    }

    [Test]
    public async Task DeinterlacingStillSitsBetweenCropAndFps()
    {
        string vf = ArgsBuilder.BuildFilters(320, 240, 15.0, "crop", "center", "black",
            "neighbor", Fmt, "yadif", "send_frame", "auto", false);
        int crop = vf.IndexOf("crop=", StringComparison.Ordinal);
        int deint = vf.IndexOf("yadif=", StringComparison.Ordinal);
        int fps = vf.IndexOf("fps=", StringComparison.Ordinal);
        await Assert.That(crop).IsLessThan(deint);
        await Assert.That(deint).IsLessThan(fps);
    }

    [Test]
    public async Task TheOldBooleanMapsOntoTheNewModes()
    {
        // v7.7 stored "crop 4:3" as a bool; yes -> crop, no -> stretch.
        await Assert.That(EncoderMath.CanonicalFitMode("yes")).IsEqualTo("crop");
        await Assert.That(EncoderMath.CanonicalFitMode("no")).IsEqualTo("stretch");
        await Assert.That(EncoderMath.CanonicalFitMode("letterbox")).IsEqualTo("fit");
        await Assert.That(EncoderMath.CanonicalFitMode("nonsense")).IsEqualTo("crop");
    }

    [Test]
    public async Task PresetsCoverBothAspectRatios()
    {
        await Assert.That(Constants.ResolutionPresets.Any(p => p is (320, 240, "4:3"))).IsTrue();
        await Assert.That(Constants.ResolutionPresets.Any(p => p is (640, 360, "16:9"))).IsTrue();
        await Assert.That(Constants.ResolutionPresets.Any(p => p is (1280, 720, "16:9"))).IsTrue();

        foreach ((int w, int h, string label) in Constants.ResolutionPresets)
        {
            double ratio = (double)w / h;
            double expected = label == "16:9" ? 16.0 / 9.0 : 4.0 / 3.0;
            await Assert.That(ratio).IsEqualTo(expected).Within(0.01).Because($"{w}x{h} is not {label}");
        }
    }
}
