using Lae.Encoding;

namespace Lae.Tests.Encoding;

/// <summary>
/// The chain's failure policy, tested once -- because it is now written once.
///
/// <para>Every hop here is a fake: what is under test is not a transcode but the
/// guarantees the runner makes on behalf of any hop, including the plugin hops
/// M3 adds. Each of them was a line in <c>RecompressRun</c> before; they are the
/// lines that decide whether a failed pass costs one file or the recording.</para>
/// </summary>
public sealed class PostChainRunTests
{
    private sealed class TempFolder : IDisposable
    {
        internal TempFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "lae-postchain-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string File(string name) => System.IO.Path.Combine(Path, name);

        /// <summary>Every part product still on disk, whatever hop it belonged to.</summary>
        internal string[] PartFiles() => Directory.GetFiles(Path, "*.part.*");

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { /* a leftover is fine */ }
        }
    }

    /// <summary>A hop that writes its input's text plus one marker, so order is visible in the bytes.</summary>
    private static PostHop Hop(TempFolder folder, int number, string label, string name, string marker)
        => new()
        {
            Number = number,
            Label = label,
            OutputPath = folder.File(name),
            Produce = (input, part, _) =>
            {
                System.IO.File.WriteAllText(part, System.IO.File.ReadAllText(input) + marker);
                return null;
            },
        };

    [Test]
    public async Task HopsReadWhatThePreviousOneWrote()
    {
        using var folder = new TempFolder();
        string start = folder.File("live.avi");
        await System.IO.File.WriteAllTextAsync(start, "live");

        var seen = new List<string>();

        PostHop Recorded(int number, string name, string marker) => new()
        {
            Number = number,
            Label = $"pass {number} (mp4)",
            OutputPath = folder.File(name),
            Produce = (input, part, _) =>
            {
                seen.Add(System.IO.Path.GetFileName(input));
                System.IO.File.WriteAllText(part, System.IO.File.ReadAllText(input) + marker);
                return null;
            },
        };

        PostChainOutcome outcome = PostChainRun.Run(
            [Recorded(1, "live_web.mp4", "+1"), Recorded(2, "live_web2.webm", "+2")],
            start, warn: null, CancellationToken.None);

        await Assert.That(string.Join(" | ", seen)).IsEqualTo("live.avi | live_web.mp4");
        await Assert.That(string.Join(" | ", outcome.Written.Select(System.IO.Path.GetFileName)))
            .IsEqualTo("live_web.mp4 | live_web2.webm");
        await Assert.That(System.IO.Path.GetFileName(outcome.FinalPath)).IsEqualTo("live_web2.webm");
        await Assert.That(outcome.Failed).IsFalse();

        // The bytes prove the chain: each hop appended to what it was handed.
        await Assert.That(await System.IO.File.ReadAllTextAsync(outcome.FinalPath)).IsEqualTo("live+1+2");
    }

    [Test]
    public async Task AFailureStopsTheChainAndNamesTheHop()
    {
        using var folder = new TempFolder();
        string start = folder.File("live.avi");
        await System.IO.File.WriteAllTextAsync(start, "live");

        PostChainOutcome outcome = PostChainRun.Run(
            [
                Hop(folder, 1, "pass 1 (mp4)", "live_web.mp4", "+1"),
                new PostHop
                {
                    Number = 2,
                    Label = "pass 2 (webm)",
                    OutputPath = folder.File("live_web2.webm"),
                    Produce = (_, part, _) =>
                    {
                        System.IO.File.WriteAllText(part, "half a file");
                        return "FFmpeg exited with code 1";
                    },
                },
                Hop(folder, 3, "pass 3 (mkv)", "live_web3.mkv", "+3"),
            ],
            start, warn: null, CancellationToken.None);

        await Assert.That(outcome.Failed).IsTrue();
        await Assert.That(outcome.FailedStep).IsEqualTo(2);
        await Assert.That(outcome.Error).IsEqualTo("pass 2 (webm) failed: FFmpeg exited with code 1");

        // What finished stays; what failed leaves nothing behind.
        await Assert.That(string.Join(" | ", outcome.Written.Select(System.IO.Path.GetFileName)))
            .IsEqualTo("live_web.mp4");
        await Assert.That(System.IO.Path.GetFileName(outcome.FinalPath)).IsEqualTo("live_web.mp4");
        await Assert.That(System.IO.File.Exists(folder.File("live_web.mp4"))).IsTrue();
        await Assert.That(System.IO.File.Exists(folder.File("live_web2.webm"))).IsFalse();
        await Assert.That(System.IO.File.Exists(folder.File("live_web3.mkv"))).IsFalse();
    }

    /// <summary>
    /// The half-written part product goes too: a truncated file wearing a
    /// plausible name is exactly what the next pass, or the user, would trust.
    /// </summary>
    [Test]
    public async Task AFailedHopLeavesNoTemporaryBehind()
    {
        using var folder = new TempFolder();
        string start = folder.File("live.avi");
        await System.IO.File.WriteAllTextAsync(start, "live");

        PostChainOutcome outcome = PostChainRun.Run(
            [
                new PostHop
                {
                    Number = 1,
                    Label = "plugin lae-damage (between-passes)",
                    OutputPath = folder.File("live_damaged.mp4"),
                    Produce = (_, part, _) =>
                    {
                        System.IO.File.WriteAllText(part, "half");
                        return "the plugin broke its handshake";
                    },
                },
            ],
            start, warn: null, CancellationToken.None);

        await Assert.That(outcome.Failed).IsTrue();
        await Assert.That(folder.PartFiles().Length).IsEqualTo(0);
        await Assert.That(outcome.FinalPath).IsEqualTo(start);
    }

    /// <summary>
    /// A hop that says it succeeded but wrote nothing is not promoted, and the
    /// sentence names the hop rather than a missing path.
    /// </summary>
    [Test]
    public async Task AHopThatWritesNothingIsNotPromoted()
    {
        using var folder = new TempFolder();
        string start = folder.File("live.avi");
        await System.IO.File.WriteAllTextAsync(start, "live");

        PostChainOutcome outcome = PostChainRun.Run(
            [
                new PostHop
                {
                    Number = 1,
                    Label = "pass 1 (mp4)",
                    OutputPath = folder.File("live_web.mp4"),
                    Produce = (_, _, _) => null,
                },
            ],
            start, warn: null, CancellationToken.None);

        await Assert.That(outcome.Failed).IsTrue();
        await Assert.That(outcome.Error!.StartsWith("pass 1 (mp4) left nothing to keep:", StringComparison.Ordinal))
            .IsTrue();
        await Assert.That(System.IO.File.Exists(folder.File("live_web.mp4"))).IsFalse();
    }

    /// <summary>
    /// An interrupt is not a hop failure: the part product goes, the exception
    /// travels so the process can answer with 130, and the recording is intact.
    /// </summary>
    [Test]
    public async Task CancellationDiscardsThePartAndTravelsOn()
    {
        using var folder = new TempFolder();
        string start = folder.File("live.avi");
        await System.IO.File.WriteAllTextAsync(start, "live");

        using var stop = new CancellationTokenSource();

        await Assert.That(() =>
        {
            PostChainRun.Run(
                [
                    Hop(folder, 1, "pass 1 (mp4)", "live_web.mp4", "+1"),
                    new PostHop
                    {
                        Number = 2,
                        Label = "pass 2 (webm)",
                        OutputPath = folder.File("live_web2.webm"),
                        Produce = (_, part, _) =>
                        {
                            System.IO.File.WriteAllText(part, "half");
                            stop.Cancel();
                            stop.Token.ThrowIfCancellationRequested();
                            return null;
                        },
                    },
                ],
                start, warn: null, stop.Token);
        }).Throws<OperationCanceledException>();

        await Assert.That(folder.PartFiles().Length).IsEqualTo(0);
        await Assert.That(System.IO.File.Exists(folder.File("live_web.mp4"))).IsTrue();
        await Assert.That(System.IO.File.Exists(start)).IsTrue();
    }

    /// <summary>
    /// A part product left by a run that was killed from outside is cleared
    /// before the hop starts, and the fact that it could not be removed is a
    /// sentence rather than a silent overwrite.
    /// </summary>
    [Test]
    public async Task ALeftoverIsClearedBeforeTheHopRuns()
    {
        using var folder = new TempFolder();
        string start = folder.File("live.avi");
        await System.IO.File.WriteAllTextAsync(start, "live");
        await System.IO.File.WriteAllTextAsync(folder.File("live_web.part.mp4"), "stale");

        var warnings = new List<string>();

        PostChainOutcome outcome = PostChainRun.Run(
            [Hop(folder, 1, "pass 1 (mp4)", "live_web.mp4", "+1")],
            start, warnings.Add, CancellationToken.None);

        await Assert.That(outcome.Failed).IsFalse();
        await Assert.That(warnings).IsEmpty();
        await Assert.That(await System.IO.File.ReadAllTextAsync(folder.File("live_web.mp4")))
            .IsEqualTo("live+1").Because("the stale part was discarded, not appended to");
    }

    /// <summary>
    /// No hops is a legal chain: <c>--recompress-only</c> with nothing to do.
    /// It must hand back its input rather than an empty path.
    /// </summary>
    [Test]
    public async Task AnEmptyChainHandsBackItsInput()
    {
        using var folder = new TempFolder();
        string start = folder.File("live.avi");

        PostChainOutcome outcome = PostChainRun.Run([], start, warn: null, CancellationToken.None);

        await Assert.That(outcome.Failed).IsFalse();
        await Assert.That(outcome.FinalPath).IsEqualTo(start);
        await Assert.That(outcome.Written).IsEmpty();
    }
}
