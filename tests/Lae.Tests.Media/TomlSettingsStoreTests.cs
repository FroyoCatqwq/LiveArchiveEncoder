using Lae.Core;
using Lae.Core.Settings;
using Lae.Media.Settings;

namespace Lae.Tests.Media;

/// <summary>
/// The TOML settings store.
///
/// <para>Three behaviours matter, and all three are things v7 got wrong at least
/// once: the save is atomic, an unreadable file is never overwritten, and an older
/// file is converted without being deleted. Two more are new with the format: a
/// save must not erase the user's comments, and a misspelled key must be reported
/// instead of silently falling back to a default.</para>
/// </summary>
public sealed class TomlSettingsStoreTests
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
            var store = new TomlSettingsStore(SettingsLocation.For(folder));
            var document = new SettingsDocument
            {
                Ui = new UiSettings { Language = "zh" },
                Defaults = new EncodeDefaults { Quality = 21, FpsLevels = [24.0, 12.0], Fps = 12 },
            };

            string? failure = store.TrySave(document);
            await Assert.That(failure).IsNull();

            var reopened = new TomlSettingsStore(SettingsLocation.For(folder));
            SettingsDocument read = reopened.Load();

            // "No failure" is not the same as "the file reads back": the first
            // version of this test passed the write and silently got defaults,
            // because the validator refused the empty-array form of the presets.
            await Assert.That(reopened.MigrationNotes.Count).IsEqualTo(0);
            await Assert.That(read.Ui.Language).IsEqualTo("zh");
            await Assert.That(read.Defaults.Quality).IsEqualTo(21);
            await Assert.That(read.Defaults.Fps).IsEqualTo(12.0);
            await Assert.That(read.Defaults.FpsLevels.Count).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The write goes to a temporary sibling first. If a leftover <c>.tmp</c> file
    /// ever appears next to the real one, a crash between the two writes would have
    /// left a half document behind.
    /// </summary>
    [Test]
    public async Task SavingLeavesNoTemporaryFileBehind()
    {
        string folder = NewFolder();
        try
        {
            var store = new TomlSettingsStore(SettingsLocation.For(folder));
            store.TrySave(new SettingsDocument());

            string[] files = Directory.GetFiles(folder);
            await Assert.That(files.Length).IsEqualTo(1);
            await Assert.That(files[0].EndsWith("settings.toml", StringComparison.Ordinal)).IsTrue();
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
            SettingsDocument document = new TomlSettingsStore(SettingsLocation.For(folder)).Load();

            await Assert.That(document.SchemaVersion).IsEqualTo(SettingsDocument.CurrentSchemaVersion);
            await Assert.That(document.Defaults.FpsLevels.Count).IsGreaterThan(0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A file can say only what it wants to change. Every other field keeps the
    /// default the type declares -- otherwise every upgrade would need a migration
    /// that fills in the new keys.
    /// </summary>
    [Test]
    public async Task AHandWrittenFileNeedNotMentionEveryKey()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.File, """
                # 那年的机子只有这点带宽
                [defaults]
                quality = 21
                fps = 12
                fpsLevels = [12, 8]
                """);

            SettingsDocument document = new TomlSettingsStore(location).Load();

            await Assert.That(document.Defaults.Quality).IsEqualTo(21);
            await Assert.That(document.Defaults.Fps).IsEqualTo(12.0);
            await Assert.That(document.Defaults.FpsLevels.Count).IsEqualTo(2);
            await Assert.That(document.Defaults.Width).IsEqualTo(Constants.DefaultWidth);
            await Assert.That(document.Toolchain.FfmpegSource).IsEqualTo("path");
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
            const string broken = "[defaults\nquality = 8";
            await File.WriteAllTextAsync(location.File, broken);

            var store = new TomlSettingsStore(location);
            SettingsDocument document = store.Load();

            await Assert.That(store.MigrationNotes.Count).IsEqualTo(1);
            await Assert.That(document.SchemaVersion).IsEqualTo(SettingsDocument.CurrentSchemaVersion);
            await Assert.That(await File.ReadAllTextAsync(location.File)).IsEqualTo(broken);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// ...and a save after that read must refuse. Saying "left alone" in a note
    /// while the next save replaced the file was worse than saying nothing: the
    /// user is told their settings are safe and then loses them, through a command
    /// like <c>preset add</c> that has nothing to do with the damage.
    /// </summary>
    [Test]
    public async Task ASaveIsRefusedWhileTheFileCannotBeRead()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.File, "[defaults\nquality = 8");

            var store = new TomlSettingsStore(location);
            _ = store.Load();

            string? failure = store.TrySave(new SettingsDocument());

            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.Contains("could not be read", StringComparison.Ordinal)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(location.File)).IsEqualTo("[defaults\nquality = 8");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A misspelled key is a refusal, not a default. This is the whole reason the
    /// key set is derived from the document type: the serializer would happily
    /// ignore <c>qualityy</c> and the user would think the edit took.
    /// </summary>
    [Test]
    public async Task AnUnknownKeyIsRefusedInsteadOfIgnored()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.File, """
                [defaults]
                qualityy = 5
                """);

            var store = new TomlSettingsStore(location);
            SettingsDocument document = store.Load();

            await Assert.That(store.MigrationNotes.Count).IsEqualTo(1);
            await Assert.That(store.MigrationNotes[0].Contains("qualityy", StringComparison.Ordinal)).IsTrue();

            // Refused, not half-applied: the value never reaches the document.
            await Assert.That(document.Defaults.Quality).IsEqualTo(new EncodeDefaults().Quality);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The reason for choosing TOML at all: the program writes one block and the
    /// rest of the file -- comments included -- comes back byte for byte.
    /// </summary>
    [Test]
    public async Task CommentsSurviveWhenPresetsChange()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.File, """
                schemaVersion = 11

                # 那一年的播放器缓冲只有几十秒，200 kbps 是能连续播的上限
                [defaults]
                quality = 9
                limitKbps = 200.0

                # 老站点的味道：320x240、每秒 15 帧
                [defaults.subtitles]
                boxAspect = "4:3"
                """);

            var store = new TomlSettingsStore(location);
            SettingsDocument loaded = store.Load();
            await Assert.That(loaded.Defaults.Quality).IsEqualTo(9);

            SettingsDocument withPreset = loaded with
            {
                CustomPresets = [new CustomPreset { Id = "portal-2006", Encoder = "mpeg4", LiveQv = 6 }],
            };

            string? failure = store.TrySave(withPreset);
            await Assert.That(failure).IsNull();

            string text = await File.ReadAllTextAsync(location.File);
            await Assert.That(text.Contains("那一年的播放器缓冲", StringComparison.Ordinal)).IsTrue();
            await Assert.That(text.Contains("老站点的味道", StringComparison.Ordinal)).IsTrue();
            await Assert.That(text.Contains("boxAspect = \"4:3\"", StringComparison.Ordinal)).IsTrue();
            await Assert.That(text.Contains("[[customPresets]]", StringComparison.Ordinal)).IsTrue();

            // And the result still reads back with the preset attached.
            SettingsDocument again = new TomlSettingsStore(location).Load();
            await Assert.That(again.CustomPresets.Count).IsEqualTo(1);
            await Assert.That(again.CustomPresets[0].Id).IsEqualTo("portal-2006");
            await Assert.That(again.Defaults.Quality).IsEqualTo(9);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Three presets survive a save, and removing the middle one really removes it.
    ///
    /// <para>Every preset in an array of tables is its own header, so a block
    /// boundary found by "the next line starting with a bracket" stops at the second
    /// preset. That cost every preset after the first, on every save, while the
    /// command printed "stored". The test above only ever went from zero presets to
    /// one, which is exactly the path the bug was not on.</para>
    /// </summary>
    [Test]
    public async Task EveryPresetSurvivesASaveAndRemovalRemovesTheRightOne()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            var store = new TomlSettingsStore(location);

            // Three separate saves, which is what three `lae preset add` calls do:
            // the second one is the first to run against an existing block.
            foreach (string id in new[] { "a", "b", "c" })
            {
                SettingsDocument current = store.Load();
                SettingsDocument next = current with
                {
                    CustomPresets =
                    [
                        .. current.CustomPresets,
                        new CustomPreset { Id = id, Encoder = "mpeg4", LiveQv = 6 },
                    ],
                };

                await Assert.That(store.TrySave(next)).IsNull();
            }

            SettingsDocument afterAdds = new TomlSettingsStore(location).Load();
            await Assert.That(string.Join(",", afterAdds.CustomPresets.Select(p => p.Id))).IsEqualTo("a,b,c");

            SettingsDocument withoutB = afterAdds with
            {
                CustomPresets = [.. afterAdds.CustomPresets.Where(p => p.Id != "b")],
            };

            await Assert.That(store.TrySave(withoutB)).IsNull();

            SettingsDocument afterRemove = new TomlSettingsStore(location).Load();
            await Assert.That(string.Join(",", afterRemove.CustomPresets.Select(p => p.Id))).IsEqualTo("a,c");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// First read of a folder that has only a v7 file: the document is converted,
    /// written as <c>settings.toml</c>, the v7 file stays where it is, and a copy is
    /// kept so the conversion can be inspected later.
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

            var store = new TomlSettingsStore(location);
            SettingsDocument first = store.Load();

            await Assert.That(first.Defaults.Encoder).IsEqualTo("mpeg4");
            await Assert.That(first.Defaults.Quality).IsEqualTo(9);
            await Assert.That(first.Toolchain.FfmpegSource).IsEqualTo("btbn");

            await Assert.That(File.Exists(location.File)).IsTrue();
            await Assert.That(File.Exists(location.LegacyFile)).IsTrue();
            await Assert.That(File.Exists(location.LegacyBackup)).IsTrue();
            await Assert.That(store.MigrationNotes.Any(n => n.Contains("migrated", StringComparison.Ordinal))).IsTrue();

            // Second read: the TOML file now exists, so nothing migrates again.
            var second = new TomlSettingsStore(location);
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
    /// The JSON file v8 wrote before the format changed is converted on the first
    /// read, kept as a backup, and then removed -- leaving it in place would mean
    /// converting again on every launch and undoing every later edit.
    /// </summary>
    [Test]
    public async Task ThePreviousJsonFileIsConvertedAndKeptAsABackup()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.PreviousFile, """
                { "schemaVersion": 10, "defaults": { "encoder": "libx264", "quality": 20 } }
                """);

            var store = new TomlSettingsStore(location);
            SettingsDocument document = store.Load();

            await Assert.That(document.Defaults.Encoder).IsEqualTo("libx264");
            await Assert.That(document.Defaults.Quality).IsEqualTo(20);
            await Assert.That(File.Exists(location.File)).IsTrue();
            await Assert.That(File.Exists(location.PreviousBackup)).IsTrue();
            await Assert.That(File.Exists(location.PreviousFile)).IsFalse();
            await Assert.That(store.MigrationNotes.Any(n => n.Contains("migrated", StringComparison.Ordinal))).IsTrue();
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
    public async Task TheTomlFileWinsOverALeftoverV7File()
    {
        string folder = NewFolder();
        try
        {
            var location = SettingsLocation.For(folder);
            await File.WriteAllTextAsync(location.LegacyFile, """{ "Version": 6, "Qv": 9 }""");
            await File.WriteAllTextAsync(location.File, """
                [defaults]
                quality = 20
                """);

            var store = new TomlSettingsStore(location);
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

    /// <summary>
    /// The chain's keys are older than its type name, and both have to stay.
    ///
    /// <para>The code calls the collection <c>Steps</c> because a hop is no longer
    /// always a recompress pass; the file still calls the section
    /// <c>recompress</c> and the array <c>passes</c>, because renaming them means
    /// re-rendering the document, and re-rendering it discards every comment the
    /// user wrote. This test is the knot that keeps the two names tied together:
    /// read the old keys, accept a plugin hop in the same array, and write the
    /// old keys back.</para>
    /// </summary>
    [Test]
    public async Task TheChainKeepsItsOldKeysAndLearnsPluginHops()
    {
        string folder = NewFolder();
        try
        {
            const string text = """
                [defaults.recompress]
                enabled = true

                # the user's own note, which a save must not eat
                [[defaults.recompress.passes]]
                args = "-c:v libx264 -crf 28"
                container = "mp4"

                [[defaults.recompress.passes]]
                plugin = "lae-damage"
                stage = "damage"

                [defaults.recompress.passes.params]
                intensity = "0.25"
                """;

            File.WriteAllText(Path.Combine(folder, "settings.toml"), text);

            var store = new TomlSettingsStore(SettingsLocation.For(folder));
            SettingsDocument read = store.Load();

            await Assert.That(store.MigrationNotes.Count).IsEqualTo(0)
                .Because("these are the keys this build writes; there is nothing to migrate");

            ChainSettings chain = read.Defaults.Chain;
            await Assert.That(chain.Enabled).IsTrue();
            await Assert.That(chain.Steps.Count).IsEqualTo(2);
            await Assert.That(chain.Steps[0].Kind).IsEqualTo(ChainStepKind.Ffmpeg);
            await Assert.That(chain.Steps[0].Container).IsEqualTo("mp4");

            await Assert.That(chain.Steps[1].Kind).IsEqualTo(ChainStepKind.Plugin);
            await Assert.That(chain.Steps[1].Plugin).IsEqualTo("lae-damage");
            await Assert.That(chain.Steps[1].Params!["intensity"]).IsEqualTo("0.25")
                .Because("a plugin's parameters are free keys in a table, not a fixed set");

            await Assert.That(store.TrySave(read)).IsNull();

            string rewritten = File.ReadAllText(Path.Combine(folder, "settings.toml"));
            await Assert.That(rewritten.Contains("[defaults.recompress]", StringComparison.Ordinal)).IsTrue();
            await Assert.That(rewritten.Contains("[[defaults.recompress.passes]]", StringComparison.Ordinal)).IsTrue();
            await Assert.That(rewritten.Contains("chain", StringComparison.Ordinal)).IsFalse()
                .Because("the C# name must not leak into the user's file");
            await Assert.That(rewritten.Contains("# the user's own note", StringComparison.Ordinal)).IsTrue()
                .Because("the point of keeping the old keys is that the file stays the user's");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
