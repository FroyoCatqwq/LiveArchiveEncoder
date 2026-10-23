using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Command-line parsing, with the emphasis on the v7.4 options: source
/// selection, toolchain management and playback.
/// </summary>
public sealed class CliParserTests
{
    private static async Task<CliOptions> Parse(params string[] args)
    {
        (CliOptions? options, string? error) = CliParser.Parse(args);
        await Assert.That(error).IsNull().Because($"unexpected parse error: {error}");
        await Assert.That(options).IsNotNull();
        return options!;
    }

    private static string? Error(params string[] args) => CliParser.Parse(args).Error;

    /// <summary>
    /// Every id the parser must accept here. gyan.dev publishes no Linux build,
    /// so it is only part of the set on Windows; avbuild exists on both and is
    /// the non-default id these tests use instead.
    /// </summary>
    // Public because TUnit's source generator reaches into the class to build
    // the data-driven cases; NUnit reflected over it instead.
    public static IEnumerable<string> KnownIds()
    {
        yield return "btbn";
        if (FfmpegCatalog.Find("gyan") is not null) yield return "gyan";
        yield return "avbuild";
        yield return "nonfree";
        yield return "path";
    }

    /// <summary>A non-default source id that exists on every platform.</summary>
    private static string OtherId => "avbuild";

    // -------------------------------------------------------------- hwaccel

    /// <summary>
    /// Off by default: a hardware method needs a driver and a device, and the
    /// headless boxes this runs on (all Linux CI, any Windows box without an
    /// NVIDIA stack) used to die on the first frame with "Cannot load libcuda.so.1".
    /// </summary>
    [Test]
    public async Task Hwaccel_DefaultsToOff() =>
        await Assert.That((await Parse()).Hwaccel).IsEqualTo("off");

    [Test]
    [Arguments("off")]
    [Arguments("auto")]
    [Arguments("cuda")]
    [Arguments("vaapi")]
    [Arguments("qsv")]
    public async Task Hwaccel_AcceptsAMethodName(string method) =>
        await Assert.That((await Parse("--hwaccel", method)).Hwaccel).IsEqualTo(method);

    [Test]
    public async Task Hwaccel_NeedsAValue() =>
        await Assert.That(Error("--hwaccel")).Contains("--hwaccel requires a value");

    /// <summary>v7.4 scripts pass --no-cuda; it is "off", not a removed flag.</summary>
    [Test]
    public async Task NoCudaIsShorthandForOff()
    {
        await Assert.That((await Parse("--no-cuda")).Hwaccel).IsEqualTo("off");
        await Assert.That((await Parse("--hwaccel", "cuda", "--no-cuda")).Hwaccel).IsEqualTo("off");
    }

    // ------------------------------------------------------------------ source

    [Test]
    [MethodDataSource(nameof(KnownIds))]
    public async Task FfmpegSource_AcceptsEveryKnownIdAndPath(string id) =>
        await Assert.That((await Parse("--ffmpeg-source", id)).FfmpegSource).IsEqualTo(id);

    [Test]
    public async Task FfmpegSource_DefaultsToTheDailyBtbnBuild() =>
        await Assert.That((await Parse()).FfmpegSource).IsEqualTo("btbn");

    [Test]
    public async Task FfmpegSource_RejectsUnknownIds()
    {
        string? error = Error("--ffmpeg-source", "bogus");

        await Assert.That(error).IsNotNull();
        await Assert.That(error).Contains("must be one of");
        await Assert.That(error).Contains("path");
    }

    // ---------------------------------------------------------------- download

    [Test]
    public async Task FfmpegDownload_BareDefaultsToBtbn() =>
        await Assert.That((await Parse("--ffmpeg-download")).FfmpegDownload).IsEqualTo("btbn");

    [Test]
    [Arguments("avbuild")]
    [Arguments("nonfree")]
    public async Task FfmpegDownload_WithAnIdUsesIt(string id) =>
        await Assert.That((await Parse("--ffmpeg-download", id)).FfmpegDownload).IsEqualTo(id);

