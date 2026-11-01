using System.Text.Json;
using Lae.Core.Plugins;
using Lae.Core.Settings;

namespace Lae.Plugins;

/// <summary>A plugin that was found, and enough to start it.</summary>
public sealed record FoundPlugin
{
    public required string Id { get; init; }

    /// <summary>Program followed by its arguments, already resolved.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>
    /// Which rule found it, for the report. Worth printing: "it ran the wrong
    /// copy" is the first question when two installs exist, and the answer is
    /// only available at the moment of discovery.
    /// </summary>
    public required string Origin { get; init; }

    /// <summary>
    /// The folder the plugin was installed in, or null when it came from PATH.
    ///
    /// <para>It becomes the child's working directory. Not tidiness: a manifest may
    /// say <c>["python3", "run.py"]</c>, and a relative script name is a path only
    /// from the folder the manifest was read out of. Without this the plugin starts
    /// and cannot find its own body -- which is how the first version of this file
    /// failed, with the interpreter reporting a missing script beside the test binary.</para>
    /// </summary>
    public string? Folder { get; init; }
}

/// <summary>
/// The result of looking for one plugin.
///
/// <para>"Not found" and "found but broken" are different answers and both are
/// normal. The first means the user never installed it, and the sentence belongs
/// to whoever asked. The second means something is there and cannot be used, which
/// is a configuration error worth its own message -- skipping it silently turns a
/// typo in a folder name into "the plugin does nothing".</para>
/// </summary>
public sealed record PluginLookup
{
    public FoundPlugin? Plugin { get; init; }

    public string? Error { get; init; }

    public bool Found => Plugin is not null;
}

/// <summary>Where to look, and the two injection points that keep this testable.</summary>
public sealed record PluginSearch
{
    /// <summary>Where <c>settings.toml</c> lives; plugins installed by the user sit under it.</summary>
    public required string ConfigRoot { get; init; }

    /// <summary>
    /// The program's own folder, for the portable layout.
    ///
    /// <para><c>AppContext.BaseDirectory</c> and not <c>Assembly.Location</c>: a
    /// single-file build extracts to a temporary folder and the assembly's
    /// location points there, so a plugins folder copied next to the executable
    /// would be invisible. v7 hit this on the toolchain folder.</para>
    /// </summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary><c>--plugin id=path</c>, for debugging a plugin that is not installed.</summary>
    public IReadOnlyDictionary<string, string>? Explicit { get; init; }

    /// <summary>
    /// <c>LAE_PLUGINS</c>: extra folders, one plugin directory each. Null reads the
    /// environment; a list (even an empty one) is taken as given, which is what
    /// lets a test run without touching the real variable.
    /// </summary>
    public IReadOnlyList<string>? ExtraFolders { get; init; }

    /// <summary>PATH probe. Null means the real PATH; a function is a test's stand-in.</summary>
    public Func<string, string?>? OnPath { get; init; }
}

/// <summary>
/// Finds a plugin executable, in the documented order.
///
/// <para>Six rules, and the order is the contract: the same plugin can be installed
/// three ways at once, and which copy runs has to be predictable. An explicit
/// <c>--plugin</c> beats everything, then the command the user configured by hand,
/// then the folders they set up for CI, then the portable install beside the
/// program, then their own install, and PATH last -- because a program on PATH is
/// the one nobody remembers installing.</para>
///
/// <para>Every rule that finds a folder also decides how to start what is in it,
/// and the decision is the same everywhere: <c>plugin.json</c> wins when present,
/// otherwise an executable named after the plugin.</para>
/// </summary>
public sealed class PluginFinder(PluginSearch search)
{
    private const string ManifestName = "plugin.json";

