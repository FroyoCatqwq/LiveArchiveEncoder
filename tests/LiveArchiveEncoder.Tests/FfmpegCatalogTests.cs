using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// The distribution table. No network: these assert the metadata (ids, defaults,
/// which sources can be fetched automatically) and the archive-kind mapping.
/// </summary>
[TestFixture]
public sealed class FfmpegCatalogTests
{
    [Test]
    public void DefaultIsTheBtbnDailyBuild()
    {
        Assert.That(FfmpegCatalog.DefaultId, Is.EqualTo("btbn"));
        Assert.That(FfmpegCatalog.Find(FfmpegCatalog.DefaultId), Is.Not.Null);
        Assert.That(FfmpegCatalog.Find(FfmpegCatalog.DefaultId)!.Daily, Is.True);
    }

    [TestCase("btbn")]
    [TestCase("BTBN")]
    [TestCase("  gyan  ")]
    public void Find_IsCaseAndWhitespaceInsensitive(string id) =>
        Assert.That(FfmpegCatalog.Find(id), Is.Not.Null);

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    [TestCase("not-a-source")]
    public void Find_UnknownGivesNull(string? id) =>
        Assert.That(FfmpegCatalog.Find(id), Is.Null);

    [Test]
    public void AllSourcesAreUniqueAndCoverTheFourProjects()
    {
        Assert.That(FfmpegCatalog.All.Select(d => d.Id),
            Is.EquivalentTo(new[] { "btbn", "gyan", "avbuild", "nonfree" }));
    }

    /// <summary>
    /// SourceForge serves a Cloudflare interstitial instead of the archive, so
    /// avbuild is deliberately the one source without an automatic URL.
    /// </summary>
    [Test]
    public void OnlyAvbuildNeedsAManualDownload()
    {
        Assert.That(FfmpegCatalog.All.Where(d => !d.AutoDownload).Select(d => d.Id),
            Is.EquivalentTo(new[] { "avbuild" }));
        Assert.That(FfmpegCatalog.AutomaticIds, Does.Contain("btbn"));
        Assert.That(FfmpegCatalog.AutomaticIds, Does.Not.Contain("avbuild"));
    }

    [Test]
    public void AutomaticSourcesPointAtTheRollingArtefact()
    {
        Assert.That(FfmpegCatalog.Find("btbn")!.Url,
            Does.Contain("ffmpeg-master-latest-win64-gpl.zip"));
        Assert.That(FfmpegCatalog.Find("gyan")!.Url, Does.Contain("ffmpeg-git-full.7z"));
        Assert.That(FfmpegCatalog.Find("nonfree")!.Url,
            Does.Contain("ffmpeg-master-latest-win64-nonfree.zip"));
    }

    [Test]
    public void ArchiveKindMatchesTheExtensionTheSourcePublishes()
    {
        Assert.That(FfmpegCatalog.Find("btbn")!.Archive, Is.EqualTo(ArchiveKind.Zip));
        Assert.That(FfmpegCatalog.Find("gyan")!.Archive, Is.EqualTo(ArchiveKind.SevenZip));
        Assert.That(FfmpegCatalog.Find("avbuild")!.Archive, Is.EqualTo(ArchiveKind.SevenZip));

        Assert.That(FfmpegCatalog.Find("btbn")!.ArchiveExtension, Is.EqualTo(".zip"));
        Assert.That(FfmpegCatalog.Find("gyan")!.ArchiveExtension, Is.EqualTo(".7z"));
    }

    [TestCase("build.zip", ArchiveKind.Zip)]
    [TestCase("BUILD.ZIP", ArchiveKind.Zip)]
    [TestCase("build.7z", ArchiveKind.SevenZip)]
    public void KindOfFile_ReadsTheExtension(string name, ArchiveKind expected) =>
        Assert.That(FfmpegCatalog.KindOfFile(name), Is.EqualTo(expected));

    [TestCase("build.tar.xz")]
    [TestCase("build")]
    [TestCase("")]
    public void KindOfFile_RejectsAnythingElse(string name) =>
        Assert.That(FfmpegCatalog.KindOfFile(name), Is.Null);
}
