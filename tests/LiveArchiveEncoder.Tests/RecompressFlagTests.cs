using LiveArchiveEncoder.PostProcess;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.8: the flags that describe OpenH264, and the second pass built from
/// them.
///
/// <para>The three defects pinned here were all "accepted and ignored":
/// <c>--recompress-quality</c> on a codec with no quality flag, a bitrate on a
/// codec driven by <c>-crf</c>, and a bitrate-only codec given no bitrate at
/// all. Each produced a command that looked like the flag had worked.</para>
/// </summary>
public sealed class RecompressFlagTests
{
    [Test]
    public async Task TheThreeNewFlagsReachTheOptions()
    {
        (CliOptions? opts, string? error) = CliParser.Parse(
            ["clip.mkv", "--openh264-rc", "Quality", "--openh264-skip-frames",
             "--recompress-codec", "libopenh264", "--recompress-video-kbps", "700"]);

        await Assert.That(error).IsNull();
        // Case is the user's typing, not a different mode.
        await Assert.That(opts!.OpenH264Rc).IsEqualTo("quality");
        await Assert.That(opts.OpenH264SkipFrames).IsTrue();
        await Assert.That(opts.RecompressVideoKbps).IsEquivalentTo(new[] { 700 });
    }

    [Test]
    public async Task TheRefusedModesAreRefusedAtTheParserToo()
    {
        // The list lives in one place; the menu, the completion catalogue and
        // the parser all read it, so a value cannot be offerable in one and
        // rejected by another.
        (_, string? buffer) = CliParser.Parse(["clip.mkv", "--openh264-rc", "buffer"]);
        await Assert.That(buffer).Contains("3.1 Mbps");

        (_, string? off) = CliParser.Parse(["clip.mkv", "--openh264-rc", "off"]);
        await Assert.That(off).Contains("-qp");
    }

    [Test]
    public async Task AZeroBitrateIsAnErrorRatherThanUnset()
    {
        // -b:v 0k is not "no bitrate"; it is a broken request, and the catalog
        // default only applies when the flag is absent.
        (_, string? error) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libopenh264", "--recompress-video-kbps", "0"]);
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("positive");
    }

