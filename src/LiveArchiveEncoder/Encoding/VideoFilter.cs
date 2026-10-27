using System.Globalization;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.Encoding;

/// <summary>
/// A rectangle a subtitle may draw in, expressed against the delivered frame.
/// </summary>
public readonly record struct SubtitleBox(int Width, int Height, int X, int Y)
{
    /// <summary>True when the box is the whole frame, i.e. no compositing is needed.</summary>
    public bool IsWholeFrame(int frameW, int frameH) =>
        Width >= frameW && Height >= frameH && X == 0 && Y == 0;

    /// <summary>
    /// Fit an <paramref name="num"/>:<paramref name="den"/> box inside the frame,
    /// centred, as large as it goes -- "contain", in CSS terms.
    ///
    /// <para>Both edges are rounded down to even numbers: the transport is
    /// normally a 4:2:0 format, where an odd width or offset makes the chroma
    /// planes disagree with the luma planes and FFmpeg silently rounds (or
    /// refuses). Rounding down keeps the box inside the frame instead of one
    /// pixel over.</para>
    /// </summary>
    public static SubtitleBox Contain(int frameW, int frameH, int num, int den)
    {
        int width = Even(frameH * num / den);
        if (width <= frameW)
            return new SubtitleBox(width, Even(frameH), Even((frameW - width) / 2), 0);

        int height = Even(frameW * den / num);
        return new SubtitleBox(Even(frameW), height, 0, Even((frameH - height) / 2));
    }

    private static int Even(int value) => Math.Max(2, value / 2 * 2);

    public override string ToString() =>
        $"{Width}x{Height}+{X}+{Y}";
}

/// <summary>
/// The video filter, in whichever of the two shapes FFmpeg needs.
///
/// <para>Almost every run is a plain <c>-vf "chain"</c>: scale, fps, format,
/// then the burn filters rendered onto the finished frame. That is the cheapest
/// thing FFmpeg can do and it needs nothing else on the command line.</para>
///
/// <para>v7.9.8.3 adds the one case a linear chain cannot express: a subtitle
/// confined to a box of its own aspect ratio. libass always draws into the frame
/// it is handed, so the only way to give it a smaller canvas is to hand it one
/// -- crop the finished frame, render there, and put the result back at the same
/// offset. The crop and the overlay are lossless (same size, same place, no
/// scaling), so the pixels outside the box are the untouched picture, and inside
/// it the text is laid out exactly as it would be on a frame of that shape.</para>
/// </summary>
public sealed record VideoFilter(string Chain, string? Graph = null, string? OutputLabel = null)
{
    /// <summary>The plain <c>-vf</c> form.</summary>
    public static VideoFilter Simple(string chain) => new(chain);

    /// <summary>
    /// Burn subtitles into a box of the requested aspect, compositing the result
    /// back onto the frame.
    ///
    /// <para><paramref name="input"/> is the filtergraph selector for the video
    /// stream (<c>0:v</c> or an absolute <c>0:2</c>). <paramref name="chain"/>
    /// must already deliver a frame of exactly <paramref name="frameW"/> x
    /// <paramref name="frameH"/> in <paramref name="transportFmt"/>.</para>
    /// </summary>
    public static VideoFilter Boxed(
        string chain, string burn, string input, SubtitleBox box,
        int frameW, int frameH, string transportFmt)
    {
        if (box.IsWholeFrame(frameW, frameH))
            return Simple(Join(chain, burn));

        string fmt = $"format={transportFmt}";
        string graph = string.Create(CultureInfo.InvariantCulture,
            $"[{input}]{chain}[pre];"
            + $"[pre]split=2[vmain][vbox];"
            + $"[vbox]crop={box.Width}:{box.Height}:{box.X}:{box.Y},{burn},{fmt}[vtxt];"
            + $"[vmain][vtxt]overlay={box.X}:{box.Y}:format=auto,{fmt}[vout]");
        return new VideoFilter(Join(chain, burn), graph, "vout");
    }

    /// <summary>
    /// The arguments that stand where <c>-vf &lt;chain&gt;</c> used to. With a
    /// graph the output is labelled and mapped, because
    /// <c>-filter_complex</c> does not hand its result to the encoder on its
    /// own.
    /// </summary>
    public List<string> Args()
    {
        if (Graph is null) return ["-vf", Chain];
        return ["-filter_complex", Graph, "-map", $"[{OutputLabel}]"];
    }

    /// <summary>What the run did, for the verbose banner.</summary>
    public bool IsGraph => Graph is not null;

    private static string Join(string chain, string burn)
    {
        if (burn.Length == 0) return chain;
        return chain.Length == 0 ? burn : chain + "," + burn;
    }
}
