using Lae.Core;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The post-encode chain: what a video site would have done to the upload.
///
/// <para>These are plan tests, so nothing is run and nothing is written -- which
/// is the point of planning the chain separately. The failures that matter are
/// all discoverable before the capture: a hop that names its own input, a codec
/// that cannot be written to the container asked for, a hop whose name would land
/// on the recording it reads.</para>
/// </summary>
public sealed class RecompressPlanTests
{
    private const string Live = "out.avi";

    private static RecompressChain Chain(params RecompressPass[] passes) => new()
    {
        Enabled = true,
        Passes = passes,
    };

    /// <summary>An armed chain whose hops are bare fragments.</summary>
    private static RecompressChain Armed(params string[] fragments)
        => Chain([.. fragments.Select(f => new RecompressPass(f, ""))]);

    [Test]
    public async Task AnUnarmedChainPlansNothing()
    {
        var chain = new RecompressChain
        {
            Enabled = false,
            Passes = [new RecompressPass("-c:v libx264", "mp4")],
        };

        RecompressPlan plan = RecompressPlan.Create(chain, Live);

        await Assert.That(plan.Steps.Count).IsEqualTo(0);
        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.Any).IsFalse();
        await Assert.That(plan.FinalPath).IsEqualTo(Live);
    }

    [Test]
    public async Task AnArmedChainWithNothingInItIsAProblem()
    {
        RecompressPlan plan = RecompressPlan.Create(Chain(new RecompressPass("   ", "mp4")), Live);

        await Assert.That(plan.Steps.Count).IsEqualTo(0);
        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("every pass is blank");
    }

    [Test]
    public async Task HopsAreNumberedFromTheRecordingAndFeedEachOther()
    {
        RecompressPlan plan = RecompressPlan.Create(
            Armed("-c:v libx264 -crf 30", "-c:v libvpx-vp9 -crf 34"), Live);

        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.Steps.Count).IsEqualTo(2);

        await Assert.That(plan.Steps[0].Number).IsEqualTo(1);
        await Assert.That(plan.Steps[0].InputPath).IsEqualTo(Live);
        await Assert.That(plan.Steps[0].OutputPath).IsEqualTo("out_web.mp4");

        // The second hop reads the first hop's product, but is named from the
        // recording: out_web2, not out_web_web2.
        await Assert.That(plan.Steps[1].InputPath).IsEqualTo("out_web.mp4");
        await Assert.That(plan.Steps[1].OutputPath).IsEqualTo("out_web2.webm");
        await Assert.That(plan.FinalPath).IsEqualTo("out_web2.webm");
    }

    /// <summary>
    /// The container follows the codec. Leaving it out and guessing is how
    /// <c>-c:v libvpx-vp9</c> ends up inside a file called <c>_web.mp4</c> and
    /// dies inside the muxer several seconds into the pass.
    /// </summary>
    [Test]
    [Arguments("-c:v libx264 -crf 30", "mp4")]
    [Arguments("-c:v libvpx-vp9 -crf 34", "webm")]
    [Arguments("-c:v libx265 -crf 30", "mp4")]
    [Arguments("-c:v flv -q:v 6", "flv")]
    [Arguments("-c:v rv20 -q:v 6", "rm")]
    public async Task TheContainerComesFromTheCodec(string fragment, string expected)
    {
        RecompressPlan plan = RecompressPlan.Create(Armed(fragment), Live);

        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.Steps[0].Container).IsEqualTo(expected);
    }

    [Test]
    public async Task AnExplicitContainerWins()
    {
        RecompressPlan plan = RecompressPlan.Create(
            Chain(new RecompressPass("-c:v libx264 -crf 30", "mkv")), Live);

        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.Steps[0].Container).IsEqualTo("mkv");
        await Assert.That(plan.Steps[0].OutputPath).IsEqualTo("out_web.mkv");
    }

    [Test]
    public async Task ACodecThatCannotBeWrittenThereIsRefused()
    {
        RecompressPlan plan = RecompressPlan.Create(
            Chain(new RecompressPass("-c:v libx264 -crf 30", "rm")), Live);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("libx264 cannot be written to .rm");
    }

    /// <summary>
    /// A hop that copies the video keeps the container it was handed, and the
    /// first hop is handed the recording's own extension. Copying into a
    /// different container is a remux, which FFmpeg refuses at run time -- so the
    /// plan does not invent one.
    /// </summary>
    [Test]
    public async Task ACopyHopKeepsTheContainerItWasHanded()
    {
        RecompressPlan plan = RecompressPlan.Create(Armed("-c:v copy"), Live);

        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.Steps[0].Container).IsEqualTo("avi");
        await Assert.That(plan.Steps[0].OutputPath).IsEqualTo("out_web.avi");
    }

    [Test]
    public async Task AHopCannotTakeOverItsInput()
    {
        RecompressPlan plan = RecompressPlan.Create(Armed("-i other.mkv -c:v libx264"), Live);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("'-i' is not allowed");
    }

    [Test]
    public async Task AHopCannotNameItsOwnOutput()
    {
        RecompressPlan plan = RecompressPlan.Create(Armed("-c:v libx264 -crf 30 clip.mp4"), Live);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("looks like an output name");
    }

    [Test]
    public async Task AnEmptyFragmentIsAProblem()
    {
        RecompressPlan plan = RecompressPlan.Create(Armed(""), Live);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("every pass is blank");
    }

    [Test]
    public async Task TheCommandLineKeepsBothEnds()
    {
        RecompressPlan plan = RecompressPlan.Create(Armed("-c:v libx264 -crf 30"), Live);
        List<string> command = plan.Steps[0].Command("ffmpeg", "out_web.part.mp4");

        await Assert.That(command[0]).IsEqualTo("ffmpeg");

        // -nostdin or the pass reads the terminal's input, which in the REPL
        // means it swallows the next few keystrokes.
        await Assert.That(command.Contains("-nostdin")).IsTrue();
        await Assert.That(command[^1]).IsEqualTo("out_web.part.mp4");
        await Assert.That(command.IndexOf("-i") + 1).IsEqualTo(command.IndexOf(Live));
        await Assert.That(command.Contains("-c:v")).IsTrue();
    }
}

