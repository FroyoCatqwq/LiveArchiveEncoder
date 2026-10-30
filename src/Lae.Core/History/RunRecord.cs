namespace Lae.Core.History;

/// <summary>
/// One finished run, as a fact.
///
/// <para>Deliberately not a copy of the request. A request is what the user
/// intended; this is what happened -- how long it ran, how large the file came
/// out, which rung the ladder ended on, and whether it failed. "I last encoded
/// this episode with these settings and it came out at 214 kbps" is a question
/// the program could not answer at all before this existed: <c>lae encode</c>
/// printed a line and forgot everything.</para>
///
/// <para>Every measurement is nullable because a failed run has none of them.
/// Recording zero instead would make the history lie in the one case where it is
/// most useful -- what was different about the run that died.</para>
/// </summary>
public sealed record RunRecord
{
    /// <summary>Assigned by the store; zero until it has been written.</summary>
    public long Id { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>Bytes of the source, so "the same episode in better quality" is comparable.</summary>
    public long? SourceBytes { get; init; }

    public string? Encoder { get; init; }

    public string? Container { get; init; }

    /// <summary>The preset this run came from, when it came from one.</summary>
    public string? PresetId { get; init; }

    public int? Quality { get; init; }

    public double? LimitKbps { get; init; }

    public string? OutputPath { get; init; }

    public long? OutputBytes { get; init; }

    /// <summary>How much programme the run covered, as opposed to how long it took.</summary>
    public double? SpanSeconds { get; init; }

    public double? AverageKbps { get; init; }

    public long? Frames { get; init; }

    public long? Repeated { get; init; }

    public long? Dropped { get; init; }

    public double? EffectiveFps { get; init; }

    public int? FinalRung { get; init; }

    /// <summary>One of <see cref="RunOutcome"/>.</summary>
    public required string Result { get; init; }

    /// <summary>Why it did not finish, in the words the user saw.</summary>
    public string? Failure { get; init; }
}

/// <summary>The values <see cref="RunRecord.Result"/> can take.</summary>
public static class RunOutcome
{
    public const string Ok = "ok";

    /// <summary>Started and did not finish: the encoder died, the disk filled, the source ended early.</summary>
    public const string Failed = "failed";

    /// <summary>Stopped on Ctrl+C. Not a failure, and the history must not call it one.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Where finished runs are remembered.
///
/// <para>In Core without an implementation, like <c>ISettingsStore</c>: the
/// database lives in <c>Lae.Media</c>, and every command can then be tested
/// against a list instead of against SQLite.</para>
/// </summary>
public interface IRunHistory
{
    /// <summary>
    /// Records a run. Returns null, or why it could not be recorded.
    ///
    /// <para>Never throws. Remembering is a courtesy; a full disk or a locked
    /// database must not turn a finished encode into a failed one.</para>
    /// </summary>
    string? Append(RunRecord record);

    /// <summary>The most recent runs, newest first.</summary>
    IReadOnlyList<RunRecord> Recent(int limit);

    long Count();

    /// <summary>Where the history lives, for <c>lae history path</c>.</summary>
    string Location { get; }

    /// <summary>
    /// Why the store cannot answer, or null when it can.
    ///
    /// <para>Exists because "no rows" and "cannot read any rows" look identical in
    /// a list, and they are not the same answer: one means the archive is new, the
    /// other means the database could not be opened. The store records the reason
    /// on its first failure and this is how a command can say it out loud instead
    /// of printing an empty table that looks like truth.</para>
    /// </summary>
    string? Problem { get; }
}

/// <summary>
/// Keeps nothing. For tests, and for a dry run: a run that writes no file must not
/// create a database either.
/// </summary>
public sealed class NullRunHistory : IRunHistory
{
    public static NullRunHistory Instance { get; } = new();

    public string Location => "(none)";

    public string? Problem => null;

    public string? Append(RunRecord record) => null;

    public IReadOnlyList<RunRecord> Recent(int limit) => [];

    public long Count() => 0;
}
