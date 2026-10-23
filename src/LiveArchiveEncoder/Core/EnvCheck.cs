namespace LiveArchiveEncoder.Core;

/// <summary>
/// The documented minimums, checked on the machine that is actually running
/// (v7.9.5.1).
///
/// This is a warning, never a refusal: the numbers in
/// <c>docs/requirements.md</c> are what the project is tested against, and a
/// slow machine still makes a correct file -- it just takes longer and may not
/// be able to shard. Saying so once, at the top of an interactive session, is
/// cheaper than a user wondering why an eight-shard run is swapping.
/// </summary>
public static class EnvCheck
{
    /// <summary>A shard needs a core; below two, sharding is pointless.</summary>
    public const int MinCores = 2;

    /// <summary>Enough for one 1080p decode + encode pipeline.</summary>
    public const double MinMemoryGb = 2.0;

    /// <summary>
    /// Free space at the destination. A sharded run writes every shard before
    /// it joins them, so the peak is roughly the output size again.
    /// </summary>
    public const double MinFreeDiskGb = 2.0;

    /// <summary>Localised one-line warnings; empty when the machine is fine.</summary>
    public static List<string> Warnings(string? outputPath)
    {
        var warnings = new List<string>();

        if (Environment.ProcessorCount < MinCores)
            warnings.Add(Localization.L.T("env.cores", Environment.ProcessorCount, MinCores));

        double memoryGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        if (memoryGb > 0 && memoryGb < MinMemoryGb)
            warnings.Add(Localization.L.T("env.memory", $"{memoryGb:F1}", $"{MinMemoryGb:F1}"));

        double? freeGb = FreeSpaceGb(outputPath);
        if (freeGb is { } free && free < MinFreeDiskGb)
            warnings.Add(Localization.L.T("env.disk", $"{free:F1}", $"{MinFreeDiskGb:F1}"));

        return warnings;
    }

    /// <summary>
    /// Free space where the product will land. Null when the drive cannot be
    /// read -- a network share, a path that does not exist yet -- because a
    /// check that cannot answer must not warn about a guess.
    /// </summary>
    public static double? FreeSpaceGb(string? outputPath)
    {
        try
        {
            string full = Path.GetFullPath(outputPath.HasText ? outputPath : ".");
            string? root = Path.GetPathRoot(full);
            if (!root.HasText) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;
            return drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
        }
        catch (Exception exc) when (exc is ArgumentException or IOException
                                        or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
