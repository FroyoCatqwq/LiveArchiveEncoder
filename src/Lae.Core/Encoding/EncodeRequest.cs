namespace Lae.Core;

/// <summary>
/// One encode, fully specified and never persisted.
///
/// <para>The split from <see cref="Settings.EncodeDefaults"/> is the point.
/// v7 stored the input path, the output path, the trim range and the stream
/// picks in <c>config.json</c>, so a scripted run inherited whatever the last
/// interactive session had set: ask for a full encode and get the previous
/// session's 20-minute excerpt, with no line in the output saying why.</para>
///
/// <para>So: <c>EncodeDefaults</c> is what the program remembers, this is what
/// one invocation asks for, and the two never share a field that means different
/// things in each. The encode parameters are carried as an
/// <see cref="Settings.EncodeDefaults"/> rather than copied field by field --
/// copying them here would recreate exactly the duplicated model the settings
/// rework removed.</para>
/// </summary>
public sealed record EncodeRequest
{
    public required string SourcePath { get; init; }

    public required string DestinationPath { get; init; }

    /// <summary>
    /// The effective encode parameters: the stored defaults with this run's
    /// overrides already applied. Built with <c>with</c>, so a field added to the
    /// settings cannot be forgotten here.
    /// </summary>
    public Settings.EncodeDefaults Encode { get; init; } = new();

    /// <summary>Absolute stream index of the video track; null means the first.</summary>
    public int? VideoStream { get; init; }

    /// <summary>Absolute stream index of the audio track; null means the first.</summary>
    public int? AudioStream { get; init; }

    /// <summary>Start offset in seconds. Zero means "from the beginning".</summary>
    public double StartSeconds { get; init; }

    /// <summary>End offset in seconds; null means "to the end".</summary>
    public double? EndSeconds { get; init; }

    /// <summary>Print the progress line. The shard pipeline turns it off.</summary>
    public bool Verbose { get; init; } = true;

    /// <summary>
    /// The NNEDI3 weights file to use when <c>nnedi</c> is the deinterlacer.
    ///
    /// <para>Resolved by the host rather than stored with the encode parameters:
    /// the file lives wherever the toolchain put it, and a run that names
    /// <c>nnedi</c> without weights is a refusal with instructions, never a
    /// silent fallback to a cheaper filter.</para>
    /// </summary>
    public string? NnediWeights { get; init; }

    /// <summary>
    /// The length this run covers, when both ends are known. Null while the
    /// source has not been probed -- a request may legitimately be built before
    /// the duration is known, because the CLI has to validate paths first.
    /// </summary>
    public double? SpanSeconds(double sourceDuration)
    {
        double start = Math.Max(0, StartSeconds);
        double end = EndSeconds ?? sourceDuration;
        double span = end - start;
        return span > 0 ? span : null;
    }

    /// <summary>
    /// The parts of a request that must hold whatever it asks for. Checked
    /// before anything is spawned, so a bad request fails in milliseconds with a
    /// sentence instead of after FFmpeg has written half a file.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            problems.Add("no input path");
        }

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            problems.Add("no output path");
        }
        else if (!string.IsNullOrWhiteSpace(SourcePath) && SamePath(SourcePath, DestinationPath))
        {
            // Overwriting the source is not recoverable, and "it worked, but the
            // original is gone" is not a thing to discover afterwards.
            problems.Add("the output path is the input path");
        }

        if (StartSeconds < 0) problems.Add("the start offset cannot be negative");
        if (EndSeconds is { } end && end <= StartSeconds)
        {
            problems.Add("the end offset is not after the start offset");
        }

        if (Encode.Width <= 0 || Encode.Height <= 0) problems.Add("the frame size must be positive");
        if (Encode.Fps <= 0) problems.Add("the frame rate must be positive");
        if (Encode.Quality <= 0) problems.Add("the quality must be positive");
        if (Encode.Parallel < 1) problems.Add("the shard count must be at least 1");

        // Files and an embedded track are mutually exclusive: libass would burn
        // one and the muxer would carry the other, and the result has two
        // subtitle layers with no way to tell which is which.
        if (Encode.Subtitles.Files.Count > 0 && Encode.Subtitles.Stream is not null)
        {
            problems.Add("pick either subtitle files or an embedded track, not both");
        }

        return problems;
    }

    /// <summary>
    /// Whether two paths name the same location, comparing full paths where the
    /// platform allows it and the raw strings where it does not.
    ///
    /// <para><c>Path.GetFullPath</c> throws on an empty or syntactically invalid
    /// path, and a validation helper that throws is worse than one that misses a
    /// case: the caller asked "is this request sane" and would get an exception
    /// instead of an answer.</para>
    /// </summary>
    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exc) when (exc is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
