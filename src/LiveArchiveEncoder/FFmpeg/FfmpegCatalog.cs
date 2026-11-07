using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.FFmpeg;

public enum ArchiveKind
{
    Zip,
    SevenZip,
    TarXz,
}

/// <summary>Which operating system a distribution's binaries are built for.</summary>
public enum DistPlatform
{
    Windows,
    Linux,
}

/// <summary>
/// One FFmpeg distribution the tool can install and switch between.
///
/// Everything here points at the <b>daily / rolling</b> artefact of that
/// project: this tool exists to exercise legacy encoders, and the rolling build
/// is the one that still carries all of them.
///
/// The same <see cref="Id"/> may appear once per <see cref="Platform"/>: "btbn"
/// is the Windows build on Windows and the linux64 build on Linux, so a config
/// written on one machine keeps working on the other.
/// </summary>
public sealed class FfmpegDist
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Homepage { get; init; }
    public required ArchiveKind Archive { get; init; }

    /// <summary>True when the artefact is rebuilt from master rather than cut from a release tag.</summary>
    public required bool Daily { get; init; }

    /// <summary>Operating system this build targets. Windows-only sources are hidden elsewhere.</summary>
    public DistPlatform Platform { get; init; } = DistPlatform.Windows;

    /// <summary>
    /// Fixed download URL for an automatic install, or null for a source that
    /// has to be fetched by hand.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// v7.9.8.13.1: a GitHub <c>releases/latest</c> API URL for sources whose
    /// asset name changes with every build (AnimMouse tags every commit with a
    /// hash). Resolved at install time; <see cref="AssetPattern"/> picks the asset.
    /// </summary>
    public string? FeedUrl { get; init; }

    /// <summary>Case-insensitive regex an asset name from the feed must match.</summary>
    public string? AssetPattern { get; init; }

    /// <summary>
    /// Stable release used only when the daily feed cannot be resolved. Every
    /// supported source publishes a daily/rolling build, so this is a fallback,
    /// not the normal path.
    /// </summary>
    public string? StableUrl { get; init; }

    /// <summary>True when this source can be fetched without a browser.</summary>
    public bool AutoDownload => !string.IsNullOrEmpty(Url) || !string.IsNullOrEmpty(FeedUrl);

    public string ArchiveExtension => Archive switch
    {
        ArchiveKind.Zip => ".zip",
        ArchiveKind.SevenZip => ".7z",
        _ => ".tar.xz",
    };
}

/// <summary>
/// The supported distributions, per platform.
///
/// v7.9.8.13.1: every asset was verified against the live projects on
/// 2026-11-07. The full comparison (release tags, assets, platforms, licence
/// and measured encoder lists) lives in <c>docs/toolchain-builds.md</c>.
///
///   btbn      -- BtbN/FFmpeg-Builds, release tag "latest" (daily master).
///                win64-gpl.zip / linux64-gpl.tar.xz. Default on both.
///   nonfree   -- BnqDzj/FFmpeg-Builds-nonfree, tag "latest"; adds fdk-aac.
///   ytdlp     -- yt-dlp/FFmpeg-Builds, tag "latest"; Win + Linux GPL.
///   autobuild -- AnimMouse/ffmpeg-autobuild, one tag per commit with a hash in
///                the asset name, resolved through releases/latest at install
///                time. <b>Windows only</b>: all 30 checked releases ship just
///                win32/win64 nonfree .7z files.
///   gyan      -- gyan.dev ffmpeg-git-full.7z (rolling git master, Win only).
///   avbuild   -- SourceForge, <b>manual</b> (Cloudflare interstitial). Kept for
///                compatibility with older config.json files.
///
/// Daily/rolling builds are always preferred. A fixed-URL source has no
/// fallback (there is nothing more stable to fall back to); a feed-driven
/// source (autobuild) uses StableUrl -- a pinned, verified commit build --
/// when releases/latest cannot be read or matched. Linux .tar.xz archives
/// are extracted with tar, .7z with the official
/// ip7z/7zip build (see FFmpeg.SevenZip), which is fetched automatically.
/// </summary>
public static partial class FfmpegCatalog
{
    /// <summary>Default when nothing is configured: the daily BtbN GPL build.</summary>
    public const string DefaultId = "btbn";

