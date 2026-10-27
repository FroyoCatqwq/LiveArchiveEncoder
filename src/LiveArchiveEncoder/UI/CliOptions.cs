using System.Globalization;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.UI;

/// <summary>Parsed command line, mirroring the argparse definition of v6.3.3.</summary>
public sealed class CliOptions
{
    public string? Input { get; init; }
    public string? Output { get; init; }
    public double StartTime { get; init; }
    public double? EndTime { get; init; }
    public string Encoder { get; init; } = "rv20";

    /// <summary>
    /// True when <c>--encoder</c> appeared on the command line. The property
    /// above cannot express that on its own -- it has a default -- and an era
    /// preset must not override a choice the user actually made.
    /// </summary>
    public bool EncoderExplicit { get; init; }
    public string? AudioEncoder { get; init; }
    public double Limit { get; init; } = 200.0;
    public double? Recover { get; init; }
    public double Window { get; init; } = 2.0;
    public double Fps { get; init; } = 13.0;
    public string FpsLevels { get; init; } = "13,8,5,3";

    /// <summary>
    /// True when <c>--fps-levels</c> appeared on the command line. The default
    /// ladder starts at 13 fps, and an encoder that snaps the base rate below
    /// that (mpeg2video lands on 12, for example) must keep working: only a
    /// ladder the user actually typed is validated against the base rate.
    /// </summary>
    public bool FpsLevelsExplicit { get; init; }

    // ---- v7.9.3.1: which of the era-shaped values the user actually typed ----
    //
    // Every property below has a non-nullable default, so "was it given?" cannot
    // be read off the value. An era preset is documented as a *fallback*
    // ("预设是兜底，不是覆盖"), and without these flags `--era flv07 --width 320`
    // silently encoded at the preset's 352 -- the opposite of what the manual
    // says. One flag per value the presets touch.
    public bool FpsExplicit { get; init; }
    public bool WidthExplicit { get; init; }
    public bool HeightExplicit { get; init; }
    public bool LimitExplicit { get; init; }
    public bool ScaleAlgoExplicit { get; init; }
    public bool AudioKbpsExplicit { get; init; }
    public bool AudioChannelsExplicit { get; init; }
    public int? Qv { get; init; }
    public int Threads { get; init; } = 16;
    public int BufferFrames { get; init; } = 6;
    public int AudioKbps { get; init; } = 32;
    public int AudioChannels { get; init; } = 1;
    public int? AudioRate { get; init; }

    /// <summary>
    /// v7.9: absolute stream index of the video track to encode (the number
    /// <c>ffprobe -show_streams</c> prints). Null means the first video stream,
    /// which is what every version up to v7.8 used.
    /// </summary>
    public int? VideoStream { get; init; }

    /// <summary>v7.9: absolute stream index of the audio track to keep; null means the first.</summary>
    public int? AudioStream { get; init; }

    /// <summary>v7.9: print the source's streams and exit (pairs with the two picks above).</summary>
    public bool ListStreams { get; init; }

    // ---- v7.9 #2: subtitles ----

    /// <summary>External subtitle file(s), e.g. clip.sc.ass. Repeatable.</summary>
    public IReadOnlyList<string> SubtitleFiles { get; init; } = [];

    /// <summary>Absolute index of an embedded subtitle track to use.</summary>
    public int? SubtitleStream { get; init; }

    /// <summary>burn (into the picture), copy (as a stream) or off.</summary>
    public string SubtitleMode { get; init; } = "burn";

    /// <summary>Explicit subtitle codec for copy mode; null picks it per container.</summary>
    public string? SubtitleCodec { get; init; }

    /// <summary>
    /// v7.9.5.1: extra directory libass looks in. Imported fonts belong in the
    /// program's own <c>fonts</c> directory, which is what travels with it.
    /// </summary>
    public string? SubtitleFontDir { get; init; }

    /// <summary>
    /// v7.9.5.1: family forced over the one a subtitle file asks for. Needed for
    /// SRT/VTT (which name no font) and for ASS files whose font nobody has.
    /// </summary>
    public string? SubtitleFont { get; init; }

    /// <summary>
    /// v7.9.5.1: copy font files into the program's <c>fonts</c> directory and
    /// exit. Accepts a file or a directory.
    /// </summary>
    public string? FontImport { get; init; }

    /// <summary>v7.9.5.1: print the fonts a subtitle file needs, then exit.</summary>
    public bool ListSubtitleFonts { get; init; }
    public int Width { get; init; } = 256;
    public int Height { get; init; } = 192;

    /// <summary>v7.8: container override, e.g. avi / mkv / mp4. Null = encoder default.</summary>
    public string? Container { get; init; }

    /// <summary>v7.8: crop, fit (pad) or stretch. --no-crop still means stretch.</summary>
    public string FitMode { get; init; } = "crop";
    public string CropAnchor { get; init; } = "center";
    public string PadColor { get; init; } = "black";
    public bool NoCrop { get; init; }
    public string ScaleAlgo { get; init; } = "neighbor";

    // ---- v7.9.6: picture format and colour ----

    /// <summary>
    /// Output pixel format (<c>pal8</c>, <c>gray</c>, <c>rgb24</c>,
    /// <c>rgb555le</c>, <c>yuv420p10le</c>, ...). Null means "not given", which
    /// keeps the automatic choice; the string "auto" is accepted and means the
    /// same thing.
    /// </summary>
    public string? PixFmt { get; init; }

    /// <summary>Colour tags written onto the output; null keeps the source's.</summary>
    public string? ColorRange { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }

    /// <summary>swscale dither for low-colour output; "auto" emits no flag.</summary>
    public string Dither { get; init; } = "auto";

    /// <summary>Print the pixel formats this build / the selected encoder accepts, then exit.</summary>
    public bool ListPixFmts { get; init; }

    public string Deinterlace { get; init; } = "off";
    public string DeintMode { get; init; } = "send_frame";
    public string DeintParity { get; init; } = "auto";
    public bool DeintMarkedOnly { get; init; }
    public string X264Preset { get; init; } = "medium";
    public string X264Tune { get; init; } = "";
    public string X264Profile { get; init; } = "";
    public string X264Level { get; init; } = "";
    /// <summary>
    /// Hardware decode method. "off" is the default since v7.5: using one needs
    /// a working driver and device, both of which are verified (by decoding a
    /// frame) before the method is actually used.
    /// </summary>
    public string Hwaccel { get; init; } = "off";

