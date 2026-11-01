using Lae.App;
using Lae.App.Commands;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Localization;
using Lae.Core.Settings;
using Lae.Core.Toolchain;
using Lae.Media.Localization;
using Lae.Media.Toolchain;

namespace Lae.Tests.App;

/// <summary>
/// Turning a command line into a request, and a machine into a diagnosis.
///
/// <para>Both are pure functions of their inputs, which is exactly why they are
/// worth testing: they are the two places where the program decides what the user
/// meant and what to tell them, and neither needs a video, a toolchain or a
/// terminal to get wrong.</para>
/// </summary>
public sealed class EncodeRequestBuildingTests
{
    /// <summary>The sentences a user actually sees; the assertions below quote them.</summary>
    private static readonly IMessages En = I18NextMessages.Create("en");

    private static SettingsDocument Stored() => new()
    {
        Defaults = new EncodeDefaults
        {
            Encoder = "mpeg4",
            Container = "avi",
            Width = 320,
            Height = 240,
            Quality = 6,
            // Both of these must be dropped by a CLI run: they are only ever
            // switched on explicitly, on the command line.
            Subtitles = new SubtitleDefaults { Files = ["/subs/old.ass"] },
            Chain = new ChainSettings { Enabled = true, Steps = [new ChainStep { Args = "-c:v copy", Container = "mp4" }] },
        },
    };

    /// <summary>
    /// No <c>--out</c> means "beside the source", but the extension is the plan's
    /// to choose: it is the container the chosen encoder actually writes, and the
    /// CLI does not know the encoder until this build's catalogue has been read.
    /// The CLI used to guess "avi" here, which put an MP4 inside clip.avi the
    /// moment anyone asked for libx264 without naming an output.
    /// </summary>
    [Test]
    public async Task NoOutputIsLeftForThePlanToName()
    {
        (EncodeRequest? request, string? error) = EncodeSession.BuildRequest(
            "/media/clips/talk.mkv", new EncodeOverrides(), Stored(), En);

        await Assert.That(error).IsNull();
        await Assert.That(request!.DestinationPath).IsNull();
    }

    [Test]
    public async Task AnExplicitOutputIsTakenVerbatim()
    {
        (EncodeRequest? request, _) = EncodeSession.BuildRequest(
            "/media/clips/talk.mkv", new EncodeOverrides { Output = "/tmp/out.mkv" }, Stored(), En);

        await Assert.That(request!.DestinationPath).IsEqualTo("/tmp/out.mkv");
    }

    /// <summary>
    /// The stored quality is written in the stored encoder's scale. Switching the
    /// encoder without restating it has to carry the number across scales, or a
    /// 2004 stream's <c>-q:v 6</c> becomes <c>-crf 6</c> -- v7.9.8.2's bug, which
    /// came out at two and a half times the intended bitrate.
    /// </summary>
    [Test]
    public async Task SwitchingTheEncoderCarriesTheQualityNumberAcrossScales()
    {
        (EncodeRequest? request, _) = EncodeSession.BuildRequest(
            "/media/in.mkv", new EncodeOverrides { Encoder = "libx264" }, Stored(), En);

        await Assert.That(request!.Encode.Encoder).IsEqualTo("libx264");

        // q:v 6 -> crf round(7.5 + 1.17 * 6) = 15, per EncoderMath's calibration.
        await Assert.That(request.Encode.Quality).IsEqualTo(15);
    }

    /// <summary>
    /// ...but a number the user typed is already in the target encoder's scale,
    /// and converting it would move it a second time.
    /// </summary>
    [Test]
    public async Task RestatingQualityKeepsTheNumberTheUserTyped()
    {
        (EncodeRequest? request, _) = EncodeSession.BuildRequest(
            "/media/in.mkv", new EncodeOverrides { Encoder = "libx264", Quality = 20 }, Stored(), En);

        await Assert.That(request!.Encode.Quality).IsEqualTo(20);
    }

    /// <summary>
    /// A preset's quality is stored in both scales precisely so this works: the
    /// preset is applied against the encoder it names, and the command line may
    /// then change that encoder.
    /// </summary>
    [Test]
    public async Task APresetQualitySurvivesAnEncoderSwitchFromTheCommandLine()
    {
        SettingsDocument settings = Stored() with
        {
            CustomPresets =
            [
                new CustomPreset { Id = "small", Encoder = "mpeg4", LiveQv = 8, LiveCrf = 17 },
            ],
            Defaults = Stored().Defaults with { Encoder = null },
        };

        (EncodeRequest? request, _) = EncodeSession.BuildRequest(
            "/media/in.mkv", new EncodeOverrides { Preset = "small", Encoder = "libx264" }, settings, En);

        await Assert.That(request!.Encode.Encoder).IsEqualTo("libx264");

        // q:v 8 -> crf 17 (7.5 + 1.17 * 8 = 16.86), and no second conversion.
        await Assert.That(request.Encode.Quality).IsEqualTo(17);
    }

