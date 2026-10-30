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
    /// <summary>Folder holding <c>settings.toml</c> and every file it replaced.</summary>
    public required string Directory { get; init; }

    /// <summary>The v8 file.</summary>
    public string File => Path.Combine(Directory, "settings.toml");

    /// <summary>
    /// The JSON file v8 used before the format changed. Read once, then kept
    /// under <see cref="PreviousBackup"/>: a build of v8 older than this one may
    /// still be the one on the user's PATH.
    /// </summary>
    public string PreviousFile => Path.Combine(Directory, "settings.json");

    /// <summary>Where the pre-TOML copy is kept, so the conversion can be inspected.</summary>
    public string PreviousBackup => Path.Combine(Directory, "settings.v8-backup.json");

    /// <summary>The v7 file, still read for migration and never written by v8.</summary>
    public string LegacyFile => Path.Combine(Directory, "config.json");

    /// <summary>Where the pre-migration copy of the v7 file is kept.</summary>
    public string LegacyBackup => Path.Combine(Directory, "config.v7.backup.json");

    /// <summary>Settings for this user's profile, not for this shell.</summary>
    public string HistoryFile => Path.Combine(Directory, "history.db");

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
