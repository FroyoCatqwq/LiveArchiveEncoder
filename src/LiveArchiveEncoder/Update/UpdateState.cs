using System.Text.Json;
using System.Text.Json.Serialization;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.Update;

/// <summary>
/// What the last update check saw, kept beside the settings file.
///
/// <para>Deliberately a separate file from <c>config.json</c>. That file is a
/// snapshot of a run: <see cref="ConfigStore.Capture"/> rebuilds it from
/// <see cref="Config"/>, so any member that is not part of a run (this
/// bookkeeping is not) would be silently erased every time the shell saves.</para>
/// </summary>
public sealed class UpdateState
{
    /// <summary>The newest tag the last successful check saw, e.g. <c>v7.9.8.6</c>.</summary>
    public string? Seen { get; set; }

    /// <summary>When that check ran. Null means never.</summary>
    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>How the check ended, for the menu line: <c>ok</c> or <c>offline</c>.</summary>
    public string? Result { get; set; }

    /// <summary>The file, under the same directory as <c>config.json</c>.</summary>
    public static string DefaultPath => Path.Combine(ConfigStore.RootDirectory, "update-state.json");

    public static UpdateState Load()
    {
        try
        {
            if (!File.Exists(DefaultPath)) return new UpdateState();
            UpdateState? state = JsonSerializer.Deserialize(
                File.ReadAllText(DefaultPath), UpdateStateJson.Default.UpdateState);
            return state ?? new UpdateState();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or JsonException or NotSupportedException)
        {
            // Cache only: a damaged file means "check again", never a crash.
            return new UpdateState();
        }
    }

    public static void Save(UpdateState state)
    {
        try
        {
            string? dir = Path.GetDirectoryName(DefaultPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(DefaultPath,
                JsonSerializer.Serialize(state, UpdateStateJson.Default.UpdateState));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException)
        {
            // Same reasoning as the NNEDI3 record: bookkeeping must not fail a run.
        }
    }

    /// <summary>
    /// True when a start-up check is worth another request. A successful check
    /// lasts a day; a failed one is retried on the next start, because the usual
    /// cause is a network that came back.
    /// </summary>
    public bool IsFresh(TimeSpan ttl) =>
        CheckedAt is { } at
        && string.Equals(Result, "ok", StringComparison.Ordinal)
        && DateTimeOffset.Now - at < ttl;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateState))]
internal sealed partial class UpdateStateJson : JsonSerializerContext;
