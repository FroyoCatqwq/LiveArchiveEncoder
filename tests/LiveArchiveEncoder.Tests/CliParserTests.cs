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
            "--fps", "13", "--fps-levels", "13,8,5,3", "--turbo", "--parallel", "4"));

        await Assert.That(options.Input).IsEqualTo("in.mp4");
        await Assert.That(options.Output).IsEqualTo("out.rm");
        await Assert.That(options.Encoder).IsEqualTo("rv20");
        await Assert.That(options.Turbo).IsTrue();
        await Assert.That(options.Parallel).IsEqualTo(4);
    }

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
    }
}