    /// <summary>Kept for v7.4 scripts; means <c>--hwaccel off</c>.</summary>
    public bool NoCuda { get; init; }
    public string? FFmpeg { get; init; }
    public string? FFprobe { get; init; }
    public bool ListFilters { get; init; }
    public bool Quiet { get; init; }
    public bool NoCui { get; init; }
    /// <summary>
    /// Inline is the default because it is what v6.3.3 does. The split pipeline is
    /// opt-in: measured over a 5 s segment it is ~0.7 s <em>slower</em> (extra audio
    /// pass plus a remux) and it perturbs the adaptive controller's frame selection,
    /// which shifts the container duration on mpeg1video and asf.
    /// </summary>
    public Encoding.AudioMode AudioMode { get; init; } = Encoding.AudioMode.Inline;
    public bool Help { get; init; }
    public bool CpuInfo { get; init; }

    /// <summary>Shard count for the parallel pipeline. 1 disables sharding.</summary>
    public int Parallel { get; init; } = 1;

    /// <summary>
    /// UI language, e.g. <c>en</c> or <c>zh</c>. Null means "not given on the
    /// command line", which is what lets the interactive shell fall back to the
    /// language stored in the settings file instead of forcing the default.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>When set, print a shell completion script and exit.</summary>
    public string? Completion { get; init; }

    /// <summary>FFmpeg distribution to use: btbn / gyan / avbuild / nonfree.</summary>
    public string FfmpegSource { get; init; } = LiveArchiveEncoder.FFmpeg.FfmpegCatalog.DefaultId;

    /// <summary>Proxy for FFmpeg downloads, e.g. http://127.0.0.1:10808.</summary>
    public string? FfmpegProxy { get; init; }

    /// <summary>When set, download and install that distribution, then exit.</summary>
    public string? FfmpegDownload { get; init; }

    /// <summary>
    /// Local archive to install instead of downloading. Needed for sources whose
    /// host blocks automated downloads (SourceForge / avbuild).
    /// </summary>
    public string? FfmpegArchive { get; init; }

    /// <summary>When set, delete an installed distribution, then exit.</summary>
    public string? FfmpegDelete { get; init; }

    public bool FfmpegList { get; init; }

    /// <summary>What to open when the encode finishes.</summary>
    public PlayMode Play { get; init; } = PlayMode.Off;

    /// <summary>External player executable; empty uses the shell association.</summary>
    public string? PlayerPath { get; init; }

    // ---- v7.6: post-processing ----

    /// <summary>Arm the recompress pass, using the stored or given arguments.</summary>
    public bool Recompress { get; init; }
    public string? RecompressArgs { get; init; }
    public string? RecompressContainer { get; init; }
    public bool NoRecompress { get; init; }

    /// <summary>
    /// v7.9.5.1: pick the second pass's video codec by name instead of writing a
    /// fragment. The container follows the codec when none was named.
    /// </summary>
    public string? RecompressCodec { get; init; }

    /// <summary>CRF / q:v for <see cref="RecompressCodec"/>; null = the codec's default.</summary>
    public int? RecompressQuality { get; init; }

    /// <summary>Audio codec for the second pass (aac, libopus, libvorbis, copy, ...).</summary>
    public string? RecompressAudioCodec { get; init; }

    /// <summary>Audio bitrate for the second pass; null = the codec's default.</summary>
    public int? RecompressAudioKbps { get; init; }

    /// <summary>
    /// v7.9.5.1: render an era/site preset with libx264 + AAC in MP4 instead of
    /// the codec that site actually served. Default is the site's own codec.
    /// </summary>
    public bool EraModern { get; init; }

    /// <summary>The default spelling of <see cref="EraModern"/> being false.</summary>
    public bool EraNative { get; init; }

    /// <summary>
    /// v7.9.5: run the recompress pass <b>on its own</b>, against an existing
    /// file. No live encode happens, so no video encoder is chosen or probed.
    /// </summary>
    public bool RecompressOnly { get; init; }

    /// <summary>Era preset id to apply; see <see cref="PostProcess.EraPresets"/>.</summary>
    public string? Era { get; init; }

    /// <summary>
    /// v7.9.6.3: the frame shape an era preset should be captured and delivered
    /// in -- <c>source</c> (the preset's own), <c>4:3</c> or <c>16:9</c>. Only
    /// shapes the selected encoder can actually encode are accepted; see
    /// <see cref="PostProcess.EraAspects"/>.
    /// </summary>
    public string? EraAspect { get; init; }

    /// <summary>
    /// v7.9.7: which H.264 encoder to use where the preset says H.264 --
    /// <c>libx264</c> or <c>libopenh264</c>. Null means "the preset decides",
    /// and the 2011+ presets decide OpenH264.
    /// </summary>
    public string? H264Impl { get; init; }

    /// <summary>True when <c>--h264</c> was given; a preset must not override it.</summary>
    public bool H264Explicit { get; init; }

    /// <summary>
    /// v7.9.6.3: measure the link to an input path (or URL) and print the
    /// verdict that <c>--parallel</c> would use. No encode happens.
    /// </summary>
    public string? NetTest { get; init; }

    /// <summary>
    /// v7.9.6.3: measure the link with <c>iperf3</c> against this host instead
    /// of by reading the source. Needs an iperf3 server on the other end.
    /// </summary>
    public string? Iperf3Host { get; init; }

    // ---- v7.9.1: settings-file conversion ----

    /// <summary>
    /// Print the command line equivalent to the settings and exit. With
    /// <see cref="EmitArgsFile"/> the settings come from that file and no
    /// FFmpeg is touched; bare, they are the resolved current run.
    /// </summary>
    public bool EmitArgs { get; init; }

    /// <summary>The <c>--emit-args=FILE</c> form: a settings file to read.</summary>
    public string? EmitArgsFile { get; init; }

    /// <summary>
    /// Write the resolved settings to this file and exit without encoding:
    /// the command-line-to-settings-file direction of the conversion.
    /// </summary>
    public string? SaveConfig { get; init; }
}

