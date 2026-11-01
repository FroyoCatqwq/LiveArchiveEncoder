using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Lae.App.Ui;

/// <summary>One line of the L2 event stream.</summary>
internal sealed record JsonEvent
{
    [JsonPropertyName("event")]
    public required string Kind { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("done")]
    public long? Done { get; init; }

    [JsonPropertyName("total")]
    public long? Total { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<string>? Artifacts { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long? ElapsedMs { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonEvent))]
internal sealed partial class JsonEventContext : JsonSerializerContext;

/// <summary>
/// The second renderer of the event stream: one JSON object per line on stdout.
///
/// <para>This is what the event sink was built for. The alternative -- a second set
/// of print calls next to the human ones -- is how a report and a log end up
/// disagreeing about how many frames were written, and the disagreement is only
/// visible to whoever compares them afterwards.</para>
///
/// <para>Progress is throttled to four lines a second. A 13 fps encode emits a
/// progress event per slot, and 390 lines of JSON is not a stream a reader can
/// follow; the terminal renderer throttles for the same reason.</para>
/// </summary>
internal sealed class JsonEventSink(ITerminal terminal) : IEventSink
{
    private static readonly long IntervalTicks = Stopwatch.Frequency / 4;

    private long _lastProgress;

    public void Stage(string name)
        => Emit(new JsonEvent { Kind = "stage", Name = name });

    public void Progress(long done, long? total)
    {
        long now = Stopwatch.GetTimestamp();

        // The last slot always goes out, so the final number is never the one that
        // got throttled away.
        bool last = total is { } bound && done >= bound;
        if (!last && now - _lastProgress < IntervalTicks) return;

        _lastProgress = now;
        Emit(new JsonEvent { Kind = "progress", Done = done, Total = total });
    }

    public void Warn(string message)
        => Emit(new JsonEvent { Kind = "warn", Message = message });

    public void Done(string summary, IReadOnlyList<string> artifacts, TimeSpan elapsed)
        => Emit(new JsonEvent
        {
            Kind = "done",
            Summary = summary,
            Artifacts = artifacts,
            ElapsedMs = (long)elapsed.TotalMilliseconds,
        });

    private void Emit(JsonEvent value)
        => terminal.Line(System.Text.Json.JsonSerializer.Serialize(value, JsonEventContext.Default.JsonEvent));
}

/// <summary>
/// Sends stdout to stderr, so a machine-readable stream can have stdout to itself.
///
/// <para>Only for <c>--json-events</c>: the report's own lines ("written:
/// clip_web.mp4", the summary) would otherwise land between two JSON objects and
/// make the stream unparseable. Nothing is lost -- stderr still shows a human
/// everything -- but a reader of stdout sees only JSON.</para>
/// </summary>
internal sealed class MergedIntoError(ITerminal inner) : ITerminal
{
    public void Line(string text = "") => inner.ErrorLine(text);

    public void ErrorLine(string text) => inner.ErrorLine(text);

    public void Inline(string text) => inner.Inline(text);

    public bool IsErrorInteractive => inner.IsErrorInteractive;
}