    [Test]
    public async Task ABitrateOnlyCodecAlwaysGetsABitrateInTheChain()
    {
        (CliOptions? opts, _) = CliParser.Parse(["clip.mkv", "--recompress-codec", "libopenh264"]);
        (Configuration.RecompressSettings rc, string? error) = Cli.ResolveRecompress(opts!, null, true);

        await Assert.That(error).IsNull();
        await Assert.That(rc.Args).Contains("-c:v libopenh264");
        await Assert.That(rc.Args).Contains("-b:v 300k");
        await Assert.That(rc.Args).Contains("-rc_mode bitrate");

        (CliOptions? explicitRate, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libopenh264", "--recompress-video-kbps", "700"]);
        (Configuration.RecompressSettings at700, _) = Cli.ResolveRecompress(explicitRate!, null, true);
        await Assert.That(at700.Args).Contains("-b:v 700k");
    }

    [Test]
    public async Task AValueWrittenForTheWrongPassIsRefusedWithTheReason()
    {
        (CliOptions? quality, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libopenh264", "--recompress-quality", "24"]);
        (_, string? qualityError) = Cli.ResolveRecompress(quality!, null, true);
        await Assert.That(qualityError).IsNotNull();
        await Assert.That(qualityError!).Contains("does not apply to libopenh264");
        await Assert.That(qualityError!).Contains("--recompress-video-kbps");

        (CliOptions? kbps, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libx264", "--recompress-video-kbps", "700"]);
        (_, string? kbpsError) = Cli.ResolveRecompress(kbps!, null, true);
        await Assert.That(kbpsError).IsNotNull();
        await Assert.That(kbpsError!).Contains("does not apply to libx264");
        await Assert.That(kbpsError!).Contains("-crf");
    }

    [Test]
    public async Task NumbersApplyWhereTheyApplyInAMixedChain()
    {
        // A chain of both kinds -- x264 for the viewing copy, OpenH264 for the
        // site's look -- is a legitimate way to write "published twice". Each
        // kind of number applies where it applies, because the alternative
        // (refusing a number the pass cannot use) would make the chain
        // impossible to write: one number cannot skip a pass, and writing it
        // twice does not help.
        (CliOptions? both, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libx264", "--recompress-codec", "libopenh264",
             "--recompress-quality", "26", "--recompress-video-kbps", "400"]);
        (Configuration.RecompressSettings rc, string? error) = Cli.ResolveRecompress(both!, null, true);

        await Assert.That(error).IsNull();
        await Assert.That(rc.Args).Contains("-crf 26");
        await Assert.That(rc.Args.Contains("-b:v", StringComparison.Ordinal)).IsFalse();
        await Assert.That(rc.Extra.Count).IsEqualTo(1);
        await Assert.That(rc.Extra[0].Args).Contains("-b:v 400k");
        await Assert.That(rc.Extra[0].Args.Contains("-crf", StringComparison.Ordinal)).IsFalse();

        // One number covering the chain still reaches the pass that can use it,
        // and the other pass falls back to its catalog default rather than to
        // nothing.
        (CliOptions? one, _) = CliParser.Parse(
            ["clip.mkv", "--recompress-codec", "libx264", "--recompress-codec", "libopenh264",
             "--recompress-quality", "26"]);
        (Configuration.RecompressSettings single, string? singleError) = Cli.ResolveRecompress(one!, null, true);

        await Assert.That(singleError).IsNull();
        await Assert.That(single.Args).Contains("-crf 26");
        await Assert.That(single.Extra[0].Args).Contains("-b:v 300k");
    }

    [Test]
    public async Task EditingAnUnrelatedFieldKeepsTheBitrate()
    {
        // ComposeSecondPass rewrites the whole video half, so an audio edit used
        // to drop a hand-set bitrate back to the catalog default while printing
        // "audio codec set" -- a change nobody asked for.
        (List<string> tokens, _) = PostArgs.Parse("-c:v libopenh264 -b:v 700k -rc_mode bitrate -pix_fmt yuv420p");
        SecondPassVideoCodec codec = RecompressCodecs.FindVideo("libopenh264")!;

        string kept = Cui.ComposeSecondPass(tokens, codec, null, "aac", null);
        await Assert.That(kept).Contains("-b:v 700k");

        // An explicit number from the bitrate row still wins.
        string replaced = Cui.ComposeSecondPass(tokens, codec, null, "aac", null, 500);
        await Assert.That(replaced).Contains("-b:v 500k");

        // And a quality codec keeps its own knob rather than a stray bitrate.
        (List<string> x264Tokens, _) = PostArgs.Parse("-c:v libx264 -crf 22 -preset veryfast");
        string x264 = Cui.ComposeSecondPass(x264Tokens, RecompressCodecs.FindVideo("libx264")!, 22, "aac", null);
        await Assert.That(x264).Contains("-crf 22");
        await Assert.That(x264.Contains("-b:v", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task TheOpenH264FlagsAreRefusedOnAnotherEncoder()
    {
        // The flag names one encoder. Accepting it for another one is the
        // defect this release is about -- the value would never reach FFmpeg,
        // and nothing would say so.
        (CliOptions? quality, _) = CliParser.Parse(["clip.mkv", "--openh264-rc", "quality"]);
        string? problem = Cli.OpenH264FlagProblem(quality!, "libx264");
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!).Contains("only applies to libopenh264");
        await Assert.That(problem!).Contains("libx264");

        // The encoder they are about is the one they are allowed on.
        await Assert.That(Cli.OpenH264FlagProblem(quality!, "libopenh264")).IsNull();

        (CliOptions? skip, _) = CliParser.Parse(["clip.mkv", "--openh264-skip-frames"]);
        await Assert.That(Cli.OpenH264FlagProblem(skip!, "h264_nvenc")).Contains("--openh264-skip-frames");

        // No flags: nothing to refuse, whatever the encoder is.
        (CliOptions? none, _) = CliParser.Parse(["clip.mkv"]);
        await Assert.That(Cli.OpenH264FlagProblem(none!, "mpeg4")).IsNull();
    }
}
