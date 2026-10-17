using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.Core;

/// <summary>Raw transport pixel-format helpers (port of the Python helpers).</summary>
public static class PixelFormatUtil
{
    public static int RawFrameSize(int width, int height, string pixFmt)
    {
        if (!Constants.PixelFormatSize.TryGetValue(pixFmt, out double ratio))
        {
            throw new ArgumentException(
                $"Unsupported raw transport pixel format '{pixFmt}'. "
                + "Choose a known encoder-compatible raw format.");
        }
        return (int)(width * height * ratio + 0.5);
    }

    public static string Family(string fmt)
    {
        string f = fmt.ToLowerInvariant();
        if (f.StartsWith("yuv", StringComparison.Ordinal))
        {
            if (f.Contains("420", StringComparison.Ordinal)) return "yuv420";
            if (f.Contains("422", StringComparison.Ordinal)) return "yuv422";
            if (f.Contains("444", StringComparison.Ordinal)) return "yuv444";
            if (f.Contains("411", StringComparison.Ordinal)) return "yuv411";
            if (f.Contains("410", StringComparison.Ordinal)) return "yuv410";
            return "yuv";
        }
        if (f.StartsWith("gray", StringComparison.Ordinal)) return "gray";
        if (f.Contains("rgb", StringComparison.Ordinal) || f.Contains("bgr", StringComparison.Ordinal)
            || f.Contains("argb", StringComparison.Ordinal) || f.Contains("rgba", StringComparison.Ordinal)
            || f.Contains("bgra", StringComparison.Ordinal) || f.Contains("abgr", StringComparison.Ordinal))
        {
            return "rgb";
        }
        return f == "pal8" ? "pal8" : f;
    }

    public static string ChooseTransportPixFmt(string sourceFmt, EncoderCaps caps)
    {
        List<string> supported = caps.SupportedPixFmts;
        if (supported.Count == 0)
        {
            return Constants.PixelFormatSize.ContainsKey(sourceFmt) ? sourceFmt : "yuv420p";
        }
        if (supported.Contains(sourceFmt) && Constants.PixelFormatSize.ContainsKey(sourceFmt))
        {
            return sourceFmt;
        }

        string sourceFamily = Family(sourceFmt);
        foreach (string fmt in supported)
        {
            if (Family(fmt) == sourceFamily && Constants.PixelFormatSize.ContainsKey(fmt))
                return fmt;
        }

        string[] preferred = ["yuv420p", "yuv422p", "yuv444p", "rgb24", "gray", "yuv411p", "yuv410p"];
        foreach (string item in preferred)
        {
            if (supported.Contains(item) && Constants.PixelFormatSize.ContainsKey(item))
                return item;
        }
        foreach (string item in supported)
        {
            if (Constants.PixelFormatSize.ContainsKey(item)) return item;
        }

        throw new ArgumentException(
            $"No supported raw transport format known for encoder {caps.Name}: {string.Join(' ', supported)}");
    }

    public static (int W, int H) RequiredDivisibility(string pixFmt)
    {
        string fmt = pixFmt.ToLowerInvariant();
        if (fmt.Contains("420", StringComparison.Ordinal) || fmt.Contains("440", StringComparison.Ordinal))
            return (2, 2);
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
