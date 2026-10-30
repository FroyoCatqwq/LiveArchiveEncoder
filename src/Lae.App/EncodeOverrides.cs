using Lae.Core.Settings;

namespace Lae.App;

/// <summary>What the command line asked for, already parsed and free of System.CommandLine.</summary>
public sealed record EncodeOverrides
{
    public string? Output { get; init; }

    public string? Encoder { get; init; }

    public string? Container { get; init; }

    public double? From { get; init; }

    public double? To { get; init; }

    public int? Quality { get; init; }

    /// <summary>Bitrate ceiling in kbps; the ladder descends when it is reached.</summary>
    public double? LimitKbps { get; init; }

    /// <summary>Bitrate the ladder climbs back from.</summary>
    public double? RecoverKbps { get; init; }

    /// <summary>Ladder rungs, when the command line named them.</summary>
    public IReadOnlyList<double>? FpsLevels { get; init; }

    /// <summary>Per-rung thresholds that came with the rungs.</summary>
    public IReadOnlyList<FpsThreshold>? FpsThresholds { get; init; }

    /// <summary>WIDTHxHEIGHT as typed.</summary>
    public string? Size { get; init; }

    /// <summary>Id of a stored preset to lay over the defaults before anything else.</summary>
    public string? Preset { get; init; }

    /// <summary>Sidecar subtitle files to burn, in order. Empty means none.</summary>
    public IReadOnlyList<string>? SubtitleFiles { get; init; }

    /// <summary>Absolute index of an embedded subtitle track to burn.</summary>
    public int? SubtitleTrack { get; init; }

    /// <summary>Absolute indices of subtitle tracks to carry into the output.</summary>
    public IReadOnlyList<int>? SubtitleCopyTracks { get; init; }

    /// <summary>Shape the burned text is laid out in: "source", "4:3" or "16:9".</summary>
    public string? SubtitleBox { get; init; }

    /// <summary>Font family forced over whatever the subtitle asks for.</summary>
    public string? SubtitleFont { get; init; }

    /// <summary>Extra directory libass may look in.</summary>
    public string? FontsDir { get; init; }

    /// <summary>
    /// One FFmpeg fragment per post-encode hop, in order. Empty means no chain:
    /// the chain is never taken from the settings, only from the command line.
    /// </summary>
    public IReadOnlyList<string>? RecompressArgs { get; init; }

    /// <summary>Container per hop, paired by position with <see cref="RecompressArgs"/>.</summary>
    public IReadOnlyList<string>? RecompressContainers { get; init; }

    /// <summary>
    /// Run the chain against <c>input</c> and nothing else: no capture, no encoder
    /// choice, no probe of its own beyond the length.
    /// </summary>
    public bool RecompressOnly { get; init; }
}
