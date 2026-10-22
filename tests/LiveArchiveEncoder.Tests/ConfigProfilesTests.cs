using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.3: the shelf of named settings files.
///
/// The interesting half is <see cref="ConfigProfiles.NormalizeName"/>: the name
/// arrives from a prompt, so <c>../../evil</c> must not become a write outside
/// the shelf, and a missing extension must not produce a file the loader will
/// never list again.
///
/// Not in parallel: the shelf lives under <c>LAE_CONFIG_DIR</c>, which is a
/// process-wide environment variable and would be restored under another test's
/// feet by TUnit's default parallelism.
/// </summary>
[NotInParallel]
public sealed class ConfigProfilesTests
{
    private static async Task WithTempRoot(Func<string, Task> body)
    {
        string root = Path.Combine(Path.GetTempPath(), $"lae-profiles-{Guid.NewGuid():N}");
        string? previous = Environment.GetEnvironmentVariable("LAE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("LAE_CONFIG_DIR", root);
        try
        {
            await body(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAE_CONFIG_DIR", previous);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Test]
    public async Task PathTraversalNeverEscapesTheShelf()
    {
        await Assert.That(ConfigProfiles.NormalizeName("../../evil")).IsEqualTo("evil.json");
        await Assert.That(ConfigProfiles.NormalizeName(@"..\..\evil.json")).IsEqualTo("evil.json");
        await Assert.That(ConfigProfiles.NormalizeName("sub/dir/name")).IsEqualTo("name.json");
    }

    [Test]
    public async Task NamesGetTheJsonSuffixAndKeepIt()
    {
        await Assert.That(ConfigProfiles.NormalizeName("arch")).IsEqualTo("arch.json");
        await Assert.That(ConfigProfiles.NormalizeName("arch.JSON")).IsEqualTo("arch.JSON");
        await Assert.That(ConfigProfiles.NormalizeName("  空白  ")).IsEqualTo("空白.json");
    }

    [Test]
    public async Task EmptyOrDirectoryOnlyNamesAreRefused()
    {
        await Assert.That(ConfigProfiles.NormalizeName("")).IsNull();
        await Assert.That(ConfigProfiles.NormalizeName("   ")).IsNull();
        await Assert.That(ConfigProfiles.NormalizeName("..")).IsNull();
        await Assert.That(ConfigProfiles.NormalizeName("../")).IsNull();
        await Assert.That(ConfigProfiles.NormalizeName(null)).IsNull();
    }

    [Test]
    public async Task InvalidCharactersBecomeUnderscores()
    {
        string? name = ConfigProfiles.NormalizeName("a<b>c|d" + '"' + "e.json");
        await Assert.That(name).IsNotNull();
        foreach (char bad in Path.GetInvalidFileNameChars())
            await Assert.That(name!.Contains(bad)).IsFalse();
        await Assert.That(name!.EndsWith(".json", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SaveThenLoadRoundTripsAndLists()
    {
        await WithTempRoot(async root =>
        {
            var config = new Config { Encoder = null, Width = 320, Height = 240, Fps = 12.5, Parallel = 3 };

            FileInfo? saved = ConfigProfiles.SaveAs(config, "arch-2007");
            await Assert.That(saved).IsNotNull();
            await Assert.That(File.Exists(saved!.FullName)).IsTrue();
            await Assert.That(saved.DirectoryName).IsEqualTo(Path.Combine(root, "configs"))
                .Because("profiles stay on the shelf, never next to the caller");

            var listed = ConfigProfiles.List();
            await Assert.That(listed.Select(f => f.Name)).IsEquivalentTo(new[] { "arch-2007.json" });

            var restored = new Config();
            FileInfo? read = ConfigProfiles.LoadInto(restored, listed[0], []);
            await Assert.That(read).IsNotNull();
            await Assert.That(restored.Width).IsEqualTo(320);
            await Assert.That(restored.Height).IsEqualTo(240);
            await Assert.That(restored.Fps).IsEqualTo(12.5);
            await Assert.That(restored.Parallel).IsEqualTo(3);
        });
    }

    [Test]
    public async Task LoadingAMissingFileChangesNothing()
    {
        await WithTempRoot(async _ =>
        {
            var config = new Config { Width = 512 };
            var missing = new FileInfo(Path.Combine(ConfigProfiles.Directory, "nope.json"));

            FileInfo? read = ConfigProfiles.LoadInto(config, missing, []);

            await Assert.That(read).IsNull();
            await Assert.That(config.Width).IsEqualTo(512).Because("a failed load must leave the live config alone");
        });
    }

    [Test]
    public async Task DeletingIsShelfScopedAndIdempotent()
    {
        await WithTempRoot(async root =>
        {
            ConfigProfiles.SaveAs(new Config { Width = 320 }, "gone");
            await Assert.That(File.Exists(Path.Combine(root, "configs", "gone.json"))).IsTrue();

            await Assert.That(ConfigProfiles.Delete("gone")).IsTrue();
            await Assert.That(File.Exists(Path.Combine(root, "configs", "gone.json"))).IsFalse();
            // Deleting something already gone is not an error the menu must report.
            await Assert.That(ConfigProfiles.Delete("gone")).IsTrue();
            // A traversal attempt deletes "outside.json" *inside* the shelf, if anything.
            await Assert.That(ConfigProfiles.Delete("../outside")).IsTrue();
            await Assert.That(File.Exists(Path.Combine(root, "outside.json"))).IsFalse();
        });
    }
}