/// <summary>
/// Splitting a fragment, and the four tokens that are not allowed in it.
/// </summary>
public sealed class ArgsFragmentTests
{
    [Test]
    public async Task QuotedValuesStayOneToken()
    {
        List<string> tokens = ArgsFragment.Tokenize("-vf \"scale=320:240,fps=15\" -c:v libx264");

        await Assert.That(tokens.Count).IsEqualTo(4);
        await Assert.That(tokens[1]).IsEqualTo("scale=320:240,fps=15");
    }

    /// <summary>
    /// Backslashes are literal on purpose: a Windows path inside a filter
    /// expression relies on them, and unescaping would break the common case.
    /// </summary>
    [Test]
    public async Task BackslashesAreNotEscapes()
    {
        List<string> tokens = ArgsFragment.Tokenize("-vf drawtext=fontfile=C:\\Fonts\\a.ttf");

        await Assert.That(tokens[1]).IsEqualTo("drawtext=fontfile=C:\\Fonts\\a.ttf");
    }

    [Test]
    public async Task AnOrdinaryFragmentIsAccepted()
    {
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264", "-crf", "30"])).IsNull();
        await Assert.That(ArgsFragment.Validate(["-c:a", "copy"])).IsNull();
        await Assert.That(ArgsFragment.Validate(["-vf", "yadif,fps=15"])).IsNull();
    }

    [Test]
    public async Task AnOutputFlagIsRefused()
    {
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264", "-o"])).Contains("'-o' is not an FFmpeg flag");
    }

    /// <summary>
    /// A bare word such as libx264 must not read as a path -- only a separator or
    /// a known container extension does.
    /// </summary>
    [Test]
    public async Task ABareWordIsNotAPath()
    {
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264"])).IsNull();
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264", "clip.webm"])).Contains("looks like an output name");
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264", "/tmp/clip"])).Contains("looks like an output name");
    }
}
