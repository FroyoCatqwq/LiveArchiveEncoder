using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.PostProcess;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.3 regressions: shared fragment editing, VBV validation for BOTH the
/// CLI and the CUI, output-injection defence, and the new menu keys.
/// </summary>
public sealed class V7993Tests
{
    [Test]
    public async Task PostArgsRejectsABareOutputAnywhereInTheFragment()
    {
        // v7.9.9.3: Validate used to inspect only the LAST token. A bare path
        // in the middle became an extra FFmpeg output that could overwrite an
        // unrelated file.
        (List<string> _, string? error) = PostArgs.Parse(
            "-c:v libx264 evil.mp4 -crf 20");
        await Assert.That(error).IsNotNull();

        (_, error) = PostArgs.Parse("-c:v libx264 -crf 20");
        await Assert.That(error).IsNull();

        // Quoted filter expressions are one value, not a stray path.
        (_, error) = PostArgs.Parse("-vf \"yadif,fps=15\" -c:v libx264");
        await Assert.That(error).IsNull();

        (_, error) = PostArgs.Parse("-i other.mp4");
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task FragmentEditorSetsMergesAndDeduplicates()
    {
        const string fragment = "-c:v libx264 -b:v 250k -preset slow";

        string withMax = FragmentEditor.SetFlag(fragment, "-maxrate", "300k");
        await Assert.That(withMax.Contains("-maxrate 300k", StringComparison.Ordinal)).IsTrue();
        await Assert.That(FragmentEditor.SetFlag(withMax, "-maxrate", null)
            .Contains("-maxrate", StringComparison.Ordinal)).IsFalse();
        // Replacing does not duplicate either.
        await Assert.That(FragmentEditor.SetFlag(withMax, "-maxrate", "500k")
            .Contains("-maxrate 300k", StringComparison.Ordinal)).IsFalse();

        // v7.9.9.2 concatenated strings; a pinned -maxrate was duplicated and
        // FFmpeg silently used the last one.
        string pinned = FragmentEditor.Merge(
            "-c:v libx264 -b:v 250k -maxrate 400k",
            "-maxrate 300k -bufsize 600k");
        await Assert.That(pinned.Contains("-maxrate 400k", StringComparison.Ordinal)).IsTrue();
        await Assert.That(pinned.Contains("-maxrate 300k", StringComparison.Ordinal)).IsFalse();
        await Assert.That(pinned.Contains("-bufsize 600k", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task VbvValidationIsSharedByBothPasses()
    {
        await Assert.That(FragmentEditor.ValidateVbv(
            "-c:v libx264 -maxrate 300k")).IsNotNull();
        await Assert.That(FragmentEditor.ValidateVbv(
            "-c:v libx264 -b:v 250k -maxrate 300k")).IsNull();

        string? bad = FragmentEditor.ValidateVbv(
            "-c:v libx264 -b:v 250k -maxrate 100k -minrate 300k");
        await Assert.That(bad).IsNotNull();
        await Assert.That(bad!).Contains("must not exceed");

        // The live encoder generates its own -b:v, so only bounds apply.
        await Assert.That(FragmentEditor.ValidateBounds(
            "-maxrate 100k -minrate 300k")).IsNotNull();
        await Assert.That(FragmentEditor.ValidateBounds(
            "-maxrate 300k -minrate 100k")).IsNull();
    }

    [Test]
    public async Task FirstWinsKeepsTheUsersPresetWithoutEatingRepeatedMaps()
    {
        string result = FragmentEditor.FirstWins(
            "-preset slow -c:v libx264 -preset veryfast");
        await Assert.That(result.Contains("-preset slow", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Contains("-preset veryfast", StringComparison.Ordinal)).IsFalse();

        // Multiple -map entries are legitimate output-side arguments.
        await Assert.That(FragmentEditor.FirstWins("-map 0:v:0 -map 1:a:0"))
            .IsEqualTo("-map 0:v:0 -map 1:a:0");
    }

    [Test]
    public async Task TryValueDoesNotSwallowTheNextOption()
    {
        // Forgetting the value used to turn --quiet into an FFmpeg argument.
        (CliOptions? missing, string? error) = CliParser.Parse(
            ["clip.mkv", "--recompress-extra", "--quiet"]);
        await Assert.That(error).IsNotNull();

        // A fragment that starts with '-' is still a perfectly valid value.
        (CliOptions? fragment, error) = CliParser.Parse(
            ["clip.mkv", "--recompress-extra", "-preset slow -ac 1"]);
        await Assert.That(error).IsNull();
        await Assert.That(fragment!.RecompressExtra).IsEqualTo("-preset slow -ac 1");
    }

    [Test]
    public async Task CliResolveRecompressUsesTheSharedVbvRuleWithoutDuplicates()
    {
        (CliOptions? opts, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libx264",
             "--recompress-video-kbps", "250", "--recompress-maxrate", "300",
             "--recompress-minrate", "100", "--recompress-bufsize", "600"]);
        (RecompressSettings settings, string? error) =
            Cli.ResolveRecompress(opts!, null, true);
        await Assert.That(error).IsNull();

        string args = settings.Args;
        foreach (string flag in new[]
                 { "-b:v 250k", "-maxrate 300k", "-minrate 100k", "-bufsize 600k" })
            await Assert.That(args.Contains(flag, StringComparison.Ordinal)).IsTrue();
        await Assert.That(args.Split("-maxrate").Length - 1).IsEqualTo(1);
        await Assert.That(FragmentEditor.ValidateVbv(args)).IsNull();
    }

    [Test]
    public async Task ExtraEncoderArgsAreRememberedInTheSnapshot()
    {
        // v7.9.9.3: --emit-args must reproduce the live-side generic options.
        var stored = new UserConfig { ExtraEncoderArgs = "-profile:v high" };
        List<string> argv = CliArgs.FromUserConfig(stored);
        int at = argv.IndexOf("--extra-encoder-args");
        await Assert.That(at).IsGreaterThanOrEqualTo(0);
        await Assert.That(argv[at + 1]).IsEqualTo("-profile:v high");

        (CliOptions? parsed, string? error) = CliParser.Parse(["clip.mkv", .. argv]);
        await Assert.That(error).IsNull();
        await Assert.That(parsed!.ExtraEncoderArgs).IsEqualTo("-profile:v high");
    }

    [Test]
    public async Task EraSitePresetsNowCarryTheirAdvertisedVbvCap()
    {
        // The old `Contains("-b:v") is false` guard was dead code: the native
        // video args always carry -b:v, so site presets never got the cap.
        foreach (string id in new[] { "cctv/2006", "youku/2007", "ku6/2007" })
        {
            EraPreset? preset = EraPresets.Find(id);
            if (preset is null) continue;
            string fragment = preset.RecompressArgsFor(true, null);
            await Assert.That(fragment.Contains("-b:v", StringComparison.Ordinal)).IsTrue();
            await Assert.That(fragment.Contains("-maxrate", StringComparison.Ordinal)).IsTrue();
            await Assert.That(fragment.Contains("-bufsize", StringComparison.Ordinal)).IsTrue();
            await Assert.That(FragmentEditor.ValidateVbv(fragment)).IsNull();
        }
    }

    [Test]
    public async Task NewCuiKeysExistInBothLanguages()
    {
        string[] keys =
        [
            "rc.menu.chain", "rc.menu.maxrate", "rc.menu.minrate", "rc.menu.bufsize",
            "rc.menu.preset", "rc.menu.profile", "rc.menu.channels",
            "rc.prompt.flag", "rc.flag.set", "rc.vbv.bad",
            "param.maxrate", "param.minrate", "param.bufsize",
        ];
        foreach (string key in keys)
        {
            await Assert.That(L.KeysFor(L.English).Contains(key)).IsTrue();
            await Assert.That(L.KeysFor(L.Chinese).Contains(key)).IsTrue();
        }
    }
}
