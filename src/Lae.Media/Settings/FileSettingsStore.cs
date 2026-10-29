using System.Text.Json;
using Lae.Core.Settings;

namespace Lae.Media.Settings;

/// <summary>
/// Where the settings live on this machine.
///
/// <para>The program's own directory is not an option any more. v8 publishes as
/// a self-contained single file, and that form unpacks itself into a temporary
/// folder at startup -- so <c>AppContext.BaseDirectory</c> points at the
/// extraction directory and a config written there is gone on the next run
/// (v7's fallback did exactly that). <c>LAE_CONFIG_DIR</c> is how a portable
/// install says "put it next to me" instead.</para>
/// </summary>
public sealed record SettingsLocation
{
    /// <summary>Folder holding <c>settings.json</c>, the v7 file and the backups.</summary>
    public required string Directory { get; init; }

    /// <summary>The v8 file.</summary>
    public string File => Path.Combine(Directory, "settings.json");

    /// <summary>The v7 file, still read for migration and never written by v8.</summary>
    public string LegacyFile => Path.Combine(Directory, "config.json");

    /// <summary>Where the pre-migration copy is kept.</summary>
    public string LegacyBackup => Path.Combine(Directory, "config.v7.backup.json");

    /// <summary>
    /// Resolves the folder: <c>LAE_CONFIG_DIR</c> when set, otherwise the
    /// per-user application data folder.
    /// </summary>
    public static SettingsLocation Resolve()
    {
        string? over = Environment.GetEnvironmentVariable("LAE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(over)) return For(over);

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            // No profile at all: a service account or a stripped container. The
            // temporary folder is the only writable place left, and saying so is
            // better than writing somewhere the next run cannot find.
            appData = Path.Combine(Path.GetTempPath(), "LiveArchiveEncoder");
        }

        return For(Path.Combine(appData, "LiveArchiveEncoder"));
    }

    public static SettingsLocation For(string directory) => new() { Directory = directory };
}

/// <summary>
/// The settings file on disk.
///
/// <para>Reads <c>settings.json</c>. A v7 <c>config.json</c> sitting beside it is
/// converted once, on first read, and left untouched: v7 may still be the only
/// build the user has, and deleting the file the other version needs is not a
/// migration, it is a break. A copy lands in <c>config.v7.backup.json</c> so the
/// conversion can be inspected after the fact.</para>
/// </summary>
public sealed class FileSettingsStore(SettingsLocation location) : ISettingsStore
{
    private readonly SettingsLocation _location = location;

    public string Location => _location.File;

    /// <summary>What the last <see cref="Load"/> did to the file, for the caller to report.</summary>
    public IReadOnlyList<string> MigrationNotes { get; private set; } = [];

    public SettingsDocument Load()
    {
        MigrationNotes = [];

        if (File.Exists(_location.File) && TryRead(_location.File) is { } current)
        {
            return current.Normalised();
        }

        if (File.Exists(_location.File))
        {
            // Present but unreadable. Refuse to overwrite it: the content may be
            // the only copy of settings someone spent an hour on, and "start from
            // defaults" would destroy it on the next save.
            MigrationNotes = [$"the settings file could not be parsed and was left untouched: {_location.File}"];
            return new SettingsDocument();
        }

        if (File.Exists(_location.LegacyFile) && TryReadLegacy() is { } migrated)
        {
            return migrated;
        }

        return new SettingsDocument();
    }

    private static SettingsDocument? TryRead(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsDocument);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private SettingsDocument? TryReadLegacy()
    {
        try
        {
            string json = File.ReadAllText(_location.LegacyFile);
            if (!LegacyV7Converter.LooksLikeV7(json)) return null;
            if (LegacyV7Converter.Parse(json) is not { } old) return null;

            MigrationOutcome outcome = LegacyV7Converter.Convert(old);

            // Keep a copy before writing anything, so the conversion can be
            // checked even if the new file is later edited by hand.
            Directory.CreateDirectory(_location.Directory);
            if (!File.Exists(_location.LegacyBackup)) File.Copy(_location.LegacyFile, _location.LegacyBackup);

            var notes = new List<string>(outcome.Notes)
            {
                $"migrated the v7 settings from {_location.LegacyFile} (left in place)",
                $"the pre-migration copy is {_location.LegacyBackup}",
            };

            if (TrySave(outcome.Document) is { } failure)
            {
                notes.Add($"the migrated settings could not be written ({failure}); they apply to this run only");
            }
            else
            {
                notes.Add($"wrote {_location.File}");
            }

            MigrationNotes = notes;
            return outcome.Document;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or JsonException)
        {
            MigrationNotes = [$"the v7 settings could not be read: {exc.Message}"];
            return null;
        }
    }

    /// <summary>
    /// Writes atomically: a temporary sibling is fully written and flushed
    /// before it replaces the real file, so a crash mid-save leaves the previous
    /// settings rather than a truncated file that reads as "everything reset".
    /// </summary>
    public string? TrySave(SettingsDocument document)
    {
        try
        {
            Directory.CreateDirectory(_location.Directory);
            string json = JsonSerializer.Serialize(document, SettingsJsonContext.Default.SettingsDocument);

            string temporary = _location.File + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _location.File, overwrite: true);
            return null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException or ArgumentException)
        {
            return exc.Message;
        }
    }
}
