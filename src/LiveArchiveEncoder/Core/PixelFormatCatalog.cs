using System.Collections.Frozen;
using System.Globalization;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// One row of the measured pixel-format table.
///
/// <para><see cref="BytesPerPixel"/> is not arithmetic guesswork: every row was
/// produced by dumping N frames of 64x48 rawvideo and dividing, with
/// <c>tools/pixfmt_sweep.py</c>. A single wrong byte per frame shifts the whole
/// pipe and turns the picture into noise (that is what made msrle black in
/// v7.9.4), so the table is measured rather than remembered.</para>
/// </summary>
/// <param name="Name">FFmpeg's own name, e.g. <c>pal8</c>.</param>
/// <param name="BytesPerPixel">Bytes one pixel occupies in a raw frame.</param>
/// <param name="Family">Chroma layout: yuv420, yuv422, yuv444, yuv411, yuv410,
/// yuv440, gray, rgb, pal8.</param>
/// <param name="BitDepth">Bits per component: 1 (monob), 8, 9, 10, 12, 14, 16, 32.</param>
/// <param name="SubW">Chroma width divisor (2 for 4:2:0, 4 for 4:1:0, else 1).</param>
/// <param name="SubH">Chroma height divisor (2 for 4:2:0, 4 for 4:1:0, 2 for 4:4:0, else 1).</param>
/// <param name="HasAlpha">Carries an alpha plane or channel.</param>
/// <param name="HasPalette">Carries a 1024-byte palette in every raw frame.</param>
/// <param name="LowColour">
/// Converting into this format throws colour information away (a palette, a
/// 5:6:5 packing, 8-bit grey), so swscale dithering changes what it looks like.
/// It is a property of the format rather than a rule about bit depth: rgb8 is
/// 8 bits per pixel and still loses colour, yuv422p is 16 bits per pixel and
/// loses only resolution.
/// </param>
public sealed record PixFmtInfo(
    string Name,
    double BytesPerPixel,
    string Family,
    int BitDepth,
    int SubW,
    int SubH,
    bool HasAlpha = false,
    bool HasPalette = false,
    bool LowColour = false)
{
    /// <summary>True for the bit-packed monochrome pair, whose rows are byte aligned.</summary>
    public bool IsBitPacked => BytesPerPixel < 1.0;

    /// <summary>
    /// Bytes this format adds to one raw frame.
    ///
    /// Bit-packed formats pad every row to a whole byte, so the size is
    /// <c>ceil(width/8)*height</c>, not <c>width*height/8</c> -- a 65-pixel-wide
    /// monob frame is 9 bytes per row, not 8.125.
    /// </summary>
    public int FrameBytes(int width, int height)
    {
        long size = IsBitPacked
            ? (long)((width + 7) / 8) * height
            : (long)Math.Round((long)width * height * BytesPerPixel, MidpointRounding.AwayFromZero);
        return (int)(HasPalette ? size + PixelFormatCatalog.PaletteBytes : size);
    }

    /// <summary>"10-bit 4:2:0, 3.00 B/px" -- what the format actually is.</summary>
    public string Describe()
    {
        string chroma = Family switch
        {
            "yuv420" => "4:2:0",
            "yuv422" => "4:2:2",
            "yuv444" => "4:4:4",
            "yuv411" => "4:1:1",
            "yuv410" => "4:1:0",
            "yuv440" => "4:4:0",
            "gray" => "grey",
            "rgb" => HasAlpha ? "RGB+A" : "RGB",
            "pal8" => "palette",
            _ => Family,
        };
        string tail = HasPalette ? " + 1 KB palette" : "";
        string bpp = BytesPerPixel.ToString("0.###", CultureInfo.InvariantCulture);
        return $"{BitDepth}-bit {chroma}, {bpp} B/px{tail}";
    }
}

/// <summary>
/// The pixel formats this tool can move between a decoder and an encoder over a
/// rawvideo pipe.
///
/// <para>Why a table at all: the encoder never reads the source file. Frames
/// arrive as <c>rawvideo</c>, so both ends must agree on how many bytes one
/// frame is. FFmpeg will happily write a format whose frame size this program
/// computes differently, and the result is not an error message -- it is a
/// picture that slides sideways one byte at a time.</para>
///
/// <para>v7.9.6: this table replaced a 20-row list that had two wrong
/// entries. <c>yuv440p</c> was declared as 1.5 B/px; it is 2.0 (4:4:0 keeps U
/// and V at full width and half height), and its frame-size divisibility was
/// taken as 2x2 when 4:4:0 only needs height to be even. Neither had ever been
/// hit, because nothing in the legacy set emits 4:4:0 -- which is exactly how
/// a latent corruption survives. The table is now measured end to end, and
/// <c>PixelFormatCatalogTests</c> pins every row.</para>
/// </summary>
public static class PixelFormatCatalog
{
    /// <summary>
    /// Bytes an indexed format adds to every raw frame: FFmpeg's rawvideo
    /// format carries the palette inline, 1024 bytes (256 entries x BGRA).
    /// </summary>
    public const int PaletteBytes = 1024;

