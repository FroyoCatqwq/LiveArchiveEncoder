using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.6: a CLI run used to save a settings file built from scratch, which
/// silently replaced the interactive shell's subtitles, recompress chain, GitHub
/// front, NNEDI3 weights path and auto-update switch on every encode. The rule is
/// now "this run owns only what it was told"; these cases pin both halves of it.
/// </summary>
public sealed class StoredPreferencesTests
{
    private static UserConfig FullSettings() => new()
    {
        GhProxy = "https://gh-proxy.com/",
        NnediWeights = "D:/lae/nnedi3_weights.bin",
        AutoUpdateCheck = false,
        EraAspect = "16:9",
        FfmpegSource = "gyan",
        FfmpegProxy = "http://127.0.0.1:7890",
        PlayMode = "ffplay",
        PlayerPath = "C:/tools/ffplay.exe",
        Subtitles = new SubtitleConfig { Mode = "copy", BoxAspect = "4:3", Codec = "mov_text" },
        Recompress = new RecompressSettings { Enabled = true, Args = "-c:v libx264", Container = "mp4" },
    };

    private static CliOptions Parse(params string[] args)
    {
        (CliOptions? options, string? error) = CliParser.Parse(args);
        if (options is null) throw new InvalidOperationException($"parse failed: {error}");
        return options;
    }

    [Test]
    public async Task APlainRunKeepsEverythingItWasNotToldAbout()
    {
        UserConfig stored = FullSettings();
        Config config = new();

        Cli.MergeStoredPreferences(config, stored, Parse());

        await Assert.That(config.GhProxy).IsEqualTo(stored.GhProxy);
        await Assert.That(config.NnediWeights).IsEqualTo(stored.NnediWeights);
        await Assert.That(config.AutoUpdateCheck).IsFalse();
        await Assert.That(config.EraAspect).IsEqualTo("16:9");
        await Assert.That(config.FfmpegSource).IsEqualTo("gyan");
        await Assert.That(config.FfmpegProxy).IsEqualTo(stored.FfmpegProxy);
        await Assert.That(config.Play).IsEqualTo(PlayMode.Ffplay);
        await Assert.That(config.PlayerPath).IsEqualTo(stored.PlayerPath);
        await Assert.That(config.Subtitles.Mode).IsEqualTo("copy");
        await Assert.That(config.Subtitles.BoxAspect).IsEqualTo("4:3");
    }

    /// <summary>
    /// The one field a run must never inherit. The saved chain is what the shell
    /// configured; adopting it would make a plain `--encoder mpeg4 in out.mp4` also
    /// re-encode its own output -- which the e2e suite caught the moment this merge
    /// first went in. It is preserved on disk (see Cli.Run's ConfigStore.Save), not
    /// in the live Config.
    /// </summary>
    [Test]
    public async Task APlainRunNeverAdoptsTheStoredRecompressChain()
    {
        Config config = new();
        Cli.MergeStoredPreferences(config, FullSettings(), Parse());

        await Assert.That(config.Recompress.Enabled).IsFalse();
        await Assert.That(config.Recompress.Args).IsEqualTo("");
    }

    [Test]
    public async Task AFlagOnTheCommandLineWinsOverTheStoredValue()
    {
        CliOptions args = Parse(
            "--nnedi-weights", "D:/other/weights.bin",
            "--era-aspect", "4:3",
            "--ffmpeg-source", "btbn",
            "--play", "external",
            "--player", "C:/other/player.exe",
            "--recompress-args", "-c:v flv");

        Config config = new();
        Cli.MergeStoredPreferences(config, FullSettings(), args);

        await Assert.That(config.NnediWeights).IsEqualTo("D:/other/weights.bin");
        await Assert.That(config.EraAspect).IsEqualTo("4:3");
        await Assert.That(config.FfmpegSource).IsEqualTo("btbn");
        await Assert.That(config.Play).IsEqualTo(PlayMode.External);
        await Assert.That(config.PlayerPath).IsEqualTo("C:/other/player.exe");
        // The run owns the chain -- Cli.Run resolved it from these flags -- so the
        // merge must leave it untouched: the stored libx264/mp4 chain is not copied
        // in over the request that was just built.
        await Assert.That(config.Recompress.Enabled).IsFalse();
        await Assert.That(config.Recompress.Args).IsEqualTo("");
        // ...but the switches it did not touch still come from the file.
        await Assert.That(config.AutoUpdateCheck).IsFalse();
        await Assert.That(config.Subtitles.BoxAspect).IsEqualTo("4:3");
    }

    [Test]
    public async Task TurningTheGithubFrontOffIsNotTheSameAsNotMentioningIt()
    {
        // "" is --no-gh-proxy, a real instruction; null is silence. Using ?? here
        // instead of a presence check would make "off" fall back to the stored front.
        Config off = new();
        Cli.MergeStoredPreferences(off, FullSettings(), Parse("--no-gh-proxy"));
        await Assert.That(off.GhProxy).IsEqualTo("");

        Config silent = new();
        Cli.MergeStoredPreferences(silent, FullSettings(), Parse());
        await Assert.That(silent.GhProxy).IsEqualTo("https://gh-proxy.com/");
    }

    [Test]
    public async Task AnEmptySubtitleFlagDoesNotClearTheStoredDefaults()
    {
        Config config = new();
        Cli.MergeStoredPreferences(config, FullSettings(), Parse("--subtitle-aspect", "16:9"));

        // The run spoke about the shape only, so the mode and codec survive.
        await Assert.That(config.Subtitles.BoxAspect).IsEqualTo("16:9");
        await Assert.That(config.Subtitles.Mode).IsEqualTo("copy");
        await Assert.That(config.Subtitles.Codec).IsEqualTo("mov_text");
    }
}