    public PluginLookup Find(string id, PluginRegistration? registration)
    {
        if (string.IsNullOrWhiteSpace(id)) return new PluginLookup { Error = "the plugin id is empty" };

        // 1. --plugin id=path
        IReadOnlyDictionary<string, string> explicitPaths = search.Explicit ?? new Dictionary<string, string>();
        if (explicitPaths.TryGetValue(id, out string? explicitPath))
            return FromPath(id, explicitPath, "given on the command line");

        // 2. the command in [[plugins]]
        if (registration?.Command is { Length: > 0 } configured)
            return FromConfigured(id, configured);

        // 3. LAE_PLUGINS
        foreach (string folder in ExtraFolders())
        {
            if (InFolder(id, folder, "LAE_PLUGINS") is { } found) return found;
        }

        // 4. beside the program
        if (InFolder(id, Path.Combine(search.BaseDirectory, "plugins"), "the program's own plugins folder")
            is { } portable)
        {
            return portable;
        }

        // 5. the user's install
        if (InFolder(id, Path.Combine(search.ConfigRoot, "plugins"), "the user plugins folder") is { } installed)
            return installed;

        // 6. PATH
        string launchName = "lae-" + id;
        if (Probe(launchName) is { } onPath)
            return new PluginLookup { Plugin = new FoundPlugin { Id = id, Command = [onPath], Origin = "PATH" } };

        return new PluginLookup();
    }

    private IReadOnlyList<string> ExtraFolders()
    {
        if (search.ExtraFolders is { } given) return given;

        string? raw = Environment.GetEnvironmentVariable("LAE_PLUGINS");
        if (string.IsNullOrWhiteSpace(raw)) return [];

        return [.. raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>An explicit path is either the executable itself or a plugin folder.</summary>
    private static PluginLookup FromPath(string id, string path, string origin)
    {
        if (Directory.Exists(path)) return Describe(id, path, origin);

        return File.Exists(path)
            ? new PluginLookup
            {
                Plugin = new FoundPlugin
                {
                    Id = id,
                    Command = [path],
                    Origin = origin,
                    Folder = Path.GetDirectoryName(Path.GetFullPath(path)),
                },
            }
            : new PluginLookup { Error = $"{id}: '{path}' does not exist, and it was {origin}" };
    }

    /// <summary>
    /// The command from <c>[[plugins]]</c>. One executable, no inline arguments:
    /// splitting a string on spaces would need quoting rules, and the manifest
    /// already has a field for arguments.
    /// </summary>
    private PluginLookup FromConfigured(string id, string command)
    {
        string trimmed = command.Trim();

        if (Path.IsPathRooted(trimmed))
        {
            return File.Exists(trimmed)
                ? new PluginLookup
                {
                    Plugin = new FoundPlugin
                    {
                        Id = id,
                        Command = [trimmed],
                        Origin = "settings",
                        Folder = Path.GetDirectoryName(trimmed),
                    },
                }
                : new PluginLookup { Error = $"{id}: the command in settings.toml is '{trimmed}', which is not there" };
        }

        // A relative path is relative to the plugin's own folder, which is where a
        // hand-installed plugin would keep a launcher script. Forward slashes are
        // turned into the platform's separator so the path the user wrote and the
        // path this program reports are the same string.
        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            string folder = Path.Combine(search.ConfigRoot, "plugins", id);
            string full = Path.Combine(folder, trimmed.Replace('/', Path.DirectorySeparatorChar));

            return File.Exists(full)
                ? new PluginLookup
                {
                    Plugin = new FoundPlugin
                    {
                        Id = id,
                        Command = [full],
                        Origin = "settings",
                        Folder = folder,
                    },
                }
                : new PluginLookup { Error = $"{id}: the command in settings.toml is '{trimmed}', which is not under {folder}" };
        }

        // A bare name: look in the plugin's folder, then on PATH.
        if (Probe(trimmed) is { } resolved)
        {
            return new PluginLookup
            {
                Plugin = new FoundPlugin
                {
                    Id = id,
                    Command = [resolved],
                    Origin = "settings",
                    Folder = Path.GetDirectoryName(resolved),
                },
            };
        }

        return new PluginLookup { Error = $"{id}: '{trimmed}' from settings.toml is neither in the plugin folder nor on PATH" };
    }

    /// <summary>One of the folder rules: <paramref name="parent"/>/<paramref name="id"/>.</summary>
    private static PluginLookup? InFolder(string id, string parent, string origin)
    {
        string folder = Path.Combine(parent, id);
        return Directory.Exists(folder) ? Describe(id, folder, origin) : null;
    }

    /// <summary>
    /// How to start what is in <paramref name="folder"/>.
    ///
    /// <para>A folder counts as "found" the moment it exists, so a folder without a
    /// usable entry point is an error rather than a miss. Otherwise a typo in a
    /// launcher name would be reported as "plugin not installed", which sends the
    /// user to reinstall something that is already there.</para>
    /// </summary>
    private static PluginLookup Describe(string id, string folder, string origin)
    {
        string manifestPath = Path.Combine(folder, ManifestName);

        if (File.Exists(manifestPath))
        {
            PluginManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(
                    File.ReadAllText(manifestPath), PluginJsonContext.Default.PluginManifest);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or JsonException)
            {
                return new PluginLookup { Error = $"{id}: {manifestPath} could not be read ({exc.Message})" };
            }

            if (manifest?.Command is not { Count: > 0 } declared)
                return new PluginLookup { Error = $"{id}: {manifestPath} declares no command" };

            return ResolveDeclared(id, folder, declared, origin);
        }

        foreach (string candidate in LauncherNames(id))
        {
            string path = Path.Combine(folder, candidate);
            if (Executable(path))
            {
                return new PluginLookup
                {
                    Plugin = new FoundPlugin { Id = id, Command = [path], Origin = origin, Folder = folder },
                };
            }
        }

        return new PluginLookup
        {
            Error = $"{id}: {folder} exists but holds no {ManifestName} and nothing named like the plugin",
        };
    }