    /// <summary>Every format, keyed by FFmpeg's own name (case-insensitive).</summary>
    public static readonly FrozenDictionary<string, PixFmtInfo> Formats = Build();

    /// <summary>All names, ordinal-sorted, for menus and error messages.</summary>
    public static readonly IReadOnlyList<string> Names =
        [.. Formats.Keys.OrderBy(x => x, StringComparer.Ordinal)];

    /// <summary>
    /// The list a user is most likely to want, in a sensible order: the
    /// transports this tool has always used first, then the exotic ones.
    /// </summary>
    public static readonly IReadOnlyList<string> Common =
    [
        "yuv420p", "yuv422p", "yuv444p", "yuvj420p", "gray", "rgb24", "bgr24",
        "pal8", "yuv420p10le", "yuv422p10le", "yuv444p10le", "gray10le", "nv12",
        "rgb555le", "rgb565le", "rgb48le",
    ];

    public static bool TryGet(string? name, out PixFmtInfo info)
    {
        string key = Normalise(name);
        if (key.Length > 0 && Formats.TryGetValue(key, out PixFmtInfo? found))
        {
            info = found;
            return true;
        }
        info = null!;
        return false;
    }

    /// <summary>
    /// FFmpeg's spelling: lower case, no surrounding space. Returns "" for
    /// null/blank, and "auto" is normalised to "" because it is the "no
    /// request" spelling everywhere in this program.
    /// </summary>
    public static string Normalise(string? name)
    {
        string value = (name ?? "").Trim().ToLowerInvariant();
        return value is "auto" or "source" or "-" ? "" : value;
    }

