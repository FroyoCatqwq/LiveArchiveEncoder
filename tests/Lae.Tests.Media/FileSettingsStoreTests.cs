using Lae.Core.Settings;
using Lae.Media.Settings;

namespace Lae.Tests.Media;

/// <summary>
/// The file-backed settings store.
///
/// <para>Three behaviours matter, and all three are things v7 got wrong at least
/// once: the save is atomic, an unreadable file is never overwritten, and a v7
/// file is converted without being deleted.</para>
/// </summary>
public sealed class FileSettingsStoreTests
{
    /// <summary>A private folder per test, removed whatever happens.</summary>
    private static string NewFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "lae-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public async Task SaveThenLoadReturnsTheSameDocument()
    {
        string folder = NewFolder();
        try
        {
            var store = new FileSettingsStore(SettingsLocation.For(folder));
            var document = new SettingsDocument
            {
                Ui = new UiSettings { Language = "zh" },
                Defaults = new EncodeDefaults { Quality = 21, FpsLevels = [24, 12] },
            };

            string? failure = store.TrySave(document);
            await Assert.That(failure).IsNull();

            SettingsDocument read = new FileSettingsStore(SettingsLocation.For(folder)).Load();
            await Assert.That(read.Ui.Language).IsEqualTo("zh");
            await Assert.That(read.Defaults.Quality).IsEqualTo(21);
            await Assert.That(read.Defaults.FpsLevels.Count).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The write goes to a temporary sibling first. If a leftover <c>.tmp</c> file
    /// ever appears next to the real one, a crash between the two writes would
    /// have left a half document behind.
    /// </summary>
    [Test]
    public async Task SavingLeavesNoTemporaryFileBehind()
    {
        string folder = NewFolder();
        try
        {
            var store = new FileSettingsStore(SettingsLocation.For(folder));
            store.TrySave(new SettingsDocument());

            string[] files = Directory.GetFiles(folder);
            await Assert.That(files.Length).IsEqualTo(1);
            await Assert.That(files[0].EndsWith("settings.json", StringComparison.Ordinal)).IsTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task AnEmptyFolderYieldsDefaults()
    {
        string folder = NewFolder();
        try
        {
            SettingsDocument document = new FileSettingsStore(SettingsLocation.For(folder)).Load();

            await Assert.That(document.SchemaVersion).IsEqualTo(SettingsDocument.CurrentSchemaVersion);
            await Assert.That(document.Defaults.FpsLevels.Count).IsGreaterThan(0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A corrupt file must not be replaced by defaults: the next save would then
    /// destroy whatever the user had. The store reports the problem instead.
    /// </summary>
    [Test]
    public async Task ACorruptFileIsReportedAndLeftAlone()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.File, "{ this is not json");
            string before = await File.ReadAllTextAsync(location.File);

            var store = new FileSettingsStore(location);
            SettingsDocument document = store.Load();

            await Assert.That(store.MigrationNotes.Count).IsEqualTo(1);
            await Assert.That(document.SchemaVersion).IsEqualTo(SettingsDocument.CurrentSchemaVersion);

            string after = await File.ReadAllTextAsync(location.File);
            await Assert.That(after).IsEqualTo(before);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// ...and a save after that read must refuse. Saying "left alone" in a note
    /// while the next save replaced the file was worse than saying nothing: the
    /// user is told their settings are safe and then loses them, through a
    /// command like <c>preset add</c> that has nothing to do with the damage.
    /// </summary>
    [Test]
    public async Task ASaveIsRefusedWhileTheFileCannotBeRead()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.File, "{ this is not json");

            var store = new FileSettingsStore(location);
            _ = store.Load();

            string? failure = store.TrySave(new SettingsDocument());

            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.Contains("could not be read", StringComparison.Ordinal)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(location.File)).IsEqualTo("{ this is not json");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// First read of a folder that only has a v7 file: the document is converted,
    /// written as <c>settings.json</c>, the v7 file stays where it is, and a copy
    /// is kept so the conversion can be inspected later.
    /// </summary>
    [Test]
    public async Task AV7FileIsMigratedOnceAndNeverDeleted()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.LegacyFile, """
                {
                  "Version": 6,
                  "Encoder": "mpeg4",
                  "Qv": 9,
                  "FpsLevels": [10, 5],
                  "FfmpegSource": "btbn",
                  "PlayMode": "off"
                }
                """);

            var store = new FileSettingsStore(location);
            SettingsDocument first = store.Load();

            await Assert.That(first.Defaults.Encoder).IsEqualTo("mpeg4");
            await Assert.That(first.Defaults.Quality).IsEqualTo(9);
            await Assert.That(first.Toolchain.FfmpegSource).IsEqualTo("btbn");

            await Assert.That(File.Exists(location.File)).IsTrue();
            await Assert.That(File.Exists(location.LegacyFile)).IsTrue();
            await Assert.That(File.Exists(location.LegacyBackup)).IsTrue();
            await Assert.That(store.MigrationNotes.Any(n => n.Contains("migrated", StringComparison.Ordinal))).IsTrue();

            // Second read: the v8 file now exists, so nothing migrates again.
            var second = new FileSettingsStore(location);
            SettingsDocument again = second.Load();
            await Assert.That(second.MigrationNotes.Count).IsEqualTo(0);
            await Assert.That(again.Defaults.Encoder).IsEqualTo("mpeg4");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A v8 file wins over a leftover v7 one. Otherwise an upgrade would keep
    /// re-reading the old settings every launch and silently undo every edit.
    /// </summary>
    [Test]
    public async Task TheV8FileWinsOverALeftoverV7File()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.LegacyFile, """{ "Version": 6, "Qv": 9 }""");
            string? written = new FileSettingsStore(location).TrySave(
                new SettingsDocument { Defaults = new EncodeDefaults { Quality = 20 } });
            await Assert.That(written).IsNull();

            var store = new FileSettingsStore(location);
            SettingsDocument document = store.Load();

            await Assert.That(document.Defaults.Quality).IsEqualTo(20);
            await Assert.That(store.MigrationNotes.Count).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// <c>LAE_CONFIG_DIR</c> is how a portable install and an isolated test run
    /// both work, so the resolution order is asserted rather than assumed.
    /// </summary>
    [Test]
    public async Task TheEnvironmentOverrideWins()
    {
        string folder = NewFolder();
        string? previous = Environment.GetEnvironmentVariable("LAE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("LAE_CONFIG_DIR", folder);

            SettingsLocation location = SettingsLocation.Resolve();
            await Assert.That(Path.GetFullPath(location.Directory)).IsEqualTo(Path.GetFullPath(folder));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAE_CONFIG_DIR", previous);
            Directory.Delete(folder, recursive: true);
        }
    }
}
