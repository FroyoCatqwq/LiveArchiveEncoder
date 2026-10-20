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
public sealed class FfmpegCatalogTests
{
    private const DistPlatform Win = DistPlatform.Windows;
    private const DistPlatform Lin = DistPlatform.Linux;

    [Test]
    public async Task DefaultIsTheBtbnDailyBuild()
    {
        await Assert.That(FfmpegCatalog.DefaultId).IsEqualTo("btbn");
        await Assert.That(FfmpegCatalog.Find(FfmpegCatalog.DefaultId)).IsNotNull();
        await Assert.That(FfmpegCatalog.Find(FfmpegCatalog.DefaultId)!.Daily).IsTrue();
    }

    [Test]
    [Arguments("btbn")]
    [Arguments("BTBN")]
    [Arguments("  nonfree  ")]
    public async Task Find_IsCaseAndWhitespaceInsensitive(string id) =>
        await Assert.That(FfmpegCatalog.Find(id)).IsNotNull();

    [Test]
    public async Task Find_GyanOnlyOnWindows()
    {
        // gyan.dev publishes no Linux build, so the source cannot leak into
        // the Linux menu -- and must still be there on Windows.
        await Assert.That(FfmpegCatalog.FindKnown("gyan", Win)).IsNotNull();
        await Assert.That(FfmpegCatalog.FindKnown("gyan", Lin)).IsNull();

        await Assert.That(FfmpegCatalog.Find("gyan") is not null).IsEqualTo(FfmpegCatalog.Current == Win);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    [Arguments("not-a-source")]
    public async Task Find_UnknownGivesNull(string? id) =>
        await Assert.That(FfmpegCatalog.Find(id)).IsNull();

    /// <summary>
    /// The id set is per platform: gyan is the only source without a Linux
    /// build, everything else mirrors across.
    /// </summary>
    [Test]
    public async Task SourcesAreUniquePerPlatform()
    {
        await Assert.That(FfmpegCatalog.All.Where(d => d.Platform == Win).Select(d => d.Id)).IsEquivalentTo(new[] { "btbn", "gyan", "avbuild", "nonfree" });
        await Assert.That(FfmpegCatalog.All.Where(d => d.Platform == Lin).Select(d => d.Id)).IsEquivalentTo(new[] { "btbn", "avbuild", "nonfree" });

        // Available is exactly this platform's slice, with no duplicate ids --
        // a duplicate would make Installed() list the same build twice.
        string[] offered = FfmpegCatalog.Available.Select(d => d.Id).ToArray();
        await Assert.That(offered.Distinct().Count()).IsEqualTo(offered.Length);
        await Assert.That(offered).Contains("btbn");
    }

    /// <summary>
    /// SourceForge serves a Cloudflare interstitial instead of the archive, so
    /// avbuild is deliberately the one source without an automatic URL -- on
    /// either platform.
    /// </summary>
    [Test]
    public async Task OnlyAvbuildNeedsAManualDownload()
    {
        await Assert.That(FfmpegCatalog.All.Where(d => !d.AutoDownload).Select(d => d.Id).Distinct()).IsEquivalentTo(new[] { "avbuild" });
        await Assert.That(FfmpegCatalog.AutomaticIds).Contains("btbn");
        await Assert.That(FfmpegCatalog.AutomaticIds).DoesNotContain("avbuild");
    }

    [Test]
    public async Task AutomaticSourcesPointAtTheRollingArtefact()
    {
        await Assert.That(FfmpegCatalog.FindKnown("btbn", Win)!.Url).Contains("ffmpeg-master-latest-win64-gpl.zip");
        await Assert.That(FfmpegCatalog.FindKnown("gyan", Win)!.Url).Contains("ffmpeg-git-full.7z");
        await Assert.That(FfmpegCatalog.FindKnown("nonfree", Win)!.Url).Contains("ffmpeg-master-latest-win64-nonfree.zip");

        await Assert.That(FfmpegCatalog.FindKnown("btbn", Lin)!.Url).Contains("ffmpeg-master-latest-linux64-gpl.tar.xz");
        await Assert.That(FfmpegCatalog.FindKnown("nonfree", Lin)!.Url).Contains("ffmpeg-master-latest-linux64-nonfree.tar.xz");
    }

    [Test]
    public async Task ArchiveKindMatchesTheExtensionTheSourcePublishes()
    {
        await Assert.That(FfmpegCatalog.FindKnown("btbn", Win)!.Archive).IsEqualTo(ArchiveKind.Zip);
        await Assert.That(FfmpegCatalog.FindKnown("gyan", Win)!.Archive).IsEqualTo(ArchiveKind.SevenZip);
        await Assert.That(FfmpegCatalog.FindKnown("avbuild", Win)!.Archive).IsEqualTo(ArchiveKind.SevenZip);

        await Assert.That(FfmpegCatalog.FindKnown("btbn", Lin)!.Archive).IsEqualTo(ArchiveKind.TarXz);
        await Assert.That(FfmpegCatalog.FindKnown("nonfree", Lin)!.Archive).IsEqualTo(ArchiveKind.TarXz);

        await Assert.That(FfmpegCatalog.FindKnown("btbn", Win)!.ArchiveExtension).IsEqualTo(".zip");
        await Assert.That(FfmpegCatalog.FindKnown("gyan", Win)!.ArchiveExtension).IsEqualTo(".7z");
        await Assert.That(FfmpegCatalog.FindKnown("btbn", Lin)!.ArchiveExtension).IsEqualTo(".tar.xz");
    }

    [Test]
    [Arguments("build.zip", ArchiveKind.Zip)]
    [Arguments("BUILD.ZIP", ArchiveKind.Zip)]
    [Arguments("build.7z", ArchiveKind.SevenZip)]
    [Arguments("build.tar.xz", ArchiveKind.TarXz)]
    [Arguments("build.TXZ", ArchiveKind.TarXz)]
    public async Task KindOfFile_ReadsTheExtension(string name, ArchiveKind expected) =>
        await Assert.That(FfmpegCatalog.KindOfFile(name)).IsEqualTo(expected);

    [Test]
    [Arguments("build")]
    [Arguments("build.tar")]
    [Arguments("build.xz")]
    [Arguments("")]
    public async Task KindOfFile_RejectsAnythingElse(string name) =>
        await Assert.That(FfmpegCatalog.KindOfFile(name)).IsNull();

    /// <summary>TarXz is the format every Linux source ships; the extractor and
    /// the magic-byte check both key off this.</summary>
    [Test]
    public async Task LinuxSourcesAllUseTarXz()
    {
        foreach (ArchiveKind archive in FfmpegCatalog.All.Where(d => d.Platform == Lin && d.AutoDownload)
            .Select(d => d.Archive))
        {
            await Assert.That(archive).IsEqualTo(ArchiveKind.TarXz);
        }
    }
}

/// <summary>
/// SMB path handling for the Unix forms: <c>smb://</c> URLs and CIFS mounts.
///
/// Everything here is pure string/option parsing, so it runs on Windows too --
/// that is the point, because the Linux half has to be covered by CI runners
/// nobody logs into.
/// </summary>
public sealed class SmbUnixTests
{
    [Test]
    [Arguments("smb://nas/share/clip.mp4")]
    [Arguments("SMB://nas/share/clip.mp4")]
    [Arguments("smb://nas/share")]
    public async Task IsSmbUrl_AcceptsTheUrlForm(string path)
    {
        await Assert.That(Smb.IsSmbUrl(path)).IsTrue();
        await Assert.That(Smb.IsUncPath(path)).IsTrue().Because("a URL still counts as remote input");
        await Assert.That(Smb.IsRemotePath(path)).IsTrue();
    }