    /// <summary>
    /// A manifest command: the program may be relative to the folder, and the
    /// remaining elements are arguments passed as they were written.
    /// </summary>
    private static PluginLookup ResolveDeclared(string id, string folder, IReadOnlyList<string> declared, string origin)
    {
        string program = declared[0];

        // A manifest is written by hand, so a forward slash is likely even on
        // Windows. The platform accepts it, but the path this returns is compared
        // with, printed and logged -- so it is normalised rather than left as two
        // spellings of the same file.
        string relative = program.Replace('/', Path.DirectorySeparatorChar);

        string resolved = Path.IsPathRooted(program)
            ? program
            : Path.Combine(folder, relative);

        if (!Executable(resolved))
        {
            return new PluginLookup
            {
                Error = $"{id}: {ManifestName} says to run '{program}', which is not runnable"
                      + (Path.IsPathRooted(program) ? "" : $" from {folder}"),
            };
        }

        return new PluginLookup
        {
            Plugin = new FoundPlugin
            {
                Id = id,
                Command = [resolved, .. declared.Skip(1)],
                Origin = origin,
                Folder = folder,
            },
        };
    }

    private static string[] LauncherNames(string id) => OperatingSystem.IsWindows()
        ? [$"{id}.exe", $"{id}.cmd", $"{id}.bat", id]
        : [id];

    private string? Probe(string name)
    {
        if (search.OnPath is { } given) return given(name);

        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string candidate in LauncherNames(name))
            {
                string path = Path.Combine(folder, candidate);
                if (Executable(path)) return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this path is something the OS will actually start.
    ///
    /// <para>On Unix the existence check is not enough: a launcher without its
    /// execute bit spawns nothing, and the message the user gets is about permission
    /// denied long after the plugin was found. Checking here keeps "found" meaning
    /// "can be started".</para>
    /// </summary>
    private static bool Executable(string path)
    {
        if (!File.Exists(path)) return false;

        if (OperatingSystem.IsWindows()) return true;

        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                                                | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
