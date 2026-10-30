using Lae.App;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Settings;
using Lae.Encoding;
using Lae.Media.Toolchain;

namespace Lae.Tests.App;

/// <summary>
/// <c>lae encode</c>, driven without FFmpeg.
///
/// <para>What a user scripts against is the triple 0 / 1 / 2 and the sentences
/// next to them, so those are what these tests pin. The command line itself is
/// System.CommandLine's job and is exercised by the end-to-end suite; what is
/// tested here is the layer between the two, which used to be untestable because
/// it lived inside a closure that called <c>Console</c> and <c>Process</c>.</para>
/// </summary>
public sealed class EncodeSessionTests
{
    private const string Source = "/media/in.mkv";

    private static EncoderInfo Mpeg4() => new()
    {
        Name = "mpeg4",
        Extension = "avi",
        FormatName = "avi",
        Codec = "mpeg4",
        Family = "mpeg4",
    };

    private static SourceInfo SourceInfo() => new()
    {
        DurationSeconds = 10,
        Width = 640,
        Height = 480,
        PixelFormat = "yuv420p",
        HasAudio = false,
        Streams = [new SourceStream(0, "video", "h264", null, null, new StreamInfo { Width = 640, Height = 480 })],
    };

    /// <summary>The dependencies, with each one replaceable by a test.</summary>
    private sealed class Deps : IEncodeRunner
    {
        public bool ToolchainPresent { get; init; } = true;
        public bool SourceReadable { get; init; } = true;
        public string? EncoderListError { get; init; }
        public List<EncoderInfo> Encoders { get; init; } = [Mpeg4()];
        public Action<EncodePlan, string>? OnRun { get; init; }
        public bool Ran { get; private set; }

        public EncodeDependencies Build() => new()
        {
            ProbeToolchain = _ => ToolchainPresent
                ? new ToolchainProbeResult { Ffmpeg = "/usr/bin/ffmpeg", Ffprobe = "/usr/bin/ffprobe" }
                : new ToolchainProbeResult(),
            ProbeSource = (_, _) => SourceReadable
                ? (SourceInfo(), null)
                : (null, "no such stream"),
            ListEncoders = _ => (Encoders, EncoderListError),
            Runner = this,
        };

        public LiveRun Run(
            EncodePlan plan, string ffmpeg,
            Action<long, long?>? progress, Action<string>? warn, CancellationToken token)
        {
            Ran = true;
            OnRun?.Invoke(plan, ffmpeg);
            token.ThrowIfCancellationRequested();

            return new LiveRun
            {
                Pump = new PumpStats
                {
                    Frames = plan.TargetFrames,
                    Fresh = plan.TargetFrames,
                    Repeated = 0,
                    Seconds = plan.SpanSeconds,
                    SourceEnded = false,
                },
                OutputPath = plan.DestinationPath,
                OutputBytes = 1024 * 300,
                SpanSeconds = plan.SpanSeconds,
                HasAudio = plan.HasAudio,
            };
        }
    }

    private static (int Code, BufferedTerminal Terminal) Run(
        Deps deps, EncodeOverrides? overrides = null, bool dryRun = false,
        SettingsDocument? settings = null)
    {
        var terminal = new BufferedTerminal();
        int code = EncodeSession.Run(
            Source, overrides ?? new EncodeOverrides { Output = "/media/out.avi" },
            settings ?? new SettingsDocument(), deps.Build(), terminal, dryRun, CancellationToken.None);

        return (code, terminal);
    }

    [Test]
    public async Task AMachineWithoutFfmpegIsASetupProblem()
    {
        (int code, BufferedTerminal terminal) = Run(new Deps { ToolchainPresent = false });

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);

        string errors = string.Join('\n', terminal.ErrorLines);
        await Assert.That(errors.Contains("missing ffmpeg", StringComparison.Ordinal)).IsTrue();
        await Assert.That(errors.Contains("lae setup", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AnUnreadableSourceFailsWithTheProbeReason()
    {
        (int code, BufferedTerminal terminal) = Run(new Deps { SourceReadable = false });

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("no such stream", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// A request that cannot be honoured is a usage error (2), not a failure (1):
    /// the distinction is what lets a script tell "I typed it wrong" from "this
    /// machine cannot do it".
    /// </summary>
    [Test]
    public async Task AMalformedSizeIsAUsageError()
    {
        (int code, BufferedTerminal terminal) =
            Run(new Deps(), new EncodeOverrides { Output = "/media/out.avi", Size = "320" });

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("WIDTHxHEIGHT", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task AnUnknownEncoderIsAUsageErrorThatNamesTheCommonOnes()
    {
        (int code, BufferedTerminal terminal) =
            Run(new Deps { Encoders = [] }, new EncodeOverrides { Output = "/media/out.avi", Encoder = "notacodec" });

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("no encoder named 'notacodec'", StringComparison.Ordinal))).IsTrue();
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("mpeg4", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task AFailedEncoderListIsAFailureNotAnEmptyMachine()
    {
        (int code, BufferedTerminal terminal) = Run(new Deps { EncoderListError = "ffmpeg did not answer within 60s" });

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("did not answer", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task WritingOverTheSourceIsAUsageError()
    {
        var deps = new Deps();
        (int code, BufferedTerminal terminal) =
            Run(deps, new EncodeOverrides { Output = Source });

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("the output path is the input path", StringComparison.Ordinal))).IsTrue();
        await Assert.That(deps.Ran).IsFalse();
    }

    [Test]
    public async Task ADryRunPrintsBothCommandsAndRunsNothing()
    {
        var deps = new Deps();
        (int code, BufferedTerminal terminal) = Run(deps, dryRun: true);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Ran).IsFalse();

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("decoder:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("encoder:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("pipe:1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("pipe:0", StringComparison.Ordinal)).IsTrue();

        // The notes are commentary and belong on stderr, so a dry run can be
        // redirected into a file and still be two command lines.
        await Assert.That(terminal.ErrorLines.All(l => !l.StartsWith("source", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ASuccessfulRunReportsWhatItWrote()
    {
        (int code, BufferedTerminal terminal) = Run(new Deps());

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("written", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("/media/out.avi", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("pacing", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A cancel is not a failure and must not be reported as one: it has to reach
    /// the top of the program, which turns it into exit 130 and one line.
    /// </summary>
    [Test]
    public async Task ACancelledRunPropagatesInsteadOfReturningACode()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var terminal = new BufferedTerminal();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
        {
            EncodeSession.Run(
                Source, new EncodeOverrides { Output = "/media/out.avi" },
                new SettingsDocument(), new Deps().Build(), terminal, dryRun: false, cancel.Token);
            return Task.CompletedTask;
        });
    }
}
