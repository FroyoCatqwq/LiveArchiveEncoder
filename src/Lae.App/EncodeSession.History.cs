using Lae.App.Ui;
using Lae.Core;
using Lae.Core.History;
using Lae.Encoding;

namespace Lae.App;

/// <summary>
/// Turning a finished run into a row.
///
/// <para>Kept apart from the run itself so the mapping is a pure function with a
/// name, and can be checked without a video: what goes into the record is a policy
/// decision (which numbers are facts, which are absent on a failure) and policy
/// that cannot be tested is policy that drifts.</para>
/// </summary>
public static partial class EncodeSession
{
    /// <summary>
    /// The record for one finished run.
    ///
    /// <para><paramref name="run"/> is null when the run never produced numbers --
    /// a cancel, or an encoder that died at the first frame. Every measurement is
    /// then absent rather than zero: a zero would read as "it ran and wrote
    /// nothing", which is a different and much more alarming claim.</para>
    /// </summary>
    public static RunRecord BuildRecord(
        EncodeRequest request,
        EncodePlan plan,
        LiveRun? run,
        string? presetId,
        string result,
        string? failure,
        DateTimeOffset startedAt,
        long? sourceBytes) => new()
        {
            StartedAt = startedAt,
            SourcePath = request.SourcePath,
            SourceBytes = sourceBytes,
            Encoder = plan.Encode.Encoder,
            Container = plan.Container,
            PresetId = presetId,
            Quality = plan.Encode.Quality,
            LimitKbps = plan.Encode.LimitKbps,
            OutputPath = run?.OutputPath ?? plan.DestinationPath,
            OutputBytes = run?.OutputBytes,
            SpanSeconds = run?.SpanSeconds ?? (plan.SpanSeconds > 0 ? plan.SpanSeconds : null),
            AverageKbps = run?.AverageKbps,
            Frames = run?.Pump.Frames,
            Repeated = run?.Pump.Repeated,
            Dropped = run?.Pump.Dropped,
            EffectiveFps = run?.Pump.EffectiveFps,
            FinalRung = run?.Pump.FinalRung,
            Result = result,
            Failure = failure,
        };

    /// <summary>
    /// Writes the record, and says so when it cannot.
    ///
    /// <para>Best effort by design: the encode is already finished, and a locked
    /// database is not a reason to report a failure. It is, however, a reason to
    /// print one -- silence here would be a program quietly losing its own
    /// history.</para>
    /// </summary>
    private static void Remember(IRunHistory history, RunRecord record, ITerminal terminal)
    {
        if (history.Append(record) is { } failure)
        {
            terminal.ErrorLine($"note: this run could not be recorded ({failure})");
        }
    }

    /// <summary>
    /// Size of the source, when it is a local file. Absent for a stream or a
    /// source behind a share that is already gone -- and absent is the truth there,
    /// not zero.
    /// </summary>
    private static long? SourceSize(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
