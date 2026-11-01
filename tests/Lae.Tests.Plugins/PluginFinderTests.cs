using Lae.Core.Settings;
using Lae.Plugins;

namespace Lae.Tests.Plugins;

/// <summary>
/// The six discovery rules, and the order between them.
///
/// <para>Every input is injected -- base folder, config folder, the extra list and
/// the PATH probe -- so these tests describe the rule and not this machine. The one
/// thing that is real is the filesystem, because "a folder exists but holds nothing
/// runnable" is a distinction only the filesystem can make.</para>
/// </summary>
public sealed class PluginFinderTests
{
    private const string Id = "lae-fake";

    [Test]
    public async Task TheFolderBesideTheProgramIsFound()
    {
        using var box = new Box();
        string exe = Box.Executable(Path.Combine(box.Base, "plugins", Id, Id));

        PluginLookup found = box.Finder().Find(Id, null);

        await Assert.That(found.Found).IsTrue().Because(found.Error ?? "discovery gave no reason");
        await Assert.That(Path.GetFileName(found.Plugin!.Command[0])).IsEqualTo(Path.GetFileName(exe));
        await Assert.That(Path.GetDirectoryName(found.Plugin.Command[0])).IsEqualTo(Path.GetDirectoryName(exe));
        await Assert.That(found.Plugin.Origin).Contains("program's own");
    }

    [Test]
    public async Task TheUsersInstallIsFoundWhenThePortableOneIsAbsent()
    {
        using var box = new Box();
        string exe = Box.Executable(Path.Combine(box.Config, "plugins", Id, Id));

        PluginLookup found = box.Finder().Find(Id, null);

        await Assert.That(found.Found).IsTrue().Because(found.Error ?? "discovery gave no reason");
        await Assert.That(Path.GetFileName(found.Plugin!.Command[0])).IsEqualTo(Path.GetFileName(exe));
        await Assert.That(Path.GetDirectoryName(found.Plugin.Command[0])).IsEqualTo(Path.GetDirectoryName(exe));
        await Assert.That(found.Plugin.Origin).Contains("user plugins");
    }

    /// <summary>
    /// The portable install wins. Two copies can be present at once -- one that
    /// shipped, one the user installed -- and the one next to the program is the one
    /// they are looking at.
    /// </summary>
    [Test]
    public async Task ThePortableInstallBeatsTheUsersOwn()
    {
        using var box = new Box();
        string portable = Box.Executable(Path.Combine(box.Base, "plugins", Id, Id));
        Box.Executable(Path.Combine(box.Config, "plugins", Id, Id));

        PluginLookup found = box.Finder().Find(Id, null);

        await Assert.That(Path.GetDirectoryName(found.Plugin!.Command[0]))
            .IsEqualTo(Path.Combine(box.Base, "plugins", Id))
            .Because("found " + found.Plugin.Command[0] + " while the portable copy is " + portable);
    }

    [Test]
    public async Task AnExplicitPathBeatsEverything()
    {
        using var box = new Box();
        Box.Executable(Path.Combine(box.Base, "plugins", Id, Id));
        string chosen = Box.Executable(Path.Combine(box.Root, "somewhere", "else", Id));

        PluginLookup found = box.Finder(new Dictionary<string, string> { [Id] = chosen }).Find(Id, null);

        await Assert.That(found.Plugin!.Command[0]).IsEqualTo(chosen);
        await Assert.That(found.Plugin.Origin).Contains("command line");
    }

    [Test]
    public async Task ACommandInTheSettingsIsUsed()
    {
        using var box = new Box();
        string exe = Box.Executable(Path.Combine(box.Root, "hand", "installed", Id));
        var registration = new PluginRegistration { Id = Id, Command = exe };

        PluginLookup found = box.Finder().Find(Id, registration);

        await Assert.That(found.Found).IsTrue().Because(found.Error ?? "discovery gave no reason");
        await Assert.That(Path.GetFileName(found.Plugin!.Command[0])).IsEqualTo(Path.GetFileName(exe));
        await Assert.That(Path.GetDirectoryName(found.Plugin.Command[0])).IsEqualTo(Path.GetDirectoryName(exe));
    }

    /// <summary>
    /// A relative command is relative to the plugin's own folder, which is where a
    /// hand-installed plugin keeps its launcher.
    /// </summary>
    [Test]
    public async Task ARelativeCommandIsResolvedAgainstThePluginFolder()
    {
        using var box = new Box();
        Box.Executable(Path.Combine(box.Config, "plugins", Id, "bin", "run"));
        var registration = new PluginRegistration { Id = Id, Command = "bin/run" };

        PluginLookup found = box.Finder().Find(Id, registration);

        await Assert.That(found.Found).IsTrue().Because(found.Error ?? "discovery gave no reason");
        await Assert.That(found.Plugin!.Command[0]).IsEqualTo(
            Path.Combine(box.Config, "plugins", Id, "bin", "run"));
    }

