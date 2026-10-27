namespace LiveArchiveEncoder.Core;

/// <summary>
/// Escaping for values placed inside an FFmpeg filter description.
///
/// <para>FFmpeg parses a filtergraph on two levels -- the graph (which splits on
/// <c>,</c> and <c>;</c>) and each option value (which splits on <c>:</c>) -- and
/// both treat <c>\</c> as an escape. A Windows path therefore needs its
/// separators doubled and its drive colon escaped, and the result has to be
/// single-quoted as well. Measured 2026-10-21:
/// <c>'C\:\\Users\\x.srt'</c> opens; the unquoted spellings are all parsed as the
/// <c>original_size</c> option with a mangled name.</para>
///
/// <para>v7.9.8.3: lives here rather than on the subtitle type because the nnedi
/// weights path needs the identical escaping, and <c>FFmpeg</c> must not depend
/// on <c>Encoding</c>.</para>
/// </summary>
public static class FilterArgs
{
    /// <summary>
    /// A filesystem path, ready to be an option value. Quotes are load-bearing:
    /// without them the parser splits the drive letter off as an option name.
    /// </summary>
    public static string EscapePath(string path) =>
        "'" + path.Replace("\\", "\\\\", StringComparison.Ordinal)
                  .Replace(":", "\\:", StringComparison.Ordinal)
                  .Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Plain text -- a font family, a number, a file name that is not a path.
    /// Escapes the whole set the two parser levels treat specially.
    /// </summary>
    public static string EscapeValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case ':': sb.Append("\\:"); break;
                case '\'': sb.Append("\\'"); break;
                case '[': sb.Append("\\["); break;
                case ']': sb.Append("\\]"); break;
                case ',': sb.Append("\\,"); break;
                case ';': sb.Append("\\;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
