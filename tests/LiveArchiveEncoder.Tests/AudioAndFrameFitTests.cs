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
[TestFixture]
public sealed class AudioCatalogTests
{
    private static readonly string[] Build = ["ac3", "aac", "libmp3lame", "mp2", "wmav2",
        "libvorbis", "opus", "adpcm_ima_amv", "roq_dpcm", "pcm_s16le"];

    [Test]
    public void Ac3OnlyAcceptsThreeSampleRates()
    {
        AudioCodecInfo? ac3 = AudioCatalog.Find("ac3");
        Assert.That(ac3, Is.Not.Null);
        Assert.That(ac3!.SampleRates, Is.EqualTo(new[] { 32000, 44100, 48000 }));
    }

    [Test]
    public void AnUnsupportedRateIsRejectedWithTheSupportedOnes()
    {
        string? error = AudioCatalog.Validate("ac3", "rm", 16000, 1, 32);
        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("16000"));
        Assert.That(error, Does.Contain("32000"));
    }

    [Test]
    public void NearestRateSnapsToTheClosestSupportedValue()
    {
        Assert.That(AudioCatalog.NearestRate("ac3", 16000), Is.EqualTo(32000));
        Assert.That(AudioCatalog.NearestRate("ac3", 47000), Is.EqualTo(48000));
        Assert.That(AudioCatalog.NearestRate("ac3", 44100), Is.EqualTo(44100));
        // Unconstrained codecs and unknown ones are left alone.
        Assert.That(AudioCatalog.NearestRate("pcm_s16le", 12345), Is.EqualTo(12345));
        Assert.That(AudioCatalog.NearestRate("not_a_codec", 16000), Is.EqualTo(16000));
    }

    [Test]
    public void EffectiveSettingsSnapInsteadOfFailing()
    {
        (int? rate, int channels, int kbps) = AudioCatalog.Effective("ac3", 16000, 1, 32);
        Assert.That(rate, Is.EqualTo(32000));
        Assert.That(channels, Is.EqualTo(1));
        Assert.That(kbps, Is.EqualTo(32));

        // roq_dpcm keeps its own stricter rule.
        (int? roqRate, int roqChannels, _) = AudioCatalog.Effective("roq_dpcm", 44100, 6, 64);
        Assert.That(roqRate, Is.EqualTo(22050));
        Assert.That(roqChannels, Is.EqualTo(2));
    }

    [Test]
    public void ChannelsAreClampedToWhatTheCodecTakes()
    {
        Assert.That(AudioCatalog.ClampChannels("libmp3lame", 6), Is.EqualTo(2));
        Assert.That(AudioCatalog.ClampChannels("ac3", 6), Is.EqualTo(6));
        Assert.That(AudioCatalog.ClampChannels("adpcm_ima_amv", 2), Is.EqualTo(1));
    }

    [Test]
    public void CompatibilityIsTheIntersectionOfBuildAndContainer()
    {
        List<string> rm = AudioCatalog.Compatible("rm", Build);
        Assert.That(rm, Is.EqualTo(new[] { "ac3", "aac" }));

        List<string> ogg = AudioCatalog.Compatible("ogg", Build);
        Assert.That(ogg, Does.Contain("libvorbis"));
        Assert.That(ogg, Does.Not.Contain("ac3"));

        // A container with no audio stream at all offers nothing.
        Assert.That(AudioCatalog.Compatible("h261", Build), Is.Empty);
    }

    [Test]
    public void MuxingIsRejectedAcrossContainers()
    {
        Assert.That(AudioCatalog.SupportsContainer("ac3", "rm"), Is.True);
        Assert.That(AudioCatalog.SupportsContainer("ac3", "ogg"), Is.False);
        Assert.That(AudioCatalog.Validate("libvorbis", "asf", 44100, 1, 64), Is.Not.Null);
    }

    [Test]
    public void MatroskaTakesTheWidestSelection()
    {
        List<string> mkv = AudioCatalog.Compatible("matroska", Build);
        Assert.That(mkv, Does.Contain("aac"));
        Assert.That(mkv, Does.Contain("ac3"));
        Assert.That(mkv, Does.Contain("libvorbis"));
    }

    [Test]
    public void UnknownCodecsAreNeverRejected()
    {
        // A codec this build has and this table does not model must still run.
        Assert.That(AudioCatalog.Validate("libfdk_aac", "mp4", 12345, 3, 999), Is.Null);
    }
}

