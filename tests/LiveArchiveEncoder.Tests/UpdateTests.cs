using System.IO.Compression;
using LiveArchiveEncoder.Update;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.5: the updater's decisions, separated from the network and the file
/// system so they can be asserted.
///
/// <para>Three things must not go wrong here, because each of them ends with a
/// working program replaced by a broken one: a tag compared wrongly (offering an
/// older build as an upgrade), an asset picked for the wrong platform, and an
/// archive entry that escapes the staging directory.</para>
/// </summary>
public sealed class UpdateTests
{
    // ---- versions ----

    [Test]
    public async Task ATagLosesItsVPrefix()
    {
        VersionTag? tag = VersionTag.Parse("v7.9.8.5");

        await Assert.That(tag).IsNotNull();
        await Assert.That(tag!.ToString()).IsEqualTo("7.9.8.5");
    }

    [Test]
    public async Task TheFourthComponentIsSignificant()
    {
        VersionTag four = VersionTag.Parse("7.9.8.5")!;
        VersionTag three = VersionTag.Parse("v7.9.8")!;

        await Assert.That(four.IsNewerThan(three)).IsTrue();
        await Assert.That(three.IsNewerThan(four)).IsFalse();
    }

    [Test]
    public async Task AMissingComponentCountsAsZero()
    {
        VersionTag shorter = VersionTag.Parse("7.9.8")!;
        VersionTag padded = VersionTag.Parse("7.9.8.0")!;

        await Assert.That(shorter.CompareTo(padded)).IsEqualTo(0);
        await Assert.That(shorter == padded).IsTrue();
    }

    [Test]
    public async Task NumbersCompareAsNumbersNotText()
    {
        // 7.9.10 > 7.9.9 is the case string comparison gets wrong.
        await Assert.That(VersionTag.Parse("7.9.10")!.IsNewerThan(VersionTag.Parse("7.9.9")!)).IsTrue();
        await Assert.That(VersionTag.Parse("7.10")!.IsNewerThan(VersionTag.Parse("7.9.8")!)).IsTrue();
    }

    [Test]
    public async Task APreReleaseSuffixIsIgnored()
    {
        VersionTag tagged = VersionTag.Parse("v7.9.9-rc1")!;

        await Assert.That(tagged.ToString()).IsEqualTo("7.9.9");
        await Assert.That(tagged.CompareTo(VersionTag.Parse("7.9.9")!)).IsEqualTo(0);
    }

    [Test]
    public async Task GarbageIsNotAVersion()
    {
        foreach (string text in new[] { "", "latest", "v", "7..8", "7.x.1", "-1.2", "master" })
        {
            await Assert.That(VersionTag.Parse(text)).IsNull();
        }
    }

    // ---- the feed ----

    private const string Feed = """
        {
          "tag_name": "v7.9.8.6",
          "name": "v7.9.8.6 - a test release",
          "body": "notes",
          "html_url": "https://example.invalid/releases/tag/v7.9.8.6",
          "assets": [
            { "name": "LiveArchiveEncoder-v7.9.8.6-win-x64.zip",
              "size": 348432,
              "browser_download_url": "https://example.invalid/win.zip",
              "digest": "sha256:2245E549F98A1FE57FB8E9E56D8097C0FA7A5B7BAD27D8B3F4CB2329DD10DA4E" },
            { "name": "LiveArchiveEncoder-v7.9.8.6-win-x64-aot.zip",
              "size": 4056654,
              "browser_download_url": "https://example.invalid/win-aot.zip",
              "digest": "sha256:7eebf8376f651c7ea1fc4190148396fce5a2ed7ecf98e8e82687a14a3e7a5168" }
          ]
        }
        """;

    [Test]
    public async Task TheFeedBecomesARelease()
    {
        (Release? release, string? error) = SelfUpdate.Parse(Feed);

        await Assert.That(error).IsNull();
        await Assert.That(release).IsNotNull();
        await Assert.That(release!.Tag).IsEqualTo("v7.9.8.6");
        await Assert.That(release.Version.ToString()).IsEqualTo("7.9.8.6");
        await Assert.That(release.Assets.Count).IsEqualTo(2);
        await Assert.That(release.Assets[0].Size).IsEqualTo(348432L);
    }

    [Test]
    public async Task TheDigestLosesItsPrefixAndIsLowercased()
    {
        (Release? release, _) = SelfUpdate.Parse(Feed);

        await Assert.That(release!.Assets[0].Sha256)
            .IsEqualTo("2245e549f98a1fe57fb8e9e56d8097c0fa7a5b7bad27d8b3f4cb2329dd10da4e");
    }

