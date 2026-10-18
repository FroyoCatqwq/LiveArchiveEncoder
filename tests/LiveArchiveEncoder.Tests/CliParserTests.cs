using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Command-line parsing, with the emphasis on the v7.4 options: source
/// selection, toolchain management and playback.
/// </summary>
[TestFixture]
public sealed class CliParserTests
{
    private static CliOptions Parse(params string[] args)
    {
        (CliOptions? options, string? error) = CliParser.Parse(args);
        Assert.That(error, Is.Null, $"unexpected parse error: {error}");
        Assert.That(options, Is.Not.Null);
        return options!;
    }

    private static string? Error(params string[] args) => CliParser.Parse(args).Error;

    /// <summary>
    /// Every id the parser must accept here. gyan.dev publishes no Linux build,
    /// so it is only part of the set on Windows; avbuild exists on both and is
    /// the non-default id these tests use instead.
    /// </summary>
    private static IEnumerable<string> KnownIds()
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
    public void Hwaccel_DefaultsToOff() =>
        Assert.That(Parse().Hwaccel, Is.EqualTo("off"));

    [TestCase("off")]
    [TestCase("auto")]
    [TestCase("cuda")]
    [TestCase("vaapi")]
    [TestCase("qsv")]
    public void Hwaccel_AcceptsAMethodName(string method) =>
        Assert.That(Parse("--hwaccel", method).Hwaccel, Is.EqualTo(method));

    [Test]
    public void Hwaccel_NeedsAValue() =>
        Assert.That(Error("--hwaccel"), Does.Contain("--hwaccel requires a value"));

    /// <summary>v7.4 scripts pass --no-cuda; it is "off", not a removed flag.</summary>
    [Test]
    public void NoCudaIsShorthandForOff()
    {
        Assert.That(Parse("--no-cuda").Hwaccel, Is.EqualTo("off"));
        Assert.That(Parse("--hwaccel", "cuda", "--no-cuda").Hwaccel, Is.EqualTo("off"));
    }

    // ------------------------------------------------------------------ source

    [TestCaseSource(nameof(KnownIds))]
    public void FfmpegSource_AcceptsEveryKnownIdAndPath(string id) =>
        Assert.That(Parse("--ffmpeg-source", id).FfmpegSource, Is.EqualTo(id));

    [Test]
    public void FfmpegSource_DefaultsToTheDailyBtbnBuild() =>
        Assert.That(Parse().FfmpegSource, Is.EqualTo("btbn"));

