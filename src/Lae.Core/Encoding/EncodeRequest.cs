using Lae.Core.Localization;

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

    /// <summary>
    /// Where the result goes, or null for "beside the source, named after the
    /// container the plan settles on".
    ///
    /// <para>Null rather than a guess, because only the plan knows the container:
    /// it is chosen from the encoder once the encoder has been looked up in the
    /// catalogue this FFmpeg build reported. A caller that derives the name
    /// itself has to assume a container, and assuming <c>avi</c> puts an MP4
    /// inside a file called <c>clip.avi</c> the moment someone asks for libx264
    /// without naming an output.</para>
    /// </summary>
    public string? DestinationPath { get; init; }

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
    /// Where libass may look for fonts, or null for "the system's only".
    ///
    /// <para>Resolved by the host for the same reason as the weights: the answer
    /// is a folder beside FFmpeg, which this assembly cannot know. v7 kept the
    /// fonts next to the binary so a distribution install could ship the one
    /// family its presets asked for; the burn filter needs to be told where that
    /// is, and CAPS-style guesses about the user's font library are how a caption
    /// ends up rendered in something nobody chose.</para>
    /// </summary>
    public string? FontsDirectory { get; init; }

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
    /// <param name="destination">
    /// The path the run will really write, when the request does not name one.
    /// Passing it in keeps the "is this the same file as the source" check
    /// meaningful for a derived name -- <c>clip.avi</c> encoded with mpeg4 to a
    /// derived name lands exactly on the source.
    /// </param>
    /// <param name="messages">
    /// The language to refuse in. A parameter rather than a field because the same
    /// request must be able to answer in either language within one process, which
    /// is what the tests and <c>lae repl</c> both do.
    /// </param>
    public IReadOnlyList<string> Validate(string? destination, IMessages messages)
    {
        var problems = new List<string>();
        string? output = destination ?? DestinationPath;

        void Refuse(string key, object? values = null)
            => problems.Add(values is null ? messages[key] : messages.Format(key, values));

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            Refuse("plan.error.noInput");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            Refuse("plan.error.noOutput");
        }
        else if (!string.IsNullOrWhiteSpace(SourcePath) && SamePath(SourcePath, output))
        {
            // Overwriting the source is not recoverable, and "it worked, but the
            // original is gone" is not a thing to discover afterwards. Saying how
            // to proceed matters: the derived output name is the source name with
            // the encoder's extension, so encoding an .avi to mpeg4 lands here on
            // the first try and reads like a refusal of the whole command.
            Refuse("plan.error.outputIsInput");
        }

        if (StartSeconds < 0) Refuse("plan.error.negativeStart");
        if (EndSeconds is { } end && end <= StartSeconds)
        {
            Refuse("plan.error.endBeforeStart");
        }

        if (Encode.Width <= 0 || Encode.Height <= 0) Refuse("plan.error.badSize");
        if (Encode.Fps <= 0) Refuse("plan.error.badFps");
        if (Encode.Quality <= 0) Refuse("plan.error.badQuality");
        if (Encode.Parallel < 1) Refuse("plan.error.badParallel");
        if (Encode.Threads < 1) Refuse("plan.error.badThreads");

        // Sharding was measured and rejected, and the rejection is not "it was
        // slow": the wall clock *is* the pacing, so N shards each pacing in
        // realtime finish in the same wall-clock time as one, and each pays its
        // own probe and startup on top. Measured 2026-10-17: a 20 s source at
        // --parallel 4 took 2.33 s against 1.85 s for a single unthrottled pass,
        // and the only encoder whose marginal cost would justify it (roqvideo)
        // is the one whose container concatenation destroys. The setting survives
        // because a settings file may still say 4; saying so beats running
        // single-threaded and letting the number look honoured.
        if (Encode.Parallel > 1)
        {
            // "shards" rather than "count": i18next reads `count` as a request for
            // its plural resolver, which would look for a different key entirely.
            Refuse("plan.error.parallelUnsupported", new { shards = Encode.Parallel });
        }

        // A window of zero is not "measure instantly", it is a division by zero
        // one layer down: the ladder's window refuses to be constructed at all,
        // and the exception arrives after both children have been started. A
        // hand-edited settings file can produce it, so it is checked here where
        // it costs a sentence instead of a stack trace.
        if (Encode.WindowSeconds <= 0) Refuse("plan.error.badWindow");

        // The queue between the decoder and the pump. Zero would mean "drop every
        // frame", which is a stall dressed up as a setting.
        if (Encode.BufferFrames < 1) Refuse("plan.error.badBuffer");

        // Files and an embedded track are mutually exclusive: libass would burn
        // one and the muxer would carry the other, and the result has two
        // subtitle layers with no way to tell which is which. A carried track is
        // the same argument one step further -- three ways of saying "subtitles",
        // and the run has to pick one.
        int sources = (Encode.Subtitles.Files.Count > 0 ? 1 : 0)
                    + (Encode.Subtitles.Stream is not null ? 1 : 0)
                    + (Encode.Subtitles.CopyTracks.Count > 0 ? 1 : 0);
        if (sources > 1)
        {
            Refuse("plan.error.subtitleSources");
        }

        return problems;
    }

    /// <summary>
    /// The default output name: same folder, same stem, the given extension.
    ///
    /// <para>Pure, and here rather than in the CLI, because the extension comes
    /// from the container the plan chose and the plan is the only thing that
    /// knows it. The result may equal the source (an <c>.avi</c> encoded to
    /// mpeg4), which <see cref="Validate"/> then refuses with a sentence rather
    /// than leaving FFmpeg to discover it.</para>
    /// </summary>
    public static string DeriveDestination(string sourcePath, string container)
    {
        string full = Path.GetFullPath(sourcePath);
        string folder = Path.GetDirectoryName(full) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(full);
        return Path.Combine(folder, $"{stem}.{container}");
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
