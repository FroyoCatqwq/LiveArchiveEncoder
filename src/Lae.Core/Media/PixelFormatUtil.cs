
namespace Lae.Core;

/// <summary>Raw transport pixel-format helpers.</summary>
public static class PixelFormatUtil
{
    /// <summary>
    /// Kept for callers that asked the old question directly; the value lives
    /// in <see cref="PixelFormatCatalog"/> now, which is the single source of
    /// truth for the measured table.
    /// </summary>
    public const int PaletteBytes = PixelFormatCatalog.PaletteBytes;

    /// <summary>
    /// True for the indexed formats whose frame buffer is indices plus a
    /// palette. Only <c>pal8</c> qualifies among the formats here: rgb8/bgr8
    /// are packed 8-bit RGB and carry no palette, despite the name.
    /// </summary>
    public static bool CarriesPalette(string pixFmt) =>
        PixelFormatCatalog.TryGet(pixFmt, out PixFmtInfo info) && info.HasPalette;

    /// <summary>
    /// Bytes per frame on the rawvideo pipe.
    ///
    /// v7.9.4: a pal8 frame is w*h <b>+ 1024</b>, not w*h. Measured against this
    /// build (2026-10-22): 64x48 pal8 is 4096 bytes per frame, not 3072. The
    /// pipe reader used to cut every frame 1024 bytes short, so each read
    /// swallowed the previous frame's palette and the frame data shifted by a
    /// kilobyte per frame -- msrle came out black because its palette, which is
    /// the only place the colours live, ended up in the wrong place entirely.
    ///
    /// v7.9.6: the table moved to <see cref="PixelFormatCatalog"/> and is now
    /// measured rather than remembered.
    /// </summary>
    public static int RawFrameSize(int width, int height, string pixFmt)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"frame size needs positive dimensions, got {width}x{height}");
        if (!PixelFormatCatalog.TryGet(pixFmt, out PixFmtInfo info))
        {
            throw new ArgumentException(
                $"Unsupported raw transport pixel format '{pixFmt}'. "
                + "Choose a known encoder-compatible raw format.");
        }
        return info.FrameBytes(width, height);
    }

    /// <summary>
    /// Chroma family of a format, used to pick a transport format from what the
    /// encoder declares. Falls back to the name heuristics for formats outside
    /// the measured table, so an unusual source still maps onto a usable family.
    /// </summary>
    public static string Family(string fmt)
    {
        if (PixelFormatCatalog.TryGet(fmt, out PixFmtInfo info)) return info.Family;

        string f = (fmt ?? "").ToLowerInvariant();
        if (f.StartsWith("yuv", StringComparison.Ordinal))
        {
            if (f.Contains("420", StringComparison.Ordinal)) return "yuv420";
            if (f.Contains("422", StringComparison.Ordinal)) return "yuv422";
            if (f.Contains("444", StringComparison.Ordinal)) return "yuv444";
            if (f.Contains("411", StringComparison.Ordinal)) return "yuv411";
            if (f.Contains("410", StringComparison.Ordinal)) return "yuv410";
            if (f.Contains("440", StringComparison.Ordinal)) return "yuv440";
            return "yuv420";
        }
        if (f.StartsWith("gray", StringComparison.Ordinal)) return "gray";
        if (f.Contains("rgb", StringComparison.Ordinal) || f.Contains("bgr", StringComparison.Ordinal)
            || f.StartsWith("gbr", StringComparison.Ordinal))
        {
            return "rgb";
        }
        return f == "pal8" ? "pal8" : f;
    }

    /// <summary>
    /// The format frames travel in between the decoder and the encoder.
    ///
    /// With no request this is the v7.9.5 behaviour: keep the source's own
    /// format when the encoder takes it, else the nearest format of the same
    /// chroma family, else a sensible default. A request is honoured exactly or
    /// refused -- never silently replaced, because "I asked for 10-bit and got
    /// 8-bit" is the kind of thing only a frame counter would notice.
    /// </summary>
    public static string ChooseTransportPixFmt(string sourceFmt, EncoderCaps caps, string? requested = null)
    {
        if (PixelFormatCatalog.Normalise(requested) is { Length: > 0 } wanted)
        {
            string? problem = PixelFormatCatalog.Validate(wanted, caps);
            if (problem is not null) throw new ArgumentException(problem);
            return PixelFormatCatalog.Normalise(wanted);
        }

        List<string> supported = caps.SupportedPixFmts;
        if (supported.Count == 0)
            return PixelFormatCatalog.TryGet(sourceFmt, out _) ? PixelFormatCatalog.Normalise(sourceFmt) : "yuv420p";

        // Case-insensitive: FFmpeg prints lower case, but a stored profile or a
        // hand-written config may not, and a mismatch here silently downgrades
        // a 10-bit source to 8-bit.
        string? exact = supported.FirstOrDefault(
            x => string.Equals(x, sourceFmt, StringComparison.OrdinalIgnoreCase));
        if (exact is not null && PixelFormatCatalog.TryGet(exact, out _))
            return PixelFormatCatalog.Normalise(exact);

        string sourceFamily = Family(sourceFmt);
        foreach (string fmt in supported)
        {
            if (Family(fmt) == sourceFamily && PixelFormatCatalog.TryGet(fmt, out PixFmtInfo info))
                return info.Name;
        }

        string[] preferred = ["yuv420p", "yuv422p", "yuv444p", "rgb24", "gray", "yuv411p", "yuv410p"];
        foreach (string item in preferred)
        {
            if (supported.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase))
                && PixelFormatCatalog.TryGet(item, out _))
            {
                return item;
            }
        }
        foreach (string item in supported)
        {
            if (PixelFormatCatalog.TryGet(item, out PixFmtInfo info)) return info.Name;
        }

        throw new ArgumentException(
            $"No supported raw transport format known for encoder {caps.Name}: {string.Join(' ', supported)}");
    }

    /// <summary>
    /// Why this transport format cannot be written to this container, or null.
    ///
    /// <para>v7.9.4 put this rule inside the shard planner, because v7.9.4 was
    /// about sharding. That left the plain single-pass run unguarded: msrle into
    /// Matroska was measured at YAVG 16 (pure black) while the same clip in AVI
    /// is YAVG 123, and it was only refused when the user also asked for
    /// --parallel. The rule is about the *container*, not about sharding, so it
    /// belongs here and is checked on every path.</para>
    /// </summary>
    public static string? PaletteContainerConflict(string pixFmt, string container)
    {
        if (!CarriesPalette(pixFmt)) return null;
        if (!Constants.PaletteLossyFormats.Contains(container)) return null;

        return $"{pixFmt} frames cannot be stored in '{container}': the container writes the "
            + "codec header without its palette, so the picture decodes as black. "
            + "Use AVI or MOV instead.";
    }

    /// <summary>
    /// True when reducing the picture to this format loses so much colour that
    /// dithering changes what it looks like: a palette, a sub-8-bit RGB packing
    /// (5:5:5, 5:6:5, 3:3:2), 1-bit monochrome, or 8-bit grey.
    ///
    /// <para>Chroma-subsampled formats are deliberately excluded: swscale's
    /// dither only affects bit-depth reduction, so setting the flag on yuv420p
    /// would be a no-op with a misleading log line.</para>
    /// </summary>
    public static bool WantsDither(string pixFmt)
    {
        if (!PixelFormatCatalog.TryGet(pixFmt, out PixFmtInfo info)) return false;
        return info.LowColour || info.HasPalette;
    }

    /// <summary>
    /// Width/height must be divisible by the chroma grid before a frame can be
    /// written. The old answer was a name heuristic -- which read 4:4:0
    /// (<c>yuv440p</c>) as a 4:2:0 family and demanded an even width that 4:4:0
    /// does not need. Subsampling now comes from the measured table.
    /// </summary>
    public static (int W, int H) RequiredDivisibility(string pixFmt)
    {
        if (PixelFormatCatalog.TryGet(pixFmt, out PixFmtInfo info))
        {
            // Bit-packed rows are byte aligned: an odd width would shift the
            // whole frame, so the width has to reach a whole byte.
            return info.IsBitPacked ? (8, 1) : (info.SubW, info.SubH);
        }

        string fmt = (pixFmt ?? "").ToLowerInvariant();
        if (fmt.Contains("440", StringComparison.Ordinal)) return (1, 2);
        if (fmt.Contains("420", StringComparison.Ordinal)) return (2, 2);
        if (fmt.Contains("422", StringComparison.Ordinal)) return (2, 1);
        if (fmt.Contains("411", StringComparison.Ordinal)) return (4, 1);
        if (fmt.Contains("410", StringComparison.Ordinal)) return (4, 4);
        return (1, 1);
    }

    public static (int W, int H, bool Exact) NormalizeTransportDimensions(
        int width, int height, string pixFmt)
    {
        (int divW, int divH) = RequiredDivisibility(pixFmt);
        if (width % divW == 0 && height % divH == 0) return (width, height, true);
        int newW = Math.Max(divW, width - width % divW);
        int newH = Math.Max(divH, height - height % divH);
        return (newW, newH, false);
    }
}