    [Test]
    public void FfmpegSource_RejectsUnknownIds()
    {
        string? error = Error("--ffmpeg-source", "bogus");

        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("must be one of"));
        Assert.That(error, Does.Contain("path"));
    }

    // ---------------------------------------------------------------- download

    [Test]
    public void FfmpegDownload_BareDefaultsToBtbn() =>
        Assert.That(Parse("--ffmpeg-download").FfmpegDownload, Is.EqualTo("btbn"));

    [TestCase("avbuild")]
    [TestCase("nonfree")]
    public void FfmpegDownload_WithAnIdUsesIt(string id) =>
        Assert.That(Parse("--ffmpeg-download", id).FfmpegDownload, Is.EqualTo(id));

    /// <summary>
    /// <c>--ffmpeg-download</c> is usable bare, so its value must only be
    /// consumed when it really names a source. Without this the following
    /// <c>--quiet</c> would be swallowed as the source id.
    /// </summary>
    [Test]
    public void FfmpegDownload_BareDoesNotSwallowTheNextFlag()
    {
        CliOptions options = Parse("--ffmpeg-download", "--quiet");

        Assert.That(options.FfmpegDownload, Is.EqualTo("btbn"));
        Assert.That(options.Quiet, Is.True);
    }

    [Test]
    public void FfmpegDownload_EqualsSyntaxWorks() =>
        Assert.That(Parse("--ffmpeg-download=" + OtherId).FfmpegDownload, Is.EqualTo(OtherId));

    // ----------------------------------------------------------------- delete

    [Test]
    public void FfmpegDelete_RequiresAKnownSource()
    {
        Assert.That(Parse("--ffmpeg-delete", OtherId).FfmpegDelete, Is.EqualTo(OtherId));
        Assert.That(Error("--ffmpeg-delete", "bogus"), Does.Contain("--ffmpeg-delete requires"));
    }

    // ---------------------------------------------------------------- archive

    [Test]
    public void FfmpegArchive_KeepsThePath()
    {
        CliOptions options = Parse("--ffmpeg-download", "avbuild",
            "--ffmpeg-archive", @"C:\dl\master.7z");

        Assert.That(options.FfmpegDownload, Is.EqualTo("avbuild"));
        Assert.That(options.FfmpegArchive, Is.EqualTo(@"C:\dl\master.7z"));
    }

    // ------------------------------------------------------------------- play

    [TestCase("off", PlayMode.Off)]
    [TestCase("ffplay", PlayMode.Ffplay)]
    [TestCase("external", PlayMode.External)]
    public void Play_AcceptsTheThreeModes(string raw, PlayMode expected) =>
        Assert.That(Parse("--play", raw).Play, Is.EqualTo(expected));

    [Test]
    public void Play_DefaultsToOffSoScriptsNeverPopAWindow() =>
        Assert.That(Parse().Play, Is.EqualTo(PlayMode.Off));

    [Test]
    public void Play_RejectsUnknownModes() =>
        Assert.That(Error("--play", "vlc"), Does.Contain("--play supports"));

    [Test]
    public void Player_KeepsThePath() =>
        Assert.That(Parse("--player", @"C:\mpc\mpc-hc64.exe").PlayerPath,
            Is.EqualTo(@"C:\mpc\mpc-hc64.exe"));

    // ------------------------------------------------------------------ proxy

    [Test]
    public void Proxy_IsRememberedForDownloads() =>
        Assert.That(Parse("--proxy", "http://127.0.0.1:10808").FfmpegProxy,
            Is.EqualTo("http://127.0.0.1:10808"));

    [Test]
    public void Proxy_IsUnsetByDefault() => Assert.That(Parse().FfmpegProxy, Is.Null);

    // ------------------------------------------------------- existing contract

    [Test]
    public void LongstandingOptionsStillParse()
    {
        CliOptions options = Parse("in.mp4", "out.rm", "--encoder", "rv20",
            "--fps", "13", "--fps-levels", "13,8,5,3", "--turbo", "--parallel", "4");

        Assert.That(options.Input, Is.EqualTo("in.mp4"));
        Assert.That(options.Output, Is.EqualTo("out.rm"));
        Assert.That(options.Encoder, Is.EqualTo("rv20"));
        Assert.That(options.Turbo, Is.True);
        Assert.That(options.Parallel, Is.EqualTo(4));
    }

    [Test]
    public void HelpAndCpuInfoStillWork()
    {
        Assert.That(Parse("--help").Help, Is.True);
        Assert.That(Parse("-h").Help, Is.True);
        Assert.That(Parse("--cpu-info").CpuInfo, Is.True);
    }

    [Test]
    public void UnknownArgumentsAreStillRejected() =>
        Assert.That(Error("--bogus-flag"), Does.Contain("unrecognized argument"));

    [Test]
    public void Usage_ListsTheNewOptions()
    {
        string usage = CliParser.Usage();

        Assert.That(usage, Does.Contain("--ffmpeg-source"));
        Assert.That(usage, Does.Contain("--ffmpeg-download"));
        Assert.That(usage, Does.Contain("--ffmpeg-archive"));
        Assert.That(usage, Does.Contain("--ffmpeg-delete"));
        Assert.That(usage, Does.Contain("--ffmpeg-list"));
        Assert.That(usage, Does.Contain("--proxy"));
        Assert.That(usage, Does.Contain("--play"));
        Assert.That(usage, Does.Contain("--player"));
    }
}