    [Test]
    public async Task AnAssetIsChosenByPlatformAndFlavour()
    {
        (Release? release, _) = SelfUpdate.Parse(Feed);

        await Assert.That(SelfUpdate.Pick(release!, "-win-x64.zip")!.Url)
            .IsEqualTo("https://example.invalid/win.zip");
        await Assert.That(SelfUpdate.Pick(release!, "-win-x64-aot.zip")!.Url)
            .IsEqualTo("https://example.invalid/win-aot.zip");
        // A JIT request must not be satisfied by the AOT asset, and vice versa:
        // the suffix compare is what keeps a machine without the .NET runtime
        // from updating into a build that needs it.
        await Assert.That(SelfUpdate.Pick(release!, "-linux-x64.tar.gz")).IsNull();
    }

    [Test]
    public async Task ThePlatformSuffixNamesAnArchive()
    {
        string suffix = SelfUpdate.AssetSuffix();

        await Assert.That(suffix.StartsWith('-')).IsTrue();
        await Assert.That(suffix.EndsWith(".zip") || suffix.EndsWith(".tar.gz")).IsTrue();
        await Assert.That(suffix.Contains("x64") || suffix.Contains("arm64")).IsTrue();
    }

    [Test]
    public async Task AFeedWithoutATagIsRefused()
    {
        (Release? release, string? error) = SelfUpdate.Parse("""{"name":"no tag here"}""");

        await Assert.That(release).IsNull();
        await Assert.That(error).Contains("tag_name");
    }

    [Test]
    public async Task AFeedThatIsNotJsonIsRefused()
    {
        (Release? release, string? error) = SelfUpdate.Parse("<html>404</html>");

        await Assert.That(release).IsNull();
        await Assert.That(error).Contains("JSON");
    }

    [Test]
    public async Task AnAssetNameWithAPathSeparatorIsDropped()
    {
        const string hostile = """
            { "tag_name": "v9.9.9",
              "assets": [ { "name": "../../evil.zip", "size": 1,
                            "browser_download_url": "https://example.invalid/e.zip" } ] }
            """;

        (Release? release, _) = SelfUpdate.Parse(hostile);

        await Assert.That(release!.Assets.Count).IsEqualTo(0);
    }

    // ---- unpacking ----

    [Test]
    public async Task AZipCannotWriteOutsideItsDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "lae-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, "hostile.zip");
        string staging = Path.Combine(root, "stage");

        try
        {
            using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                Write(zip, "LiveArchiveEncoder.exe", "payload");
                Write(zip, "../escaped.txt", "should not exist");
                Write(zip, "nested/../../escaped2.txt", "should not exist either");
            }

            (bool ok, string? error) = SelfUpdate.Extract(archive, staging);

            await Assert.That(error).IsNull();
            await Assert.That(ok).IsTrue();
            await Assert.That(File.Exists(Path.Combine(staging, "LiveArchiveEncoder.exe"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(root, "escaped.txt"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escaped.txt"))).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task AnUnknownArchiveTypeIsRefused()
    {
        (bool ok, string? error) = SelfUpdate.Extract("build.7z", Path.Combine(Path.GetTempPath(), "lae-unused"));

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("unknown archive type");
    }

    [Test]
    public async Task TheSettingsFileKeepsTheUpdatePreference()
    {
        // Capture() rebuilds the snapshot from a run, so a member that is not
        // copied there is silently reset every time the shell saves.
        var config = new Configuration.Config { AutoUpdateCheck = false };

        await Assert.That(Configuration.ConfigStore.Capture(config).AutoUpdateCheck).IsFalse();
    }

    [Test]
    public async Task TurningTheCheckOffTravelsWithTheCommandLine()
    {
        // --emit-args exists to reproduce a settings file; a preference that does
        // not travel is a preference the replay silently overrides.
        var stored = new Configuration.UserConfig { AutoUpdateCheck = false };

        string line = UI.CliArgs.Join(UI.CliArgs.FromUserConfig(stored));

        await Assert.That(line).Contains("--no-update-check");
    }

    [Test]
    public async Task TheDefaultDoesNotAddAnythingToTheCommandLine()
    {
        var stored = new Configuration.UserConfig();

        string line = UI.CliArgs.Join(UI.CliArgs.FromUserConfig(stored));

        await Assert.That(line.Contains("--no-update-check")).IsFalse();
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }
}