    /// <summary>
    /// <c>--ffmpeg-download</c> is usable bare, so its value must only be
    /// consumed when it really names a source. Without this the following
    /// <c>--quiet</c> would be swallowed as the source id.
    /// </summary>
    [Test]
    public async Task FfmpegDownload_BareDoesNotSwallowTheNextFlag()
    {
        CliOptions options = (await Parse("--ffmpeg-download", "--quiet"));

        await Assert.That(options.FfmpegDownload).IsEqualTo("btbn");
        await Assert.That(options.Quiet).IsTrue();
    }

    [Test]
    public async Task FfmpegDownload_EqualsSyntaxWorks() =>
        await Assert.That((await Parse("--ffmpeg-download=" + OtherId)).FfmpegDownload).IsEqualTo(OtherId);

    // ----------------------------------------------------------------- delete

    [Test]
    public async Task FfmpegDelete_RequiresAKnownSource()
    {
        await Assert.That((await Parse("--ffmpeg-delete", OtherId)).FfmpegDelete).IsEqualTo(OtherId);
        await Assert.That(Error("--ffmpeg-delete", "bogus")).Contains("--ffmpeg-delete requires");
    }

    // ---------------------------------------------------------------- archive

    [Test]
    public async Task FfmpegArchive_KeepsThePath()
    {
        CliOptions options = (await Parse("--ffmpeg-download", "avbuild",
            "--ffmpeg-archive", @"C:\dl\master.7z"));

        await Assert.That(options.FfmpegDownload).IsEqualTo("avbuild");
        await Assert.That(options.FfmpegArchive).IsEqualTo(@"C:\dl\master.7z");
    }

    // ------------------------------------------------------------------- play

    [Test]
    [Arguments("off", PlayMode.Off)]
    [Arguments("ffplay", PlayMode.Ffplay)]
    [Arguments("external", PlayMode.External)]
    public async Task Play_AcceptsTheThreeModes(string raw, PlayMode expected) =>
        await Assert.That((await Parse("--play", raw)).Play).IsEqualTo(expected);

    [Test]
    public async Task Play_DefaultsToOffSoScriptsNeverPopAWindow() =>
        await Assert.That((await Parse()).Play).IsEqualTo(PlayMode.Off);

    [Test]
    public async Task Play_RejectsUnknownModes() =>
        await Assert.That(Error("--play", "vlc")).Contains("--play supports");

    [Test]
    public async Task Player_KeepsThePath() =>
        await Assert.That((await Parse("--player", @"C:\mpc\mpc-hc64.exe")).PlayerPath).IsEqualTo(@"C:\mpc\mpc-hc64.exe");

    // ------------------------------------------------------------------ proxy

    [Test]
    public async Task Proxy_IsRememberedForDownloads() =>
        await Assert.That((await Parse("--proxy", "http://127.0.0.1:10808")).FfmpegProxy).IsEqualTo("http://127.0.0.1:10808");

    [Test]
    public async Task Proxy_IsUnsetByDefault() => await Assert.That((await Parse()).FfmpegProxy).IsNull();

    // ------------------------------------------------------- existing contract

    [Test]
    public async Task LongstandingOptionsStillParse()
    {
        CliOptions options = (await Parse("in.mp4", "out.rm", "--encoder", "rv20",
            "--fps", "13", "--fps-levels", "13,8,5,3", "--parallel", "4"));

        await Assert.That(options.Input).IsEqualTo("in.mp4");
        await Assert.That(options.Output).IsEqualTo("out.rm");
        await Assert.That(options.Encoder).IsEqualTo("rv20");
        await Assert.That(options.Parallel).IsEqualTo(4);
    }

    /// <summary>
    /// v7.9 removed Turbo outright. The flag is gone, not hidden: an old script
    /// that still passes it must fail loudly instead of silently losing the
    /// pacing change it asked for.
    /// </summary>
    [Test]
    public async Task TurboIsGone() =>
        await Assert.That(Error("--turbo")).Contains("unrecognized argument");

    [Test]
    public async Task HelpAndCpuInfoStillWork()
    {
        await Assert.That((await Parse("--help")).Help).IsTrue();
        await Assert.That((await Parse("-h")).Help).IsTrue();
        await Assert.That((await Parse("--cpu-info")).CpuInfo).IsTrue();
    }

    [Test]
    public async Task UnknownArgumentsAreStillRejected() =>
        await Assert.That(Error("--bogus-flag")).Contains("unrecognized argument");

    // ------------------------------------------------- v7.9: stream selection