    [Test]
    public async Task LAE_PLUGINSFoldersAreSearchedBeforeTheInstalls()
    {
        using var box = new Box();
        Box.Executable(Path.Combine(box.Base, "plugins", Id, Id));
        string fromEnv = Box.Executable(Path.Combine(box.Root, "ci-plugins", Id, Id));

        PluginLookup found = box.Finder(extra: [Path.Combine(box.Root, "ci-plugins")]).Find(Id, null);

        await Assert.That(found.Plugin!.Command[0]).IsEqualTo(fromEnv);
        await Assert.That(found.Plugin.Origin).Contains("LAE_PLUGINS");
    }

    [Test]
    public async Task PATHIsTheLastResort()
    {
        using var box = new Box();

        PluginLookup found = box.Finder(onPath: name => name == "lae-" + Id ? "/usr/bin/lae-" + Id : null)
            .Find(Id, null);

        await Assert.That(found.Found).IsTrue().Because(found.Error ?? "discovery gave no reason");
        await Assert.That(found.Plugin!.Origin).IsEqualTo("PATH");
    }

    /// <summary>
    /// A manifest decides how to start what is in the folder. This is what makes a
    /// plugin in a language with its own launcher possible at all: the entry point
    /// does not have to be named after the plugin.
    /// </summary>
    [Test]
    public async Task AManifestDecidesTheCommand()
    {
        using var box = new Box();
        string folder = Path.Combine(box.Base, "plugins", Id);
        Box.Executable(Path.Combine(folder, "ignored-by-the-manifest"));
        Box.Executable(Path.Combine(folder, "tool", "python3"));

        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "plugin.json"),
            """{ "id": "lae-fake", "command": ["tool/python3", "-m", "lae_fake"] }""");

        PluginLookup found = box.Finder().Find(Id, null);

        await Assert.That(found.Found).IsTrue().Because(found.Error ?? "discovery gave no reason");
        await Assert.That(found.Plugin!.Command.Count).IsEqualTo(3);
        await Assert.That(found.Plugin.Command[0]).IsEqualTo(Path.Combine(folder, "tool", "python3"));
        await Assert.That(found.Plugin.Command[1]).IsEqualTo("-m");
        await Assert.That(found.Plugin.Command[2]).IsEqualTo("lae_fake");
    }

    /// <summary>
    /// A folder with nothing runnable in it is an error, not a miss.
    ///
    /// <para>Reporting it as "not installed" would send the user to reinstall
    /// something that is already there, and the actual problem -- a launcher named
    /// something else, or a lost execute bit -- would stay invisible.</para>
    /// </summary>
    [Test]
    public async Task AFolderWithNothingRunnableIsAnError()
    {
        using var box = new Box();
        Directory.CreateDirectory(Path.Combine(box.Base, "plugins", Id));

        PluginLookup found = box.Finder().Find(Id, null);

        await Assert.That(found.Found).IsFalse();
        await Assert.That(found.Error).Contains("holds no");
    }

    [Test]
    public async Task AManifestThatNamesAMissingProgramIsAnError()
    {
        using var box = new Box();
        string folder = Path.Combine(box.Base, "plugins", Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), """{ "command": ["nope"] }""");

        PluginLookup found = box.Finder().Find(Id, null);

        await Assert.That(found.Found).IsFalse();
        await Assert.That(found.Error).Contains("not runnable");
    }

    [Test]
    public async Task AManifestThatWillNotParseIsAnError()
    {
        using var box = new Box();
        string folder = Path.Combine(box.Base, "plugins", Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), "{ this is not json");

        PluginLookup found = box.Finder().Find(Id, null);

        await Assert.That(found.Found).IsFalse();
        await Assert.That(found.Error).Contains("could not be read");
    }

    /// <summary>
    /// Nothing anywhere is not an error: the message belongs to whoever asked, and
    /// only it knows whether the plugin was optional.
    /// </summary>
    [Test]
    public async Task NothingFoundIsNotAProblemInItself()
    {
        using var box = new Box();

        PluginLookup found = box.Finder().Find(Id, null);

        await Assert.That(found.Found).IsFalse();
        await Assert.That(found.Error).IsNull();
    }

    /// <summary>A throwaway tree, and the two paths the finder is allowed to know.</summary>
    private sealed class Box : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "lae-finder-" + Guid.NewGuid().ToString("N"));

        internal Box() => Directory.CreateDirectory(_root);

        internal string Root => _root;

        internal string Base => Path.Combine(_root, "app");

        internal string Config => Path.Combine(_root, "config");

        internal PluginFinder Finder(
            IReadOnlyDictionary<string, string>? explicitPaths = null,
            IReadOnlyList<string>? extra = null,
            Func<string, string?>? onPath = null)
            => new(new PluginSearch
            {
                ConfigRoot = Config,
                BaseDirectory = Base,
                Explicit = explicitPaths,
                ExtraFolders = extra ?? [],
                OnPath = onPath ?? (_ => null),
            });

        /// <summary>Write a file that would be startable, on either platform.</summary>
        internal static string Executable(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n");

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary folder is not a test failure.
            }
        }
    }
}
