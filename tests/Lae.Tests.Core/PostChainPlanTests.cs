using Lae.Core;
using Lae.Core.Settings;

using static Lae.Tests.Core.TestMessages;

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
public sealed class PostChainPlanTests
{
    private const string Live = "out.avi";

    private static ChainSettings Chain(params ChainStep[] steps) => new()
    {
        Enabled = true,
        Steps = steps,
    };

    /// <summary>An armed chain whose hops are bare fragments.</summary>
    private static ChainSettings Armed(params string[] fragments)
        => Chain([.. fragments.Select(f => new ChainStep { Args = f, Container = "" })]);

    [Test]
    public async Task AnUnarmedChainPlansNothing()
    {
        var chain = new ChainSettings
        {
            Enabled = false,
            Steps = [new ChainStep { Args = "-c:v libx264", Container = "mp4" }],
        };

        PostChainPlan plan = PostChainPlan.Create(chain, Live, En);

        await Assert.That(plan.Steps.Count).IsEqualTo(0);
        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.Any).IsFalse();
        await Assert.That(plan.FinalPath).IsEqualTo(Live);
    }

    [Test]
    public async Task AnArmedChainWithNothingInItIsAProblem()
    {
        PostChainPlan plan = PostChainPlan.Create(Chain(new ChainStep { Args = "   ", Container = "mp4" }), Live, En);

        await Assert.That(plan.Steps.Count).IsEqualTo(0);
        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("every pass is blank");
    }

    [Test]
    public async Task HopsAreNumberedFromTheRecordingAndFeedEachOther()
    {
        PostChainPlan plan = PostChainPlan.Create(
            Armed("-c:v libx264 -crf 30", "-c:v libvpx-vp9 -crf 34"), Live, En);

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
        PostChainPlan plan = PostChainPlan.Create(Armed(fragment), Live, En);

        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.Steps[0].Container).IsEqualTo(expected);
    }

    [Test]
    public async Task AnExplicitContainerWins()
    {
        PostChainPlan plan = PostChainPlan.Create(
            Chain(new ChainStep { Args = "-c:v libx264 -crf 30", Container = "mkv" }), Live, En);

        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.Steps[0].Container).IsEqualTo("mkv");
        await Assert.That(plan.Steps[0].OutputPath).IsEqualTo("out_web.mkv");
    }

    [Test]
    public async Task ACodecThatCannotBeWrittenThereIsRefused()
    {
        PostChainPlan plan = PostChainPlan.Create(
            Chain(new ChainStep { Args = "-c:v libx264 -crf 30", Container = "rm" }), Live, En);

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
        PostChainPlan plan = PostChainPlan.Create(Armed("-c:v copy"), Live, En);

        await Assert.That(plan.Problems.Count).IsEqualTo(0);
        await Assert.That(plan.Steps[0].Container).IsEqualTo("avi");
        await Assert.That(plan.Steps[0].OutputPath).IsEqualTo("out_web.avi");
    }

    [Test]
    public async Task AHopCannotTakeOverItsInput()
    {
        PostChainPlan plan = PostChainPlan.Create(Armed("-i other.mkv -c:v libx264"), Live, En);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("'-i' is not allowed");
    }

    [Test]
    public async Task AHopCannotNameItsOwnOutput()
    {
        PostChainPlan plan = PostChainPlan.Create(Armed("-c:v libx264 -crf 30 clip.mp4"), Live, En);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("looks like an output name");
    }

    [Test]
    public async Task AnEmptyFragmentIsAProblem()
    {
        PostChainPlan plan = PostChainPlan.Create(Armed(""), Live, En);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("every pass is blank");
    }

    [Test]
    public async Task TheCommandLineKeepsBothEnds()
    {
        PostChainPlan plan = PostChainPlan.Create(Armed("-c:v libx264 -crf 30"), Live, En);
        List<string> command = plan.Steps[0].Command("ffmpeg", "out_web.part.mp4");

        await Assert.That(command[0]).IsEqualTo("ffmpeg");

        // -nostdin or the pass reads the terminal's input, which in the REPL
        // means it swallows the next few keystrokes.
        await Assert.That(command.Contains("-nostdin")).IsTrue();
        await Assert.That(command[^1]).IsEqualTo("out_web.part.mp4");
        await Assert.That(command.IndexOf("-i") + 1).IsEqualTo(command.IndexOf(Live));
        await Assert.That(command.Contains("-c:v")).IsTrue();
    }

    /// <summary>
    /// A plugin hop is refused, not skipped.
    ///
    /// <para>Skipping it would deliver a product the chain did not describe, and
    /// the user would have no way to tell: the run says "done", the file exists,
    /// and the step they wrote simply did not happen. Refusing costs a sentence
    /// now and names the build's gap.</para>
    /// </summary>
    [Test]
    public async Task APluginStepIsRefusedUntilThereIsAHost()
    {
        PostChainPlan plan = PostChainPlan.Create(
            Chain(new ChainStep { Plugin = "lae-damage", Stage = "damage" }), Live, En);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("lae-damage");
        await Assert.That(plan.Problems[0]).Contains("no plugin host");
    }

    /// <summary>
    /// Both halves in one step is a configuration mistake, and precedence is not
    /// an answer: whichever half lost would sit in the file looking active.
    /// </summary>
    [Test]
    public async Task AStepThatNamesBothAPluginAndAFragmentIsRefused()
    {
        PostChainPlan plan = PostChainPlan.Create(
            Chain(new ChainStep { Args = "-c:v libx264", Plugin = "lae-damage" }), Live, En);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("both a plugin and an FFmpeg fragment");
    }

    /// <summary>
    /// One bad step stops the plan; it does not quietly plan the good ones and
    /// run a shorter chain than the user wrote.
    /// </summary>
    [Test]
    public async Task APluginStepAnywhereStopsTheWholePlan()
    {
        PostChainPlan plan = PostChainPlan.Create(
            Chain(
                new ChainStep { Args = "-c:v libx264 -crf 30", Container = "mp4" },
                new ChainStep { Plugin = "lae-damage" }),
            Live, En);

        await Assert.That(plan.IsRunnable).IsFalse();
        await Assert.That(plan.Problems[0]).Contains("step 2");
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
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264", "-crf", "30"], En)).IsNull();
        await Assert.That(ArgsFragment.Validate(["-c:a", "copy"], En)).IsNull();
        await Assert.That(ArgsFragment.Validate(["-vf", "yadif,fps=15"], En)).IsNull();
    }

    [Test]
    public async Task AnOutputFlagIsRefused()
    {
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264", "-o"], En)).Contains("'-o' is not an FFmpeg flag");
    }

    /// <summary>
    /// A bare word such as libx264 must not read as a path -- only a separator or
    /// a known container extension does.
    /// </summary>
    [Test]
    public async Task ABareWordIsNotAPath()
    {
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264"], En)).IsNull();
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264", "clip.webm"], En)).Contains("looks like an output name");
        await Assert.That(ArgsFragment.Validate(["-c:v", "libx264", "/tmp/clip"], En)).Contains("looks like an output name");
    }

    /// <summary>
    /// A stray output in the middle is the same takeover as one at the end.
    ///
    /// <para>Validation used to look only at the last token, so
    /// <c>-c:v copy /tmp/x.mp4 -an</c> passed and FFmpeg wrote a second file
    /// nobody asked for. The program owns both ends of the command; a bare token
    /// is only legitimate as the value of the option in front of it.</para>
    /// </summary>
    [Test]
    public async Task AnOutputInTheMiddleIsRefused()
    {
        string? error = ArgsFragment.Validate(["-c:v", "copy", "/tmp/x.mp4", "-an"], En);

        await Assert.That(error).IsNotNull();
        await Assert.That(error!).Contains("/tmp/x.mp4");
    }

    /// <summary>
    /// A filter expression carries a path, and it is a value. Refusing it would
    /// break the most useful fragment there is -- a watermark.
    /// </summary>
    [Test]
    public async Task APathInsideAnOptionValueIsFine()
    {
        await Assert.That(ArgsFragment.Validate(
            ["-vf", "drawtext=fontfile=C:/Fonts/arial.ttf:text=hi", "-c:v", "libx264"], En)).IsNull();
    }

    [Test]
    public async Task AnEmptyArgumentIsRefused()
    {
        await Assert.That(ArgsFragment.Validate(["-c:v", "", "libx264"], En)).IsNotNull();
    }
}


