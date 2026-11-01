using Lae.Core.Settings;

namespace Lae.Core;

/// <summary>
/// What burning subtitles costs: the filter fragment, the shape it wants, and the
/// reasons it cannot be done.
/// </summary>
/// <param name="Filter">The fragment, or "" when there is nothing to burn.</param>
/// <param name="BoxAspect">
/// The frame shape the text is laid out in -- 0 when it should use the whole
/// frame. libass scales a line of text against the canvas it is given, so a 4:3
/// caption burned onto a 16:9 frame comes out either tiny or stretched; the only
/// way to keep the layout is to hand libass a canvas of its own shape.
/// </param>
public readonly record struct SubtitleBurn(
    string Filter,
    (int Num, int Den) BoxAspect,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes);

/// <summary>
/// Turns a subtitle request into the filter fragment that burns it.
///
/// <para>The burn happens on the decoder side, before the raw frames leave the
/// pipe. That is not an implementation detail: it is what lets the encoder stay
/// the same in every run. A subtitle burned after encoding would have to be
/// re-encoded, and a subtitle copied as a separate stream would change the
/// container's shape -- neither is what a 2004 archive box did, and both would
/// make the two commands in a dry run a lie about what happens.</para>
///
/// <para>The clock is the part that is easy to get wrong. The decoder seeks with
/// <c>-ss</c> before <c>-i</c>, which rebases the output to zero; the subtitle
/// file is still timed against the original. Burned without a shift, every
/// caption arrives <c>start</c> seconds late -- and on a 20-minute excerpt
/// starting at 18:00, that is the whole point of the excerpt. So the timestamps
/// are pushed forward before the filter and taken back afterwards, which is the
/// same pair of moves v7.9.8.3 settled on.</para>
/// </summary>
public static class SubtitleChain
{
    /// <summary>How many track options a run may burn at once.</summary>
    public const int MaxFiles = 4;

