using Lae.Core.History;
using Lae.Media.History;

namespace Lae.Tests.Media;

/// <summary>
/// The run history on disk.
///
/// <para>What matters here is that a row means what it says: absent numbers stay
/// absent, the newest run is first, and a database that cannot be written is a
/// sentence rather than an exception out of a finished encode.</para>
/// </summary>
public sealed class SqliteHistoryStoreTests
{
    private static string NewFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "lae-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static RunRecord Record(string source = "tape.mkv", string result = RunOutcome.Ok) => new()
    {
        StartedAt = new DateTimeOffset(2026, 10, 31, 1, 2, 3, TimeSpan.Zero),
        SourcePath = source,
        SourceBytes = 512L * 1024 * 1024,
        Encoder = "mpeg4",
        Container = "avi",
        PresetId = "portal-2006",
        Quality = 6,
        LimitKbps = 200,
        OutputPath = "tape_live.avi",
        OutputBytes = 3_400_000,
        SpanSeconds = 1420.5,
        AverageKbps = 214.3,
        Frames = 21_300,
        Repeated = 12,
        Dropped = 4,
        EffectiveFps = 14.98,
        FinalRung = 1,
        Result = result,
    };

    [Test]
    public async Task AFinishedRunComesBackWithEveryNumberIntact()
    {
        string folder = NewFolder();
        try
        {
            var history = new SqliteHistoryStore(Path.Combine(folder, "history.db"));

            string? failure = history.Append(Record());
            await Assert.That(failure).IsNull();

            IReadOnlyList<RunRecord> runs = history.Recent(10);
            await Assert.That(runs.Count).IsEqualTo(1);

            RunRecord run = runs[0];
            await Assert.That(run.Id).IsGreaterThan(0L);
            await Assert.That(run.SourcePath).IsEqualTo("tape.mkv");
            await Assert.That(run.SourceBytes).IsEqualTo(512L * 1024 * 1024);
            await Assert.That(run.Encoder).IsEqualTo("mpeg4");
            await Assert.That(run.Container).IsEqualTo("avi");
            await Assert.That(run.PresetId).IsEqualTo("portal-2006");
            await Assert.That(run.Quality).IsEqualTo(6);
            await Assert.That(run.AverageKbps!.Value).IsBetween(214.2, 214.4);
            await Assert.That(run.Frames).IsEqualTo(21_300L);
            await Assert.That(run.FinalRung).IsEqualTo(1);
            await Assert.That(run.Result).IsEqualTo(RunOutcome.Ok);
            await Assert.That(run.StartedAt.ToUniversalTime())
                .IsEqualTo(new DateTimeOffset(2026, 10, 31, 1, 2, 3, TimeSpan.Zero));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A failure has no measurements, and the history must not invent zeroes: a
    /// row that says "0 kbps over 0 seconds" reads as a successful empty encode,
    /// which is a much more alarming thing than "it died".
    /// </summary>
    [Test]
    public async Task AFailedRunKeepsItsAbsentMeasurementsAbsent()
    {
        string folder = NewFolder();
        try
        {
            var history = new SqliteHistoryStore(Path.Combine(folder, "history.db"));

            history.Append(new RunRecord
            {
                StartedAt = DateTimeOffset.UtcNow,
                SourcePath = "tape.mkv",
                Encoder = "mpeg4",
                Result = RunOutcome.Failed,
                Failure = "the encoder exited with 1",
            });

            RunRecord run = history.Recent(1)[0];
            await Assert.That(run.Result).IsEqualTo(RunOutcome.Failed);
            await Assert.That(run.Failure).IsEqualTo("the encoder exited with 1");
            await Assert.That(run.OutputBytes).IsNull();
            await Assert.That(run.AverageKbps).IsNull();
            await Assert.That(run.Frames).IsNull();
            await Assert.That(run.FinalRung).IsNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task TheNewestRunComesFirstAndTheLimitIsHonoured()
    {
        string folder = NewFolder();
        try
        {
            var history = new SqliteHistoryStore(Path.Combine(folder, "history.db"));
            for (int i = 0; i < 3; i++) history.Append(Record($"tape{i}.mkv"));

            await Assert.That(history.Count()).IsEqualTo(3L);

            IReadOnlyList<RunRecord> two = history.Recent(2);
            await Assert.That(two.Count).IsEqualTo(2);
            await Assert.That(two[0].SourcePath).IsEqualTo("tape2.mkv");
            await Assert.That(two[1].SourcePath).IsEqualTo("tape1.mkv");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Reading must not create the file. A machine that has never encoded anything
    /// should not grow a database just because someone asked what is in it.
    /// </summary>
    [Test]
    public async Task ReadingAnEmptyHistoryCreatesNothing()
    {
        string folder = NewFolder();
        try
        {
            string file = Path.Combine(folder, "history.db");
            var history = new SqliteHistoryStore(file);

            await Assert.That(history.Count()).IsEqualTo(0L);
            await Assert.That(history.Recent(10).Count).IsEqualTo(0);
            await Assert.That(File.Exists(file)).IsFalse();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The one case that must not throw: a finished encode reporting a failure to
    /// remember it. Here the path is unreachable because a file sits where the
    /// folder would go.
    /// </summary>
    [Test]
    public async Task AnUnwritableLocationIsReportedAndNotThrown()
    {
        string folder = NewFolder();
        try
        {
            string blocker = Path.Combine(folder, "blocker");
            await File.WriteAllTextAsync(blocker, "not a folder");

            var history = new SqliteHistoryStore(Path.Combine(blocker, "history.db"));
            string? failure = history.Append(Record());

            await Assert.That(failure).IsNotNull();
            await Assert.That(history.Recent(5).Count).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
