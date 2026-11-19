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
    /// The libopenh264 rate-control switch. Undocumented in the manual, present
    /// in the wrapper's AVOption table, so it has to be read off the build being
    /// used rather than assumed.
    /// </summary>
    [GeneratedRegex(@"^\s*-rc_mode\s+", RegexOptions.Multiline)]
    private static partial Regex RcModeOption();

    /// <summary>
    /// <c>ffmpeg -encoders</c> subtitle lines: <c> S..... ass    ASS (...)</c>.
    /// v7.9 goal 2 asked for subtitle support "以 ffmpeg -encoders 为准", so the
    /// set is read from the binary in use rather than hard-coded.
    ///
    /// v7.9.5: the parser moved next to the video/audio ones in
    /// <see cref="FFmpegDiscovery.ParseSubtitleEncoders"/>, so a single
    /// <c>-encoders</c> run feeds all three lists.
    /// </summary>

    /// <summary>
    /// Parse the -filters registry. FFmpeg writes it to stdout, but builds/wrappers
    /// can redirect diagnostics to stderr, so both streams are inspected.
    ///
    /// v7.9.5: the process call lives in <see cref="BuildProfileStore.Sweep"/>
    /// now; this half is kept because it is what makes the parsing testable
    /// without running FFmpeg.
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
            if (!AnnouncesUnknownFilter(text, name)) verified.Add(name);
        }
        return verified;
    }

    /// <summary>
    /// Does this output say the filter does not exist?
    ///
    /// v7.9.8.1: this used to build a regex per filter (<c>$@"^Unknown filter
    /// ['""]{Regex.Escape(name)}['""]\.?$"</c>), which the runtime cannot cache
    /// because the pattern differs every time, and which under NativeAOT runs in
    /// the interpreter. The line says exactly one thing, so it is read as text.
    /// </summary>
    private static bool AnnouncesUnknownFilter(string text, string name)
    {
        const string Lead = "Unknown filter";
        foreach (string raw in text.Split('\n'))
        {
            ReadOnlySpan<char> line = raw.AsSpan().Trim();
            if (!line.StartsWith(Lead, StringComparison.OrdinalIgnoreCase)) continue;

            ReadOnlySpan<char> rest = line[Lead.Length..].Trim();
            if (rest.Length >= 2 && rest[^1] == '.') rest = rest[..^1].TrimEnd();
            if (rest.Length < 3) continue;

            char quote = rest[0];
            if (quote is not ('\'' or '"')) continue;
            if (rest[^1] != quote) continue;
            if (rest[1..^1].Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static HashSet<string> DetectScaleAlgorithms(string ffmpeg)
    {
        CaptureResult p = ProcessRunner.Run([ffmpeg, "-hide_banner", "-h", "filter=scale"], 20);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                p.Stderr.Trim() is { Length: > 0 } err ? err : "ffmpeg -h filter=scale failed");
        }

        return ParseScaleAlgorithms(p.Stdout + "\n" + p.Stderr);
    }

    /// <summary>
    /// Scaler kernels inside <c>ffmpeg -h filter=scale</c>. Split out from the
    /// process call so the capability sweep can parse text it already has.
    /// </summary>
    public static HashSet<string> ParseScaleAlgorithms(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string canonical in Constants.ScaleAlgorithmAliases.Values.Distinct())
        {
            if (MentionsIndentedWord(text, canonical)) found.Add(canonical);
        }
        if (found.Contains("gauss") || MentionsIndentedWord(text, "gaussian")) found.Add("gauss");
        if (found.Count == 0)
            throw new InvalidOperationException(
                "Could not detect scaler algorithms from `ffmpeg -h filter=scale`");
        return found;
    }

    /// <summary>
    /// Is <paramref name="word"/> listed at the start of a line (after indent)?
    ///
    /// v7.9.8.1: replaces a <c>Regex.IsMatch</c> whose pattern was built from the
    /// name on every call -- uncacheable, interpreter-only under NativeAOT, and
    /// doing by parser what a span comparison does directly.
    /// </summary>
    private static bool MentionsIndentedWord(string text, string word)
    {
        foreach (string raw in text.Split('\n'))
        {
            ReadOnlySpan<char> line = raw.AsSpan().TrimStart();
            if (!line.StartsWith(word, StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Length == word.Length || char.IsWhiteSpace(line[word.Length])) return true;
        }
        return false;
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
        string help = p.Stdout + "\n" + p.Stderr;
        // v7.9.9.12: some wrappers answer "unknown encoder" with exit code 0.
        // Do not trust a zero exit alone -- the filter probe already learned
        // this the hard way.
        if (p.ExitCode != 0 || AnnouncesUnknownEncoder(help, encoder.Name))
        {
            throw new InvalidOperationException(
                help.Trim() is { Length: > 0 } err ? err : $"cannot query encoder {encoder.Name}");
        }

        // v7.9.9.11: some builds and wrappers write `-h encoder=...` to stderr.
        // VerifyNamedFilters and BuildProfileStore.Sweep already inspect both
        // streams; parsing only stdout silently lost OpenH264's -rc_mode and
        // the supported pixel-format list on those builds.
        EncoderCaps caps = ParseEncoderText(help, encoder);
        return caps with { CanEncode = CanActuallyEncode(ffmpeg, encoder.Name, caps.SupportedPixFmts) };
    }

    private static bool AnnouncesUnknownEncoder(string text, string name) =>
        text.Contains($"Unknown encoder '{name}'", StringComparison.OrdinalIgnoreCase)
        || text.Contains($"Encoder not found: {name}", StringComparison.OrdinalIgnoreCase)
        // FFmpeg 9 answers `Codec 'libopenh264' is not recognized by FFmpeg.`
        // with exit code 0 -- the exact trap this check exists for.
        || text.Contains($"Codec '{name}' is not recognized", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// v7.9.9.12: `ffmpeg -encoders` is a compile-time list; a wrapper such as
    /// yt-dlp's can omit an encoder that the CUI still offers from an older
    /// cached profile. Encode one real frame and only then call it supported.
    /// </summary>
    public static bool CanActuallyEncode(string ffmpeg, string name, IReadOnlyList<string>? pixFormats = null)
    {
        // CIF/25 fps is the one geometry every classic encoder in the catalog
        // accepts: MPEG-1 refuses 12 fps, H.261/H.263 refuse 256x192. A probe
        // must not reject an encoder the real preflight can open, so it uses
        // the same shape Preflight.Video uses (testsrc2, three frames).
        var formats = new List<string> { "", "yuv420p" };
        if (pixFormats is not null)
        {
            foreach (string fmt in pixFormats.Take(4))
                if (fmt.Length > 0 && !formats.Contains(fmt, StringComparer.Ordinal)) formats.Add(fmt);
        }
        foreach (string fmt in formats)
        {
            var args = new List<string>
            {
                ffmpeg, "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=352x288:rate=25:duration=0.3",
                "-frames:v", "3", "-c:v", name,
            };
            if (fmt.Length > 0) args.AddRange(["-pix_fmt", fmt]);
            args.AddRange(["-f", "null", "-"]);
            try
            {
                if (ProcessRunner.Run(args, 30).ExitCode == 0) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // v7.9.9.13: a Ctrl+C caught while probing is NOT a failed
                // pixel format. Caching "cannot encode" would make every later
                // run in this CUI session reject the encoder.
                if (Core.Interrupt.CurrentToken.IsCancellationRequested) throw;
                /* this format did not work; try the next */
            }
        }
        return false;
    }

    internal static EncoderCaps ParseEncoderText(string text, EncoderInfo encoder)
    {
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
            HasRcMode = RcModeOption().IsMatch(text),
        };
    }
}