    /// <summary>
    /// The fragment and its problems for this request.
    /// </summary>
    /// <param name="subtitles">The subtitle settings of this run.</param>
    /// <param name="startSeconds">Where the run starts in the source.</param>
    /// <param name="fontsDirectory">
    /// Where libass may look for fonts, or null for "only the system's". Resolved
    /// by the host: it is the folder beside FFmpeg, which this assembly cannot
    /// know about.
    /// </param>
    public static SubtitleBurn Build(
        SubtitleDefaults subtitles, double startSeconds, string? fontsDirectory, Localization.IMessages messages)
    {
        ArgumentNullException.ThrowIfNull(subtitles);

        var problems = new List<string>();
        var notes = new List<string>();

        if (subtitles.Mode == "off")
            return new SubtitleBurn("", default, problems, notes);

        // Only parsed once something will actually be burned. A box aspect is a
        // layout instruction for libass; reading it up front meant a typo in a
        // setting this run does not use refused the whole encode with "unknown
        // subtitle box aspect" -- a video-encoding request blocked by a caption
        // option that would never have been looked at.
        (int, int) aspect = (0, 0);

        // An embedded track is an input, not a file: it has to be unwrapped before
        // the filter can see it, and that is the host's job (it is the only layer
        // allowed to start a process). Reaching here means a caller skipped that
        // step, so say which flag it came from rather than burning nothing.
        if (subtitles.Stream is { } track)
        {
            problems.Add(messages.Format("plan.error.trackNotUnwrapped", new { index = track }));
            return new SubtitleBurn("", default, problems, notes);
        }

        if (subtitles.Mode == "copy")
        {
            // Nothing to draw: a carried track is a stream of its own and the
            // encoder command line is where it belongs, not the filter chain. All
            // this side has to say is whether the request named anything.
            if (subtitles.CopyTracks.Count == 0)
                problems.Add(messages["plan.error.copyNeedsTracks"]);

            return new SubtitleBurn("", default, problems, notes);
        }

        if (subtitles.Mode is not ("burn" or ""))
        {
            problems.Add(messages.Format("plan.error.unknownSubtitleMode", new { mode = subtitles.Mode }));
            return new SubtitleBurn("", default, problems, notes);
        }

        List<string> files = [.. subtitles.Files.Where(f => f.HasText)];
        if (files.Count == 0) return new SubtitleBurn("", default, problems, notes);

        if (files.Count > MaxFiles)
        {
            problems.Add(messages.Format("plan.error.tooManySubtitleFiles", new
            {
                max = MaxFiles,

                // "given", not "count": i18next treats `count` as a plural lookup.
                given = files.Count,
            }));

            return new SubtitleBurn("", default, problems, notes);
        }

        var parts = new List<string>(files.Count * 2 + 2);

        // Here, and not a line earlier: from this point on captions are really
        // being drawn, so this is the first place the box shape means anything.
        aspect = Aspect(subtitles.BoxAspect, problems, messages);
        if (problems.Count > 0) return new SubtitleBurn("", default, problems, notes);

        // Push the timeline forward so the file's own times line up with the
        // excerpt, then put it back. Both halves are required: leaving the second
        // one out hands the encoder frames whose timestamps start at `start`,
        // which the `-r`/fps_mode pair then retimes.
        string shift = ArgsBuilder.FloatArg(startSeconds);
        if (startSeconds > 0) parts.Add($"setpts=PTS+{shift}/TB");

        foreach (string file in files)
        {
            var filters = new List<string> { $"subtitles=filename={FilterArgs.EscapePath(file)}" };

            if (fontsDirectory.HasText)
                filters.Add($"fontsdir={FilterArgs.EscapePath(fontsDirectory)}");

            if (subtitles.FallbackFont.HasText)
            {
                // force_style takes ASS override syntax, one `Name=Value` per
                // entry; the whole value is quoted so the graph parser leaves the
                // commas inside it alone.
                filters.Add($"force_style='{FilterArgs.EscapeValue("FontName=" + subtitles.FallbackFont)}'");
            }

            parts.Add(string.Join(':', filters));
        }

        if (startSeconds > 0) parts.Add($"setpts=PTS-{shift}/TB");

        // No existence check here on purpose: this assembly does not touch the
        // disk, and FFmpeg's own report -- exit 1 naming the path -- is the
        // authority on whether the file could be opened. A planner that guessed
        // would refuse runs that work.
        return new SubtitleBurn(string.Join(',', parts), aspect, problems, notes);
    }

    /// <summary>
    /// <see cref="VideoFilter"/> for a chain that ends in this burn: the plain
    /// form when the text may use the whole frame, the boxed graph when it may
    /// not.
    /// </summary>
    public static VideoFilter Apply(
        string chain, SubtitleBurn burn, string inputSelector,
        int frameWidth, int frameHeight, string transportFmt)
    {
        if (burn.Filter.Length == 0) return VideoFilter.Simple(chain);
        if (burn.BoxAspect == default) return VideoFilter.Simple(Join(chain, burn.Filter));

        SubtitleBox box = SubtitleBox.Contain(
            frameWidth, frameHeight, burn.BoxAspect.Num, burn.BoxAspect.Den);

        return VideoFilter.Boxed(chain, burn.Filter, inputSelector, box, frameWidth, frameHeight, transportFmt);
    }

    /// <summary>"source" means the whole frame; anything else is a shape libass must be given.</summary>
    private static (int Num, int Den) Aspect(
        string? boxAspect, List<string> problems, Localization.IMessages messages)
    {
        switch ((boxAspect ?? "source").Trim().ToLowerInvariant())
        {
            case "source":
            case "":
                return default;
            case "4:3":
                return (4, 3);
            case "16:9":
                return (16, 9);
            default:
                problems.Add(messages.Format("plan.error.unknownBoxAspect", new { value = boxAspect }));
                return default;
        }
    }

    private static string Join(string chain, string burn)
        => chain.Length == 0 ? burn : chain + "," + burn;
}
