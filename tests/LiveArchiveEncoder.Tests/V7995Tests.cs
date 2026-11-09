using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Frames;
using LiveArchiveEncoder.Remote;
using LiveArchiveEncoder.UI;
using LiveArchiveEncoder.Update;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.5 regressions: the CLI/config three-state bugs, the completion
/// catalogue's value kinds, untrusted feed/file names, and the numeric and
/// path boundaries found by the full audit.
/// </summary>
public sealed class V7995Tests
{
    [Test]
    public async Task RealTimeReachesTheConfigAndHonoursTheStoredToggle()
    {
        // A CUI "direct encode" toggle is a remembered setting; the CLI flag is
        // a per-run override. Both used to be ignored by Cli.Run's Config.
        (CliOptions? plain, string? plainError) = CliParser.Parse([]);
        await Assert.That(plainError).IsNull();

        var cfg = new Config();
        Cli.MergeStoredPreferences(cfg, new UserConfig { RealTime = false }, plain!);
        await Assert.That(cfg.RealTime).IsFalse();

        (CliOptions? flagged, string? flagError) = CliParser.Parse(["--no-realtime"]);
        await Assert.That(flagError).IsNull();

        var cfg2 = new Config();
        Cli.MergeStoredPreferences(cfg2, new UserConfig(), flagged!);
        await Assert.That(cfg2.RealTime).IsFalse();

        var cfg3 = new Config();
        Cli.MergeStoredPreferences(cfg3, new UserConfig { RealTime = true }, plain!);
        await Assert.That(cfg3.RealTime).IsTrue();
    }

    [Test]
    public async Task RememberedOffFrontIsEmittedAndParsesBackToOff()
    {
        var cfg = new UserConfig
        {
            InputPath = "in.rm", OutputPath = "out.rm", GhProxy = "",
        };

        List<string> argv = CliArgs.FromUserConfig(cfg);
        await Assert.That(argv).Contains("--no-gh-proxy");

        (CliOptions? parsed, string? error) = CliParser.Parse([.. argv]);
        await Assert.That(error).IsNull();
        await Assert.That(parsed!.GhProxy).IsEqualTo("");
    }

    [Test]
    public async Task DirectModeAndResolvedH264KeepTheirThreeStateMeaning()
    {
        var direct = new UserConfig
        {
            InputPath = "in.rm", OutputPath = "out.rm", RealTime = false,
        };
        List<string> argv = CliArgs.FromUserConfig(direct);
        await Assert.That(argv).Contains("--no-realtime");

        // null = "let the preset decide"; emitting --h264 with the resolved
        // value used to turn a preset run into an explicit override on replay.
        UserConfig captured = ConfigStore.Capture(new Config { H264Impl = null });
        await Assert.That(captured.H264Impl).IsNull();
        await Assert.That(CliArgs.FromUserConfig(captured).Contains("--h264")).IsFalse();
    }

    [Test]
    public async Task NumericSettingsRoundTripWithoutPrecisionLoss()
    {
        const double limit = 1234567.89;
        var cfg = new UserConfig { InputPath = "in.rm", OutputPath = "out.rm", Limit = limit };

        (CliOptions? parsed, string? error) = CliParser.Parse([.. CliArgs.FromUserConfig(cfg)]);

        await Assert.That(error).IsNull();
        await Assert.That(parsed!.Limit).IsEqualTo(limit);
    }

    [Test]
    public async Task BareGhProxyDoesNotSwallowThePositionalInput()
    {
        (CliOptions? parsed, string? error) =
            CliParser.Parse(["--gh-proxy", "in.rm", "out.rm"]);

        await Assert.That(error).IsNull();
        await Assert.That(parsed!.Input).IsEqualTo("in.rm");
        await Assert.That(parsed.Output).IsEqualTo("out.rm");
        await Assert.That(parsed.GhProxy).IsEqualTo(GitHubProxy.DefaultPrefix);
    }

    [Test]
    public async Task MalformedFrontIsRefusedAtParseTime()
    {
        (CliOptions? _, string? error) =
            CliParser.Parse(["--gh-proxy=not a url", "in.rm", "out.rm"]);

        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("--gh-proxy");
    }

    [Test]
    public async Task UrlFileNameCannotBecomeAPathOrDeviceName()
    {
        await Assert.That(HttpSource.FileNameFromUrl("https://h/x/C%3Aevil.mp4"))
            .IsEqualTo("C_evil.mp4");
        await Assert.That(HttpSource.FileNameFromUrl("https://h/x/%2E%2E"))
            .IsEqualTo("remote");
        await Assert.That(HttpSource.FileNameFromUrl("https://h/x/CON"))
            .IsEqualTo("_CON");
    }

