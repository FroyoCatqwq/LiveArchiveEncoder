using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.6 regressions: the parser's argparse-compatible short circuits and
/// the "--" terminator (a filename that begins with a dash).
/// </summary>
public sealed class V7996Tests
{
    [Test]
    public async Task HelpStopsBeforeLaterArguments()
    {
        // argparse prints help and exits before validating what follows, so a
        // stale script with one bad flag after --help still gets its help.
        (CliOptions? options, string? error) = CliParser.Parse(["--help", "--bogus"]);

        await Assert.That(error).IsNull();
        await Assert.That(options!.Help).IsTrue();
    }

    [Test]
    public async Task VersionStopsBeforeLaterArguments()
    {
        (CliOptions? options, string? error) = CliParser.Parse(["--version", "--bogus"]);

        await Assert.That(error).IsNull();
        await Assert.That(options!.ShowVersion).IsTrue();
    }

    [Test]
    public async Task DoubleDashEndsOptionParsing()
    {
        (CliOptions? options, string? error) = CliParser.Parse(["--", "-clip.mp4", "out.avi"]);

        await Assert.That(error).IsNull();
        await Assert.That(options!.Input).IsEqualTo("-clip.mp4");
        await Assert.That(options.Output).IsEqualTo("out.avi");

        // Without the terminator the same name is an unknown option.
        (CliOptions? _, string? without) = CliParser.Parse(["-clip.mp4", "out.avi"]);
        await Assert.That(without).IsNotNull();
    }

    [Test]
    public async Task DoubleDashKeepsOptionLookalikesAsMorePositionals()
    {
        // Everything after -- is a positional, including switches the parser
        // would otherwise recognise; a third positional is still refused.
        (CliOptions? options, string? error) = CliParser.Parse(["--", "in.rm", "--quiet"]);

        await Assert.That(error).IsNull();
        await Assert.That(options!.Input).IsEqualTo("in.rm");
        await Assert.That(options.Output).IsEqualTo("--quiet");
    }

    [Test]
    public async Task ASingleSubtitleFieldCountsAsTouched()
    {
        // v7.9.9.6: `--subtitle-mode off` with a stored sidecar used to be
        // ignored because TouchedSubtitles only looked at files/stream.
        (CliOptions? mode, string? modeError) = CliParser.Parse(["--subtitle-mode", "off"]);
        await Assert.That(modeError).IsNull();
        await Assert.That(mode!.SubtitleModeExplicit).IsTrue();
        await Assert.That(mode.TouchedSubtitles).IsTrue();

        (CliOptions? font, string? fontError) = CliParser.Parse(["--subtitle-font", "Arial"]);
        await Assert.That(fontError).IsNull();
        await Assert.That(font!.SubtitleFontExplicit).IsTrue();
        await Assert.That(font.TouchedSubtitles).IsTrue();

        // An unrelated run must still leave the stored block alone.
        (CliOptions? plain, string? plainError) = CliParser.Parse(["in.mp4", "out.avi"]);
        await Assert.That(plainError).IsNull();
        await Assert.That(plain!.TouchedSubtitles).IsFalse();
    }

    [Test]
    public async Task SubtitleModeAloneKeepsTheStoredSelection()
    {
        var saved = new SubtitleConfig
        {
            Mode = "burn",
            Files = ["a.srt"],
            Codec = "mov_text",
        };
        (CliOptions? args, string? error) = CliParser.Parse(["--subtitle-mode", "off"]);
        await Assert.That(error).IsNull();

        SubtitleConfig merged = Cli.SubtitlesForRun(saved, args!);

        await Assert.That(merged.Mode).IsEqualTo("off");
        await Assert.That(merged.Files).IsEquivalentTo(["a.srt"]);
        await Assert.That(merged.Codec).IsEqualTo("mov_text");
    }

    [Test]
    public async Task SubtitleFontAloneKeepsTheStoredBlock()
    {
        var saved = new SubtitleConfig
        {
            Mode = "copy",
            Files = ["a.srt"],
            FontDir = "fonts",
        };
        (CliOptions? args, string? error) = CliParser.Parse(["--subtitle-font", "Arial"]);
        await Assert.That(error).IsNull();

        SubtitleConfig merged = Cli.SubtitlesForRun(saved, args!);

        await Assert.That(merged.Mode).IsEqualTo("copy");
        await Assert.That(merged.Files).IsEquivalentTo(["a.srt"]);
        await Assert.That(merged.FontDir).IsEqualTo("fonts");
        await Assert.That(merged.FallbackFont).IsEqualTo("Arial");
    }
}
