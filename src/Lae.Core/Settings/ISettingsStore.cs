namespace Lae.Core.Settings;

/// <summary>
/// Where the settings come from and go to.
///
/// <para>The interface lives here and the file-backed implementation lives in
/// <c>Lae.Media</c>, because reading a file is I/O and this assembly does not do
/// I/O. The payoff is that every command can be tested against an in-memory
/// store without touching the developer's own settings file -- which v7's e2e
/// suite learned to regret: it had been writing the real
/// <c>%LOCALAPPDATA%</c> config, so one test's leftovers changed the next test's
/// starting point.</para>
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Reads the document, migrating an older one if needed, and falls back to
    /// defaults when there is nothing readable. A corrupt file must never stop
    /// the program from starting.
    /// </summary>
    SettingsDocument Load();

    /// <summary>
    /// Writes the document atomically: a crash mid-save leaves the previous file
    /// intact rather than a truncated one that would reset everything.
    /// </summary>
    /// <returns>Null on success, or the reason the write failed.</returns>
    string? TrySave(SettingsDocument document);

    /// <summary>Human-readable location, for <c>lae config path</c> and error messages.</summary>
    string Location { get; }
}

/// <summary>
/// A store that keeps the document in memory. Used by tests and by <c>--dry-run</c>
/// paths that must not touch the disk.
/// </summary>
public sealed class MemorySettingsStore(SettingsDocument? initial = null) : ISettingsStore
{
    private SettingsDocument _document = initial ?? new SettingsDocument();

    public string Location => "(memory)";

    public SettingsDocument Load() => _document;

    public string? TrySave(SettingsDocument document)
    {
        _document = document;
        return null;
    }
}