    [Test]
    public async Task QuotingCoversShellExpansionCharacters()
    {
        await Assert.That(CliArgs.Quote("a$b")).IsEqualTo("\"a$b\"");
        await Assert.That(CliArgs.Quote("it's.rm")).IsEqualTo("\"it's.rm\"");
        await Assert.That(CliArgs.Quote("plain.rm")).IsEqualTo("plain.rm");
    }

    [Test]
    public async Task FutureUpdateTimestampIsNotFresh()
    {
        var state = new UpdateState
        {
            Result = "ok",
            CheckedAt = DateTimeOffset.Now.AddDays(1),
        };
        await Assert.That(state.IsFresh(TimeSpan.FromHours(24))).IsFalse();
    }

    [Test]
    public async Task PreReleaseTagsAreMarkedButNotConfusedWithStableOnes()
    {
        (Release? rc, string? rcError) = SelfUpdate.Parse("""{"tag_name":"v9.9.9-rc1","assets":[]}""");
        await Assert.That(rcError).IsNull();
        await Assert.That(rc!.Prerelease).IsTrue();

        (Release? stable, string? stableError) = SelfUpdate.Parse("""{"tag_name":"v9.9.9","assets":[]}""");
        await Assert.That(stableError).IsNull();
        await Assert.That(stable!.Prerelease).IsFalse();
    }

    [Test]
    public async Task FeedAssetNamesWithPathSyntaxAreRejected()
    {
        (Release? release, string? error) = SelfUpdate.Parse("""
            {"tag_name":"v9.9.9","assets":[
              {"name":"C:evil.zip","browser_download_url":"https://h/a.zip"},
              {"name":"..","browser_download_url":"https://h/b.zip"},
              {"name":"good-win-x64.zip","browser_download_url":"https://h/c.zip"}]}
            """);
        await Assert.That(error).IsNull();
        await Assert.That(release!.Assets.Count).IsEqualTo(1);
        await Assert.That(release.Assets[0].Name).IsEqualTo("good-win-x64.zip");
    }

    [Test]
    public async Task ProfileBlockEscapesShellSyntaxAndKeepsCrLf()
    {
        string block = ToolchainPath.ProfileBlock("/opt/weird$/dir`x");
        await Assert.That(block.Contains("\\$", StringComparison.Ordinal)).IsTrue();
        await Assert.That(block.Contains("\\`", StringComparison.Ordinal)).IsTrue();

        const string original = "export EDITOR=vim\r\n";
        string withBlock = ToolchainPath.InsertProfileBlock(original, "/opt/lae/bin");
        await Assert.That(ToolchainPath.RemoveProfileBlock(withBlock)).IsEqualTo(original);
    }

    [Test]
    public async Task CompletionCatalogKnowsWhichValuesAreOptional()
    {
        string zsh = Completion.Generate("zsh", []);
        await Assert.That(zsh.Contains("--gh-proxy::value:", StringComparison.Ordinal)).IsTrue();

        string powershell = Completion.Generate("powershell", []);
        await Assert.That(powershell.Contains("$elements[-2]", StringComparison.Ordinal)).IsTrue();

        await Assert.That(OptionCatalog.All.Single(o => o.Name == "--recompress-args").OptionKind)
            .IsEqualTo(OptionCatalog.Kind.Value);
    }

    [Test]
    public async Task FramePoolRejectsAnImpossibleFrameSize()
    {
        await Assert.That(() => new FrameBufferPool(4, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new FrameBufferPool(0, 1024)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ShardPlannerClampsAHugeRange()
    {
        List<ShardRange> ranges = ShardPlanner.Plan(0.0, 1e9, 30.0, 4);
        await Assert.That(ranges.Count).IsEqualTo(4);
        await Assert.That(ranges.All(r => r.Slots > 0)).IsTrue();
    }

    [Test]
    public async Task LadderMigrationRunsOnlyOncePerFile()
    {
        // Normalise is private; TryLoad is the public read boundary that calls it.
        string path = Path.Combine(Path.GetTempPath(), "lae_v7995_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"FpsLevels\":[13,8,5,3]}");
            UserConfig? migrated = ConfigStore.TryLoad(path);
            await Assert.That(migrated).IsNotNull();
            await Assert.That(migrated!.LadderMigrated).IsTrue();
            await Assert.That(migrated.FpsLevels).IsEquivalentTo(Constants.DefaultFpsLevels);

            // A file the program itself saved keeps a user-chosen legacy ladder.
            File.WriteAllText(path, "{\"FpsLevels\":[13,8,5,3],\"LadderMigrated\":true}");
            UserConfig? chosen = ConfigStore.TryLoad(path);
            await Assert.That(chosen!.FpsLevels).IsEquivalentTo(Constants.LegacyFpsLevels);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
