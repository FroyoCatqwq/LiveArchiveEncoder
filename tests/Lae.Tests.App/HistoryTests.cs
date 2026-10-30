using Lae.App;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.History;
using Lae.Core.Localization;
using Lae.Core.Settings;
using Lae.Media.Localization;

namespace Lae.Tests.App;

/// <summary>
/// Recording a run, and reading the record back.
///
/// <para>The mapping is the interesting part: which numbers are facts about the
/// run and which must stay absent when there were none. A history that fills a
/// failed run with zeroes is worse than no history, because it reads like a
/// successful encode of nothing.</para>
/// </summary>
public sealed class HistoryTests
{
    /// <summary>A history that keeps rows in memory, newest last.</summary>
    private sealed class Fake : IRunHistory
    {
        public List<RunRecord> Rows { get; } = [];

        public string Location => "(fake)";

        /// <summary>Set to pretend the store cannot answer at all.</summary>
        public string? Failure { get; init; }

        public string? Problem => Failure;

        public string? Append(RunRecord record)
        {
            Rows.Add(record with { Id = Rows.Count + 1 });
            return null;
        }

        public IReadOnlyList<RunRecord> Recent(int limit) => [.. Rows.AsEnumerable().Reverse().Take(limit)];

        public long Count() => Rows.Count;
    }

    private static EncodeRequest Request() => new()
    {
        SourcePath = "/media/tape.mkv",
        DestinationPath = "/media/tape_live.avi",
        Encode = new EncodeDefaults { Encoder = "mpeg4", Container = "avi", Quality = 6, LimitKbps = 200 },
    };

    private static EncodePlan Plan() => new()
    {
        TransportPixelFormat = "yuv420p",
        Width = 320,
        Height = 240,
        Fps = 15,
        HasAudio = false,
        Container = "avi",
        Muxer = "avi",
        DestinationPath = "/media/tape_live.avi",
        SpanSeconds = 1420.5,
        Encode = new EncodeDefaults { Encoder = "mpeg4", Container = "avi", Quality = 6, LimitKbps = 200 },
        DecoderArgs = [],
        EncoderArgs = [],
    };

    /// <summary>
    /// A run that produced no numbers still produces a record: what was asked for,
    /// the preset it came from, and how it ended. The measurements stay null.
    /// </summary>
    [Test]
    public async Task AFailedRunRecordsWhatWasAskedAndHowItEnded()
    {
        var started = new DateTimeOffset(2026, 10, 31, 1, 2, 3, TimeSpan.Zero);

        RunRecord record = EncodeSession.BuildRecord(
            Request(), Plan(), run: null, presetId: "portal-2006",
            RunOutcome.Failed, "the encoder exited with 1", started, sourceBytes: 1234);

        await Assert.That(record.StartedAt).IsEqualTo(started);
        await Assert.That(record.SourcePath).IsEqualTo("/media/tape.mkv");
        await Assert.That(record.SourceBytes).IsEqualTo(1234L);
        await Assert.That(record.Encoder).IsEqualTo("mpeg4");
        await Assert.That(record.Container).IsEqualTo("avi");
        await Assert.That(record.PresetId).IsEqualTo("portal-2006");
        await Assert.That(record.Result).IsEqualTo(RunOutcome.Failed);
        await Assert.That(record.Failure).IsEqualTo("the encoder exited with 1");

        // No numbers, because there were none.
        await Assert.That(record.Frames).IsNull();
        await Assert.That(record.OutputBytes).IsNull();
        await Assert.That(record.AverageKbps).IsNull();

        // The span survives: it was decided before the run started.
        await Assert.That(record.SpanSeconds).IsEqualTo(1420.5);
    }

    private static AppHost Host(IRunHistory history, BufferedTerminal terminal, IMessages? messages = null)
        => new()
        {
            Settings = new MemorySettingsStore(),
            History = history,
            Terminal = terminal,
            Messages = messages ?? AppHost.InMemory().Messages,
        };

