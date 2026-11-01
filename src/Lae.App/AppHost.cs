using Lae.App.Ui;
using Lae.Core.History;
using Lae.Core.Localization;
using Lae.Core.Settings;
using Lae.Media.History;
using Lae.Media.Localization;
using Lae.Media.Settings;

namespace Lae.App;

/// <summary>
/// The composition root.
///
/// <para>Hand-built rather than a container. The program has one entry point,
/// one process and a fixed set of collaborators; a DI container would add a
/// reflection-based resolution step (plus a start-up dependency) to save the
/// twelve lines below. v8 publishes as a self-contained single file, and
/// reflection-heavy wiring is the first thing that gets awkward in that shape.</para>
///
/// <para>The point is not "no container" -- it is that every dependency is
/// visible here in one screen. v7 wired nothing: 56 static classes reached for
/// the console, the clock and the disk directly, which is why none of the
/// interesting code could be tested at all.</para>
/// </summary>
public sealed class AppHost
{
    public required ISettingsStore Settings { get; init; }

    /// <summary>
    /// Where finished runs are remembered. A no-op unless a real host is built, so
    /// tests and dry runs never create a database.
    /// </summary>
    public IRunHistory History { get; init; } = NullRunHistory.Instance;

    /// <summary>Where commands write. Swapped for a buffer in tests.</summary>
    public ITerminal Terminal { get; init; } = new SystemTerminal();

    /// <summary>
    /// The language a run speaks. English unless the settings or <c>LAE_LANG</c> say
    /// otherwise, and resolved from the real resources rather than from a stub: a
    /// test that asserts a sentence should be looking at the sentence a user sees.
    /// </summary>
    public IMessages Messages { get; init; } = DefaultMessages;

    private static readonly IMessages DefaultMessages = I18NextMessages.Create(LanguageChoice.English);

    /// <summary>
    /// The folder the settings live in, which is where a managed FFmpeg install
    /// goes. Null for a store with no disk location.
    /// </summary>
    public string? ConfigRoot => Settings.DataDirectory;

    /// <summary>
    /// What the very first read of the settings had to say -- a migration that
    /// happened, or a file that could not be read and is being run on defaults.
    ///
    /// <para>Captured here rather than read by each command because it cannot be
    /// reproduced: the read that notices a migration also performs it, so the
    /// command's own read afterwards sees a clean file and says nothing. v8 used to
    /// drop this list on the floor, which made "your settings could not be read, so
    /// these are the defaults" a thing the user never found out.</para>
    /// </summary>
    public IReadOnlyList<string> StartupNotes { get; init; } = [];

    /// <summary>Prints the startup notes once, on standard error, in the chosen language.</summary>
    public void ReportStartupNotes()
    {
        foreach (string note in StartupNotes)
            Terminal.ErrorLine(Messages.Format("note.prefixed", new { text = note }));
    }

    /// <summary>Real machine: settings beside the user's application data.</summary>
    public static AppHost CreateDefault()
    {
        SettingsLocation location = SettingsLocation.Resolve();
        var settings = new TomlSettingsStore(location);

        // The settings are read once here to answer "which language" and to catch
        // what that read had to do -- a migration, or a file that will not parse.
        // Commands read them again; that is a second read of a small text file,
        // and it buys not threading the document through every constructor.
        SettingsDocument first = settings.Load();
        string language = LanguageChoice.Pick(
            Environment.GetEnvironmentVariable("LAE_LANG"),
            first.Ui.Language);

        return new()
        {
            Settings = settings,
            History = new SqliteHistoryStore(location.HistoryFile),
            Messages = I18NextMessages.Create(language),
            Terminal = new SystemTerminal(),
            StartupNotes = settings.MigrationNotes,
        };
    }

    /// <summary>Tests and dry runs: nothing touches the disk, nothing is printed.</summary>
    public static AppHost InMemory(SettingsDocument? document = null, ITerminal? terminal = null)
        => new()
        {
            Settings = new MemorySettingsStore(document),
            Terminal = terminal ?? new BufferedTerminal(),
        };
}