/// <summary>Minimal, dependency-free parser with argparse-compatible semantics.</summary>
public static class CliParser
{
    public static (CliOptions? Options, string? Error) Parse(string[] args)
    {
        string? input = null;
        string? output = null;
        double start = 0.0;
        double? end = null;
        string encoder = "rv20";
        bool encoderExplicit = false;
        string? audioEncoder = null;
        double limit = 200.0;
        double? recover = null;
        double window = 2.0;
        double fps = 13.0;
        string fpsLevels = "13,8,5,3";
        bool fpsLevelsExplicit = false;
        bool fpsExplicit = false;
        bool widthExplicit = false;
        bool heightExplicit = false;
        bool limitExplicit = false;
        bool scaleAlgoExplicit = false;
        bool audioKbpsExplicit = false;
        bool audioChannelsExplicit = false;
        int? qv = null;
        int threads = 16;
        int bufferFrames = 6;
        int audioKbps = 32;
        int audioChannels = 1;
        int? audioRate = null;
        int? videoStream = null;
        int? audioStream = null;
        bool listStreams = false;
        var subtitleFiles = new List<string>();
        int? subtitleStream = null;
        string subtitleMode = "burn";
        string? subtitleCodec = null;
        string? subtitleFontDir = null;
        string? subtitleFont = null;
        string? fontImport = null;
        bool listSubtitleFonts = false;
        int width = 256;
        int height = 192;
        string? container = null;
        string fitMode = "crop";
        string cropAnchor = "center";
        string padColor = "black";
        bool noCrop = false;
        string scaleAlgo = "neighbor";
        string? pixFmt = null;
        string? colorRange = null;
        string? colorSpace = null;
        string? colorPrimaries = null;
        string? colorTransfer = null;
        string dither = "auto";
        bool listPixFmts = false;
        string deinterlace = "off";
        string deintMode = "send_frame";
        string deintParity = "auto";
        bool deintMarkedOnly = false;
        string x264Preset = "medium";
        string x264Tune = "";
        string x264Profile = "";
        string x264Level = "";
        bool noCuda = false;
        string hwaccel = "off";
        string? ffmpeg = null;
        string? ffprobe = null;
        bool listFilters = false;
        bool quiet = false;
        bool noCui = false;
        Encoding.AudioMode audioMode = Encoding.AudioMode.Inline;
        bool help = false;
        bool cpuInfo = false;
        int parallel = 1;
        string? language = null;
        string? completion = null;
        string ffmpegSource = FFmpeg.FfmpegCatalog.DefaultId;
        string? ffmpegProxy = null;
        string? ffmpegDownload = null;
        string? ffmpegArchive = null;
        string? ffmpegDelete = null;
        bool ffmpegList = false;
        PlayMode play = PlayMode.Off;
        string? playerPath = null;
        bool recompress = false;
        string? recompressArgs = null;
        string? recompressContainer = null;
        bool noRecompress = false;
        bool recompressOnly = false;
        string? recompressCodec = null;
        int? recompressQuality = null;
        string? recompressAudioCodec = null;
        int? recompressAudioKbps = null;
        bool eraModern = false;
        bool eraNative = false;
        string? era = null;
        string? eraAspect = null;
        string? h264 = null;
        string? netTest = null;
        string? iperf3Host = null;
        bool emitArgs = false;
        string? emitArgsFile = null;
        string? saveConfig = null;

        var positionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith('-'))
            {
                positionals.Add(arg);
                continue;
            }