/// <summary>
/// v7.8 #4: containers became a choice for the encoders that can actually write
/// more than one. The default is unchanged, which is what keeps the 35-encoder
/// sweep from moving.
/// </summary>
[TestFixture]
public sealed class ContainerChoiceTests
{
    private static EncoderInfo Encoder(string name, string ext, string format) => new()
    {
        Name = name, Extension = ext, FormatName = format,
    };

    [Test]
    public void DefaultsAreUnchanged()
    {
        Assert.That(EncoderMath.AllowedContainers(Encoder("mpeg1video", "mpg", "mpeg"))[0].Format,
            Is.EqualTo("mpeg"));
        Assert.That(EncoderMath.AllowedContainers(Encoder("mpeg4", "avi", "avi"))[0].Format,
            Is.EqualTo("avi"));
        Assert.That(EncoderMath.AllowedContainers(Encoder("libx264", "mp4", "mp4"))[0].Format,
            Is.EqualTo("mp4"));
    }

    [Test]
    public void TheFiveEncodersGainAvi()
    {
        foreach (string name in new[] { "mpeg1video", "mpeg2video", "mpeg4", "libxvid", "libx264" })
        {
            Assert.That(EncoderMath.AllowedContainers(Encoder(name, "x", "x"))
                    .Select(x => x.Format), Does.Contain("avi"),
                $"{name} should offer AVI");
        }
    }

    [Test]
    public void Libx264GainsMatroska()
    {
        Assert.That(EncoderMath.AllowedContainers(Encoder("libx264", "mp4", "mp4"))
            .Select(x => x.Format), Does.Contain("matroska"));
    }

    [Test]
    public void ResolveSwapsTheContainerAndKeepsTheEncoder()
    {
        EncoderInfo mpeg1 = Encoder("mpeg1video", "mpg", "mpeg");
        EncoderInfo? avi = EncoderMath.ResolveContainer(mpeg1, "avi");
        Assert.That(avi, Is.Not.Null);
        Assert.That(avi!.FormatName, Is.EqualTo("avi"));
        Assert.That(avi.Extension, Is.EqualTo("avi"));
        Assert.That(avi.Name, Is.EqualTo("mpeg1video"));

        // Both the muxer name and the extension are accepted.
        Assert.That(EncoderMath.ResolveContainer(Encoder("libx264", "mp4", "mp4"), "mkv")!.FormatName,
            Is.EqualTo("matroska"));
        Assert.That(EncoderMath.ResolveContainer(Encoder("libx264", "mp4", "mp4"), "matroska")!.FormatName,
            Is.EqualTo("matroska"));
    }

    [Test]
    public void AnUnsupportedContainerIsRefusedRatherThanSilentlyReplaced()
    {
        Assert.That(EncoderMath.ResolveContainer(Encoder("rv20", "rm", "rm"), "mkv"), Is.Null);
        Assert.That(Validation.ValidateContainer(Encoder("rv20", "rm", "rm"), "mkv"), Is.Not.Null);
        Assert.That(Validation.ValidateContainer(Encoder("rv20", "rm", "rm"), null), Is.Null);
    }

    [Test]
    public void MatroskaFilesAreRecognisedAsSuch()
    {
        Assert.That(Constants.FormatExtensions["matroska"], Does.Contain(".mkv"));
    }
}

/// <summary>
/// v7.8 #5/#6: 16:9 output and the three ways of mapping a picture onto the
/// output frame -- crop, fit-and-pad, stretch.
/// </summary>
[TestFixture]
public sealed class FrameFitTests
{
    private const string Fmt = "yuv420p";

    private static string Build(string fit, string anchor = "center", string pad = "black") =>
        ArgsBuilder.BuildFilters(320, 240, 15.0, fit, anchor, pad, "neighbor", Fmt,
            "off", "send_frame", "auto", false);

