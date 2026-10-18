using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Remote;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// The distribution table. No network: these assert the metadata (ids, defaults,
/// which sources can be fetched automatically) and the archive-kind mapping.
///
/// The catalogue holds one entry per id <em>per platform</em>, so every
/// assertion that names a platform uses <c>FindKnown(id, platform)</c> and
/// therefore passes on both Windows and Linux. Assertions about "what is
/// offered here" go through <see cref="FfmpegCatalog.Available"/>.
/// </summary>
[TestFixture]
public sealed class FfmpegCatalogTests
{
    private const DistPlatform Win = DistPlatform.Windows;
    private const DistPlatform Lin = DistPlatform.Linux;

    [Test]
    public void DefaultIsTheBtbnDailyBuild()
    {
        Assert.That(FfmpegCatalog.DefaultId, Is.EqualTo("btbn"));
        Assert.That(FfmpegCatalog.Find(FfmpegCatalog.DefaultId), Is.Not.Null);
        Assert.That(FfmpegCatalog.Find(FfmpegCatalog.DefaultId)!.Daily, Is.True);
    }

    [TestCase("btbn")]
    [TestCase("BTBN")]
    [TestCase("  nonfree  ")]
    public void Find_IsCaseAndWhitespaceInsensitive(string id) =>
        Assert.That(FfmpegCatalog.Find(id), Is.Not.Null);

    [Test]
    public void Find_GyanOnlyOnWindows()
    {
        // gyan.dev publishes no Linux build, so the source cannot leak into
        // the Linux menu -- and must still be there on Windows.
        Assert.That(FfmpegCatalog.FindKnown("gyan", Win), Is.Not.Null);
        Assert.That(FfmpegCatalog.FindKnown("gyan", Lin), Is.Null);

        Assert.That(FfmpegCatalog.Find("gyan") is not null,
            Is.EqualTo(FfmpegCatalog.Current == Win));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    [TestCase("not-a-source")]
    public void Find_UnknownGivesNull(string? id) =>
        Assert.That(FfmpegCatalog.Find(id), Is.Null);

    /// <summary>
    /// The id set is per platform: gyan is the only source without a Linux
    /// build, everything else mirrors across.
    /// </summary>
    [Test]
    public void SourcesAreUniquePerPlatform()
    {
        Assert.That(FfmpegCatalog.All.Where(d => d.Platform == Win).Select(d => d.Id),
            Is.EquivalentTo(new[] { "btbn", "gyan", "avbuild", "nonfree" }));
        Assert.That(FfmpegCatalog.All.Where(d => d.Platform == Lin).Select(d => d.Id),
            Is.EquivalentTo(new[] { "btbn", "avbuild", "nonfree" }));

        // Available is exactly this platform's slice, with no duplicate ids --
        // a duplicate would make Installed() list the same build twice.
        string[] offered = FfmpegCatalog.Available.Select(d => d.Id).ToArray();
        Assert.That(offered.Distinct().Count(), Is.EqualTo(offered.Length));
        Assert.That(offered, Does.Contain("btbn"));
    }

    /// <summary>
    /// SourceForge serves a Cloudflare interstitial instead of the archive, so
    /// avbuild is deliberately the one source without an automatic URL -- on
    /// either platform.
    /// </summary>
    [Test]
    public void OnlyAvbuildNeedsAManualDownload()
    {
        Assert.That(FfmpegCatalog.All.Where(d => !d.AutoDownload).Select(d => d.Id).Distinct(),
            Is.EquivalentTo(new[] { "avbuild" }));
        Assert.That(FfmpegCatalog.AutomaticIds, Does.Contain("btbn"));
        Assert.That(FfmpegCatalog.AutomaticIds, Does.Not.Contain("avbuild"));
    }

    [Test]
    public void AutomaticSourcesPointAtTheRollingArtefact()
    {
        Assert.That(FfmpegCatalog.FindKnown("btbn", Win)!.Url,
            Does.Contain("ffmpeg-master-latest-win64-gpl.zip"));
        Assert.That(FfmpegCatalog.FindKnown("gyan", Win)!.Url, Does.Contain("ffmpeg-git-full.7z"));
        Assert.That(FfmpegCatalog.FindKnown("nonfree", Win)!.Url,
            Does.Contain("ffmpeg-master-latest-win64-nonfree.zip"));

        Assert.That(FfmpegCatalog.FindKnown("btbn", Lin)!.Url,
            Does.Contain("ffmpeg-master-latest-linux64-gpl.tar.xz"));
        Assert.That(FfmpegCatalog.FindKnown("nonfree", Lin)!.Url,
            Does.Contain("ffmpeg-master-latest-linux64-nonfree.tar.xz"));
    }

    [Test]
    public void ArchiveKindMatchesTheExtensionTheSourcePublishes()
    {
        Assert.That(FfmpegCatalog.FindKnown("btbn", Win)!.Archive, Is.EqualTo(ArchiveKind.Zip));
        Assert.That(FfmpegCatalog.FindKnown("gyan", Win)!.Archive, Is.EqualTo(ArchiveKind.SevenZip));
        Assert.That(FfmpegCatalog.FindKnown("avbuild", Win)!.Archive, Is.EqualTo(ArchiveKind.SevenZip));

        Assert.That(FfmpegCatalog.FindKnown("btbn", Lin)!.Archive, Is.EqualTo(ArchiveKind.TarXz));
        Assert.That(FfmpegCatalog.FindKnown("nonfree", Lin)!.Archive, Is.EqualTo(ArchiveKind.TarXz));

        Assert.That(FfmpegCatalog.FindKnown("btbn", Win)!.ArchiveExtension, Is.EqualTo(".zip"));
        Assert.That(FfmpegCatalog.FindKnown("gyan", Win)!.ArchiveExtension, Is.EqualTo(".7z"));
        Assert.That(FfmpegCatalog.FindKnown("btbn", Lin)!.ArchiveExtension, Is.EqualTo(".tar.xz"));
    }

    [TestCase("build.zip", ArchiveKind.Zip)]
    [TestCase("BUILD.ZIP", ArchiveKind.Zip)]
    [TestCase("build.7z", ArchiveKind.SevenZip)]
    [TestCase("build.tar.xz", ArchiveKind.TarXz)]
    [TestCase("build.TXZ", ArchiveKind.TarXz)]
    public void KindOfFile_ReadsTheExtension(string name, ArchiveKind expected) =>
        Assert.That(FfmpegCatalog.KindOfFile(name), Is.EqualTo(expected));

    [TestCase("build")]
    [TestCase("build.tar")]
    [TestCase("build.xz")]
    [TestCase("")]
    public void KindOfFile_RejectsAnythingElse(string name) =>
        Assert.That(FfmpegCatalog.KindOfFile(name), Is.Null);

    /// <summary>TarXz is the format every Linux source ships; the extractor and
    /// the magic-byte check both key off this.</summary>
    [Test]
    public void LinuxSourcesAllUseTarXz() =>
        Assert.That(FfmpegCatalog.All.Where(d => d.Platform == Lin && d.AutoDownload)
            .Select(d => d.Archive), Is.All.EqualTo(ArchiveKind.TarXz));
}

/// <summary>
/// SMB path handling for the Unix forms: <c>smb://</c> URLs and CIFS mounts.
///
/// Everything here is pure string/option parsing, so it runs on Windows too --
/// that is the point, because the Linux half has to be covered by CI runners
/// nobody logs into.
/// </summary>
[TestFixture]
public sealed class SmbUnixTests
{
    [TestCase("smb://nas/share/clip.mp4")]
    [TestCase("SMB://nas/share/clip.mp4")]
    [TestCase("smb://nas/share")]
    public void IsSmbUrl_AcceptsTheUrlForm(string path)
    {
        Assert.That(Smb.IsSmbUrl(path), Is.True);
        Assert.That(Smb.IsUncPath(path), Is.True, "a URL still counts as remote input");
        Assert.That(Smb.IsRemotePath(path), Is.True);
    }

