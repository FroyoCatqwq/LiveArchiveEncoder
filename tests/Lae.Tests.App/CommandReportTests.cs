using Lae.App;
using Lae.App.Commands;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Settings;
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
            Recompress = new RecompressChain { Enabled = true, Passes = [new RecompressPass("-c:v copy", "mp4")] },
        },
    };

    [Test]
    public async Task TheDefaultOutputSitsBesideTheSourceWithTheContainerExtension()
    {
        (EncodeRequest? request, string? error) = EncodeSession.BuildRequest(
            "/media/clips/talk.mkv", new EncodeOverrides(), Stored());

        await Assert.That(error).IsNull();

        // Compared through Path so the same assertion holds on both platforms:
        // the point is "same folder, same stem, the container's extension".
        string expectedFolder = Path.GetDirectoryName(Path.GetFullPath("/media/clips/talk.mkv"))!;
        await Assert.That(Path.GetFileName(request!.DestinationPath)).IsEqualTo("talk.avi");
        await Assert.That(Path.GetDirectoryName(request.DestinationPath)).IsEqualTo(expectedFolder);
    }

    [Test]
    public async Task OverridesReplaceTheStoredValuesTheyName()
    {
        (EncodeRequest? request, _) = EncodeSession.BuildRequest(
            "/media/in.mkv",
            new EncodeOverrides { Container = "mkv", Quality = 3, Size = "256X192", From = 12, To = 30 },
            Stored());

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
        (EncodeRequest? request, _) = EncodeSession.BuildRequest("/media/in.mkv", new EncodeOverrides(), Stored());

        await Assert.That(request!.Encode.Subtitles.Any).IsFalse();
        await Assert.That(request.Encode.Recompress.Enabled).IsFalse();
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
    private static SettingsDocument Settings(string? path = null)
        => new() { Toolchain = new ToolchainSettings { FfmpegSource = "path", FfmpegPath = path } };

    [Test]
    public async Task AReadyMachineSaysSoAndSuggestsADryRun()
    {
        var terminal = new BufferedTerminal();
        var probe = new ToolchainProbeResult { Ffmpeg = "/usr/bin/ffmpeg", Ffprobe = "/usr/bin/ffprobe" };

        int code = SetupReport.Run(Settings(), "/home/x/settings.json", probe, terminal);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(terminal.OutputLines.Any(l => l.Contains("Ready.", StringComparison.Ordinal))).IsTrue();
        await Assert.That(terminal.OutputLines.Any(l => l.Contains("--dry-run", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// A dead end is what this command exists to prevent, so the failure text must
    /// name both tools and a next step, and must not promise a download command
    /// this build does not have.
    /// </summary>
    [Test]
    public async Task AMachineWithNothingInstalledGetsBothToolsAndANextStep()
    {
        var terminal = new BufferedTerminal();

        int code = SetupReport.Run(Settings(), "/home/x/settings.json", new ToolchainProbeResult(), terminal);

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);

        string text = string.Join('\n', terminal.OutputLines);
        await Assert.That(text.Contains("ffmpeg", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("ffprobe", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("PATH", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains("no download command yet", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TheConfiguredFolderIsReportedSoTheUserCanSeeWhatAnEncodeWillUse()
    {
        var terminal = new BufferedTerminal();
        var probe = new ToolchainProbeResult { Ffmpeg = "/opt/ff/bin/ffmpeg" };

        SetupReport.Run(Settings("/opt/ff"), "/home/x/settings.json", probe, terminal);

        await Assert.That(terminal.OutputLines.Any(l => l.Contains("/opt/ff", StringComparison.Ordinal))).IsTrue();
    }
}
