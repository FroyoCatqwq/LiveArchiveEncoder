using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>v7.9.8.13.1: the unified toolchain catalogue and 7-Zip resolver.</summary>
public sealed class ToolchainBuildsTests
{
    [Test]
    public async Task BothPlatformsUseTheSameOfficialSevenZipRelease()
    {
        await Assert.That(SevenZip.AssetFor(DistPlatform.Windows)).IsEqualTo("7z2605-x64.exe");
        await Assert.That(SevenZip.AssetFor(DistPlatform.Linux))
            .IsEqualTo("7z2605-linux-x64.tar.xz");
        await Assert.That(SevenZip.UrlFor(DistPlatform.Windows))
            .Contains("github.com/ip7z/7zip/releases/download/");
        await Assert.That(SevenZip.UrlFor(DistPlatform.Linux))
            .Contains("github.com/ip7z/7zip/releases/download/");
        await Assert.That(SevenZip.ExeNameFor(DistPlatform.Windows)).IsEqualTo("7z.exe");
        await Assert.That(SevenZip.ExeNameFor(DistPlatform.Linux)).IsEqualTo("7zz");
        await Assert.That(SevenZip.ReleasePage)
            .IsEqualTo("https://github.com/ip7z/7zip/releases");
    }

    [Test]
    public async Task TheNewSourcesAreDailyAndPlatformFiltered()
    {
        FfmpegDist ytdlpWin = FfmpegCatalog.FindKnown("ytdlp", DistPlatform.Windows)!;
        FfmpegDist ytdlpLin = FfmpegCatalog.FindKnown("ytdlp", DistPlatform.Linux)!;
        await Assert.That(ytdlpWin.Daily).IsTrue();
        await Assert.That(ytdlpLin.Daily).IsTrue();
        await Assert.That(ytdlpWin.Url!).Contains("yt-dlp/FFmpeg-Builds");
        await Assert.That(ytdlpWin.Archive).IsEqualTo(ArchiveKind.Zip);
        await Assert.That(ytdlpLin.Archive).IsEqualTo(ArchiveKind.TarXz);

        FfmpegDist auto = FfmpegCatalog.FindKnown("autobuild", DistPlatform.Windows)!;
        await Assert.That(auto.AutoDownload).IsTrue();
        await Assert.That(auto.FeedUrl!).Contains("releases/latest");
        await Assert.That(auto.Archive).IsEqualTo(ArchiveKind.SevenZip);
        // Verified 2026-11-07: AnimMouse publishes win32/win64 assets only.
        await Assert.That(FfmpegCatalog.FindKnown("autobuild", DistPlatform.Linux)).IsNull();
    }

    [Test]
    public async Task EveryAutomaticSourcePrefersADailyBuild()
    {
        foreach (FfmpegDist dist in FfmpegCatalog.All.Where(d => d.AutoDownload))
        {
            await Assert.That(dist.Daily).IsTrue();
            await Assert.That(dist.Url is { Length: > 0 }
                              || dist.FeedUrl is { Length: > 0 }).IsTrue();
        }
        // Only avbuild still needs --ffmpeg-archive: the old config id must
        // remain loadable.
        await Assert.That(FfmpegCatalog.All.Where(d => !d.AutoDownload)
            .Select(d => d.Id).Distinct()).IsEquivalentTo(new[] { "avbuild" });
    }

    [Test]
    public async Task APerCommitAssetReallyMatchesItsFeedPattern()
    {
        FfmpegDist auto = FfmpegCatalog.FindKnown("autobuild", DistPlatform.Windows)!;
        // The concrete asset name from the 2026-11-07 release.
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(
                "ffmpeg-e196e57-89ebf7b-win64-nonfree.7z", auto.AssetPattern!,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)).IsTrue();
        // The win32 asset and unrelated files must not be picked.
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(
                "ffmpeg-e196e57-89ebf7b-win32-nonfree.7z", auto.AssetPattern!,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)).IsFalse();
    }

    [Test]
    public async Task ResolveUsesTheFixedUrlWithoutTouchingTheNetwork()
    {
        FfmpegDist btbn = FfmpegCatalog.FindKnown("btbn", DistPlatform.Windows)!;
        (string url, string? error) = ReleaseFeed.Resolve(btbn, null, null, _ => { });
        await Assert.That(error).IsNull();
        await Assert.That(url).IsEqualTo(btbn.Url!);

        var stableOnly = new FfmpegDist
        {
            Id = "test", Label = "test", Homepage = "https://example.test",
            Archive = ArchiveKind.Zip, Daily = false,
            StableUrl = "https://example.test/stable.zip",
        };
        (string fallback, _) = ReleaseFeed.Resolve(stableOnly, null, null, _ => { });
        await Assert.That(fallback).IsEqualTo("https://example.test/stable.zip");
    }
}
