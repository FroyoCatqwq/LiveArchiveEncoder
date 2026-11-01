namespace Lae.Core;

/// <summary>
/// What FFmpeg's <c>-progress</c> stream says the muxer has produced.
/// </summary>
/// <param name="Bytes">Total bytes written to the output so far.</param>
/// <param name="MediaSeconds">
/// How much programme has been written, in seconds. This is the denominator the
/// bitrate wants: the wall clock includes every moment this machine was busy
/// elsewhere, and averaging over it reports a rate the file does not have.
/// </param>
public readonly record struct EncoderProgress(long Bytes, double MediaSeconds);

/// <summary>
/// Reads <c>ffmpeg -progress pipe:1</c>.
///
/// <para>v7 measured the encoder's output this way and v8 tried to measure it by
/// asking the file how long it was. That looked equivalent and was not: Windows
/// serves a file's length from its directory entry, which is only refreshed when
/// the writer flushes or closes, so a fourteen-second encode reported the same
/// number for seconds at a time and the ladder oscillated between its first and
/// last rung. FFmpeg already counts what it wrote -- asking it is both cheaper
/// and correct.</para>
///
/// <para>The stream arrives as <c>key=value</c> lines, one block per report,
/// terminated by <c>progress=continue</c> or <c>progress=end</c>. Fields this
/// program does not use are ignored, and <c>N/A</c> is treated as "no reading"
/// rather than as zero: a muxer that cannot know its size must not look like one
/// that wrote nothing.</para>
/// </summary>
public static class FfmpegProgress
{
    /// <summary>
    /// Folds one line into <paramref name="current"/>. Returns true when the
    /// line ended a report (<c>progress=...</c>), which is the cue to take the
    /// value.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<char> line, ref EncoderProgress current)
    {
        line = line.Trim();

        if (line.StartsWith("total_size=", StringComparison.Ordinal))
        {
            if (long.TryParse(line["total_size=".Length..], out long bytes) && bytes >= 0)
                current = current with { Bytes = bytes };
            return false;
        }

        // Both fields carry microseconds despite what their names say. out_time_ms
        // is documented as milliseconds and is not: it holds AV_TIME_BASE ticks,
        // which are microseconds, and has since the field was added. Measured on
        // FFmpeg N-128198 with a two-second clip, both lines read
        // `=2000000`. Reading the second as milliseconds reports 2000 seconds,
        // and every bitrate derived from it is a thousand times too small --
        // which is the dangerous part, because 2000 is still a plausible
        // duration for an archive encode, so nothing looks broken.
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
            || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
        {
            if (long.TryParse(line[(line.IndexOf('=') + 1)..], out long micros) && micros >= 0)
                current = current with { MediaSeconds = micros / 1_000_000.0 };

            return false;
        }

        return line.StartsWith("progress=", StringComparison.Ordinal);
    }
}