    [Test]
    public async Task StreamPicksDefaultToNull()
    {
        CliOptions options = await Parse();
        await Assert.That(options.VideoStream).IsNull();
        await Assert.That(options.AudioStream).IsNull();
        await Assert.That(options.ListStreams).IsFalse();
    }

    [Test]
    public async Task StreamPicksParseAsIntegers()
    {
        CliOptions options = await Parse("--video-stream", "2", "--audio-stream", "3");
        await Assert.That(options.VideoStream).IsEqualTo(2);
        await Assert.That(options.AudioStream).IsEqualTo(3);
    }

    /// <summary>
    /// Stream 0 is a real pick (the first track) and must not read as "unset" --
    /// that is why these are <c>int?</c> rather than 0-based ints.
    /// </summary>
    [Test]
    public async Task StreamPickZeroIsKept() =>
        await Assert.That((await Parse("--video-stream", "0")).VideoStream).IsEqualTo(0);

    [Test]
    public async Task ListStreamsIsAFlag() =>
        await Assert.That((await Parse("--list-streams")).ListStreams).IsTrue();

    [Test]
    public async Task StreamPicksNeedAnInteger()
    {
        await Assert.That(Error("--video-stream", "first")).Contains("--video-stream requires an integer");
        await Assert.That(Error("--audio-stream", "two")).Contains("--audio-stream requires an integer");
    }

    [Test]
    public async Task StreamPicksRejectNegative()
    {
        await Assert.That(Error("--video-stream", "-1")).Contains("0 or greater");
        await Assert.That(Error("--audio-stream", "-1")).Contains("0 or greater");
    }

    [Test]
    public async Task Usage_ListsTheNewOptions()
    {
        string usage = CliParser.Usage();

        await Assert.That(usage).Contains("--ffmpeg-source");
        await Assert.That(usage).Contains("--ffmpeg-download");
        await Assert.That(usage).Contains("--ffmpeg-archive");
        await Assert.That(usage).Contains("--ffmpeg-delete");
        await Assert.That(usage).Contains("--ffmpeg-list");
        await Assert.That(usage).Contains("--proxy");
        await Assert.That(usage).Contains("--play");
        await Assert.That(usage).Contains("--player");
        // v7.9: --list-streams has to be discoverable, since the index it
        // prints is the only way to know what to pass to the two picks.
        await Assert.That(usage).Contains("--list-streams");
        await Assert.That(usage).Contains("--video-stream");
        await Assert.That(usage).Contains("--audio-stream");
    }

    // ------------------------------------------------ v7.9.3.1: hardening

    /// <summary>
    /// An unknown mode is a parse error, not an exception.
    ///
    /// It used to be thrown out of <see cref="CliParser.Parse"/> as a
    /// FormatException, and Program.Main only inspects the returned error --
    /// so `--audio-mode bogus` crashed with a stack trace instead of printing
    /// "ERROR: ..." and exiting 2 like every other bad value.
    /// </summary>
    [Test]
    public async Task AudioMode_RejectsAnUnknownValue()
    {
        await Assert.That(Error("--audio-mode", "bogus")).Contains("auto, inline or separate");
        await Assert.That(Error("--audio-mode")).Contains("--audio-mode requires a value");
    }

    [Test]
    [Arguments("inline", LiveArchiveEncoder.Encoding.AudioMode.Inline)]
    [Arguments("auto", LiveArchiveEncoder.Encoding.AudioMode.Auto)]
    [Arguments("separate", LiveArchiveEncoder.Encoding.AudioMode.Separate)]
    public async Task AudioMode_AcceptsTheThreeNames(
        string text, LiveArchiveEncoder.Encoding.AudioMode expected) =>
        await Assert.That((await Parse("--audio-mode", text)).AudioMode).IsEqualTo(expected);