    /// <summary>
    /// Human-readable reason a format cannot be used, or null. Kept separate
    /// from the throw so the CLI can report it as an argument error (exit 2)
    /// while the encoder path can throw it as a run failure.
    /// </summary>
    public static string? Validate(string requested, Configuration.EncoderCaps caps)
    {
        if (!TryGet(requested, out PixFmtInfo info))
        {
            return $"unknown or unverified pixel format '{requested}'; "
                + $"known ones include: {string.Join(", ", Common)} "
                + "(see --list-pix-fmts for the full list)";
        }

        // An encoder that declares no list cannot say no; the preflight and the
        // post-run check are what catch it in that case.
        if (caps.SupportedPixFmts.Count > 0
            && !caps.SupportedPixFmts.Any(x => string.Equals(x, info.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"encoder '{caps.Name}' cannot take {info.Name} ({info.Describe()}); "
                + $"it supports: {string.Join(", ", caps.SupportedPixFmts)}";
        }
        return null;
    }

    private static FrozenDictionary<string, PixFmtInfo> Build()
    {
        var table = new Dictionary<string, PixFmtInfo>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, double bpp, string family, int depth,
            int subW = 1, int subH = 1, bool alpha = false, bool palette = false,
            bool lowColour = false) =>
            table[name] = new PixFmtInfo(name, bpp, family, depth, subW, subH, alpha, palette, lowColour);

        // ---- planar YUV, 8 bit ------------------------------------------------
        // 4:1:0 keeps U and V at a quarter width and a quarter height.
        Add("yuv410p", 1.125, "yuv410", 8, 4, 4);
        Add("yuvj411p", 1.5, "yuv411", 8, 4, 1);
        Add("yuv411p", 1.5, "yuv411", 8, 4, 1);
        Add("yuv420p", 1.5, "yuv420", 8, 2, 2);
        Add("yuvj420p", 1.5, "yuv420", 8, 2, 2);
        Add("yuv422p", 2.0, "yuv422", 8, 2, 1);
        Add("yuvj422p", 2.0, "yuv422", 8, 2, 1);
        // 4:4:0: full width, half height -- 2 bytes/pixel, not 1.5.
        Add("yuv440p", 2.0, "yuv440", 8, 1, 2);
        Add("yuv444p", 3.0, "yuv444", 8);
        Add("yuvj444p", 3.0, "yuv444", 8);

        // ---- planar YUV, 9/10/12/14/16 bit (little endian) --------------------
        // One sample is two bytes whatever the depth, so the depth does not
        // change bytes-per-pixel -- only the number of meaningful bits.
        foreach ((int depth, string suffix) in new[]
                 {
                     (9, "p9le"), (10, "p10le"), (12, "p12le"), (14, "p14le"), (16, "p16le"),
                 })
        {
            Add($"yuv420{suffix}", 3.0, "yuv420", depth, 2, 2);
            Add($"yuv422{suffix}", 4.0, "yuv422", depth, 2, 1);
            Add($"yuv444{suffix}", 6.0, "yuv444", depth);
        }

        // ---- semi-planar ------------------------------------------------------
        Add("nv12", 1.5, "yuv420", 8, 2, 2);
        Add("nv21", 1.5, "yuv420", 8, 2, 2);
        Add("nv16", 2.0, "yuv422", 8, 2, 1);
        Add("nv20le", 4.0, "yuv422", 10, 2, 1);
        Add("nv24", 3.0, "yuv444", 8);
        Add("nv42", 3.0, "yuv444", 8);
        Add("p010le", 3.0, "yuv420", 10, 2, 2);
        Add("p012le", 3.0, "yuv420", 12, 2, 2);
        Add("p016le", 3.0, "yuv420", 16, 2, 2);
        Add("p210le", 4.0, "yuv422", 10, 2, 1);
        Add("p410le", 6.0, "yuv444", 10);

        // ---- packed YUV -------------------------------------------------------
        Add("uyvy422", 2.0, "yuv422", 8, 2, 1);
        Add("yuyv422", 2.0, "yuv422", 8, 2, 1);
        Add("yvyu422", 2.0, "yuv422", 8, 2, 1);

        // ---- alpha variants ---------------------------------------------------
        Add("yuva420p", 2.5, "yuv420", 8, 2, 2, alpha: true);
        Add("yuva422p", 3.0, "yuv422", 8, 2, 1, alpha: true);
        Add("yuva444p", 4.0, "yuv444", 8, alpha: true);
        Add("yuva420p10le", 5.0, "yuv420", 10, 2, 2, alpha: true);

        // ---- grey -------------------------------------------------------------
        Add("gray", 1.0, "gray", 8, lowColour: true);
        foreach (int depth in new[] { 9, 10, 12, 14, 16 })
            Add($"gray{depth}le", 2.0, "gray", depth);
        Add("grayf32le", 4.0, "gray", 32);
        Add("ya8", 2.0, "gray", 8, alpha: true);
        Add("ya16le", 4.0, "gray", 16, alpha: true);
        // Bit-packed: one bit per pixel, rows padded to a whole byte (64x48
        // measures 384 bytes, i.e. exactly 64*48/8).
        Add("monob", 0.125, "gray", 1, lowColour: true);
        Add("monow", 0.125, "gray", 1, lowColour: true);

        // ---- packed RGB -------------------------------------------------------
        Add("rgb24", 3.0, "rgb", 8);
        Add("bgr24", 3.0, "rgb", 8);
        // 3:3:2 packed -- one byte per pixel, no palette.
        Add("rgb8", 1.0, "rgb", 8, lowColour: true);
        Add("bgr8", 1.0, "rgb", 8, lowColour: true);
        Add("rgb4_byte", 1.0, "rgb", 8, lowColour: true);
        Add("bgr4_byte", 1.0, "rgb", 8, lowColour: true);
        Add("rgb555le", 2.0, "rgb", 5, lowColour: true);
        Add("rgb555be", 2.0, "rgb", 5, lowColour: true);
        Add("bgr555le", 2.0, "rgb", 5, lowColour: true);
        Add("rgb565le", 2.0, "rgb", 6, lowColour: true);
        Add("rgb565be", 2.0, "rgb", 6, lowColour: true);
        Add("bgr565le", 2.0, "rgb", 6, lowColour: true);
        Add("x2rgb10le", 4.0, "rgb", 10);
        Add("x2bgr10le", 4.0, "rgb", 10);
        Add("rgb48le", 6.0, "rgb", 16);
        Add("rgb48be", 6.0, "rgb", 16);
        // 32-bit RGB with a padding byte: four bytes, three of them colour.
        Add("rgba", 4.0, "rgb", 8, alpha: true);
        Add("bgra", 4.0, "rgb", 8, alpha: true);
        Add("argb", 4.0, "rgb", 8, alpha: true);
        Add("abgr", 4.0, "rgb", 8, alpha: true);
        Add("rgb0", 4.0, "rgb", 8);
        Add("bgr0", 4.0, "rgb", 8);
        Add("0rgb", 4.0, "rgb", 8);
        Add("0bgr", 4.0, "rgb", 8);

        // ---- planar RGB -------------------------------------------------------
        Add("gbrp", 3.0, "rgb", 8);
        foreach (int depth in new[] { 9, 10, 12, 14, 16 })
            Add($"gbrp{depth}le", 6.0, "rgb", depth);
        Add("gbrpf32le", 12.0, "rgb", 32);

        // ---- indexed ----------------------------------------------------------
        Add("pal8", 1.0, "pal8", 8, palette: true, lowColour: true);

        return table.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
