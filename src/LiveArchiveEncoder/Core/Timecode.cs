using System.Globalization;
using System.Text.RegularExpressions;

namespace LiveArchiveEncoder.Core;

/// <summary>HH:MM:SS[.mmm] timecode parsing and formatting (port of the Python helpers).</summary>
public static partial class Timecode
{
    [GeneratedRegex(@"(?<hours>\d+):(?<minutes>\d{1,2}):(?<seconds>\d{1,2}(?:\.\d{1,3})?)")]
    private static partial Regex TimecodeRegex();

    public static double Parse(string value)
    {
        Match m = TimecodeRegex().Match(value.Trim());
        if (!m.Success || m.Length != value.Trim().Length)
        {
            throw new FormatException($"invalid timecode '{value}'; use HH:MM:SS or HH:MM:SS.mmm");
        }

        int hours = int.Parse(m.Groups["hours"].Value, CultureInfo.InvariantCulture);
        int minutes = int.Parse(m.Groups["minutes"].Value, CultureInfo.InvariantCulture);
        double seconds = double.Parse(m.Groups["seconds"].Value, CultureInfo.InvariantCulture);
        if (minutes >= 60 || seconds is < 0 or >= 60)
        {
            throw new FormatException("minutes and seconds in a timecode must be between 00 and 59");
        }

        double result = hours * 3600 + minutes * 60 + seconds;
        if (!double.IsFinite(result))
        {
            throw new FormatException("timecode must be finite");
        }
        return result;
    }

    public static string Format(double? seconds)
    {
        if (seconds is null) return "end of source";
        double value = seconds.Value;
        if (!double.IsFinite(value) || value < 0) return value.ToString(CultureInfo.InvariantCulture);

        long totalMs = (long)Math.Round(value * 1000);
        long hours = totalMs / 3_600_000;
        long remainder = totalMs % 3_600_000;
        long minutes = remainder / 60_000;
        remainder %= 60_000;
        long wholeSeconds = remainder / 1000;
        long milliseconds = remainder % 1000;

        string baseText = string.Create(
            CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{wholeSeconds:00}");
        return milliseconds != 0
            ? string.Create(CultureInfo.InvariantCulture, $"{baseText}.{milliseconds:000}")
            : baseText;
    }

    /// <summary>
    /// Validate and resolve the selected segment. Returns (resolvedEnd, duration).
    /// Duration is null only when ffprobe cannot determine the source duration
    /// and no end time was supplied.
    /// </summary>
    public static (double? End, double? Duration) ResolveRange(
        double start, double? end, double? sourceDuration)
    {
        if (!double.IsFinite(start) || start < 0)
            throw new ArgumentException("start time must be a finite non-negative timecode");

        double? resolvedEnd = null;
        if (end is not null)
        {
            if (!double.IsFinite(end.Value) || end.Value < 0)
                throw new ArgumentException("end time must be a finite non-negative timecode");
            if (end.Value <= start)
                throw new ArgumentException("end time must be later than start time");
            resolvedEnd = end.Value;
        }

        // A non-finite or non-positive duration is the same as "ffprobe could not
        // tell us": every branch below is skipped and the end time stays whatever
        // the caller asked for.
        double? durationValue =
            sourceDuration is { } d && double.IsFinite(d) && d > 0 ? d : null;
        if (durationValue is not null)
        {
            if (start >= durationValue.Value)
            {
                throw new ArgumentException(
                    $"start time {Format(start)} is at or beyond the source duration "
                    + $"({Format(durationValue)})");
            }
            if (resolvedEnd is not null)
            {
                if (resolvedEnd.Value > durationValue.Value + 0.05)
                {
                    throw new ArgumentException(
                        $"end time {Format(resolvedEnd)} exceeds the source duration "
                        + $"({Format(durationValue)})");
                }
                resolvedEnd = Math.Min(resolvedEnd.Value, durationValue.Value);
            }
            else
            {
                resolvedEnd = durationValue.Value;
            }
        }

        if (resolvedEnd is null) return (null, null);

        double duration = resolvedEnd.Value - start;
        if (duration <= 0) throw new ArgumentException("selected time range has no duration");
        return (resolvedEnd, duration);
    }
}