    [Test]
    public async Task OverridesReplaceTheStoredValuesTheyName()
    {
        (EncodeRequest? request, _) = EncodeSession.BuildRequest(
            "/media/in.mkv",
            new EncodeOverrides { Container = "mkv", Quality = 3, Size = "256X192", From = 12, To = 30 },
            Stored(), En);

        await Assert.That(request!.Encode.Container).IsEqualTo("mkv");
        await Assert.That(request.Encode.Quality).IsEqualTo(3);
        await Assert.That(request.Encode.Width).IsEqualTo(256);
        await Assert.That(request.Encode.Height).IsEqualTo(192);
        await Assert.That(request.StartSeconds).IsEqualTo(12.0);
        await Assert.That(request.EndSeconds).IsEqualTo(30.0);
    }

    /// <summary>
    /// The settings file may remember a subtitle block and a recompress chain from
    /// an interactive session; a run that did not ask for them must not inherit
    /// them. That rule is older than the file format.
    /// </summary>
    [Test]
    public async Task StoredSubtitlesAndRecompressNeverLeakIntoARun()
    {
        (EncodeRequest? request, _) = EncodeSession.BuildRequest("/media/in.mkv", new EncodeOverrides(), Stored(), En);

        await Assert.That(request!.Encode.Subtitles.Any).IsFalse();
        await Assert.That(request.Encode.Chain.Enabled).IsFalse();
    }

    [Test]
    [Arguments("320x240", 320, 240)]
    [Arguments("1280X720", 1280, 720)]
    public async Task SizesAreParsedBothWays(string text, int width, int height)
    {
        await Assert.That(EncodeSession.TryParseSize(text, out int w, out int h)).IsTrue();
        await Assert.That(w).IsEqualTo(width);
        await Assert.That(h).IsEqualTo(height);
    }

    [Test]
    [Arguments("320")]
    [Arguments("320x")]
    [Arguments("x240")]
    [Arguments("320x0")]
    [Arguments("-320x240")]
    [Arguments("320x240x480")]
    [Arguments("wide")]
    public async Task MalformedSizesAreRejected(string text)
    {
        await Assert.That(EncodeSession.TryParseSize(text, out _, out _)).IsFalse();
    }
}

/// <summary>
/// <c>lae setup</c>'s text is the entire value of the command, so it gets a test.
/// </summary>
public sealed class SetupReportTests
{
    /// <summary>The sentences a user actually sees; the assertions below quote them.</summary>
    private static readonly IMessages En = I18NextMessages.Create("en");

    private static SettingsDocument Settings(string? path = null)
        => new() { Toolchain = new ToolchainSettings { FfmpegSource = "path", FfmpegPath = path } };

    [Test]
    public async Task AReadyMachineSaysSoAndSuggestsADryRun()
    {
        var terminal = new BufferedTerminal();
        var probe = new ToolchainProbeResult { Ffmpeg = "/usr/bin/ffmpeg", Ffprobe = "/usr/bin/ffprobe" };

        int code = SetupReport.Run(Settings(), "/home/x/settings.json", null, probe, terminal, En);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(terminal.OutputLines.Any(l => l.Contains("Ready.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(terminal.OutputLines.Any(l => l.Contains("--dry-run", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// A dead end is what this command exists to prevent, so the failure text must
    /// name both tools and a next step -- and the next step it names must be a
    /// command that exists. v7 shipped one pointing at a menu entry nobody had
    /// written; the fix is not "recommend something plausible".
    /// </summary>
    [Test]
    public async Task AMachineWithNothingInstalledGetsBothToolsAndANextStep()
    {
        var terminal = new BufferedTerminal();

        int code = SetupReport.Run(Settings(), "/home/x/settings.json", null, new ToolchainProbeResult(), terminal, En);

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);

        string text = string.Join('\n', terminal.OutputLines);
        await Assert.That(text.Contains("ffmpeg", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("ffprobe", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("PATH", StringComparison.Ordinal)).IsTrue();

        // The download command exists now, and naming it is the whole fix.
        await Assert.That(text.Contains("lae toolchain install", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// "Nothing is installed" and "this machine has no FFmpeg" are different
    /// problems: the first has a one-command fix and the second may not. The source
    /// line has to tell them apart, or the advice reads as if nothing is configured.
    /// </summary>
    [Test]
    public async Task AConfiguredSourceThatIsNotInstalledSaysSo()
    {
        var terminal = new BufferedTerminal();
        var settings = new SettingsDocument
        {
            Toolchain = new ToolchainSettings { FfmpegSource = FfmpegCatalog.DefaultId },
        };

        SetupReport.Run(settings, "/home/x/settings.toml", null, new ToolchainProbeResult(), terminal, En);

        string text = string.Join('\n', terminal.OutputLines);
        await Assert.That(text.Contains("is not installed here yet", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TheConfiguredFolderIsReportedSoTheUserCanSeeWhatAnEncodeWillUse()
    {
        var terminal = new BufferedTerminal();
        var probe = new ToolchainProbeResult { Ffmpeg = "/opt/ff/bin/ffmpeg" };

        SetupReport.Run(Settings("/opt/ff"), "/home/x/settings.json", "/opt/ff", probe, terminal, En);

        await Assert.That(terminal.OutputLines.Any(l => l.Contains("/opt/ff", StringComparison.Ordinal))).IsTrue();
    }
}


