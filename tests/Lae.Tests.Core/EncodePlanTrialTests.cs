using Lae.Core;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The two operations the preflight is built from: cut a plan down to a trial,
/// and change how it decodes.
///
/// <para>Both are pure rewrites of an argument list that has already been
/// decided, which is why they live here rather than in the runner. A trial that
/// is not the same command as the real run proves nothing, so the thing worth
/// pinning is exactly what the rewrite keeps and what it changes.</para>
/// </summary>
public sealed class EncodePlanTrialTests
{
    private static EncoderInfo Encoder() => new()
    {
        Name = "mpeg4",
        Extension = "avi",
        FormatName = "avi",
        Codec = "mpeg4",
        Family = "mpeg4",
    };

    private static SourceInfo Source(bool audio) => new()
    {
        DurationSeconds = 300,
        Width = 640,
        Height = 480,
        PixelFormat = "yuv420p",
        HasAudio = audio,
        AudioRate = 48000,
        AudioChannels = 2,
        Streams = audio
            ? [
                new SourceStream(0, "video", "h264", null, null, new StreamInfo { Width = 640, Height = 480 }),
                new SourceStream(1, "audio", "aac", "jpn", null),
            ]
            : [new SourceStream(0, "video", "h264", null, null, new StreamInfo { Width = 640, Height = 480 })],
    };

    private static EncodePlan Plan(bool audio = false, string hwaccel = "off", double fps = 25) => EncodePlan.Create(
        new EncodeRequest
        {
            SourcePath = "/media/in.mkv",
            DestinationPath = "/media/out.avi",
            Encode = new EncodeDefaults { Encoder = "mpeg4", Quality = 8, Fps = fps, Hwaccel = hwaccel },
        },
        Source(audio),
        Encoder());

    private static bool HasRun(IReadOnlyList<string> args, params string[] expected)
    {
        for (int start = 0; start + expected.Length <= args.Count; start++)
        {
            bool match = true;
            for (int i = 0; i < expected.Length; i++)
            {
                if (args[start + i] != expected[i]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    private static int CountOption(IReadOnlyList<string> args, string name)
        => args.Count(arg => arg == name);

    [Test]
    public async Task ATrialIsThreeFramesOfTheSameEncode()
    {
        EncodePlan plan = Plan();
        EncodePlan trial = plan.ForTrial("/media/.probe.avi");

        // The decoder stops at three frames, and says so where FFmpeg expects an
        // output option -- before the format that names the pipe.
        await Assert.That(HasRun(trial.DecoderArgs, "-frames:v", "3", "-f", "rawvideo", "pipe:1")).IsTrue();

        // The span shrinks with it, so the pump is aiming at three slots and not
        // at the duration of the programme.
        await Assert.That(trial.SpanSeconds).IsEqualTo(3.0 / plan.Fps);
        await Assert.That(trial.TargetFrames).IsEqualTo(3L);

        // Everything that decides the output is untouched: this is the same
        // encode, shorter.
        await Assert.That(HasRun(trial.EncoderArgs, "-c:v", "mpeg4")).IsTrue();
        await Assert.That(HasRun(trial.EncoderArgs, "-q:v", "8")).IsTrue();
        await Assert.That(HasRun(trial.EncoderArgs, "-pix_fmt", plan.TransportPixelFormat)).IsTrue();

        // And it writes somewhere disposable.
        await Assert.That(trial.DestinationPath).IsEqualTo("/media/.probe.avi");
        await Assert.That(trial.EncoderArgs[^1]).IsEqualTo("/media/.probe.avi");
    }

    /// <summary>
    /// The encoder's own <c>-t</c> has to be rewritten, not added to.
    ///
    /// <para>Two <c>-t</c> options in one command line is a command whose length
    /// nobody can predict, and the failure it causes -- a trial that runs for the
    /// full requested duration because the audio input keeps the output alive --
    /// would look like the preflight hanging rather than like a bug here.</para>
    /// </summary>
    [Test]
    public async Task ATrialStatesItsLengthExactlyOnce()
    {
        EncodePlan plan = Plan(audio: true);
        EncodePlan trial = plan.ForTrial("/media/.probe.avi");

        await Assert.That(CountOption(plan.EncoderArgs, "-t")).IsEqualTo(1);
        await Assert.That(CountOption(trial.EncoderArgs, "-t")).IsEqualTo(1);
        await Assert.That(HasRun(trial.EncoderArgs, "-t", "0.120000")).IsTrue();
    }

    [Test]
    public async Task ATrialRefusesAPlanItCannotShorten()
    {
        // A plan with no encoder arguments has no muxer to find, and silently
        // running the full thing instead would be the worst possible answer.
        EncodePlan broken = Plan() with { EncoderArgs = [] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            broken.ForTrial("/media/.probe.avi");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task AutoIsHandedToFfmpegRatherThanRefused()
    {
        // "auto" is a request the user cannot resolve themselves: which hardware
        // exists is a property of the machine. Refusing it in planning was the
        // old behaviour and it made the setting untestable by the user.
        EncodePlan plan = Plan(hwaccel: "auto");

        await Assert.That(plan.IsRunnable).IsTrue().Because(string.Join("; ", plan.Problems));
        await Assert.That(HasRun(plan.DecoderArgs, "-hwaccel", "auto")).IsTrue();
    }

    [Test]
    public async Task TurningHardwareDecodingOffRemovesTheOption()
    {
        EncodePlan plan = Plan(hwaccel: "auto");
        EncodePlan software = plan.WithHwaccel("off");

        // Absent, not "-hwaccel off": the command the user reads afterwards is
        // the one FFmpeg would be given by someone who never asked for hardware.
        await Assert.That(software.DecoderArgs.Contains("-hwaccel")).IsFalse();
        await Assert.That(software.Encode.Hwaccel).IsEqualTo("off");
        await Assert.That(HasRun(software.DecoderArgs, "-i", "/media/in.mkv")).IsTrue();
    }

    [Test]
    public async Task NamingAMethodReplacesTheOneInPlace()
    {
        EncodePlan plan = Plan(hwaccel: "auto");
        EncodePlan dxva = plan.WithHwaccel("d3d11va");

        await Assert.That(HasRun(dxva.DecoderArgs, "-hwaccel", "d3d11va")).IsTrue();
        await Assert.That(HasRun(dxva.DecoderArgs, "-hwaccel", "auto")).IsFalse();
        await Assert.That(HasRun(dxva.DecoderArgs, "-i", "/media/in.mkv")).IsTrue();
    }

    [Test]
    public async Task NamingAMethodOnAPlanThatHadNoneInsertsItBeforeTheInput()
    {
        EncodePlan plan = Plan();
        EncodePlan accelerated = plan.WithHwaccel("vaapi");

        int hwaccel = accelerated.DecoderArgs.ToList().IndexOf("-hwaccel");
        int input = accelerated.DecoderArgs.ToList().IndexOf("-i");

        await Assert.That(hwaccel).IsGreaterThan(-1);
        await Assert.That(hwaccel).IsLessThan(input);
    }
}
