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

        /// <summary>What the post chain should answer; the default pretends every hop succeeded.</summary>
        public Func<RecompressPlan, RecompressOutcome>? OnRecompress { get; init; }

        /// <summary>The chain the session handed over, so a test can read what it planned.</summary>
        public RecompressPlan? Chain { get; private set; }

        public TimeSpan ChainBudget { get; private set; }

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
            RunRecompress = (plan, _, budget, _, token) =>
            {
                Chain = plan;
                ChainBudget = budget;
                token.ThrowIfCancellationRequested();

                return OnRecompress?.Invoke(plan) ?? new RecompressOutcome
                {
                    FinalPath = plan.FinalPath,
                    Written = [.. plan.Steps.Select(s => s.OutputPath)],
                };
            },
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
    /// The chain is part of the plan or it is not the plan. A dry run that shows
    /// the capture and hides three transcodes would be pasted into a shell by
    /// someone who then wonders where the files came from.
    /// </summary>
    [Test]
    public async Task ADryRunShowsEveryHopOfTheChain()
    {
        var deps = new Deps();
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30", "-c:v libvpx-vp9 -crf 34"],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides, dryRun: true);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Ran).IsFalse();

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("recompress:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("out_web.mp4", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("out_web2.webm", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("-nostdin", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// The container comes from the codec unless the user named one, and the
    /// second hop reads the first hop's product rather than the recording.
    /// </summary>
    [Test]
    public async Task AChainIsNumberedFromTheRecordingAndFeedsItself()
    {
        var deps = new Deps();
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30", "-c:v libvpx-vp9 -crf 34"],
        };

        (int code, _) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Chain).IsNotNull();

        // Compared by name, not by full path: the plan keeps the path it was
        // handed, and every platform spells a rooted path slightly differently.
        RecompressPlan chain = deps.Chain!;
        await Assert.That(chain.Steps.Count).IsEqualTo(2);
        await Assert.That(Path.GetFileName(chain.Steps[0].InputPath)).IsEqualTo("out.avi");
        await Assert.That(Path.GetFileName(chain.Steps[0].OutputPath)).IsEqualTo("out_web.mp4");
        await Assert.That(Path.GetFileName(chain.Steps[1].InputPath)).IsEqualTo("out_web.mp4");
        await Assert.That(Path.GetFileName(chain.Steps[1].OutputPath)).IsEqualTo("out_web2.webm");
        await Assert.That(chain.Steps[1].Container).IsEqualTo("webm");
    }

    [Test]
    public async Task AFinishedChainReportsEveryProduct()
    {
        var deps = new Deps();
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30"],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("recompress out_web.mp4", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A hop that fails does not take the recording with it, but it is still a
    /// failure: a script that reads exit 0 must not go on to publish a chain that
    /// stopped at its first hop.
    /// </summary>
    [Test]
    public async Task AFailedHopFailsTheRunAndSaysTheRecordingSurvived()
    {
        var deps = new Deps
        {
            OnRecompress = plan => new RecompressOutcome
            {
                FinalPath = plan.LivePath,
                Written = [],
                FailedStep = 1,
                Error = "pass 1 (mp4) failed: FFmpeg exited with code 1; Unknown encoder 'libx264'",
            },
        };

        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30"],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);
        await Assert.That(deps.Ran).IsTrue();

        string errors = string.Join('\n', terminal.ErrorLines);
        await Assert.That(errors.Contains("Unknown encoder", StringComparison.Ordinal)).IsTrue();
        await Assert.That(errors.Contains("the recording is untouched", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// The stored chain is not this run's business. v7.9.8.6 shipped a release
    /// that merged it in, and every plain encode silently recompressed itself.
    /// </summary>
    [Test]
    public async Task TheStoredChainIsNeverRunUnasked()
    {
        var settings = new SettingsDocument
        {
            Defaults = new EncodeDefaults
            {
                Recompress = new RecompressChain
                {
                    Enabled = true,
                    Passes = [new RecompressPass("-c:v libx264 -crf 30", "mp4")],
                },
            },
        };

        var deps = new Deps();
        (int code, _) = Run(deps, settings: settings);

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(deps.Chain).IsNull();
    }

    [Test]
    public async Task AContainerWithoutAPassIsAUsageError()
    {
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-c:v libx264 -crf 30"],
            RecompressContainers = ["mp4", "webm"],
        };

        (int code, BufferedTerminal terminal) = Run(new Deps(), overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("nothing for the extra one", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// A chain that cannot be honoured is a usage error before anything is
    /// spawned: "-i" in a fragment would add a second input, and the refusal has
    /// to cost a sentence rather than a full capture.
    /// </summary>
    [Test]
    public async Task AChainThatCannotRunFailsBeforeTheCapture()
    {
        var deps = new Deps();
        var overrides = new EncodeOverrides
        {
            Output = "/media/out.avi",
            RecompressArgs = ["-i /media/other.mkv -c:v libx264"],
        };

        (int code, BufferedTerminal terminal) = Run(deps, overrides);

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(deps.Ran).IsFalse();
        await Assert.That(terminal.ErrorLines.Any(l => l.Contains("'-i' is not allowed", StringComparison.Ordinal))).IsTrue();
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
