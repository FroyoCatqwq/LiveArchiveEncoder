namespace LiveArchiveEncoder.FFmpeg;

public enum ArchiveKind
{
    Zip,
    SevenZip,
}

/// <summary>
/// One FFmpeg distribution the tool can install and switch between.
///
/// Everything here points at the <b>daily / rolling</b> artefact of that
/// project: this tool exists to exercise legacy encoders, and the rolling build
/// is the one that still carries all of them.
/// </summary>
public sealed class FfmpegDist
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Homepage { get; init; }
    public required ArchiveKind Archive { get; init; }

    /// <summary>True when the artefact is rebuilt from master rather than cut from a release tag.</summary>
    public required bool Daily { get; init; }

    /// <summary>
    /// Fixed download URL for an automatic install, or null for a source that
    /// has to be fetched by hand.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>True when <see cref="Url"/> can be downloaded without a browser.</summary>
    public bool AutoDownload => Url is { Length: > 0 };

    public string ArchiveExtension => Archive == ArchiveKind.Zip ? ".zip" : ".7z";
}

/// <summary>
/// The four supported distributions.
///
/// Verified against the live projects on 2026-10-18:
///
///   btbn    -- BtbN/FFmpeg-Builds (GitHub), release tagged "latest", an
///              auto-build refreshed from master every day; asset
///              ffmpeg-master-latest-win64-gpl.zip. Automatic; zip, so no extra
///              tooling is needed. This is the default.
///   gyan    -- gyan.dev, ffmpeg-git-full.7z; the "-git" file is the rolling
///              master build. Automatic; needs 7-Zip (or any 7z on PATH).
///   avbuild -- SourceForge windows-desktop folder. <b>Manual.</b> SourceForge
///              puts the mirror redirect behind a Cloudflare "Just a moment..."
///              interstitial: both aria2c and HttpClient save the HTML page
///              instead of the archive, and spoofing a browser User-Agent gets a
///              flat 403. Rather than ship a downloader that silently writes a
///              131 KB HTML file and calls it a build, this source asks you to
///              download the archive and then install it from disk.
///   nonfree -- BnqDzj/FFmpeg-Builds-nonfree (GitHub), release tagged "latest",
///              asset ffmpeg-master-latest-win64-nonfree.zip. Adds fdk-aac and
///              friends; redistribution of those binaries is the user's problem.
/// </summary>
public static partial class FfmpegCatalog
{
    /// <summary>Default when nothing is configured: the daily BtbN GPL build.</summary>
    public const string DefaultId = "btbn";

    public static readonly FfmpegDist[] All =
    [
        new()
        {
            Id = "btbn",
            Label = "BtbN / FFmpeg-Builds (win64 GPL, daily auto-build)",
            Homepage = "https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest",
            Archive = ArchiveKind.Zip,
            Daily = true,
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
            Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-git-full.7z",
        },
        new()
        {
            Id = "avbuild",
            Label = "avbuild (SourceForge windows-desktop, master build) - manual download",
            Homepage = "https://sourceforge.net/projects/avbuild/files/windows-desktop/",
            Archive = ArchiveKind.SevenZip,
            Daily = true,
            Url = null,
        },
        new()
        {
            Id = "nonfree",
            Label = "BnqDzj / FFmpeg-Builds-nonfree (win64 nonfree, daily auto-build)",
            Homepage = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/tag/latest",
            Archive = ArchiveKind.Zip,
            Daily = true,
            Url = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/latest/download/"
                  + "ffmpeg-master-latest-win64-nonfree.zip",
        },
    ];

    /// <summary>Case-insensitive lookup; unknown ids give null rather than throwing.</summary>
    public static FfmpegDist? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (FfmpegDist d in All)
        {
            if (string.Equals(d.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) return d;
        }
        return null;
    }

    public static string Ids => string.Join(", ", All.Select(d => d.Id));

    /// <summary>Sources whose archive can be fetched without a browser.</summary>
    public static string AutomaticIds => string.Join(", ", All.Where(d => d.AutoDownload).Select(d => d.Id));

    /// <summary>Guess the archive kind from a file name; null when it is neither.</summary>
    public static ArchiveKind? KindOfFile(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.Zip
        : path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.SevenZip
        : null;
}
