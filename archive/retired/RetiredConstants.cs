// Retired members, kept for reference. NOT COMPILED: this file lives outside
// every project on purpose (see archive/README.md).
//
// How each one was found: every declaration in src/ was cross-referenced
// against src/, tests/ and the Python e2e harnesses; the ones below had zero
// references outside their own declaration line.
//
// Restoring one is a copy-paste into the file it came from.

namespace LiveArchiveEncoder.Archive;

// ---------------------------------------------------------------------------
// Configuration/Constants.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.8.1.
///
/// The default ladder as text. Never read: <see cref="Configuration.Config"/>
/// seeds <c>FpsLevels</c> and <c>FpsLevelKbps</c> directly from
/// <c>Constants.DefaultFps</c>, and <c>CliOptions</c> carries its own
/// <c>--fps-levels</c> default. Kept only because it documents the intended
/// ladder at the time it was written.
/// </summary>
internal static class RetiredConstants
{
    public const string DefaultFpsLevels = "13,8,5,3";

    /// <summary>
    /// Retired in v7.8.1.
    ///
    /// Direct download URL for one BtbN GPL build. Superseded by
    /// <c>FFmpeg.FfmpegCatalog</c>, which models every distribution (id,
    /// platform, homepage, auto-download support) instead of hard-coding one
    /// Windows URL.
    /// </summary>
    public const string BtbnGplReleaseUrl =
        "https://github.com/Btbn/FFmpeg-Builds/releases/latest/download/"
        + "ffmpeg-master-latest-win64-gpl.zip";

    /// <summary>
    /// Retired in v7.8.1.
    ///
    /// The legacy encoder name filters, one anchored pattern each, tested in a
    /// loop by <c>FFmpegDiscovery.LegacyFamily</c>.
    ///
    /// Replaced by a single source-generated alternation,
    /// <c>FFmpegDiscovery.LegacyEncoderName()</c>:
    /// <c>^(?:a64multi(?:5)?|amv|asv[12]|...|vivo)$</c> with
    /// <c>RegexOptions.IgnoreCase</c>. Equivalent because every pattern here is
    /// fully anchored and was applied with the same option, and the alternation
    /// is tried in the same order. The generated version is compiled ahead of
    /// time and needs no reflection, which is what makes JIT and NativeAOT
    /// behave identically.
    /// </summary>
    public static readonly string[] LegacyEncoderPatterns =
    [
        @"^a64multi(?:5)?$", @"^amv$", @"^asv[12]$", @"^cinepak$", @"^cljr$",
        @"^flashsv2?$", @"^flv$", @"^h261$", @"^h263(?:p)?$", @"^mjpeg$",
        @"^msmpeg4(?:v2)?$", @"^msrle$", @"^msvideo1$", @"^mpeg[124]video$",
        @"^mpeg4$", @"^qtrle$", @"^roqvideo$", @"^rpza$", @"^rv(?:10|20)$",
        @"^smc$", @"^snow$", @"^svq1$", @"^wmv[12]$", @"^zmbv$", @"^libtheora$",
        @"^libx264$", @"^libxvid$", @"^vivo$",
    ];
}

// ---------------------------------------------------------------------------
// Core/EncoderMath.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.8.1.
///
/// Last survivor of the Python fallback chain used when an encoder declares no
/// usable raw transport format. <c>PixelFormatUtil.ChooseTransportPixFmt</c>
/// resolves that case on its own now, so the preferred-order list had a single
/// reader: this property, which itself had none.
/// </summary>
internal static class RetiredEncoderMath
{
    private static readonly string[] PreferredTransportFallback =
        ["yuv420p", "yuv422p", "yuv444p", "rgb24", "gray"];

    public static string FallbackTransportFormat() => PreferredTransportFallback[0];
}

// ---------------------------------------------------------------------------
// Core/AudioCatalog.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.8.1.
///
/// Three convenience reads over <c>AudioCatalog.Known</c> /
/// <c>AudioCatalog.ContainerCodecs</c>. They were written for the v7.8 audio
/// scan menu and never wired up: the menu walks
/// <c>AudioCatalog.Compatible(container, available)</c>, which already filters
/// by what this FFmpeg build actually has, and reports rates with
/// <c>AudioCatalog.DescribeRates</c>. A catalogue entry that exists here but
/// not in the build is worse than no entry at all, so the unfiltered views were
/// left out rather than exposed.
/// </summary>
internal static class RetiredAudioCatalog
{
    public static IReadOnlyList<string> Names => [.. Known.Select(x => x.Name)];

    /// <summary>True when the codec is one we know; unknown codecs are unconstrained.</summary>
    public static bool IsKnown(string? name) => Find(name) is not null;

    /// <summary>Codecs known to mux into a container, whether or not this build
    /// has them. Used for "that container accepts: ..." messages.</summary>
    public static IReadOnlyList<string> KnownFor(string containerFormat) =>
        ContainerCodecs.TryGetValue(containerFormat, out string[]? list) ? list : [];
}

// ---------------------------------------------------------------------------
// UI/CliOptions.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.8.1.
///
/// <c>Recover ?? Limit * 0.75</c> computed on the parsed options. Unused because
/// <c>Cli.Run</c> has to derive it after era presets have been applied:
/// the preset can move the bitrate ceiling, so the recovery threshold must
/// follow <c>effectiveLimit</c>, not the raw <c>--limit</c>. The live
/// expression is <c>args.Recover ?? effectiveLimit * 0.75</c>.
/// </summary>
internal static class RetiredCliOptions
{
    public double EffectiveRecover => Recover ?? Limit * 0.75;
}