    public static readonly FfmpegDist[] All =
    [
        // ---- Windows ----
        new()
        {
            Id = "btbn",
            Label = "BtbN / FFmpeg-Builds (win64 GPL, daily auto-build)",
            Homepage = "https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest",
            Archive = ArchiveKind.Zip,
            Daily = true,
            Platform = DistPlatform.Windows,
            Url = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/"
                  + "ffmpeg-master-latest-win64-gpl.zip",
        },
        new()
        {
            Id = "gyan",
            Label = "gyan.dev (win64 full, git master build)",
            Homepage = "https://www.gyan.dev/ffmpeg/builds/",
            Archive = ArchiveKind.SevenZip,
            Daily = true,
            Platform = DistPlatform.Windows,
            Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-git-full.7z",
        },
        new()
        {
            Id = "avbuild",
            Label = "avbuild (SourceForge windows-desktop, master build) - manual download",
            Homepage = "https://sourceforge.net/projects/avbuild/files/windows-desktop/",
            Archive = ArchiveKind.SevenZip,
            Daily = true,
            Platform = DistPlatform.Windows,
            Url = null,
        },
        new()
        {
            Id = "nonfree",
            Label = "BnqDzj / FFmpeg-Builds-nonfree (win64 nonfree, daily auto-build)",
            Homepage = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/tag/latest",
            Archive = ArchiveKind.Zip,
            Daily = true,
            Platform = DistPlatform.Windows,
            Url = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/latest/download/"
                  + "ffmpeg-master-latest-win64-nonfree.zip",
        },
        new()
        {
            Id = "ytdlp",
            Label = "yt-dlp / FFmpeg-Builds (win64 GPL, daily auto-build)",
            Homepage = "https://github.com/yt-dlp/FFmpeg-Builds/releases/tag/latest",
            Archive = ArchiveKind.Zip,
            Daily = true,
            Platform = DistPlatform.Windows,
            Url = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/"
                  + "ffmpeg-master-latest-win64-gpl.zip",
        },
        new()
        {
            Id = "autobuild",
            Label = "AnimMouse / ffmpeg-autobuild (win64 nonfree, per-commit build)",
            Homepage = "https://github.com/AnimMouse/ffmpeg-autobuild/releases",
            Archive = ArchiveKind.SevenZip,
            Daily = true,
            Platform = DistPlatform.Windows,
            // This project tags every commit with a date+hash, so the asset
            // name cannot be a constant. Resolve releases/latest at install
            // time. Verified 2026-11-07: only win32 / win64 assets exist.
            FeedUrl = "https://api.github.com/repos/AnimMouse/ffmpeg-autobuild/releases/latest",
            AssetPattern = "win64.*nonfree.*\\.7z$",
            // v7.9.8.13.2: without this, a feed outage made the "stable
            // fallback" dead code. This is a verified, pinned commit build;
            // it is only used when releases/latest cannot be read or matched.
            StableUrl = "https://github.com/AnimMouse/ffmpeg-autobuild/releases/download/"
                      + "2026-11-07-00-17-e196e57-89ebf7b/"
                      + "ffmpeg-e196e57-89ebf7b-win64-nonfree.7z",
        },

        // ---- Linux ----
        new()
        {
            Id = "btbn",
            Label = "BtbN / FFmpeg-Builds (linux64 GPL, daily auto-build)",
            Homepage = "https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest",
            Archive = ArchiveKind.TarXz,
            Daily = true,
            Platform = DistPlatform.Linux,
            // v7.9.8.11: same "latest daily" URL shape as the Windows entry.
            Url = "https://github.com/btbn/ffmpeg-builds/releases/latest/download/"
                  + "ffmpeg-master-latest-linux64-gpl.tar.xz",
        },
        new()
        {
            Id = "avbuild",
            Label = "avbuild (SourceForge linux-desktop, master build) - manual download",
            Homepage = "https://sourceforge.net/projects/avbuild/files/linux-desktop/",
            Archive = ArchiveKind.SevenZip,
            Daily = true,
            Platform = DistPlatform.Linux,
            Url = null,
        },
        new()
        {
            Id = "nonfree",
            Label = "BnqDzj / FFmpeg-Builds-nonfree (linux64 nonfree, daily auto-build)",
            Homepage = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/tag/latest",
            Archive = ArchiveKind.TarXz,
            Daily = true,
            Platform = DistPlatform.Linux,
            Url = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/latest/download/"
                  + "ffmpeg-master-latest-linux64-nonfree.tar.xz",
        },
        new()
        {
            Id = "ytdlp",
            Label = "yt-dlp / FFmpeg-Builds (linux64 GPL, daily auto-build)",
            Homepage = "https://github.com/yt-dlp/FFmpeg-Builds/releases/tag/latest",
            Archive = ArchiveKind.TarXz,
            Daily = true,
            Platform = DistPlatform.Linux,
            Url = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/"
                  + "ffmpeg-master-latest-linux64-gpl.tar.xz",
        },
    ];

    /// <summary>What this process is running on. Anything non-Windows is treated as Linux.</summary>
    public static DistPlatform Current =>
        OperatingSystem.IsWindows() ? DistPlatform.Windows : DistPlatform.Linux;

    /// <summary>The distributions that can actually be installed here.</summary>
    public static IEnumerable<FfmpegDist> Available => All.Where(d => d.Platform == Current);

    /// <summary>Case-insensitive lookup among <see cref="Available"/>; null when unusable here.</summary>
    public static FfmpegDist? Find(string? id)
    {
        if (!id.HasText) return null;
        foreach (FfmpegDist d in Available)
        {
            if (string.Equals(d.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) return d;
        }
        return null;
    }

    /// <summary>Lookup across every platform, for diagnostics and tests.</summary>
    public static FfmpegDist? FindKnown(string? id) => FindKnown(id, Current);

    public static FfmpegDist? FindKnown(string? id, DistPlatform platform)
    {
        if (!id.HasText) return null;
        foreach (FfmpegDist d in All)
        {
            if (d.Platform == platform
                && string.Equals(d.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }
        return null;
    }

    /// <summary>Ids offered on this platform, for usage/error text.</summary>
    public static string Ids => string.Join(", ", Available.Select(d => d.Id).Distinct());

    /// <summary>Sources whose archive can be fetched without a browser, on this platform.</summary>
    public static string AutomaticIds =>
        string.Join(", ", Available.Where(d => d.AutoDownload).Select(d => d.Id).Distinct());

    /// <summary>Guess the archive kind from a file name; null when it is neither.</summary>
    public static ArchiveKind? KindOfFile(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.Zip
        : path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.SevenZip
        : path.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.TarXz
        : path.EndsWith(".txz", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.TarXz
        : null;
}