    /// <summary>
    /// The era-preset values have non-nullable defaults, so "was this given?"
    /// cannot be read off the value. Without these flags an explicit
    /// <c>--width 320</c> lost to the preset's 352, which is the opposite of
    /// what the manual promises ("preset is a fallback, not an override").
    /// </summary>
    [Test]
    public async Task ExplicitFlags_AreRecordedSoAPresetCannotWin()
    {
        CliOptions bare = await Parse("--era", "flv07");
        await Assert.That(bare.WidthExplicit).IsFalse();
        await Assert.That(bare.HeightExplicit).IsFalse();
        await Assert.That(bare.FpsExplicit).IsFalse();
        await Assert.That(bare.LimitExplicit).IsFalse();
        await Assert.That(bare.ScaleAlgoExplicit).IsFalse();
        await Assert.That(bare.AudioKbpsExplicit).IsFalse();
        await Assert.That(bare.AudioChannelsExplicit).IsFalse();

        CliOptions typed = await Parse("--era", "flv07", "--width", "320", "--height", "180",
            "--fps", "12", "--limit", "250", "--scale-algo", "lanczos",
            "--audio-kbps", "64", "--audio-channels", "2");
        await Assert.That(typed.WidthExplicit).IsTrue();
        await Assert.That(typed.HeightExplicit).IsTrue();
        await Assert.That(typed.FpsExplicit).IsTrue();
        await Assert.That(typed.LimitExplicit).IsTrue();
        await Assert.That(typed.ScaleAlgoExplicit).IsTrue();
        await Assert.That(typed.AudioKbpsExplicit).IsTrue();
        await Assert.That(typed.AudioChannelsExplicit).IsTrue();
        // The values themselves still carry what was typed.
        await Assert.That(typed.Width).IsEqualTo(320);
        await Assert.That(typed.Limit).IsEqualTo(250.0);
    }

    /// <summary>--scale-flags is the v7.4 spelling of --scale-algo; both set the flag.</summary>
    [Test]
    public async Task ScaleFlags_CountsAsAnExplicitScaleChoice() =>
        await Assert.That((await Parse("--scale-flags", "bicubic")).ScaleAlgoExplicit).IsTrue();

    // ---- v7.9.6: pixel format, colour tags and dither ----

    [Test]
    public async Task PictureFlagsAreParsedAndNormalised()
    {
        CliOptions opts = await Parse("--pix-fmt", " PAL8 ", "--color-range", "PC",
            "--colorspace", "BT709", "--color-primaries", "bt709",
            "--color-trc", "smpte2084", "--dither", "BAYER");

        await Assert.That(opts.PixFmt).IsEqualTo("pal8");
        await Assert.That(opts.ColorRange).IsEqualTo("pc");
        await Assert.That(opts.ColorSpace).IsEqualTo("bt709");
        await Assert.That(opts.ColorPrimaries).IsEqualTo("bt709");
        await Assert.That(opts.ColorTransfer).IsEqualTo("smpte2084");
        await Assert.That(opts.Dither).IsEqualTo("bayer");
    }

    /// <summary>"auto" is how a settings file spells "no request", so the parser
    /// must not treat it as an unknown format.</summary>
    [Test]
    public async Task AutoPixelFormatMeansNoRequest()
    {
        await Assert.That((await Parse("--pix-fmt", "auto")).PixFmt).IsNull();
        await Assert.That((await Parse()).Dither).IsEqualTo("auto");
    }

    [Test]
    public async Task UnsupportedPictureValuesAreRefusedAtParseTime()
    {
        await Assert.That(Error("--color-range", "studio")).Contains("--color-range");
        await Assert.That(Error("--colorspace", "bt601")).Contains("--colorspace");
        await Assert.That(Error("--color-primaries", "ntsc")).Contains("--color-primaries");
        await Assert.That(Error("--color-trc", "pq")).Contains("--color-trc");
        await Assert.That(Error("--dither", "floyd")).Contains("--dither");
        // The format itself is checked against the encoder, not the parser, so
        // the name is only lower-cased here -- an unknown one is reported with
        // the encoder's own list when the run is resolved.
        await Assert.That((await Parse("--pix-fmt", "yuv420p10be")).PixFmt).IsEqualTo("yuv420p10be");
    }

    [Test]
    public async Task ListPixelFormatsIsABareSwitch()
    {
        await Assert.That((await Parse("--list-pix-fmts")).ListPixFmts).IsTrue();
        // ... and must not swallow the next argument as its value.
        CliOptions opts = await Parse("--list-pix-fmts", "clip.mkv");
        await Assert.That(opts.ListPixFmts).IsTrue();
        await Assert.That(opts.Input).IsEqualTo("clip.mkv");
    }
}
