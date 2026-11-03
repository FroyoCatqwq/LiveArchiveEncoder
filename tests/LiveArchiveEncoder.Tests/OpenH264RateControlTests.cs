using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Encoding;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.8: OpenH264's rate control, pinned to what the wrapper actually does.
///
/// <para>Read off <c>libavcodec/libopenh264enc.c</c> and then measured against
/// this build; every number in these assertions came from a run, not from a
/// blog post. The trap this file exists to prevent is a flag that is accepted
/// and ignored: <c>-qp</c> does not exist (the wrapper never reads
/// <c>global_quality</c>, and FFmpeg prints "AVOption qp ... has not been used
/// for any stream"), and <c>-qmin/-qmax</c> are copied into
/// <c>param.iMinQp/iMaxQp</c> but change nothing at <c>RC_OFF_MODE</c> -- qmin
/// = qmax of 10 and of 51 produced byte-identical files.</para>
/// </summary>
public sealed class OpenH264RateControlTests
{
    [Test]
    public async Task OnlyTheThreeModesThatHoldACeilingAreAccepted()
    {
        foreach (string mode in new[] { "bitrate", "quality", "timestamp" })
        {
            await Assert.That(OpenH264RateControl.Problem(mode)).IsNull().Because(mode);
        }

        // Case and stray spaces are the user's typing, not a different mode.
        await Assert.That(OpenH264RateControl.Problem(" Quality ")).IsNull();
        await Assert.That(OpenH264RateControl.Normalize("TIMESTAMP")).IsEqualTo("timestamp");
    }

    [Test]
    public async Task BufferAndOffAreRefusedWithTheMeasurement()
    {
        // Both are legal values of the FFmpeg option and both are useless here.
        // Offering them would be a switch that looks adjustable and is not.
        string buffer = OpenH264RateControl.Problem("buffer")!;
        await Assert.That(buffer).Contains("3.1 Mbps");
        await Assert.That(buffer).Contains("300 kbps");

        string off = OpenH264RateControl.Problem("off")!;
        await Assert.That(off).Contains("-qp");
        await Assert.That(off).Contains("26");

        string unknown = OpenH264RateControl.Problem("vbr")!;
        await Assert.That(unknown).Contains("bitrate");
        await Assert.That(unknown).Contains("quality");
        await Assert.That(unknown).Contains("timestamp");
    }

    [Test]
    public async Task TheModeReachesTheCommandLine()
    {
        string bitrate = string.Join(' ', ArgsBuilder.VideoQualityArgs("libopenh264", 21, 700));
        await Assert.That(bitrate).IsEqualTo("-rc_mode bitrate -b:v 700k");

        string quality = string.Join(' ',
            ArgsBuilder.VideoQualityArgs("libopenh264", 21, 700, "quality"));
        await Assert.That(quality).IsEqualTo("-rc_mode quality -b:v 700k");

        string timestamp = string.Join(' ',
            ArgsBuilder.VideoQualityArgs("libopenh264", 21, 700, "timestamp"));
        await Assert.That(timestamp).IsEqualTo("-rc_mode timestamp -b:v 700k");
    }

    [Test]
    public async Task SkipFramesIsOptInAndOnlyForTheBitrateEncoder()
    {
        // The wrapper passes it to bEnableFrameSkip. Measured at 300 kbps:
        // 354 kbps without it, 302 with it -- the difference between "target"
        // and "ceiling you can actually sit under".
        string on = string.Join(' ', ArgsBuilder.VideoQualityArgs(
            "libopenh264", 21, 300, "bitrate", allowSkipFrames: true));
        await Assert.That(on).Contains("-allow_skip_frames 1");

        // Default off: the fps ladder owns frame rate, and a second dropper
        // behind its back hides the cause of a rate drop from the ladder.
        string off = string.Join(' ', ArgsBuilder.VideoQualityArgs("libopenh264", 21, 300));
        await Assert.That(off.Contains("-allow_skip_frames", StringComparison.Ordinal)).IsFalse();

        // libx264 has no such option; emitting it there would abort the run.
        string x264 = string.Join(' ', ArgsBuilder.VideoQualityArgs(
            "libx264", 22, 300, "bitrate", allowSkipFrames: true));
        await Assert.That(x264.Contains("-allow_skip_frames", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ABuildWithoutRcModeKeepsTheOldFlags()
    {
        // An option the binary does not know aborts the run, so a build whose
        // -h output has no rc_mode line must fall back to the old command --
        // which still produces a valid file, just without rate control.
        string args = string.Join(' ', ArgsBuilder.VideoQualityArgs(
            "libopenh264", 21, 700, "quality", allowSkipFrames: true, supportsRcMode: false));

        await Assert.That(args.Contains("-rc_mode", StringComparison.Ordinal)).IsFalse();
        await Assert.That(args).Contains("-b:v 700k");
    }

    [Test]
    public async Task AStoredModeThisBuildCannotHonourIsNormalisedOnLoad()
    {
        // A hand-edited settings file can say anything; a mode this build
        // refuses must not make the file unreadable.
        var stored = new LiveArchiveEncoder.Configuration.UserConfig { OpenH264Rc = "buffer" };
        var loaded = new LiveArchiveEncoder.Configuration.Config();
        LiveArchiveEncoder.Configuration.ConfigStore.Apply(stored, loaded, []);

        await Assert.That(loaded.OpenH264Rc).IsEqualTo(OpenH264RateControl.Bitrate);

        stored.OpenH264Rc = "quality";
        var second = new LiveArchiveEncoder.Configuration.Config();
        LiveArchiveEncoder.Configuration.ConfigStore.Apply(stored, second, []);
        await Assert.That(second.OpenH264Rc).IsEqualTo("quality");
    }
}
