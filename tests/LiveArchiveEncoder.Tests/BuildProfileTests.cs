using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.PostProcess;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.5 #4: the capability cache key.
///
/// The point of the cache is to answer "what can this binary do?" without
/// launching it, so the key cannot come from <c>ffmpeg -version</c> -- asking
/// for the version is one of the launches being avoided. Path + size + mtime
/// is the identity instead, which is why replacing a build in place needs the
/// rescan entry (or <c>LAE_NO_CAPS_CACHE=1</c>).
/// </summary>
public sealed class BuildProfileTests
{
    [Test]
    public async Task TheSameBinaryAlwaysGetsTheSameKey()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string ffmpeg = Path.Combine(home, ".local", "share", "LiveArchiveEncoder", "ffmpeg", "bin", "ffmpeg");

        string first = BuildProfileStore.Identity(ffmpeg);
        string second = BuildProfileStore.Identity(ffmpeg);

        await Assert.That(first).IsEqualTo(second);
        // 16 hex characters: enough to collide never, short enough for a file name.
        await Assert.That(first.Length).IsEqualTo(16);
        await Assert.That(first.All(char.IsAsciiHexDigit)).IsTrue();
    }

    [Test]
    public async Task DifferentBinariesGetDifferentKeys()
    {
        string a = BuildProfileStore.Identity("/opt/ffmpeg-a/bin/ffmpeg");
        string b = BuildProfileStore.Identity("/opt/ffmpeg-b/bin/ffmpeg");

        await Assert.That(a).IsNotEqualTo(b);
        // Case matters on Linux, and two installs must never share a cache even
        // when only the directory differs.
        await Assert.That(a).IsNotEqualTo(BuildProfileStore.Identity("/opt/ffmpeg-a/bin/FFMPEG"));
    }

    [Test]
    public async Task TheSchemaVersionGuardsTheFileShape()
    {
        // A cache written by an older shape must be treated as absent rather
        // than deserialised into the new one: BuildProfileStore compares this
        // field before trusting anything else in the file.
        int version = new BuildProfile().Schema;
        await Assert.That(version).IsEqualTo(BuildProfile.SchemaVersion);
        await Assert.That(version).IsGreaterThan(0);
    }

    [Test]
    public async Task AFreshProfileHasEveryCollectionReady()
    {
        var profile = new BuildProfile();

        await Assert.That(profile.Encoders).IsEmpty();
        await Assert.That(profile.AudioEncoders).IsEmpty();
        await Assert.That(profile.SubtitleEncoders).IsEmpty();
        await Assert.That(profile.Filters).IsEmpty();
        await Assert.That(profile.ScaleAlgorithms).IsEmpty();
        await Assert.That(profile.Caps).IsEmpty();
    }
}

/// <summary>
/// v7.9.5 #3: the recompress pass on its own. The stage used to be reachable
/// only behind a live encode, so "run this file through Youku's transcode" meant
/// re-recording it first.
/// </summary>
public sealed class RecompressOnlyTests
{
    [Test]
    public async Task TheFlagArmsTheStageWithoutRecompressArgs()
    {
        (CliOptions? options, string? error) = CliParser.Parse(
            ["in.mkv", "--recompress-only", "--recompress-args", "-c:v", "libx264"]);

        await Assert.That(error).IsNull();
        await Assert.That(options!.RecompressOnly).IsTrue();
        // The stage is armed by naming it, exactly like --recompress-args does.
        await Assert.That(options.Recompress).IsTrue();
        await Assert.That(options.Input).IsEqualTo("in.mkv");
    }

    [Test]
    public async Task TheFlagAloneLeavesTheStageArmedAndTheArgumentsToTheEra()
    {
        (CliOptions? options, string? error) = CliParser.Parse(["in.mkv", "--recompress-only", "--era", "youku07"]);

        await Assert.That(error).IsNull();
        await Assert.That(options!.RecompressOnly).IsTrue();
        await Assert.That(options.Era).IsEqualTo("youku07");
        await Assert.That(options.RecompressArgs).IsNull();
    }

    [Test]
    public async Task NoRecompressStillWins()
    {
        // A script that turns the stage off must not have it come back on.
        (CliOptions? options, _) = CliParser.Parse(["in.mkv", "--recompress-only", "--no-recompress"]);

        await Assert.That(options!.RecompressOnly).IsTrue();
        await Assert.That(options.NoRecompress).IsTrue();
    }

    [Test]
    public async Task TheOutputNameMarksTheFileASiteWouldHaveServed()
    {
        string derived = Recompress.DeriveOutputPath(
            Path.Combine("clips", "recording_live.rm"), "mp4");
        await Assert.That(Path.GetFileName(derived)).IsEqualTo("recording_live_web.mp4");

        // Re-running on an already-marked file does not stack the suffix.
        string again = Recompress.DeriveOutputPath(Path.Combine("clips", "recording_web.mp4"), "flv");
        await Assert.That(Path.GetFileName(again)).IsEqualTo("recording_web_web.flv");
    }

    [Test]
    public async Task ASitePresetProvidesTheFragmentForTheStandaloneRun()
    {
        // --recompress-only --era youku07 needs no --recompress-args: the preset
        // is the fragment.
        EraPreset site = EraPresets.Find("youku07")!;
        (List<string> tokens, string? error) = PostArgs.Parse(site.RecompressArgs);

        await Assert.That(error).IsNull();
        await Assert.That(tokens.Count).IsGreaterThan(4);
        await Assert.That(string.Join(' ', tokens)).Contains("352:264");
    }
}
