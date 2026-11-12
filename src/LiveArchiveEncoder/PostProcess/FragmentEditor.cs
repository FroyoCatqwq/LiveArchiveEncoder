using System.Globalization;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// v7.9.9.3: shared editing / validation of an FFmpeg argument fragment.
///
/// <para>Both the CLI (--recompress-extra and the VBV flags) and the CUI menus
/// keep their choices in the fragment itself. That is one source of truth:
/// ComposeSecondPass preserves them, CloneRecompress copies them, and
/// --recompress-args / --emit-args reproduce them. No new persisted fields are
/// needed for the generic encoder options.</para>
/// </summary>
public static class FragmentEditor
{
    /// <summary>Value after <paramref name="flag"/> in kbps (accepts "300k").</summary>
    public static int? Kbps(IReadOnlyList<string> tokens, string flag)
    {
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            if (!string.Equals(tokens[i], flag, StringComparison.Ordinal)) continue;
            string raw = tokens[i + 1].Trim();
            double scale = 1;
            if (raw.EndsWith('M') || raw.EndsWith('m')) { raw = raw[..^1]; scale = 1000; }
            else if (raw.EndsWith('k') || raw.EndsWith('K')) raw = raw[..^1];
            // v7.9.9.7: the codec catalog itself emits "-b:v 0" (VP9/AV1 in
            // CRF mode) and "-b:v 1M" (VP8). Treating 0 as "absent" made
            // ValidateVbv reject those legitimate fragments.
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double number) || number < 0 || double.IsNaN(number)) return null;
            double kbps = number * scale;
            return kbps <= int.MaxValue ? (int)kbps : null;
        }
        return null;
    }

    /// <summary>
    /// Add, replace or remove one flag (and its value). A blank
    /// <paramref name="value"/> removes it. Tokenize tolerates an empty
    /// fragment, so this is safe as the very first edit on a fresh config.
    /// </summary>
    public static string SetFlag(string fragment, string flag, string? value)
    {
        List<string> tokens = PostArgs.Tokenize(fragment);
        var parts = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], flag, StringComparison.Ordinal))
            {
                // v7.9.9.7: a value-less switch (-an, -shortest, ...) must not
                // swallow the following option's flag or value.
                if (!PostArgs.IsValueLess(flag)
                    && i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-')) i++;
                continue;
            }
            parts.Add(tokens[i]);
        }
        if (value is { Length: > 0 }) parts.AddRange([flag, value]);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Append extra options without producing a duplicate flag. A flag the user
    /// already pinned in the fragment wins; only the generated additions are
    /// skipped. v7.9.9.2's plain string concat left two -maxrate / two -b:v,
    /// where FFmpeg silently used the last one.
    /// </summary>
    public static string Merge(string fragment, string extras)
    {
        if (string.IsNullOrWhiteSpace(extras)) return fragment;

        List<string> baseTokens = PostArgs.Tokenize(fragment);
        List<string> extraTokens = PostArgs.Tokenize(extras);
        var pinned = new HashSet<string>(
            baseTokens.Where(t => t.StartsWith('-')), StringComparer.Ordinal);

        var merged = new List<string>(baseTokens);
        for (int i = 0; i < extraTokens.Count; i++)
        {
            string token = extraTokens[i];
            if (token.StartsWith('-') && pinned.Contains(token))
            {
                if (i + 1 < extraTokens.Count && !extraTokens[i + 1].StartsWith('-')) i++;
                continue;
            }
            merged.Add(token);
        }
        // v7.9.9.7: extras may repeat a flag internally (--recompress-maxrate
        // 300 plus --recompress-extra "-maxrate 400k"). FFmpeg is last-wins, so
        // the explicit CLI value would silently lose. FirstWins keeps the FIRST
        // occurrence, which is the base fragment's / the first explicit one.
        return FirstWins(string.Join(' ', merged));
    }

    /// <summary>
    /// FFmpeg ignores -maxrate/-minrate/-bufsize unless a target bitrate is
    /// present. Check the FINAL fragment (not just which CLI flags were seen),
    /// so a hand-written --recompress-args, an era preset and a CUI edit all go
    /// through the same rule.
    /// </summary>
    public static string? ValidateVbv(string fragment)
    {
        List<string> tokens = PostArgs.Tokenize(fragment);
        int? max = Kbps(tokens, "-maxrate");
        int? min = Kbps(tokens, "-minrate");
        int? buf = Kbps(tokens, "-bufsize");
        int? bitrate = Kbps(tokens, "-b:v");

        // Report an impossible bound first: it is a plain typo, whereas the
        // missing -b:v may just mean the codec/era has not been chosen yet.
        string? bounds = ValidateBounds(fragment);
        if (bounds is not null) return bounds;

        if ((max is not null || min is not null || buf is not null) && bitrate is null)
        {
            return "VBV (-maxrate/-minrate/-bufsize) need a target bitrate (-b:v); "
                + "set --recompress-video-kbps, or put -b:v in the fragment";
        }
        return null;
    }

    /// <summary>
    /// v7.9.9.7: FFmpeg's MPEG-video-family encoders (flv1, mpeg4, h261, ...)
    /// refuse to open when only one of -maxrate / -bufsize is present:
    /// <c>Either both buffer size and max rate or neither must be specified</c>
    /// (avcodec_open2 → EINVAL -22). Library encoders (libx264, libopenh264)
    /// tolerate the same fragment, so *rejecting* it would break valid configs.
    ///
    /// <para>Complete the pair instead. A missing buffer is two seconds of
    /// maxrate -- the same rule ArgsBuilder.VideoQualityArgs already uses -- and
    /// a missing maxrate is half the buffer. Both the 3-frame preflight and the
    /// real encode call this, so they always validate the same command.</para>
    /// </summary>
    public static (string Fragment, string? Note) NormalizeVbvPair(string fragment)
    {
        List<string> tokens = PostArgs.Tokenize(fragment);
        bool hasMax = HasFlag(tokens, "-maxrate");
        bool hasBuf = HasFlag(tokens, "-bufsize");
        if (hasMax == hasBuf) return (fragment, null);

        if (hasMax && Kbps(tokens, "-maxrate") is { } maxRate)
        {
            int buffer = maxRate * 2;
            return (SetFlag(fragment, "-bufsize", $"{buffer.ToString(CultureInfo.InvariantCulture)}k"),
                $"-maxrate {maxRate}k had no -bufsize; added -bufsize {buffer}k");
        }
        if (hasBuf && Kbps(tokens, "-bufsize") is { } bufferRate)
        {
            int max = System.Math.Max(1, bufferRate / 2);
            return (SetFlag(fragment, "-maxrate", $"{max.ToString(CultureInfo.InvariantCulture)}k"),
                $"-bufsize {bufferRate}k had no -maxrate; added -maxrate {max}k");
        }
        // Malformed values ("-maxrate abc") are left for FFmpeg to explain.
        return (fragment, null);
    }

    /// <summary>Min&lt;=max, the one VBV rule that is safe for every encoder.</summary>
    public static string? ValidateVbvPair(string fragment) => ValidateBounds(fragment);

    private static bool HasFlag(IReadOnlyList<string> tokens, string flag)
    {
        foreach (string token in tokens)
            if (string.Equals(token, flag, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Only the min&lt;=max rule. The live encoder generates its own -b:v, so
    /// the dependency check above does not apply to --extra-encoder-args.
    /// </summary>
    public static string? ValidateBounds(string fragment)
    {
        List<string> tokens = PostArgs.Tokenize(fragment);
        int? max = Kbps(tokens, "-maxrate");
        int? min = Kbps(tokens, "-minrate");
        if (max is { } mx && min is { } mn && mn > mx)
            return $"-minrate ({mn}) must not exceed -maxrate ({mx})";
        return null;
    }

    /// <summary>
    /// Keep only the FIRST occurrence of single-value flags. Catalog-generated
    /// -preset / -profile:v used to be appended after a user's own value and
    /// silently override it (FFmpeg is last-wins). Multi-valued -map is left
    /// alone.
    /// </summary>
    public static string FirstWins(string fragment)
    {
        List<string> tokens = PostArgs.Tokenize(fragment);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (SingleValueFlags.Contains(token) && !seen.Add(token))
            {
                if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-')) i++;
                continue;
            }
            kept.Add(token);
        }
        return string.Join(' ', kept);
    }

    private static readonly HashSet<string> SingleValueFlags =
        new(StringComparer.Ordinal)
        {
            "-preset", "-profile:v", "-level", "-tune", "-maxrate", "-minrate",
            "-bufsize", "-crf", "-q:v", "-qscale:v", "-b:v", "-b:a", "-ar",
            "-ac", "-pix_fmt", "-c:v", "-c:a", "-x264-params", "-tag:v",
        };
}
