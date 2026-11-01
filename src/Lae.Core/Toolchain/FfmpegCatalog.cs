namespace Lae.Core.Toolchain;

/// <summary>How a distribution ships its binaries.</summary>
public enum ArchiveKind
{
    Zip,
    TarXz,
}

/// <summary>The operating system a distribution's binaries are built for.</summary>
public enum DistPlatform
{
    Windows,
    Linux,
}

/// <summary>
/// One FFmpeg distribution this tool can install and switch between.
///
/// <para>Every entry points at the <b>rolling</b> artefact of its project, not at a
/// cut release: this tool exists to exercise legacy encoders, and the archive
/// keeps being regenerated, so a pinned version would be the thing that stops
/// working.</para>
///
/// <para>The same <see cref="Id"/> appears once per <see cref="Platform"/>, so a
/// settings file written on one machine still names something on the other
/// instead of quietly falling back to the system's copy.</para>
/// </summary>
public sealed record FfmpegDist
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Homepage { get; init; }

    public required ArchiveKind Archive { get; init; }

    public required DistPlatform Platform { get; init; }

    /// <summary>The artefact to fetch. Always set: see the note on the catalogue.</summary>
    public required string Url { get; init; }

    public string ArchiveExtension => Archive == ArchiveKind.Zip ? ".zip" : ".tar.xz";
}

/// <summary>
/// What this tool offers to install.
///
/// <para><b>Two sources, and only two.</b> v7 also listed gyan.dev and avbuild.
/// gyan publishes no Linux build at all, and SourceForge keeps its mirrors behind
/// an interstitial that answers a browser user-agent with 403 and an automated
/// request with an HTML page -- so avbuild was a "download" that saved 131 kB of
/// HTML and called it a build, and had to be marked manual. Neither belongs in a
/// command whose whole job is to be one keystroke.</para>
///
/// <para><b>BtbN is the default and the recommendation</b> on both platforms: a
/// GPL snapshot rebuilt from master daily, which is the only kind of build that
/// still carries the pre-HD encoders this tool is about. <b>nonfree</b> is the
/// same thing plus fdk-aac and friends; redistributing those binaries is the
/// user's problem, so it is never chosen for them.</para>
///
/// <para>The URLs were checked against the live projects on 2026-11-01 and are
/// the <c>latest</c> tag assets, which is why they contain no version number.</para>
/// </summary>
public static class FfmpegCatalog
{
    /// <summary>The default when nothing is configured.</summary>
    public const string DefaultId = "btbn";

    /// <summary>
    /// The settings value meaning "whatever is on PATH". Not a distribution: it
    /// has no download, and naming it here keeps every "which source" question
    /// answered by one type.
    /// </summary>
    public const string PathId = "path";

    public static readonly FfmpegDist[] All =
    [
        new()
        {
            Id = DefaultId,
            Label = "BtbN / FFmpeg-Builds (win64 GPL, daily snapshot)",
            Homepage = "https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest",
            Archive = ArchiveKind.Zip,
            Platform = DistPlatform.Windows,
            Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/"
                + "ffmpeg-master-latest-win64-gpl.zip",
        },
        new()
        {
            Id = "nonfree",
            Label = "BnqDzj / FFmpeg-Builds-nonfree (win64, daily snapshot)",
            Homepage = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/tag/latest",
            Archive = ArchiveKind.Zip,
            Platform = DistPlatform.Windows,
            Url = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/download/latest/"
                + "ffmpeg-master-latest-win64-nonfree.zip",
        },

        new()
        {
            Id = DefaultId,
            Label = "BtbN / FFmpeg-Builds (linux64 GPL, daily snapshot)",
            Homepage = "https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest",
            Archive = ArchiveKind.TarXz,
            Platform = DistPlatform.Linux,
            Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/"
                + "ffmpeg-master-latest-linux64-gpl.tar.xz",
        },
        new()
        {
            Id = "nonfree",
            Label = "BnqDzj / FFmpeg-Builds-nonfree (linux64, daily snapshot)",
            Homepage = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/tag/latest",
            Archive = ArchiveKind.TarXz,
            Platform = DistPlatform.Linux,
            Url = "https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases/download/latest/"
                + "ffmpeg-master-latest-linux64-nonfree.tar.xz",
        },
    ];

    /// <summary>What this process runs on. Anything not Windows is treated as Linux.</summary>
    public static DistPlatform Current =>
        OperatingSystem.IsWindows() ? DistPlatform.Windows : DistPlatform.Linux;

    /// <summary>The distributions this machine could actually run.</summary>
    public static IEnumerable<FfmpegDist> Available
        => All.Where(d => d.Platform == Current);

    /// <summary>Case-insensitive lookup among <see cref="Available"/>; null when not usable here.</summary>
    public static FfmpegDist? Find(string? id)
    {
        if (!id.HasText) return null;

        foreach (FfmpegDist dist in Available)
        {
            if (string.Equals(dist.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) return dist;
        }

        return null;
    }

    /// <summary>Ids offered on this machine, for the usage and refusal sentences.</summary>
    public static string Ids => string.Join(", ", Available.Select(d => d.Id));

    /// <summary>
    /// Whether <paramref name="id"/> names a distribution this build knows at all,
    /// on either platform. What a refusal wants to say when it is asked for
    /// something that exists but is a Windows-only source.
    /// </summary>
    public static FfmpegDist? FindKnown(string? id)
    {
        if (!id.HasText) return null;

        foreach (FfmpegDist dist in All)
        {
            if (string.Equals(dist.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) return dist;
        }

        return null;
    }

    /// <summary>
    /// Which archive a name refers to, or null when it is neither. Used for a
    /// hand-downloaded archive, where the catalogue's own answer does not apply.
    /// </summary>
    public static ArchiveKind? KindOfArchive(string name)
        => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.Zip
         : name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.TarXz
         : name.EndsWith(".txz", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.TarXz
         : null;
}
