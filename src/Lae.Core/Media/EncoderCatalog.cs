namespace Lae.Core;

/// <summary>
/// Reads the encoder list out of <c>ffmpeg -encoders</c>.
///
/// <para>Pure parsing, for the same reason as <see cref="FfprobeJson"/>: the
/// interesting part is not "run a process", it is "which of these lines is an
/// encoder, and what can it write". v7 kept both halves together, so the parsing
/// could only be tested on a machine that had that exact FFmpeg build.</para>
/// </summary>
public static class EncoderCatalog
{
    /// <summary>
    /// Every video encoder the build reports, in the order FFmpeg printed them.
    ///
    /// <para>Only the name, the flag word and the description are here. The
    /// capability detail (pixel formats, frame rates, whether it has a CRF knob)
    /// costs one <c>ffmpeg -h encoder=NAME</c> per encoder, so it is fetched when
    /// an encoder is actually chosen rather than for all of them at startup.</para>
    /// </summary>
    public static List<EncoderInfo> Parse(string output, bool videoOnly = true)
    {
        var encoders = new List<EncoderInfo>();

        foreach (string line in output.Split('\n'))
        {
            string raw = line.TrimEnd('\r');
            if (raw.Length == 0) continue;

            // The flag legend is the only set of rows that carries an '='; every
            // encoder row is " FLAGS NAME DESCRIPTION". Testing for '=' first is
            // what lets the leading space be tolerated -- the encoder rows are
            // indented by one, and treating a leading space as "not an encoder"
            // silently produced an empty catalogue.
            if (raw.Contains('=', StringComparison.Ordinal)) continue;

            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('-') || trimmed.EndsWith(':')) continue;

            int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0) continue;

            string flags = trimmed[..space];
            if (flags.Length != 6) continue;

            string rest = trimmed[(space + 1)..].Trim();
            int nameEnd = rest.IndexOf(' ', StringComparison.Ordinal);
            string name = nameEnd < 0 ? rest : rest[..nameEnd];
            if (name.Length == 0) continue;

            char kind = flags[0];
            if (kind != 'V' && kind != 'A') continue;
            if (videoOnly && kind != 'V') continue;

            // A capital 'D' in the third slot means the encoder is
            // non-experimental; a lone 'X' means it is experimental and FFmpeg
            // refuses it without -strict -2, so it is not offered.
            bool experimental = flags.Contains('X', StringComparison.Ordinal);
            if (experimental) continue;

            (string extension, string format) = Defaults(name);

            encoders.Add(new EncoderInfo
            {
                Name = name,
                Flags = flags,
                Description = nameEnd < 0 ? "" : rest[(nameEnd + 1)..].Trim(),
                Codec = name,
                Family = "",
                Extension = extension,
                FormatName = format,
            });
        }

        return encoders;
    }

    /// <summary>
    /// Finds an encoder by name, case-insensitively. A name that is not in the
    /// list means this FFmpeg build cannot do it, and the caller has to say so
    /// rather than produce a file with no video.
    /// </summary>
    public static EncoderInfo? Find(IEnumerable<EncoderInfo> encoders, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string wanted = name.Trim();

        foreach (EncoderInfo encoder in encoders)
        {
            if (string.Equals(encoder.Name, wanted, StringComparison.OrdinalIgnoreCase)) return encoder;
        }

        return null;
    }

    /// <summary>The container an encoder writes by default, from the archive tables.</summary>
    private static (string Extension, string Format) Defaults(string name)
    {
        if (Constants.ContainerByEncoder.TryGetValue(name, out (string Ext, string Format) known))
            return known;

        if (Constants.ContainerChoicesByEncoder.TryGetValue(name, out var choices) && choices.Length > 0)
            return choices[0];

        // An encoder nobody wrote a row for still works; it just gets the
        // container that every player reads.
        return ("avi", "avi");
    }
}