    [Test]
    public async Task TheListPrintsTheRunsNewestFirstWithATotal()
    {
        var history = new Fake();
        var terminal = new BufferedTerminal();

        history.Append(new RunRecord
        {
            StartedAt = new DateTimeOffset(2026, 10, 30, 20, 0, 0, TimeSpan.Zero),
            SourcePath = "/media/first.mkv",
            Encoder = "mpeg4",
            Container = "avi",
            AverageKbps = 214,
            OutputBytes = 3_400_000,
            Result = RunOutcome.Ok,
        });
        history.Append(new RunRecord
        {
            StartedAt = new DateTimeOffset(2026, 10, 30, 21, 0, 0, TimeSpan.Zero),
            SourcePath = "/media/second.mkv",
            Encoder = "libx264",
            Container = "mp4",
            Result = RunOutcome.Failed,
            Failure = "no such file",
        });

        int code = CommandTree.Build(Host(history, terminal)).Parse(["history", "list"]).Invoke();

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);

        string output = string.Join('\n', terminal.OutputLines);
        await Assert.That(output.Contains("second.mkv", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("first.mkv", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("failed", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("2 run(s) recorded", StringComparison.Ordinal)).IsTrue();

        // Newest first: the second row was appended last and must be printed first.
        await Assert.That(output.IndexOf("second.mkv", StringComparison.Ordinal))
            .IsLessThan(output.IndexOf("first.mkv", StringComparison.Ordinal));
    }

    [Test]
    public async Task AnEmptyHistorySaysSoInsteadOfPrintingAHeader()
    {
        var terminal = new BufferedTerminal();

        int code = CommandTree.Build(Host(new Fake(), terminal)).Parse(["history", "list"]).Invoke();

        await Assert.That(code).IsEqualTo(ExitCodes.Ok);
        await Assert.That(string.Join('\n', terminal.OutputLines))
            .Contains("No runs recorded yet.");
    }

    [Test]
    public async Task ALimitBelowOneIsRefused()
    {
        var terminal = new BufferedTerminal();

        int code = CommandTree.Build(Host(new Fake(), terminal))
            .Parse(["history", "list", "--limit", "0"])
            .Invoke();

        await Assert.That(code).IsEqualTo(ExitCodes.Usage);
        await Assert.That(string.Join('\n', terminal.ErrorLines)).Contains("--limit");
    }

    /// <summary>
    /// A store that cannot answer says why, and does not pretend the archive is
    /// empty.
    ///
    /// <para>This is the difference the <c>Problem</c> member exists for: an empty
    /// table means "you have never encoded anything", a broken database means
    /// "there are rows and I could not read them". Printing the first when the
    /// second is true is a lie the user would act on.</para>
    /// </summary>
    [Test]
    public async Task AnUnreadableHistorySaysWhyInsteadOfAnEmptyList()
    {
        var terminal = new BufferedTerminal();

        int code = CommandTree.Build(Host(new Fake { Failure = "no such table: runs" }, terminal))
            .Parse(["history", "list"])
            .Invoke();

        await Assert.That(code).IsEqualTo(ExitCodes.Failed);

        string errors = string.Join('\n', terminal.ErrorLines);
        await Assert.That(errors.Contains("no such table: runs", StringComparison.Ordinal)).IsTrue();
        await Assert.That(string.Join('\n', terminal.OutputLines)
            .Contains("No runs recorded yet.", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// The switch itself: the same command, the same empty history, two languages.
    /// This is the test that would fail if a sentence were ever built by
    /// concatenation instead of coming from the resource file.
    /// </summary>
    [Test]
    public async Task TheSameCommandSpeaksTheLanguageItWasGiven()
    {
        var english = new BufferedTerminal();
        var chinese = new BufferedTerminal();

        CommandTree.Build(Host(new Fake(), english, I18NextMessages.Create("en")))
            .Parse(["history", "list"]).Invoke();
        CommandTree.Build(Host(new Fake(), chinese, I18NextMessages.Create("zh")))
            .Parse(["history", "list"]).Invoke();

        await Assert.That(string.Join('\n', english.OutputLines)).Contains("No runs recorded yet.");
        await Assert.That(string.Join('\n', chinese.OutputLines)).Contains("还没有任何运行记录。");
        await Assert.That(string.Join('\n', chinese.OutputLines)).DoesNotContain("No runs recorded yet.");
    }
}
