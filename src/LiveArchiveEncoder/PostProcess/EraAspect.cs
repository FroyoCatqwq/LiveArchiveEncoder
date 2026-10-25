using System.Globalization;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>Which frame shape an era preset should be captured and delivered in.</summary>
public enum EraAspect
{
    /// <summary>The shape the preset itself was written for (the default).</summary>
    Source = 0,

    /// <summary>4:3 -- the shape of every cathode-ray tube and of the 2000s web.</summary>
    FourThree = 1,

    /// <summary>16:9 -- widescreen, which only arrives with the 2010s rows.</summary>
    SixteenNine = 2,
}

/// <summary>
/// Frame shapes for era presets.
///
/// <para>v7.9.6.3: a preset used to carry exactly one frame size, so a 2007
/// portal preset could only ever produce 4:3 -- even though the same upload was
/// letterboxed to 16:9 by the time the same site had a widescreen player. The
/// shape is a property of the *period's display*, not of the codec, so it is a
/// switch instead of a second table.</para>
///
/// <para>Not every shape is available everywhere, and pretending otherwise
/// would produce files that do not open: H.261 and H.263 only exist at the CIF
/// sizes, AMV wants multiples of 16, and the rest of the legacy set needs even
/// dimensions. <see cref="Resolve"/> therefore answers "this encoder cannot do
/// that shape" instead of handing back a size FFmpeg will reject a minute
/// later.</para>
/// </summary>
public static class EraAspects
{
    /// <summary>The spellings the CLI and the menu accept.</summary>
    public static bool TryParse(string? text, out EraAspect aspect)
    {
        aspect = EraAspect.Source;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim().ToLowerInvariant();
        switch (value)
        {
            case "source" or "native" or "auto" or "原始" or "orig":
                aspect = EraAspect.Source;
                return true;
            case "4:3" or "4/3" or "43" or "4x3":
                aspect = EraAspect.FourThree;
                return true;
            case "16:9" or "16/9" or "169" or "16x9":
                aspect = EraAspect.SixteenNine;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Stable spelling for reports and saved settings.</summary>
    public static string Label(EraAspect aspect) => aspect switch
    {
        EraAspect.FourThree => "4:3",
        EraAspect.SixteenNine => "16:9",
        _ => "source",
    };

    public static IEnumerable<string> Labels => ["source", "4:3", "16:9"];

    /// <summary>True when the pair really is that shape, within a rounding step.</summary>
    public static bool Is(EraAspect aspect, int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        double ratio = (double)width / height;
        double wanted = aspect switch
        {
            EraAspect.FourThree => 4.0 / 3.0,
            EraAspect.SixteenNine => 16.0 / 9.0,
            _ => ratio,
        };
        return Math.Abs(ratio - wanted) < 0.02;
    }

    /// <summary>What shape a preset's own numbers describe.</summary>
    public static EraAspect ShapeOf(int width, int height)
    {
        if (Is(EraAspect.SixteenNine, width, height)) return EraAspect.SixteenNine;
        if (Is(EraAspect.FourThree, width, height)) return EraAspect.FourThree;
        return EraAspect.Source;
    }

    /// <summary>
    /// The frame size this encoder can actually be asked for at this shape, or
    /// null plus the reason it cannot.
    ///
    /// <para>The rule is "keep the capture width, change the height": 320x240
    /// becomes 320x180, 1280x720 becomes 1280x960. Keeping the width keeps the
    /// horizontal detail the preset was tuned for, and the bitrate ceiling (a
    /// property of the period, not of the shape) is left alone.</para>
    /// </summary>
    public static (int W, int H)? Resolve(
        EraPreset preset, EraAspect aspect, string encoderName, out string? reason)
    {
        reason = null;
        if (!preset.HasLive)
        {
            reason = "this preset has no live layer to reshape";
            return null;
        }

        int width = preset.Width!.Value;
        int height = preset.Height!.Value;
        (int w, int h) = aspect == EraAspect.Source || ShapeOf(width, height) == aspect
            ? (width, height)
            : Scale(width, aspect);

        // Validated even when the preset was already that shape: asking for 4:3
        // on a 4:3 preset with H.263 is still a request H.263 cannot honour
        // (160x120 is not a CIF size), and the answer has to be the reason, not
        // a frame size FFmpeg will reject later.
        string? unsupported = Support(encoderName, w, h);
        if (unsupported is not null)
        {
            reason = unsupported;
            return null;
        }
        return (w, h);
    }

    private static (int W, int H) Scale(int width, EraAspect aspect)
    {
        double ratio = aspect == EraAspect.FourThree ? 3.0 / 4.0 : 9.0 / 16.0;
        int height = (int)Math.Round(width * ratio, MidpointRounding.AwayFromZero);
        // Both halves of a legacy encode want even numbers; a 1-pixel rounding
        // difference is invisible but an odd height is not.
        if (height % 2 != 0) height++;
        return (width, height);
    }

    /// <summary>
    /// Why this encoder cannot be asked for this frame size, or null when it can.
    /// The rules are the ones FFmpeg actually enforces for these encoders --
    /// a size outside them is refused by the encoder mid-run, which is a much
    /// worse way to find out.
    /// </summary>
    public static string? Support(string encoderName, int width, int height)
    {
        if (width <= 0 || height <= 0) return "frame size must be positive";

        string name = encoderName.Trim().ToLowerInvariant();

        // The CIF family only exists at a fixed set of sizes.
        if (Constants.SpecialSizes.TryGetValue(name, out (int W, int H)[]? fixedSizes))
        {
            if (fixedSizes.Contains((width, height))) return null;
            string list = string.Join(", ",
                fixedSizes.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.W}x{s.H}")));
            return $"{name} only exists at {list}";
        }

        if (name == "amv" && (width % Constants.AmvDimensionMultiple != 0
                              || height % Constants.AmvDimensionMultiple != 0))
        {
            return $"amv needs both sides to be multiples of {Constants.AmvDimensionMultiple}";
        }

        // Everything else in the legacy set is a 4:2:0/4:2:2 encoder: two is
        // the hard floor, and 32-bit alignment avoids the padded-to-a-macroblock
        // sizes some of these write into the header.
        if (width % 2 != 0 || height % 2 != 0)
            return $"{name} needs even width and height";

        // A cap that is about the codec, not about taste: these predate 720p
        // and quietly mis-encode beyond the frame they were designed for.
        if (name is "rv10" or "rv20" && (width > 704 || height > 576))
            return $"{name} was designed for at most 704x576";
        if (name == "flv" && (width > 1280 || height > 720))
            return "Sorenson Spark (flv) tops out at 1280x720";

        return null;
    }
}