    [Test]
    [Arguments(@"\\nas\share\clip.mp4")]
    [Arguments("C:\\video\\clip.mp4")]
    [Arguments("")]
    [Arguments(null)]
    public async Task IsSmbUrl_RejectsLocalAndUnc(string? path) =>
        await Assert.That(Smb.IsSmbUrl(path)).IsFalse();

    [Test]
    public async Task Split_ReadsServerShareAndRestFromAUrl()
    {
        var parts = Smb.Split("smb://nas/share/deep/clip.mp4");

        await Assert.That(parts).IsNotNull();
        await Assert.That(parts!.Value.Server).IsEqualTo("nas");
        await Assert.That(parts.Value.Share).IsEqualTo("share");
        await Assert.That(parts.Value.Remainder).IsEqualTo("deep/clip.mp4");
    }

    [Test]
    public async Task ShareRoot_KeepsTheUrlForm()
    {
        await Assert.That(Smb.ShareRoot("smb://nas/share/deep/clip.mp4")).IsEqualTo("smb://nas/share");
        await Assert.That(Smb.ShareRoot(@"\\nas\share\deep\clip.mp4")).IsEqualTo(@"\\nas\share");
    }

    [Test]
    [Arguments("vers=3.1.1", SmbDialect.Smb3)]
    [Arguments("rw,vers=3.02,sec=ntlmssp", SmbDialect.Smb3)]
    [Arguments("rw,vers=2.1", SmbDialect.Smb2)]
    [Arguments("rw,vers=1.0", SmbDialect.Smb1)]
    [Arguments("rw", SmbDialect.Unknown)]
    public async Task DialectFromOptions_ReadsTheKernelVersOption(string options, SmbDialect expected) =>
        await Assert.That(Smb.DialectFromOptions(options)).IsEqualTo(expected);

    /// <summary>
    /// The whole point of the hint: FFmpeg has no SMB protocol on any platform,
    /// so "input not found" twenty seconds into an encode is useless next to
    /// "mount it first".
    /// </summary>
    [Test]
    public async Task UnreachableHint_IsNullForEverydayPaths()
    {
        await Assert.That(Smb.UnreachableHint("/home/ci/clip.mp4")).IsNull();
        await Assert.That(Smb.UnreachableHint(@"C:\video\clip.mp4")).IsNull();
        await Assert.That(Smb.UnreachableHint(null)).IsNull();
    }

    [Test]
    public async Task UnreachableHint_SuggestsAMountOrAUnc()
    {
        string? hint = Smb.UnreachableHint("smb://nas/share/clip.mp4");

        await Assert.That(hint).IsNotNull();
        // The hint names a UNC path on Windows and a mount command elsewhere;
        // the original picked the expectation with a conditional constraint,
        // which TUnit's fluent API has no room for -- pick the text instead.
        string expected = OperatingSystem.IsWindows()
            ? @"\\nas\share"
            : "mount -t cifs //nas/share";
        await Assert.That(hint).Contains(expected);
    }

    [Test]
    [Arguments("smb://nas/share/clip.mp4")]
    [Arguments(@"\\nas\share\clip.mp4")]
    public async Task RemoteArgs_ApplyToBothRemoteSpellings(string path)
    {
        await Assert.That(Smb.RemoteReadArgs(path)).Contains("-rw_timeout");
        await Assert.That(Smb.RemoteWriteArgs(path)).Contains("-rw_timeout");
        await Assert.That(Smb.RemoteProbeArgs(path)).Contains("-rw_timeout");
    }
}