            string name = arg;
            string? inlineValue = null;
            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                name = arg[..eq];
                inlineValue = arg[(eq + 1)..];
            }

            bool TryValue(out string value)
            {
                if (inlineValue is not null) { value = inlineValue; return true; }
                if (i + 1 < args.Length) { value = args[++i]; return true; }
                value = "";
                return false;
            }

            // --ffmpeg-download is usable bare ("install the default"), so its
            // value is only consumed when it actually names a source. Without this
            // `--ffmpeg-download --quiet` would swallow --quiet.
            bool TrySourceValue(out string value)
            {
                value = "";
                if (inlineValue is not null) { value = inlineValue; return true; }
                if (i + 1 < args.Length && FFmpeg.FfmpegCatalog.Find(args[i + 1]) is not null)
                {
                    value = args[++i];
                    return true;
                }
                return false;
            }

            switch (name)
            {
                case "--start-time" or "--start":
                    if (!TryValue(out string startRaw)) return (null, "--start-time requires a value");
                    try { start = Core.Timecode.Parse(startRaw); }
                    catch (FormatException exc) { return (null, exc.Message); }
                    break;
                case "--end-time" or "--end":
                    if (!TryValue(out string endRaw)) return (null, "--end-time requires a value");
                    try { end = Core.Timecode.Parse(endRaw); }
                    catch (FormatException exc) { return (null, exc.Message); }
                    break;
                case "--encoder":
                    if (!TryValue(out encoder)) return (null, "--encoder requires a value");
                    encoderExplicit = true;
                    break;
                case "--audio-encoder":
                    if (!TryValue(out audioEncoder)) return (null, "--audio-encoder requires a value");
                    break;
                case "--limit":
                    if (!TryValue(out string limitRaw) || !TryDouble(limitRaw, out limit))
                        return (null, "--limit requires a number");
                    limitExplicit = true;
                    break;
                case "--recover":
                    if (!TryValue(out string recoverRaw) || !TryDouble(recoverRaw, out double recoverValue))
                        return (null, "--recover requires a number");
                    recover = recoverValue;
                    break;
                case "--window":
                    if (!TryValue(out string windowRaw) || !TryDouble(windowRaw, out window))
                        return (null, "--window requires a number");
                    break;
                case "--fps":
                    if (!TryValue(out string fpsRaw) || !TryDouble(fpsRaw, out fps))
                        return (null, "--fps requires a number");
                    fpsExplicit = true;
                    break;
                case "--fps-levels":
                    if (!TryValue(out fpsLevels)) return (null, "--fps-levels requires a value");
                    fpsLevelsExplicit = true;
                    break;
                case "--qv":
                    if (!TryValue(out string qvRaw) || !int.TryParse(qvRaw, out int qvValue))
                        return (null, "--qv requires an integer");
                    qv = qvValue;
                    break;
                case "--threads":
                    if (!TryValue(out string threadsRaw) || !int.TryParse(threadsRaw, out threads))
                        return (null, "--threads requires an integer");
                    break;
                case "--buffer-frames":
                    if (!TryValue(out string bufferRaw) || !int.TryParse(bufferRaw, out bufferFrames))
                        return (null, "--buffer-frames requires an integer");
                    break;
                case "--audio-kbps":
                    if (!TryValue(out string kbpsRaw) || !int.TryParse(kbpsRaw, out audioKbps))
                        return (null, "--audio-kbps requires an integer");
                    audioKbpsExplicit = true;
                    break;
                case "--audio-channels":
                    if (!TryValue(out string chRaw) || !int.TryParse(chRaw, out audioChannels))
                        return (null, "--audio-channels requires an integer");
                    audioChannelsExplicit = true;
                    break;
                case "--audio-rate":
                    if (!TryValue(out string rateRaw) || !int.TryParse(rateRaw, out int rateValue))
                        return (null, "--audio-rate requires an integer");
                    audioRate = rateValue;
                    break;
                // ---- v7.9: stream selection ----
                case "--video-stream":
                    if (!TryValue(out string vsRaw) || !int.TryParse(vsRaw, out int vsValue))
                        return (null, "--video-stream requires an integer (see --list-streams)");
                    if (vsValue < 0) return (null, "--video-stream must be 0 or greater");
                    videoStream = vsValue;
                    break;
                case "--audio-stream":
                    if (!TryValue(out string asRaw) || !int.TryParse(asRaw, out int asValue))
                        return (null, "--audio-stream requires an integer (see --list-streams)");
                    if (asValue < 0) return (null, "--audio-stream must be 0 or greater");
                    audioStream = asValue;
                    break;
                case "--list-streams": listStreams = true; break;

                // ---- v7.9 #2: subtitles ----
                case "--subtitle":
                    if (!TryValue(out string subRaw)) return (null, "--subtitle requires a file path");
                    // Repeatable: a release usually ships two language tracks.
                    subtitleFiles.Add(subRaw.Trim().Trim('"'));
                    break;
                case "--subtitle-stream":
                    if (!TryValue(out string ssRaw) || !int.TryParse(ssRaw, out int ssValue))
                        return (null, "--subtitle-stream requires an integer (see --list-streams)");
                    if (ssValue < 0) return (null, "--subtitle-stream must be 0 or greater");
                    subtitleStream = ssValue;
                    break;
                case "--subtitle-mode":
                    if (!TryValue(out subtitleMode)) return (null, "--subtitle-mode requires a value");
                    subtitleMode = subtitleMode.Trim().ToLowerInvariant();
                    if (!Constants.SubtitleModes.Contains(subtitleMode))
                        return (null, $"--subtitle-mode must be one of {string.Join(", ", Constants.SubtitleModes)}");
                    break;
                case "--subtitle-codec":
                    if (!TryValue(out subtitleCodec)) return (null, "--subtitle-codec requires a value");
                    subtitleCodec = subtitleCodec.Trim().ToLowerInvariant();
                    break;
                case "--subtitle-font-dir":
                    if (!TryValue(out string fontDirRaw)) return (null, "--subtitle-font-dir requires a directory");
                    subtitleFontDir = fontDirRaw.Trim().Trim('"');
                    break;
                case "--subtitle-font":
                    if (!TryValue(out string fontRaw)) return (null, "--subtitle-font requires a family name");
                    subtitleFont = fontRaw.Trim().Trim('"');
                    break;
                case "--font-import":
                    if (!TryValue(out string importRaw)) return (null, "--font-import requires a file or directory");
                    fontImport = importRaw.Trim().Trim('"');
                    break;
                case "--list-subtitle-fonts":
                    listSubtitleFonts = true;
                    break;
                case "--width":
                    if (!TryValue(out string wRaw) || !int.TryParse(wRaw, out width))
                        return (null, "--width requires an integer");
                    widthExplicit = true;
                    break;
                case "--height":
                    if (!TryValue(out string hRaw) || !int.TryParse(hRaw, out height))
                        return (null, "--height requires an integer");
                    heightExplicit = true;
                    break;
                case "--container":
                    if (!TryValue(out container)) return (null, "--container requires a value");
                    container = container.TrimStart('.').ToLowerInvariant();
                    break;
                case "--fit":
                    if (!TryValue(out fitMode)) return (null, "--fit requires a value");
                    fitMode = fitMode.ToLowerInvariant();
                    if (!Constants.FitModes.Contains(fitMode))
                        return (null, $"--fit must be one of {string.Join(", ", Constants.FitModes)}");
                    break;
                case "--crop-anchor":
                    if (!TryValue(out cropAnchor)) return (null, "--crop-anchor requires a value");
                    cropAnchor = cropAnchor.ToLowerInvariant();
                    if (!Constants.CropAnchors.Contains(cropAnchor))
                        return (null, $"--crop-anchor must be one of {string.Join(", ", Constants.CropAnchors)}");
                    break;
                case "--pad-color":
                    if (!TryValue(out padColor)) return (null, "--pad-color requires a value");
                    break;
                case "--no-crop": noCrop = true; break;
                case "--scale-algo" or "--scale-flags":
                    if (!TryValue(out scaleAlgo)) return (null, "--scale-algo requires a value");
                    scaleAlgoExplicit = true;
                    break;

                // ---- v7.9.6: picture format and colour ----
                case "--pix-fmt" or "--pixfmt" or "--pixel-format":
                    if (!TryValue(out string pixRaw)) return (null, "--pix-fmt requires a format name");
                    pixFmt = pixRaw.Trim().ToLowerInvariant();
                    // "auto" is the spelling of "no request" in the settings file,
                    // so accept it here instead of reporting an unknown format.
                    if (Core.PixelFormatCatalog.Normalise(pixFmt).Length == 0) pixFmt = null;
                    break;
                case "--color-range" or "--colour-range":
                    if (!TryValue(out colorRange)) return (null, "--color-range requires a value");
                    colorRange = colorRange.Trim().ToLowerInvariant();
                    if (colorRange != "auto" && !Constants.ColorRanges.Contains(colorRange))
                        return (null, $"--color-range must be one of {string.Join(", ", Constants.ColorRanges)}");
                    break;
                case "--colorspace" or "--color-space" or "--colour-space":
                    if (!TryValue(out colorSpace)) return (null, "--colorspace requires a value");
                    colorSpace = colorSpace.Trim().ToLowerInvariant();
                    if (colorSpace != "auto" && !Constants.ColorSpaces.Contains(colorSpace))
                        return (null, $"--colorspace must be one of {string.Join(", ", Constants.ColorSpaces)}");
                    break;
                case "--color-primaries" or "--colour-primaries":
                    if (!TryValue(out colorPrimaries)) return (null, "--color-primaries requires a value");
                    colorPrimaries = colorPrimaries.Trim().ToLowerInvariant();
                    if (colorPrimaries != "auto" && !Constants.ColorPrimaries.Contains(colorPrimaries))
                        return (null, $"--color-primaries must be one of {string.Join(", ", Constants.ColorPrimaries)}");
                    break;
                case "--color-trc" or "--color-transfer" or "--colour-trc":
                    if (!TryValue(out colorTransfer)) return (null, "--color-trc requires a value");
                    colorTransfer = colorTransfer.Trim().ToLowerInvariant();
                    if (colorTransfer != "auto" && !Constants.ColorTransfers.Contains(colorTransfer))
                        return (null, $"--color-trc must be one of {string.Join(", ", Constants.ColorTransfers)}");
                    break;
                case "--dither":
                    if (!TryValue(out dither)) return (null, "--dither requires a value");
                    dither = dither.Trim().ToLowerInvariant();
                    if (!Constants.DitherModes.Contains(dither))
                        return (null, $"--dither must be one of {string.Join(", ", Constants.DitherModes)}");
                    break;
                case "--list-pix-fmts" or "--list-pixel-formats": listPixFmts = true; break;
                case "--deinterlace":
                    if (!TryValue(out deinterlace)) return (null, "--deinterlace requires a value");
                    deinterlace = deinterlace.ToLowerInvariant();
                    break;
                case "--deint-mode":
                    if (!TryValue(out deintMode)) return (null, "--deint-mode requires a value");
                    break;
                case "--deint-parity":
                    if (!TryValue(out deintParity)) return (null, "--deint-parity requires a value");
                    break;
                case "--deint-marked-only": deintMarkedOnly = true; break;
                case "--x264-preset":
                    if (!TryValue(out x264Preset)) return (null, "--x264-preset requires a value");
                    break;
                case "--x264-tune":
                    if (!TryValue(out x264Tune)) return (null, "--x264-tune requires a value");
                    break;
                case "--x264-profile":
                    if (!TryValue(out x264Profile)) return (null, "--x264-profile requires a value");
                    break;
                case "--x264-level":
                    if (!TryValue(out x264Level)) return (null, "--x264-level requires a value");
                    break;
                case "--lang":
                    if (!TryValue(out language)) return (null, "--lang requires a value");
                    break;
                case "--completion":
                    if (!TryValue(out completion)) return (null, "--completion requires a shell name");
                    completion = completion.Trim().ToLowerInvariant();
                    if (completion is not ("bash" or "zsh" or "fish" or "powershell"))
                        return (null, "--completion supports bash, zsh, fish or powershell");
                    break;
                case "--no-cuda": noCuda = true; break;
                case "--hwaccel":
                    if (!TryValue(out hwaccel)) return (null, "--hwaccel requires a value");
                    hwaccel = hwaccel.Trim().ToLowerInvariant();
                    if (hwaccel.Length == 0) return (null, "--hwaccel requires a method name");
                    break;
                case "--parallel":
                    if (!TryValue(out string parRaw) || !int.TryParse(parRaw, out parallel))
                        return (null, "--parallel requires an integer");
                    if (parallel < 1) return (null, "--parallel must be at least 1");
                    if (parallel > Constants.MaxParallelShards)
                        return (null, $"--parallel must be at most {Constants.MaxParallelShards}");
                    break;
                case "--ffmpeg":
                    if (!TryValue(out ffmpeg)) return (null, "--ffmpeg requires a value");
                    break;
                case "--ffprobe":
                    if (!TryValue(out ffprobe)) return (null, "--ffprobe requires a value");
                    break;
                case "--ffmpeg-source":
                    if (!TryValue(out ffmpegSource)) return (null, "--ffmpeg-source requires a value");
                    ffmpegSource = ffmpegSource.Trim().ToLowerInvariant();
                    // "path" is the escape hatch back to the system FFmpeg.
                    if (FFmpeg.FfmpegCatalog.Find(ffmpegSource) is null
                        && !string.Equals(ffmpegSource, "path", StringComparison.OrdinalIgnoreCase))
                        return (null, $"--ffmpeg-source must be one of {FFmpeg.FfmpegCatalog.Ids} or path");
                    break;
                case "--ffmpeg-download":
                    ffmpegDownload = TrySourceValue(out string dl) ? dl.ToLowerInvariant()
                        : FFmpeg.FfmpegCatalog.DefaultId;
                    break;
                case "--ffmpeg-archive":
                    if (!TryValue(out ffmpegArchive)) return (null, "--ffmpeg-archive requires a path");
                    ffmpegArchive = ffmpegArchive.Trim().Trim('"');
                    break;
                case "--ffmpeg-delete":
                    if (!TrySourceValue(out ffmpegDelete))
                        return (null, $"--ffmpeg-delete requires one of {FFmpeg.FfmpegCatalog.Ids}");
                    ffmpegDelete = ffmpegDelete.ToLowerInvariant();
                    break;
                case "--ffmpeg-list": ffmpegList = true; break;
                case "--proxy":
                    if (!TryValue(out ffmpegProxy)) return (null, "--proxy requires a value");
                    break;
                case "--play":
                    if (!TryValue(out string playRaw)) return (null, "--play requires a mode");
                    play = Player.ParseMode(playRaw);
                    if (play == PlayMode.Off && !playRaw.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
                        && playRaw.Trim().Length > 0)
                        return (null, "--play supports off, ffplay or external");
                    break;
                case "--player":
                    if (!TryValue(out playerPath)) return (null, "--player requires a path");
                    break;
                case "--list-filters": listFilters = true; break;
                case "--quiet": quiet = true; break;
                case "--no-cui": noCui = true; break;
                case "--audio-mode":
                    if (!TryValue(out string modeRaw)) return (null, "--audio-mode requires a value");
                    // v7.9.3.1: this used to throw out of Parse(), which
                    // Program.Main does not catch -- a typo in a flag turned
                    // into an unhandled-exception crash instead of the
                    // "ERROR: ..." + exit 2 every other bad value gets.
                    switch (modeRaw.Trim().ToLowerInvariant())
                    {
                        case "auto": audioMode = Encoding.AudioMode.Auto; break;
                        case "inline": audioMode = Encoding.AudioMode.Inline; break;
                        case "separate": audioMode = Encoding.AudioMode.Separate; break;
                        default: return (null, "--audio-mode must be auto, inline or separate");
                    }
                    break;
                case "--cpu-info": cpuInfo = true; break;
                case "--help" or "-h":
                    help = true;
                    break;

                // ---- v7.6: post-processing ----
                case "--recompress": recompress = true; break;
                case "--recompress-args":
                    if (!TryValue(out recompressArgs)) return (null, "--recompress-args requires a value");
                    break;
                case "--recompress-container":
                    if (!TryValue(out recompressContainer)) return (null, "--recompress-container requires a value");
                    recompressContainer = recompressContainer.TrimStart('.').ToLowerInvariant();
                    break;
                case "--no-recompress": noRecompress = true; break;
                case "--recompress-only": recompressOnly = true; break;
                case "--recompress-codec":
                    if (!TryValue(out recompressCodec)) return (null, "--recompress-codec requires a codec name");
                    recompressCodec = recompressCodec.Trim().ToLowerInvariant();
                    break;
                case "--recompress-quality":
                    if (!TryValue(out string rcqRaw) || !int.TryParse(rcqRaw, out int rcqValue))
                        return (null, "--recompress-quality requires an integer");
                    if (rcqValue < 1 || rcqValue > 63)
                        return (null, "--recompress-quality must be between 1 and 63");
                    recompressQuality = rcqValue;
                    break;
                case "--recompress-audio-codec":
                    if (!TryValue(out recompressAudioCodec))
                        return (null, "--recompress-audio-codec requires a codec name");
                    recompressAudioCodec = recompressAudioCodec.Trim().ToLowerInvariant();
                    break;
                case "--recompress-audio-kbps":
                    if (!TryValue(out string rcabRaw) || !int.TryParse(rcabRaw, out int rcabValue)
                        || rcabValue < 0)
                        return (null, "--recompress-audio-kbps requires a non-negative integer");
                    recompressAudioKbps = rcabValue;
                    break;
                case "--era-modern": eraModern = true; break;
                case "--era-native": eraNative = true; break;

                case "--era":
                    if (!TryValue(out era)) return (null, "--era requires a value");
                    era = era.Trim();
                    // v7.9.5.1: the table now includes the user's own presets, and
                    // those live in the settings file -- which Parse() has not read.
                    // The name is checked in Cli.Run, where the custom rows are
                    // already registered.
                    break;

                // v7.9.6.3: the shape a preset is captured and delivered in.
                case "--era-aspect":
                    if (!TryValue(out string aspectRaw)) return (null, "--era-aspect requires a value");
                    if (!PostProcess.EraAspects.TryParse(aspectRaw, out PostProcess.EraAspect parsed))
                        return (null, $"--era-aspect must be one of {string.Join(", ", PostProcess.EraAspects.Labels)}");
                    eraAspect = PostProcess.EraAspects.Label(parsed);
                    break;

                // v7.9.7: which H.264 encoder stands in for the era's own.
                case "--h264":
                case "--h264-impl":
                    if (!TryValue(out string h264Raw)) return (null, "--h264 requires libx264 or libopenh264");
                    h264 = h264Raw.Trim().ToLowerInvariant();
                    if (h264 is not ("libx264" or "libopenh264" or "x264" or "openh264"))
                        return (null, "--h264 must be libx264 or libopenh264");
                    h264 = h264 switch
                    {
                        "x264" => "libx264",
                        "openh264" => "libopenh264",
                        _ => h264,
                    };
                    break;

                // v7.9.6.3: network measurement, which is also what decides
                // whether --parallel may be used over a share.
                case "--net-test":
                    if (!TryValue(out string netRaw)) return (null, "--net-test requires an input path or URL");
                    netTest = netRaw;
                    break;
                case "--iperf3":
                    if (!TryValue(out string iperfHost)) return (null, "--iperf3 requires a host[:port]");
                    iperf3Host = iperfHost.Trim();
                    break;

                // v7.9.1: settings-file conversion. --emit-args deliberately
                // does NOT swallow the next token -- "--emit-args in.rm" must
                // keep in.rm as the input positional -- so the file only ever
                // arrives in the --emit-args=FILE form.
                case "--emit-args":
                    emitArgs = true;
                    if (inlineValue is { Length: > 0 } emitFile) emitArgsFile = emitFile.Trim();
                    break;
                case "--save-config":
                    if (!TryValue(out saveConfig)) return (null, "--save-config requires a path");
                    saveConfig = saveConfig.Trim().Trim('"');
                    if (saveConfig.Length == 0) return (null, "--save-config requires a path");
                    break;

                default:
                    return (null, $"unrecognized argument: {arg}");
            }
        }

        if (positionals.Count > 0) input = positionals[0];
        if (positionals.Count > 1) output = positionals[1];
        if (positionals.Count > 2) return (null, "too many positional arguments");

        return (new CliOptions
        {
            Input = input,
            Output = output,
            StartTime = start,
            EndTime = end,
            Encoder = encoder,
            EncoderExplicit = encoderExplicit,
            AudioEncoder = audioEncoder,
            Limit = limit,
            Recover = recover,
            Window = window,
            Fps = fps,
            FpsLevels = fpsLevels,
            FpsLevelsExplicit = fpsLevelsExplicit,
            FpsExplicit = fpsExplicit,
            WidthExplicit = widthExplicit,
            HeightExplicit = heightExplicit,
            LimitExplicit = limitExplicit,
            ScaleAlgoExplicit = scaleAlgoExplicit,
            AudioKbpsExplicit = audioKbpsExplicit,
            AudioChannelsExplicit = audioChannelsExplicit,
            Qv = qv,
            Threads = threads,
            BufferFrames = bufferFrames,
            AudioKbps = audioKbps,
            AudioChannels = audioChannels,
            AudioRate = audioRate,
            VideoStream = videoStream,
            AudioStream = audioStream,
            ListStreams = listStreams,
            SubtitleFiles = subtitleFiles,
            SubtitleStream = subtitleStream,
            SubtitleMode = subtitleMode,
            SubtitleCodec = subtitleCodec,
            SubtitleFontDir = subtitleFontDir,
            SubtitleFont = subtitleFont,
            FontImport = fontImport,
            ListSubtitleFonts = listSubtitleFonts,
            Width = width,
            Height = height,
            Container = container,
            FitMode = noCrop ? "stretch" : fitMode,
            CropAnchor = cropAnchor,
            PadColor = padColor,
            NoCrop = noCrop,
            ScaleAlgo = scaleAlgo,
            PixFmt = pixFmt,
            ColorRange = colorRange,
            ColorSpace = colorSpace,
            ColorPrimaries = colorPrimaries,
            ColorTransfer = colorTransfer,
            Dither = dither,
            ListPixFmts = listPixFmts,
            Deinterlace = deinterlace,
            DeintMode = deintMode,
            DeintParity = deintParity,
            DeintMarkedOnly = deintMarkedOnly,
            X264Preset = x264Preset,
            X264Tune = x264Tune,
            X264Profile = x264Profile,
            X264Level = x264Level,
            // --no-cuda is the v7.4 spelling of "off" and wins over --hwaccel
            // when both are given, because it is the more explicit request.
            Hwaccel = noCuda ? "off" : hwaccel,
            NoCuda = noCuda,
            FFmpeg = ffmpeg,
            FFprobe = ffprobe,
            ListFilters = listFilters,
            Quiet = quiet,
            NoCui = noCui,
            AudioMode = audioMode,
            Help = help,
            CpuInfo = cpuInfo,
            Parallel = parallel,
            Language = language,
            Completion = completion,
            FfmpegSource = ffmpegSource,
            FfmpegProxy = ffmpegProxy,
            FfmpegDownload = ffmpegDownload,
            FfmpegArchive = ffmpegArchive,
            FfmpegDelete = ffmpegDelete,
            FfmpegList = ffmpegList,
            Play = play,
            PlayerPath = playerPath,
            // --recompress-args arms the pass on its own: nobody writes out a
            // full argument list and then expects it not to run.
            Recompress = recompress || recompressArgs is not null || recompressOnly
                || recompressCodec is not null,
            RecompressArgs = recompressArgs,
            RecompressContainer = recompressContainer,
            NoRecompress = noRecompress,
            RecompressOnly = recompressOnly,
            RecompressCodec = recompressCodec,
            RecompressQuality = recompressQuality,
            RecompressAudioCodec = recompressAudioCodec,
            RecompressAudioKbps = recompressAudioKbps,
            EraModern = eraModern,
            EraNative = eraNative,
            Era = era,
            EraAspect = eraAspect,
            H264Impl = h264,
            H264Explicit = h264 is not null,
            NetTest = netTest,
            Iperf3Host = iperf3Host,
            EmitArgs = emitArgs,
            EmitArgsFile = emitArgsFile,
            SaveConfig = saveConfig,
        }, null);
    }

    /// <summary>argparse-style help text. Printed to stdout, exit code 0.</summary>
    public static string Usage()
    {
        (string flag, string meta, string help)[] rows =
        [
            ("-h, --help", "", "show this help message and exit"),
            ("--start-time, --start", "HH:MM:SS", "segment start time, e.g. 00:12:30 (default: 00:00:00)"),
            ("--end-time, --end", "HH:MM:SS", "exclusive segment end time (default: end of source)"),
            ("--encoder", "NAME", "video encoder (default: rv20)"),
            ("--audio-encoder", "NAME", "audio encoder (default: chosen per container)"),
            ("--limit", "KBPS", "bitrate ceiling that triggers an FPS downgrade (default: 200)"),
            ("--recover", "KBPS", "bitrate floor that allows an upgrade (default: limit * 0.75)"),
            ("--window", "SEC", "rolling window for the bitrate average (default: 2)"),
            ("--fps", "FPS", "target frame rate (default: 13)"),
            ("--fps-levels", "LIST", "adaptive FPS ladder; per entry FPS, FPS:drop-kbps or "
                                      + "FPS:drop-kbps:rise-kbps (default: 13,8,5,3)"),
            ("--qv", "N", "video quality: CRF for libx264, q:v for other codecs"),
            ("--threads", "N", "FFmpeg thread count (default: 16)"),
            ("--buffer-frames", "N", "capture buffer depth (default: 6)"),
            ("--audio-kbps", "N", "audio bitrate (default: 32)"),
            ("--audio-channels", "N", "audio channels (default: 1)"),
            ("--audio-rate", "HZ", "audio sample rate (default: source)"),
            // v7.9: multi-stream sources. The index is the number ffprobe shows.
            ("--video-stream", "N", "video stream index to encode, e.g. 2 (default: the first one)"),
            ("--audio-stream", "N", "audio stream index to keep, e.g. 3 (default: the first one)"),
            ("--list-streams", "", "print the source's streams (and their indexes), then exit"),
            ("--subtitle", "FILE", "external subtitle file (.ass/.ssa/.srt/.vtt); repeatable, "
                                 + "e.g. clip.sc.ass and clip.tc.ass"),
            ("--subtitle-stream", "N", "index of an embedded subtitle track to use"),
            ("--subtitle-mode", "MODE", "burn the text into the picture (default) or copy it as a stream"),
            ("--subtitle-codec", "NAME", "subtitle codec for --subtitle-mode copy (default: per container, "
                                       + "from \"ffmpeg -encoders\")"),
            ("--subtitle-font-dir", "DIR", "extra font directory for burned subtitles (in addition to "
                                         + "the program's own fonts directory)"),
            ("--subtitle-font", "FAMILY", "force this font family over the one the file names "
                                        + "(also gives SRT/VTT a font, which they never specify)"),
            ("--font-import", "PATH", "copy a font file (or every font in a directory) into the "
                                    + "program's fonts directory, then exit"),
            ("--list-subtitle-fonts", "", "print the fonts a subtitle file needs, whether they are "
                                        + "installed, and where, then exit"),
            ("--width", "N", "output width (default: 256)"),
            ("--height", "N", "output height (default: 192)"),
            ("--container", "NAME", "container override where the encoder allows one, e.g. avi for "
                                   + "mpeg1video/mpeg2video/mpeg4/libxvid/libx264, mkv for libx264"),
            ("--fit", "MODE", "crop (fill, cut the edge), fit (keep the whole picture, pad the "
                            + "rest) or stretch (default: crop)"),
            ("--crop-anchor", "POS", "which edge survives a crop: center, top, bottom, left, right"),
            ("--pad-color", "COLOR", "bar colour for --fit, e.g. black or 0x808080 (default: black)"),
            ("--no-crop", "", "shorthand for --fit stretch"),
            ("--scale-algo, --scale-flags", "NAME", "scaler kernel; cubic is an alias for bicubic (default: neighbor)"),
            ("--pix-fmt", "FORMAT", "output pixel format: pal8, gray, rgb24, rgb555le, rgb565le, "
                                  + "rgb48le, yuv420p10le, yuv444p16le, ... (default: automatic, "
                                  + "keep the source's). See --list-pix-fmts"),
            ("--color-range", "RANGE", $"colour range tag: {string.Join(", ", Constants.ColorRanges)} "
                                     + "(default: the source's)"),
            ("--colorspace", "NAME", $"colour space tag: {string.Join(", ", Constants.ColorSpaces)} "
                                   + "(default: the source's)"),
            ("--color-primaries", "NAME", $"colour primaries tag: {string.Join(", ", Constants.ColorPrimaries)}"),
            ("--color-trc", "NAME", $"transfer characteristics tag: {string.Join(", ", Constants.ColorTransfers)}"),
            ("--dither", "MODE", $"swscale dither for low-colour output: {string.Join(", ", Constants.DitherModes)} "
                               + "(default: auto)"),
            ("--list-pix-fmts", "", "print every verified pixel format (and what the selected encoder "
                                  + "accepts), then exit"),
            ("--deinterlace", "NAME", "off, yadif, bwdif, ... (default: off)"),
            ("--deint-mode", "MODE", "send_frame or send_field (default: send_frame)"),
            ("--deint-parity", "PAR", "auto, tff or bff (default: auto)"),
            ("--deint-marked-only", "", "only deinterlace frames flagged interlaced"),
            ("--x264-preset", "NAME", "libx264 preset (default: medium)"),
            ("--x264-tune", "NAME", "libx264 tune"),
            ("--x264-profile", "NAME", "libx264 profile"),
            ("--x264-level", "NAME", "libx264 level"),
            ("--hwaccel", "NAME", "hardware decode: off (default), auto, or a method from "
                                + "\"ffmpeg -hwaccels\" (cuda, vaapi, qsv, ...); verified before use"),
            ("--no-cuda", "", "shorthand for --hwaccel off"),
            ("--ffmpeg", "PATH", "explicit ffmpeg executable"),
            ("--ffprobe", "PATH", "explicit ffprobe executable"),
            ("--list-filters", "", "print the detected filters and scalers, then exit"),
            ("--audio-mode", "MODE", "auto, inline or separate (default: inline)"),
            ("--lang", "CODE", "UI language: en or zh (default: en)"),
            ("--completion", "SHELL", "print a completion script (bash/zsh/fish/powershell) and exit"),
            ("--parallel", "N", "shard the range and encode concurrently (default: 1; "
                + "forced to 1 for a network-drive source)"),
            ("--quiet", "", "suppress the progress banner"),
            ("--no-cui", "", "never enter the interactive UI"),
            ("--ffmpeg-source", "ID", $"FFmpeg distribution: {FFmpeg.FfmpegCatalog.Ids} or path (default: btbn)"),
            ("--ffmpeg-download", "ID", "download and install a distribution, then exit"),
            ("--ffmpeg-archive", "PATH", "install from a local archive instead of downloading"),
            ("--ffmpeg-delete", "ID", "delete an installed distribution, then exit"),
            ("--ffmpeg-list", "", "list the installed distributions, then exit"),
            ("--proxy", "URL", "proxy for FFmpeg downloads, e.g. http://127.0.0.1:10808"),
            ("--play", "MODE", "after encoding: off, ffplay or external (default: off)"),
            ("--player", "PATH", "external player executable (default: system file association)"),
            ("--recompress", "", "re-encode the result the way a video site would (default: off)"),
            ("--recompress-args", "ARGS", "FFmpeg arguments for that pass; -i and the output are supplied"),
            ("--recompress-codec", "NAME", $"second-pass video codec instead of a fragment: {PostProcess.RecompressCodecs.VideoIdList}"),
            ("--recompress-quality", "N", "quality for --recompress-codec (CRF, or q:v for the MPEG family)"),
            ("--recompress-audio-codec", "NAME", $"second-pass audio codec: {PostProcess.RecompressCodecs.AudioIdList}, or copy"),
            ("--recompress-audio-kbps", "N", "second-pass audio bitrate (default: the codec's)"),
            ("--recompress-container", "EXT", "container for the recompressed file (default: implied by the codec)"),
            ("--recompress-only", "", "skip the live encode and apply the recompress pass to an "
                                    + "existing file, then exit"),
            ("--no-recompress", "", "disable the recompress stage"),
            ("--era", "ID", $"apply an era/site preset: {PostProcess.EraPresets.Ids} "
                          + "(custom ids from the settings file also work)"),
            ("--era-modern", "", "render a preset's second pass with libx264 + AAC in MP4 "
                               + "instead of the codec that site used"),
            ("--era-native", "", "render a preset's second pass with the site's own codec (default)"),
            ("--era-aspect", "4:3|16:9|source",
                "capture and deliver a preset in this frame shape; only shapes the "
                + "selected encoder can encode are accepted (default: source)"),
            ("--h264", "libx264|libopenh264",
                "which H.264 encoder to use where the preset says H.264 (default: the "
                + "preset's own -- OpenH264 from 2011 on, libx264 before)"),
            ("--net-test", "PATH|URL", "measure the link to a source and print the verdict "
                                      + "--parallel would use, then exit"),
            ("--iperf3", "HOST[:PORT]", "measure the link with iperf3 against that host "
                                      + "instead of by reading the source"),
            ("--emit-args[=FILE]", "", "print the command line equivalent to the settings, then exit "
                                       + "(FILE = a settings file to read; bare = this run's resolved settings)"),
            ("--save-config", "FILE", "write this run's resolved settings to FILE and exit without encoding"),
        ];

        var sb = new System.Text.StringBuilder();
        sb.Append("usage: LiveArchiveEncoder [--help] [options] [input] [output]\n\n");
        sb.Append("Live-style adaptive archival encoder ");
        sb.Append("(frame/packet impairment is handled by frame_damage.py)\n\n");
        sb.Append("positional arguments:\n");
        sb.Append("  input                   source file; omit to enter the interactive UI\n");
        sb.Append("  output                  destination file\n\n");
        sb.Append("options:\n");
        foreach ((string flag, string meta, string help) in rows)
        {
            string left = meta.Length > 0 ? $"{flag} {meta}" : flag;
            sb.Append("  ").Append(left);
            if (left.Length < 34) sb.Append(' ', 34 - left.Length);
            sb.Append(help).Append('\n');
        }
        return sb.ToString();
    }

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
