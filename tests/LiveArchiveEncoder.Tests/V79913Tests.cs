using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.13: audit fixes around settings round-trip, shell activation and
/// numeric boundaries.
/// </summary>
public sealed class V79913Tests
{
    [Test]
    public async Task NewSettingsAreEmittedByEmitArgs()
    {
        var cfg = new UserConfig
        {
            Tearing = "horizontal",
            MaxVideoKbps = 321,
            DynamicFps = true,
        };
        List<string> argv = CliArgs.FromUserConfig(cfg);
        int tear = argv.IndexOf("--tearing");
        int cap = argv.IndexOf("--max-video-kbps");
        await Assert.That(tear).IsGreaterThanOrEqualTo(0);
        await Assert.That(argv[tear + 1]).IsEqualTo("horizontal");
        await Assert.That(argv[cap + 1]).IsEqualTo("321");
        await Assert.That(argv).Contains("--dynamic-fps");
    }

    [Test]
    public async Task ActivationLinesEscapeShellMetacharacters()
    {
        const string evil = "C:\\tmp\\$(whoami)`id`%PATH%\"&|";
        string ps = ToolchainPath.ActivateLine("powershell", evil);
        string cmd = ToolchainPath.ActivateLine("cmd", evil);
        string fish = ToolchainPath.ActivateLine("fish", evil);
        // PowerShell single quotes keep $(...) literal; the old double-quoted
        // form evaluated it. A doubled inner quote also stays literal.
        await Assert.That(ps.Contains("= 'C:\\tmp\\$(whoami)")).IsTrue();
        await Assert.That(ps.Contains("\"C:\\tmp")).IsFalse();
        await Assert.That(cmd.Contains("%PATH%;")).IsFalse();
        await Assert.That(fish.Contains("\\$\\(")).IsTrue();
        // Ordinary paths are still printed verbatim.
        await Assert.That(ToolchainPath.ActivateLine("cmd", @"C:\ffmpeg\bin"))
            .Contains(@"C:\ffmpeg\bin");
    }

    [Test]
    public async Task HugeBitrateCapNeverBecomesInfinity()
    {
        List<string> args = ArgsBuilder.VideoQualityArgs(
            "mjpeg", 8, 600, maxVideoKbps: 1e308);
        await Assert.That(args.Contains("Infinityk")).IsFalse();
        foreach (string token in args)
            await Assert.That(token.Contains("Infinity", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task NoDynamicFpsIsDocumentedAndParsed()
    {
        await Assert.That(CliParser.Usage().Contains("--no-dynamic-fps")).IsTrue();
        (CliOptions? opts, string? error) = CliParser.Parse(["in.mp4", "--no-dynamic-fps"]);
        await Assert.That(error).IsNull();
        await Assert.That(opts!.DynamicFps).IsFalse();
    }

    [Test]
    public async Task EmptyGhProxyMeansExplicitOff()
    {
        var cfg = new Config();
        ConfigStore.Apply(new UserConfig { GhProxy = "" }, cfg, []);
        await Assert.That(cfg.GhProxy).IsEqualTo(GitHubProxy.Off);
    }
}
