using System.Runtime.InteropServices;
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

    /// <summary>True when <see cref="Url"/> can be downloaded without a browser.</summary>
    public bool AutoDownload => Url is { Length: > 0 };

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
/// Verified against the live projects on 2026-10-18:
///
///   btbn    -- BtbN/FFmpeg-Builds (GitHub), release tagged "latest", an
///              auto-build refreshed from master every day. Windows ships
///              ffmpeg-master-latest-win64-gpl.zip, Linux ships
///              ffmpeg-master-latest-linux64-gpl.tar.xz. Automatic, and it is
///              the default on both platforms.
///   gyan    -- gyan.dev, ffmpeg-git-full.7z; the "-git" file is the rolling
///              master build. <b>Windows only</b> -- gyan.dev publishes no
///              Linux build, so the source is not offered there. Needs 7-Zip
///              (or any 7z on PATH).
///   avbuild -- SourceForge windows-desktop / linux-desktop folders.
///              <b>Manual.</b> SourceForge puts the mirror redirect behind a
///              Cloudflare "Just a moment..." interstitial: both aria2c and
///              HttpClient save the HTML page instead of the archive, and
///              spoofing a browser User-Agent gets a flat 403. Rather than ship
///              a downloader that silently writes a 131 KB HTML file and calls
///              it a build, this source asks you to download the archive and
///              then install it from disk.
///   nonfree -- BnqDzj/FFmpeg-Builds-nonfree (GitHub), release tagged "latest",
///              win64-nonfree.zip / linux64-nonfree.tar.xz. Adds fdk-aac and
///              friends; redistribution of those binaries is the user's problem.
///
/// Linux note: the .tar.xz sources are extracted with <c>tar</c>, so xz-utils /
/// xz must be present. The install says so when it is missing.
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

        // ---- Linux ----
        new()
        {
            Id = "btbn",
            Label = "BtbN / FFmpeg-Builds (linux64 GPL, daily auto-build)",
            Homepage = "https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest",
            Archive = ArchiveKind.TarXz,
            Daily = true,
            Platform = DistPlatform.Linux,
            Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/"
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
            Url = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/download/latest/"
                  + "ffmpeg-master-latest-linux64-nonfree.tar.xz",
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