    [TestCase(@"\\nas\share\clip.mp4")]
    [TestCase("C:\\video\\clip.mp4")]
    [TestCase("")]
    [TestCase(null)]
    public void IsSmbUrl_RejectsLocalAndUnc(string? path) =>
        Assert.That(Smb.IsSmbUrl(path), Is.False);

    [Test]
    public void Split_ReadsServerShareAndRestFromAUrl()
    {
        var parts = Smb.Split("smb://nas/share/deep/clip.mp4");

        Assert.That(parts, Is.Not.Null);
        Assert.That(parts!.Value.Server, Is.EqualTo("nas"));
        Assert.That(parts.Value.Share, Is.EqualTo("share"));
        Assert.That(parts.Value.Remainder, Is.EqualTo("deep/clip.mp4"));
    }

    [Test]
    public void ShareRoot_KeepsTheUrlForm()
    {
        Assert.That(Smb.ShareRoot("smb://nas/share/deep/clip.mp4"), Is.EqualTo("smb://nas/share"));
        Assert.That(Smb.ShareRoot(@"\\nas\share\deep\clip.mp4"), Is.EqualTo(@"\\nas\share"));
    }

    [TestCase("vers=3.1.1", SmbDialect.Smb3)]
    [TestCase("rw,vers=3.02,sec=ntlmssp", SmbDialect.Smb3)]
    [TestCase("rw,vers=2.1", SmbDialect.Smb2)]
    [TestCase("rw,vers=1.0", SmbDialect.Smb1)]
    [TestCase("rw", SmbDialect.Unknown)]
    public void DialectFromOptions_ReadsTheKernelVersOption(string options, SmbDialect expected) =>
        Assert.That(Smb.DialectFromOptions(options), Is.EqualTo(expected));

    /// <summary>
    /// The whole point of the hint: FFmpeg has no SMB protocol on any platform,
    /// so "input not found" twenty seconds into an encode is useless next to
    /// "mount it first".
    /// </summary>
    [Test]
    public void UnreachableHint_IsNullForEverydayPaths()
    {
        Assert.That(Smb.UnreachableHint("/home/ci/clip.mp4"), Is.Null);
        Assert.That(Smb.UnreachableHint(@"C:\video\clip.mp4"), Is.Null);
        Assert.That(Smb.UnreachableHint(null), Is.Null);
    }

    [Test]
    public void UnreachableHint_SuggestsAMountOrAUnc()
    {
        string? hint = Smb.UnreachableHint("smb://nas/share/clip.mp4");

        Assert.That(hint, Is.Not.Null);
        Assert.That(hint, OperatingSystem.IsWindows()
            ? Does.Contain(@"\\nas\share")
            : Does.Contain("mount -t cifs //nas/share"));
    }

    [TestCase("smb://nas/share/clip.mp4")]
    [TestCase(@"\\nas\share\clip.mp4")]
    public void RemoteArgs_ApplyToBothRemoteSpellings(string path)
    {
        Assert.That(Smb.RemoteReadArgs(path), Does.Contain("-rw_timeout"));
        Assert.That(Smb.RemoteWriteArgs(path), Does.Contain("-rw_timeout"));
        Assert.That(Smb.RemoteProbeArgs(path), Does.Contain("-rw_timeout"));
    }
}
