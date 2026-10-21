using System.Globalization;
using System.Text.RegularExpressions;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>Capability probing: filter registry, scaler kernels and encoder capabilities.</summary>
public static partial class FFmpegCapabilities
{
    /// <summary>
    /// One line of <c>ffmpeg -filters</c>: a space, two or three flag characters
    /// (<c>T</c>imeline, <c>S</c>lice threading, <c>C</c>ommand), the filter
    /// name, then the <c>V-&gt;V</c> style I/O description.
    ///
    /// The flag column is <b>two</b> characters wide in a current build
    /// (<c>" .. subtitles"</c>, verified 2026-10-21) and three in some older
    /// ones, so both have to match -- the previous three-only pattern silently
    /// dropped every unflagged filter, which is how the subtitles filter went
    /// unreported in a build that clearly has it.
    /// </summary>
    [GeneratedRegex(@"^\s*[TSC.]{2,3}\s+(\S+)\s+[VAFN|.>-]+(?:\s|$)")]
    private static partial Regex FilterLine();

    [GeneratedRegex(@"Supported pixel formats:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex SupportedPixFmts();

    [GeneratedRegex(@"Supported framerates:\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex SupportedFramerates();

    [GeneratedRegex(@"^\s*-crf\s+", RegexOptions.Multiline)]
    private static partial Regex CrfOption();

    [GeneratedRegex(@"^\s*-qscale\s+", RegexOptions.Multiline)]
    private static partial Regex QscaleOption();

    /// <summary>
    /// <c>ffmpeg -encoders</c> subtitle lines: <c> S..... ass    ASS (...)</c>.
    /// v7.9 goal 2 asked for subtitle support "以 ffmpeg -encoders 为准", so the
    /// set is read from the binary in use rather than hard-coded. The name has
    /// to be a word: the legend block (<c> S..... = Subtitle</c>) would
    /// otherwise contribute an encoder called "=".
    /// </summary>
    [GeneratedRegex(@"^\s*S\.+\s+([A-Za-z0-9_]+)\s", RegexOptions.Multiline)]
    private static partial Regex SubtitleEncoderLine();

    /// <summary>
    /// Every subtitle encoder this FFmpeg can write, or an empty set when the
    /// build has none (a stripped-down build simply cannot carry a text track,
    /// which the caller turns into "use --subtitle-mode burn").
    /// </summary>
    public static HashSet<string> DetectSubtitleEncoders(string ffmpeg)
    {
        CaptureResult p = ProcessRunner.Run([ffmpeg, "-hide_banner", "-encoders"], 30);
        if (p.ExitCode != 0) return [];

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in SubtitleEncoderLine().Matches(p.Stdout + "\n" + p.Stderr))
            found.Add(m.Groups[1].Value);
        return found;
    }

    /// <summary>
    /// Parse the -filters registry. FFmpeg writes it to stdout, but builds/wrappers
    /// can redirect diagnostics to stderr, so both streams are inspected.
    /// </summary>
    public static HashSet<string> DetectVideoFilters(string ffmpeg)
    {
        CaptureResult p = ProcessRunner.Run(ffmpeg, "-hide_banner", "-filters");
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                (p.Stderr.Trim() is { Length: > 0 } ? p.Stderr : p.Stdout).Trim() is { Length: > 0 } msg
                    ? msg : "ffmpeg -filters failed");
        }

        string text = p.Stdout + "\n" + p.Stderr;
        return ParseFilterNames(text);
    }

    /// <summary>
    /// Filter names inside <c>ffmpeg -filters</c> output. Split out from the
    /// process call so the parsing is testable without running FFmpeg.
    /// </summary>
    public static HashSet<string> ParseFilterNames(string text)
    {
        var filters = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            Match m = FilterLine().Match(line);
            if (m.Success) filters.Add(m.Groups[1].Value);
        }
        return filters;
    }

    /// <summary>Verify important filters directly when registry parsing is inconclusive.</summary>
    public static HashSet<string> VerifyNamedFilters(
        string ffmpeg, HashSet<string> filters, IEnumerable<string> names)
    {
        var verified = new HashSet<string>(filters, StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (verified.Contains(name)) continue;
            CaptureResult p = ProcessRunner.Run([ffmpeg, "-hide_banner", "-h", $"filter={name}"], 20);
            string text = p.Stdout + "\n" + p.Stderr;
            bool unknown = Regex.IsMatch(
                text,
                $@"^Unknown filter ['""]{Regex.Escape(name)}['""]\.?\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!unknown) verified.Add(name);
        }
        return verified;
    }

    public static HashSet<string> DetectScaleAlgorithms(string ffmpeg)
    {
        CaptureResult p = ProcessRunner.Run([ffmpeg, "-hide_banner", "-h", "filter=scale"], 20);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                p.Stderr.Trim() is { Length: > 0 } err ? err : "ffmpeg -h filter=scale failed");
        }

        string text = p.Stdout + "\n" + p.Stderr;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string canonical in Constants.ScaleAlgorithmAliases.Values.Distinct())
        {
            if (Regex.IsMatch(text, $@"^\s+{Regex.Escape(canonical)}\s+",
                RegexOptions.IgnoreCase | RegexOptions.Multiline))
            {
                found.Add(canonical);
            }
        }
        if (found.Contains("gauss")
            || Regex.IsMatch(text, @"^\s+gaussian\s+", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            found.Add("gauss");
        }
        if (found.Count == 0)
            throw new InvalidOperationException(
                "Could not detect scaler algorithms from `ffmpeg -h filter=scale`");
        return found;
    }

    /// <summary>Parses "30/1", "25", "30000/1001" style FFmpeg rate declarations.</summary>
    public static double? ParseRate(string value)
    {
        string text = value.Trim();
        if (text.Length == 0) return null;

        double result;
        int slash = text.IndexOf('/');
        if (slash > 0)
        {
            if (!double.TryParse(text[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
                || !double.TryParse(text[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double den))
            {
                return null;
            }
            if (num == 0 || den == 0) return null;
            result = num / den;
        }
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
        {
            return null;
        }

        return double.IsFinite(result) && result > 0 ? result : null;
    }

    public static EncoderCaps ProbeEncoderCaps(string ffmpeg, EncoderInfo encoder)
    {
        CaptureResult p = ProcessRunner.Run([ffmpeg, "-hide_banner", "-h", $"encoder={encoder.Name}"], 20);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                p.Stderr.Trim() is { Length: > 0 } err ? err : $"cannot query encoder {encoder.Name}");
        }

        string text = p.Stdout;
        var pix = new List<string>();
        var rates = new List<double>();

        Match m = SupportedPixFmts().Match(text);
        if (m.Success)
        {
            pix.AddRange(m.Groups[1].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        m = SupportedFramerates().Match(text);
        if (m.Success)
        {
            foreach (string item in m.Groups[1].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                double? rate = ParseRate(item);
                if (rate is not null) rates.Add(rate.Value);
            }
        }

        return new EncoderCaps
        {
            Name = encoder.Name,
            Description = encoder.Description,
            Flags = encoder.Flags,
            SupportedPixFmts = pix,
            SupportedRates = [.. rates.Select(x => Math.Round(x, 6)).Distinct().Order()],
            HasCrf = CrfOption().IsMatch(text),
            HasQscale = QscaleOption().IsMatch(text),
        };
    }
}