    [Test]
    public void CropFillsTheFrameAndDiscardsTheEdge()
    {
        string vf = Build("crop");
        Assert.That(vf, Does.StartWith("crop=min(iw\\,ih*320/240):min(ih\\,iw*240/320):"));
        Assert.That(vf, Does.Contain("scale=320:240:flags=neighbor"));
        Assert.That(vf, Does.EndWith("format=yuv420p"));
        Assert.That(vf, Does.Not.Contain("pad="));
    }

    [Test]
    public void CropAnchorMovesTheSurvivingEdge()
    {
        Assert.That(Build("crop", "left"), Does.Contain(":0:(ih-min(ih\\,iw*240/320))/2"));
        Assert.That(Build("crop", "right"), Does.Contain(":(iw-min(iw\\,ih*320/240)):"));
        Assert.That(Build("crop", "top"), Does.EndWith(":0,scale=320:240:flags=neighbor,format=yuv420p")
            .Or.EndWith(":0,fps=15:round=down,scale=320:240:flags=neighbor,format=yuv420p"));
        Assert.That(Build("crop", "bottom"), Does.Contain(":(ih-min(ih\\,iw*240/320)),"));
    }

    [Test]
    public void FitKeepsTheWholePictureAndPadsIt()
    {
        string vf = Build("fit", "center", "0x808080");
        Assert.That(vf, Does.Contain("scale=320:240:force_original_aspect_ratio=decrease:flags=neighbor"));
        Assert.That(vf, Does.Contain("pad=320:240:(ow-iw)/2:(oh-ih)/2:0x808080"));
        Assert.That(vf, Does.Not.Contain("crop="));
    }

    [Test]
    public void StretchScalesStraightToTheOutput()
    {
        string vf = Build("stretch");
        Assert.That(vf, Does.Not.Contain("crop="));
        Assert.That(vf, Does.Not.Contain("pad="));
        Assert.That(vf, Does.Contain("scale=320:240:flags=neighbor"));
    }

    [Test]
    public void DeinterlacingStillSitsBetweenCropAndFps()
    {
        string vf = ArgsBuilder.BuildFilters(320, 240, 15.0, "crop", "center", "black",
            "neighbor", Fmt, "yadif", "send_frame", "auto", false);
        int crop = vf.IndexOf("crop=", StringComparison.Ordinal);
        int deint = vf.IndexOf("yadif=", StringComparison.Ordinal);
        int fps = vf.IndexOf("fps=", StringComparison.Ordinal);
        Assert.That(crop, Is.LessThan(deint));
        Assert.That(deint, Is.LessThan(fps));
    }

    [Test]
    public void TheOldBooleanMapsOntoTheNewModes()
    {
        // v7.7 stored "crop 4:3" as a bool; yes -> crop, no -> stretch.
        Assert.That(EncoderMath.CanonicalFitMode("yes"), Is.EqualTo("crop"));
        Assert.That(EncoderMath.CanonicalFitMode("no"), Is.EqualTo("stretch"));
        Assert.That(EncoderMath.CanonicalFitMode("letterbox"), Is.EqualTo("fit"));
        Assert.That(EncoderMath.CanonicalFitMode("nonsense"), Is.EqualTo("crop"));
    }

    [Test]
    public void PresetsCoverBothAspectRatios()
    {
        Assert.That(Constants.ResolutionPresets.Any(p => p is (320, 240, "4:3")), Is.True);
        Assert.That(Constants.ResolutionPresets.Any(p => p is (640, 360, "16:9")), Is.True);
        Assert.That(Constants.ResolutionPresets.Any(p => p is (1280, 720, "16:9")), Is.True);

        foreach ((int w, int h, string label) in Constants.ResolutionPresets)
        {
            double ratio = (double)w / h;
            double expected = label == "16:9" ? 16.0 / 9.0 : 4.0 / 3.0;
            Assert.That(ratio, Is.EqualTo(expected).Within(0.01), $"{w}x{h} is not {label}");
        }
    }
}
