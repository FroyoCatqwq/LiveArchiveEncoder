using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.PostProcess;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Pins for the defects found in the v7.9.8.10 full-project audit. Each one is
/// a behaviour that was previously green-tested or entirely untested: a flag
/// parsed but never read, a "widest band" that returned the narrowest, a quality
/// number with no upper bound, and a preset that wrote audio values straight
/// into a codec with different limits.
/// </summary>
public sealed class AuditRegressionTests
{
    private static EncoderInfo Encoder(string name, string ext, string format) =>
        new() { Name = name, Extension = ext, FormatName = format };

    [Test]
    public async Task AnExplicitEraNativeBeatsEraModern()
    {
        (CliOptions? modern, _) = CliParser.Parse(["clip.mkv", "--era-modern"]);
        (CliOptions? both, _) = CliParser.Parse(["clip.mkv", "--era-modern", "--era-native"]);
        (CliOptions? native, _) = CliParser.Parse(["clip.mkv", "--era-native"]);
        (CliOptions? plain, _) = CliParser.Parse(["clip.mkv"]);

        // Before 7.9.8.10 CliOptions.EraNative had no reader at all, so the
        // third case behaved exactly like the first.
        await Assert.That(modern!.EraNative).IsFalse();
        await Assert.That(both!.EraNative).IsTrue();
        await Assert.That(Cli.EraNativeOf(modern!)).IsFalse();
        await Assert.That(Cli.EraNativeOf(both!)).IsTrue();
        await Assert.That(Cli.EraNativeOf(native!)).IsTrue();
        await Assert.That(Cli.EraNativeOf(plain!)).IsTrue();
    }

    [Test]
    public async Task AnUnknownSampleRateAcceptsTheWidestBitrateBand()
    {
        // mp2 has two bands: 16 kHz takes 8..160 with 16 kbps in its discrete
        // list, 32 kHz takes 32..384. With rate == null the old loop fell
        // through to the LAST (narrowest) band and refused 16 kbps outright.
        (int min, int max, int[] values) = AudioCatalog.KbpsBand("mp2", null);
        await Assert.That(values.Contains(16)).IsTrue();
        await Assert.That(values.Contains(384)).IsTrue();
        await Assert.That(min).IsLessThanOrEqualTo(8);
        await Assert.That(max).IsGreaterThanOrEqualTo(384);

        await Assert.That(AudioCatalog.Validate("mp2", "mpeg", null, 1, 16)).IsNull();
        await Assert.That(AudioCatalog.NearestKbps("mp2", 16)).IsEqualTo(16);

        // Naming a rate still narrows the band: the union is only for "auto".
        (_, _, int[] at32k) = AudioCatalog.KbpsBand("mp2", 32000);
        await Assert.That(at32k.Contains(16)).IsFalse();
        await Assert.That(AudioCatalog.Validate("mp2", "mpeg", 32000, 1, 16)).IsNotNull();
    }

    [Test]
    public async Task AQualityOutsideTheEncodersScaleIsRefusedEarly()
    {
        // --qv 999 used to reach FFmpeg and die in preflight with an opaque
        // message; -q:v 0 on an MPEG encoder was equally unchecked.
        var x264 = new Config { Encoder = Encoder("libx264", "mp4", "mp4"), Qv = 999 };
        await Assert.That(() => Validation.ValidateCommonConfig(x264)).Throws<ArgumentException>();

        var mpeg4 = new Config { Encoder = Encoder("mpeg4", "avi", "avi"), Qv = 0 };
        await Assert.That(() => Validation.ValidateCommonConfig(mpeg4)).Throws<ArgumentException>();

        // OpenH264 has no quality scale at all -- its (0, 0) range must be
        // skipped rather than used as a bound.
        var openh264 = new Config { Encoder = Encoder("libopenh264", "mp4", "mp4"), Qv = 26 };
        await Assert.That(() => Validation.ValidateCommonConfig(openh264)).ThrowsNothing();
    }

    [Test]
    public async Task ApplyingAnEraPresetSnapsAudioToTheNewCodec()
    {
        EraPreset? era = EraPresets.All.FirstOrDefault(p => p.Encoder == "rv20");
        await Assert.That(era).IsNotNull().Because("an rv20 era row is needed for this pin");

        var c = new Config
        {
            Encoder = Encoder("libx264", "mp4", "mp4"),
            AudioEncoder = "libmp3lame",
        };
        var encoders = new List<EncoderInfo> { Encoder("rv20", "rm", "rm") };
        var audio = new HashSet<string> { "real_144", "aac", "libmp3lame" };

        Cui.ApplyPreset(c, era!, encoders, audio);

        // rm's own codec only takes 8000 Hz. Before the fix the preset wrote
        // 16000 Hz verbatim and the next "Start encoding" refused the very
        // preset the program shipped with, while the CLI snapped and ran it.
        await Assert.That(c.AudioEncoder).IsEqualTo("real_144");
        await Assert.That(c.AudioRate).IsEqualTo(8000);
        await Assert.That(() => Validation.ValidateCommonConfig(c)).ThrowsNothing();
    }

    [Test]
    public async Task OnlyGitHubHostsAreTreatedAsCredentialAudience()
    {
        // LAE_UPDATE_TOKEN is only attached on these hosts. A --update-feed
        // pointing elsewhere must never receive it.
        await Assert.That(GitHubProxy.CanWrap("https://api.github.com/repos/x/y/releases/latest")).IsTrue();
        await Assert.That(GitHubProxy.CanWrap("https://objects.githubusercontent.com/x")).IsTrue();
        await Assert.That(GitHubProxy.CanWrap("https://mirror.example.com/releases/latest")).IsFalse();
        await Assert.That(GitHubProxy.CanWrap("http://127.0.0.1:8765/feed.json")).IsFalse();
    }
}
