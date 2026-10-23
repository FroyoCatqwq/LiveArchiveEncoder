using System.Buffers.Binary;
using System.Text;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>One family name a subtitle file asks for, and where it was found.</summary>
/// <param name="Required">The name as the file spells it (localised names included).</param>
/// <param name="FoundAt">Path of a font file that provides it, or null.</param>
/// <param name="Portable">True when the file was found in the program's own
/// fonts directory (or the extra directory), i.e. it travels with the tool.</param>
public sealed record FontLookup(string Required, string? FoundAt, bool Portable);

/// <summary>One family, as read from a font file.</summary>
public sealed record FontFace(string Family, string Path, bool Portable);

/// <summary>
/// Subtitle fonts (v7.9.5.1): what an ASS file asks for, what is actually
/// installed, and how to close the gap.
///
/// An ASS/SSA file names its fonts in <c>[V4+ Styles]</c> (<c>Fontname</c>) and
/// may also carry them inside the file (<c>[Fonts]</c>). libass renders with
/// fontconfig, so a name it cannot resolve becomes a fallback face -- the
/// classic symptom is a Chinese release asking for "微软雅黑" and rendering
/// boxes on a Linux box with no Chinese fonts at all.
///
/// This class answers three questions without launching anything:
/// <list type="bullet">
/// <item><description>which families does this file ask for
/// (<see cref="RequiredFamilies"/>);</description></item>
/// <item><description>which of them does this machine have, and are they in
/// the program's own <c>fonts</c> directory (<see cref="Check"/>);</description></item>
/// <item><description>if not, copy them in (<see cref="Import"/>) or force a
/// family the machine does have (<c>subtitles=...:force_style=</c>).</description></item>
/// </list>
///
/// Family names are read straight out of the font files' <c>name</c> table, so
/// the answer does not depend on file names (msyh.ttc says "Microsoft YaHei"
/// and "微软雅黑", which a directory listing can never tell you).
/// </summary>
public static class SubtitleFonts
{
    /// <summary>Font file extensions worth reading.</summary>
    public static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".otc"];

    /// <summary>
    /// Families a set of subtitle files asks for, in first-seen order. Only ASS
    /// and SSA carry style font names; SRT and WebVTT say nothing about fonts,
    /// which is the "没有指定的" case where the user may pick one.
    /// </summary>
    public static List<string> RequiredFamilies(IEnumerable<string> files)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files ?? [])
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".ass" or ".ssa")) continue;
            if (!file.HasText) continue;
            try
            {
                foreach (string family in ParseAssFamilies(File.ReadAllLines(file)))
                {
                    if (family.HasText && seen.Add(family.Trim())) found.Add(family.Trim());
                }
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>
    /// The <c>Fontname</c> column of every style, plus anything the file embeds
    /// in its <c>[Fonts]</c> section. Split out from the file read so the
    /// parsing is testable with a string list.
    /// </summary>
    public static List<string> ParseAssFamilies(IReadOnlyList<string> lines)
    {
        var families = new List<string>();
        string section = "";
        int fontColumn = -1;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                fontColumn = -1;
                continue;
            }

            if (section is "v4+ styles" or "v4 styles" or "v4 styles+" or "v4+ styles+")
            {
                // Format: Name, Fontname, Fontsize, ...
                // Style:  Default,Arial,20,...
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string head = line[..colon].Trim();
                string rest = line[(colon + 1)..];

                if (head.Equals("Format", StringComparison.OrdinalIgnoreCase))
                {
                    string[] columns = rest.Split(',');
                    fontColumn = Array.FindIndex(columns,
                        c => c.Trim().Equals("Fontname", StringComparison.OrdinalIgnoreCase));
                }
                else if (head.Equals("Style", StringComparison.OrdinalIgnoreCase) && fontColumn >= 0)
                {
                    string[] fields = rest.Split(',');
                    if (fontColumn < fields.Length)
                    {
                        string name = fields[fontColumn].Trim();
                        if (name.Length > 0) families.Add(name);
                    }
                }
            }
            else if (section == "fonts")
            {
                // An embedded font: "fontname: AR PL UKai CN".
                int colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim().Equals("fontname", StringComparison.OrdinalIgnoreCase))
                {
                    string name = line[(colon + 1)..].Trim();
                    if (name.Length > 0) families.Add(name);
                }
            }
        }
        return families;
    }

    /// <summary>
    /// Where fonts are looked up, most portable first: the program's own
    /// directory, the user's extra directory, then whatever the OS installed.
    /// The OS directories are read-only participants -- they are reported so a
    /// file that only works on this machine can be spotted, not relied on.
    /// </summary>
    public static List<string> SearchDirectories(string? extraDir = null, bool includeSystem = true)
    {
        var dirs = new List<string> { FontResolver.FontDirectory };
        if (extraDir.HasText) dirs.Add(extraDir);
        if (includeSystem) dirs.AddRange(SystemFontDirectories());
        return [.. dirs.Where(d => d.HasText).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> SystemFontDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            string? fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (fonts.HasText) yield return fonts;
            // A per-user font install (no admin rights) lives here on Windows 10+.
            string? local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.HasText) yield return Path.Combine(local, "Microsoft", "Windows", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.HasText)
            {
                yield return Path.Combine(home, ".local", "share", "fonts");
                yield return Path.Combine(home, ".fonts");
            }
        }
    }

    /// <summary>
    /// Every family the search path can provide. Cached for the process: the
    /// scan reads a directory listing plus one <c>name</c> table per file, and
    /// the answer cannot change in the middle of a run.
    /// </summary>
    public static IReadOnlyList<FontFace> Installed(string? extraDir = null, bool includeSystem = true)
    {
        string key = (extraDir ?? "") + (includeSystem ? "|sys" : "|own");
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out List<FontFace>? cached)) return cached;
        }

        var faces = new List<FontFace>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string portableDir = FontResolver.FontDirectory;

        foreach (string dir in SearchDirectories(extraDir, includeSystem))
        {
            bool portable = string.Equals(dir, portableDir, StringComparison.OrdinalIgnoreCase)
                || (extraDir.HasText && string.Equals(dir, extraDir, StringComparison.OrdinalIgnoreCase));

            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = !portable,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                });
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { continue; }

            foreach (string file in files)
            {
                if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                // One file can hold several faces (a .ttc collection), so the
                // dedup key is family+path, not path.
                foreach (string family in ReadFamilies(file))
                {
                    if (family.Length == 0) continue;
                    if (!seen.Add(family + "\u0000" + file)) continue;
                    faces.Add(new FontFace(family, file, portable));
                }
            }
        }

        lock (CacheGate) Cache[key] = faces;
        return faces;
    }

    /// <summary>Drops the cached scan; used after an import.</summary>
    public static void Invalidate() { lock (CacheGate) Cache.Clear(); }

    /// <summary>
    /// Which of the requested families this machine can render, and from where.
    /// The list is ordered like the input so the report reads like the file.
    /// </summary>
    public static List<FontLookup> Check(
        IEnumerable<string> files, string? extraDir = null, bool includeSystem = true)
    {
        List<string> required = RequiredFamilies(files);
        IReadOnlyList<FontFace> installed = Installed(extraDir, includeSystem);

        var result = new List<FontLookup>();
        foreach (string family in required)
        {
            FontFace? match = Match(family, installed);
            result.Add(new FontLookup(family, match?.Path, match?.Portable ?? false));
        }
        return result;
    }

    /// <summary>
    /// The family a subtitle file should be forced onto when it asks for
    /// something nobody has: the first real family in the program's own
    /// directory (so the choice is portable), else the first in the search path.
    /// </summary>
    public static string? DefaultFamily(string? extraDir = null)
    {
        IReadOnlyList<FontFace> installed = Installed(extraDir);
        return installed.FirstOrDefault(f => f.Portable)?.Family
            ?? installed.FirstOrDefault()?.Family;
    }

    /// <summary>
    /// Copy font files into the program's own <c>fonts</c> directory, which is
    /// the one libass is pointed at. A directory is accepted and read one level
    /// deep. Returns how many files were copied and skipped.
    /// </summary>
    public static (int Copied, int Skipped, string Directory) Import(IEnumerable<string> sources)
    {
        string target = FontResolver.FontDirectory;
        Directory.CreateDirectory(target);

        int copied = 0, skipped = 0;
        foreach (string source in sources ?? [])
        {
            if (!source.HasText) continue;
            var files = new List<string>();
            if (Directory.Exists(source))
            {
                files.AddRange(Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
            }
            else if (File.Exists(source))
            {
                files.Add(source);
            }
            else
            {
                skipped++;
                continue;
            }

            foreach (string file in files)
            {
                if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) { skipped++; continue; }
                string name = Path.GetFileName(file);
                string destiny = Path.Combine(target, name);
                try
                {
                    if (File.Exists(destiny) && new FileInfo(destiny).Length == new FileInfo(file).Length)
                    {
                        skipped++;
                        continue;
                    }
                    File.Copy(file, destiny, overwrite: true);
                    copied++;
                }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
        }

        if (copied > 0) Invalidate();
        return (copied, skipped, target);
    }

    /// <summary>
    /// Family lookup with the three rules that make localised names work:
    /// exact (case and spacing ignored), a known translation pair (宋体 /
    /// SimSun), and the "same family, different face" case (Microsoft YaHei /
    /// Microsoft YaHei UI).
    /// </summary>
    public static FontFace? Match(string required, IReadOnlyList<FontFace> installed)
    {
        string wanted = Normalise(required);
        if (wanted.Length == 0) return null;

        FontFace? portableExact = null, anyExact = null, portableFuzzy = null, anyFuzzy = null;
        foreach (FontFace face in installed)
        {
            string candidate = Normalise(face.Family);
            if (candidate.Length == 0) continue;

            if (candidate == wanted)
            {
                if (face.Portable) portableExact ??= face;
                else anyExact ??= face;
                continue;
            }

            if (Aliases(wanted).Overlaps(Aliases(candidate)) || PrefixMatch(wanted, candidate))
            {
                if (face.Portable) portableFuzzy ??= face;
                else anyFuzzy ??= face;
            }
        }
        return portableExact ?? anyExact ?? portableFuzzy ?? anyFuzzy;
    }

    /// <summary>Lower-case, no spaces or separators: "Microsoft YaHei" → "microsoftyahei".</summary>
    public static string Normalise(string? family)
    {
        if (!family.HasText) return "";
        var sb = new StringBuilder(family.Length);
        foreach (char ch in family.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '_') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The Chinese/English pairs that mean the same family. Only pairs that are
    /// genuinely the same font, not "a font that looks similar": 宋体 is SimSun,
    /// 微软雅黑 is Microsoft YaHei, and libass cannot know that on its own.
    /// </summary>
    private static HashSet<string> Aliases(string normalised)
    {
        foreach (HashSet<string> group in AliasGroups)
        {
            if (group.Contains(normalised)) return group;
        }
        return [];
    }

    private static readonly HashSet<string>[] AliasGroups =
    [
        ["宋体", "simsun", "nsimsun", "中易宋体"],
        ["新宋体", "nsimsun"],
        ["黑体", "simhei", "中易黑体"],
        ["微软雅黑", "microsoftyahei", "microsoftyaheiui"],
        ["楷体", "kaiti", "楷体_gb2312", "kaitigb2312"],
        ["仿宋", "fangsong", "fangsong_gb2312"],
        ["等线", "dengxian"],
        ["幼圆", "youyuan"],
        ["隶书", "lishu"],
        ["华文黑体", "stheiti", "heiti"],
        ["华文楷体", "stkaiti"],
        ["华文宋体", "stsong"],
        ["华文中宋", "stzhongsong"],
        ["苹方", "pingfang", "pingfangsc"],
        ["思源黑体", "sourcehansans", "notosanscjk", "notosanssc"],
        ["思源宋体", "sourcehanserif", "notoserifcjk", "notoserifsc"],
        ["文泉驿微米黑", "wenquanyimicrohei"],
        ["timesnewroman", "times"],
    ];

    /// <summary>
    /// "Microsoft YaHei" vs "Microsoft YaHei UI": the same design, two faces.
    /// Only for names long enough that a prefix is not a coincidence.
    /// </summary>
    private static bool PrefixMatch(string a, string b)
    {
        if (a.Length < 6 || b.Length < 6) return false;
        return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }

    // ---- font file reading ----

    /// <summary>
    /// Family names inside a font file, from its <c>name</c> table. Handles
    /// TrueType/OpenType and collections (.ttc), and returns both the English
    /// and the localised names, because an ASS file may use either.
    /// </summary>
    public static List<string> ReadFamilies(string path)
    {
        var names = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 12) return names;

            byte[] head = ReadAt(fs, 0, 12);
            if (head.Length < 12) return names;

            var offsets = new List<long>();
            if (BinaryPrimitives.ReadUInt32BigEndian(head) == 0x74746366)  // 'ttcf'
            {
                uint count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
                int faces = (int)Math.Min(count, 32);
                byte[] arr = ReadAt(fs, 12, faces * 4);
                for (int i = 0; i < faces && (i + 1) * 4 <= arr.Length; i++)
                    offsets.Add(BinaryPrimitives.ReadUInt32BigEndian(arr.AsSpan(i * 4)));
            }
            else
            {
                offsets.Add(0);
            }

            foreach (long fontOffset in offsets)
            {
                byte[] dir = ReadAt(fs, fontOffset, 12);
                if (dir.Length < 12) continue;
                int tables = BinaryPrimitives.ReadUInt16BigEndian(dir.AsSpan(4));
                if (tables <= 0 || tables > 512) continue;

                byte[] records = ReadAt(fs, fontOffset + 12, tables * 16);
                for (int i = 0; i < tables && (i + 1) * 16 <= records.Length; i++)
                {
                    ReadOnlySpan<byte> record = records.AsSpan(i * 16, 16);
                    if (BinaryPrimitives.ReadUInt32BigEndian(record) != 0x6E616D65) continue;  // 'name'

                    // Table offsets are absolute in both a single font and a
                    // collection, so they are never re-based on fontOffset.
                    long tableOffset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
                    long tableLength = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
                    if (tableLength <= 0 || tableLength > 1 << 20) continue;

                    byte[] table = ReadAt(fs, tableOffset, (int)tableLength);
                    ReadNameTable(table, names);
                }
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            // A broken font file is not a reason to fail an encode.
        }
        return names;
    }

    private static byte[] ReadAt(FileStream fs, long offset, int count)
    {
        if (offset < 0 || count <= 0 || offset >= fs.Length) return [];
        int usable = (int)Math.Min(count, fs.Length - offset);
        byte[] buffer = new byte[usable];
        fs.Position = offset;
        int read = 0;
        while (read < usable)
        {
            int got = fs.Read(buffer, read, usable - read);
            if (got <= 0) break;
            read += got;
        }
        return read == usable ? buffer : buffer[..read];
    }

    /// <summary>
    /// Strings out of a <c>name</c> table: nameID 16 (typographic family) when
    /// present, else 1 (family). Every language is kept -- the localised name is
    /// exactly the one a Chinese ASS file will use.
    /// </summary>
    private static void ReadNameTable(ReadOnlySpan<byte> table, List<string> into)
    {
        if (table.Length < 6) return;
        int count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
        int stringOffset = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
        if (count <= 0 || count > 4096) return;

        var typographic = new List<string>();
        var family = new List<string>();

        for (int i = 0; i < count; i++)
        {
            int record = 6 + i * 12;
            if (record + 12 > table.Length) break;

            int platform = BinaryPrimitives.ReadUInt16BigEndian(table[record..]);
            int nameId = BinaryPrimitives.ReadUInt16BigEndian(table[(record + 6)..]);
            if (nameId is not (1 or 16)) continue;

            int length = BinaryPrimitives.ReadUInt16BigEndian(table[(record + 8)..]);
            int offset = BinaryPrimitives.ReadUInt16BigEndian(table[(record + 10)..]);
            int start = stringOffset + offset;
            if (length <= 0 || start < 0 || start + length > table.Length) continue;

            ReadOnlySpan<byte> raw = table.Slice(start, length);
            string text = Decode(raw, platform);
            if (!text.HasText) continue;

            (nameId == 16 ? typographic : family).Add(text.Trim());
        }

        // Typographic family is the cleaner one ("Microsoft YaHei UI Light"
        // still reports "Microsoft YaHei UI"), so it wins when both exist.
        foreach (string name in typographic.Count > 0 ? typographic : family)
        {
            if (!into.Contains(name, StringComparer.OrdinalIgnoreCase)) into.Add(name);
        }
    }

    /// <summary>Windows/Unicode records are UTF-16BE; Mac records are single-byte.</summary>
    private static string Decode(ReadOnlySpan<byte> raw, int platform)
    {
        if (platform is 0 or 3)
        {
            if (raw.Length % 2 != 0) return "";
            return string.Create(raw.Length / 2, raw.ToArray(), (span, bytes) =>
            {
                for (int i = 0; i < span.Length; i++)
                    span[i] = (char)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
            });
        }
        // Mac Roman / ASCII: every name we care about round-trips through Latin-1.
        return System.Text.Encoding.Latin1.GetString(raw);
    }

    private static readonly Dictionary<string, List<FontFace>> Cache = new(StringComparer.Ordinal);
    private static readonly Lock CacheGate = new();
}
