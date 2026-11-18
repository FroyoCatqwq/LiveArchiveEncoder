using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.PostProcess;
using LiveArchiveEncoder.UI;
using LiveArchiveEncoder.Update;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.11: regressions for the full-project audit -- capability probing,
/// timeout bounds, filter-graph safety, VBV overflow and argument validation.
/// </summary>
public sealed class V79911Tests
{
    [Test]
    public async Task EncoderHelpWrittenToStderrIsStillParsed()
    {
        var encoder = new EncoderInfo { Name = "libopenh264", Flags = "EV" };
        // Some builds/wrappers put `-h encoder=...` on stderr. Before 7.9.9.11
        // every capability below silently came back empty on those builds.
        string help = "\n  -rc_mode        <int>     E..A.... rate control mode\n"
                      + "Supported pixel formats: yuv420p yuv422p\n"
                      + "Supported framerates: 24 25 30\n";
        EncoderCaps caps = FFmpegCapabilities.ParseEncoderText(help, encoder);
        await Assert.That(caps.HasRcMode).IsTrue();
        await Assert.That(caps.SupportedPixFmts).Contains("yuv420p");
        await Assert.That(caps.SupportedRates).Contains(25.0);
    }

    [Test]
    public async Task AudioTimeoutStaysFiniteForCorruptDurations()
    {
        await Assert.That(RealtimeEncoder.AudioTimeoutSeconds(10)).IsEqualTo(140);
        await Assert.That(RealtimeEncoder.AudioTimeoutSeconds(double.MaxValue))
            .IsEqualTo(int.MaxValue / 1000);
        await Assert.That(RealtimeEncoder.AudioTimeoutSeconds(double.NaN)).IsEqualTo(120);
        await Assert.That(RealtimeEncoder.AudioTimeoutSeconds(double.PositiveInfinity)).IsEqualTo(120);

        await Assert.That(ParallelEncoder.JoinTimeout(double.MaxValue))
            .IsEqualTo(int.MaxValue / 1000);
        await Assert.That(ParallelEncoder.JoinTimeout(double.NaN)).IsEqualTo(120);
    }

    [Test]
    public async Task PadColourCannotInjectFilterOptions()
    {
        await Assert.That(EncoderMath.PadFilterExpr(320, 240, "black"))
            .IsEqualTo("pad=320:240:(ow-iw)/2:(oh-ih)/2:black");
        await Assert.That(EncoderMath.PadFilterExpr(320, 240, "0x102030"))
            .Contains("0x102030");

        foreach (string bad in new[] { "black,fps=1000", "black:eval=init", "black'", "a b" })
        {
            await Assert.That(() => EncoderMath.PadFilterExpr(320, 240, bad))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task VbvCompletionCannotWriteANegativeBuffer()
    {
        (string fragment, string? note) = FragmentEditor.NormalizeVbvPair(
            "-b:v 1000k -maxrate 2000000000k");
        await Assert.That(note).IsNotNull();
        await Assert.That(fragment).DoesNotContain("-bufsize -");
        await Assert.That(fragment).Contains("-bufsize 2147483647k");
    }

    [Test]
    public async Task InlineFfmpegEnvUsesTheSameShellWhitelist()
    {
        (CliOptions? _, string? error) = CliParser.Parse(["--ffmpeg-env=turbo"]);
        await Assert.That(error).IsNotNull();

        (CliOptions? ok, string? okError) = CliParser.Parse(
            ["in.mp4", "--ffmpeg-env=powershell"]);
        await Assert.That(okError).IsNull();
        await Assert.That(ok).IsNotNull();
    }

    [Test]
    public async Task NoInputGuardSkipsValuesThatStartWithADash()
    {
        // `-myffmpeg` is a (weird but legal) custom executable path, not an
        // encode option CUI would drop.
        await Assert.That(Program.EncodeOptionWithNoInput(["--ffmpeg", "-myffmpeg"])).IsNull();
        await Assert.That(Program.EncodeOptionWithNoInput(["--no-realtime"]))
            .IsEqualTo("--no-realtime");
    }

    [Test]
    public async Task VersionTagsStillParseWithAnInvariantCulture()
    {
        VersionTag? tag = VersionTag.Parse("v7.9.9.11");
        await Assert.That(tag).IsNotNull();
        await Assert.That(VersionTag.Parse("+7.9")).IsNull();
    }
}
