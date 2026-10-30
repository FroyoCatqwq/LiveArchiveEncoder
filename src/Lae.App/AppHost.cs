using Lae.App.Ui;
using Lae.Core.Settings;
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

    /// <summary>Where commands write. Swapped for a buffer in tests.</summary>
    public ITerminal Terminal { get; init; } = new SystemTerminal();

    /// <summary>Real machine: settings beside the user's application data.</summary>
    public static AppHost CreateDefault()
        => new()
        {
            Settings = new TomlSettingsStore(SettingsLocation.Resolve()),
            Terminal = new SystemTerminal(),
        };

    /// <summary>Tests and dry runs: nothing touches the disk, nothing is printed.</summary>
    public static AppHost InMemory(SettingsDocument? document = null, ITerminal? terminal = null)
        => new()
        {
            Settings = new MemorySettingsStore(document),
            Terminal = terminal ?? new BufferedTerminal(),
        };
}
