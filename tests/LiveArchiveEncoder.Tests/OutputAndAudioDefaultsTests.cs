using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.9: three defaults that used to be decided in the wrong place.
///
/// <para>An automatic output stayed beside the source even when the source was
/// on a network share; the extension the user typed was obeyed until the muxer
/// refused it; and the audio codec remembered from an earlier container was
/// never replaced, because MP3 muxes into mp4 perfectly well. Each rule now
/// lives in one place and is pinned here.</para>
/// </summary>
public sealed class OutputAndAudioDefaultsTests
{
    private static EncoderInfo Encoder(string name, string ext, string format) =>
        new() { Name = name, Extension = ext, FormatName = format };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lae-869-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task AnAutomaticOutputStaysBesideALocalSource()
    {
        string dir = TempDir();
        try
        {
            (FileInfo path, bool moved) = OutputNaming.ForSource(
                new FileInfo(Path.Combine(dir, "clip.mkv")), "mp4");

            await Assert.That(moved).IsFalse();
            await Assert.That(path.FullName).IsEqualTo(Path.Combine(dir, "clip_live.mp4"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task ANetworkSourceDoesNotWriteItsCaptureBackOverTheShare()
    {
        // A UNC path is only a network path to Windows: on Unix the same string
        // is a relative name with no separator at all, and the rule there reads
        // the mount table (/proc/self/mountinfo), which a test cannot fake.
        // The Windows job in CI runs this; the Linux job skips it.
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("UNC semantics are Windows-only; a Linux share needs a real mount");
            return;
        }

        // A UNC path needs no server to be recognised: the shape of the string
        // is the whole test, which is also what the rule is based on.
        (FileInfo path, bool moved) = OutputNaming.ForSource(
            new FileInfo(@"\\server\share\clips\clip.mkv"), "mp4");

        await Assert.That(moved).IsTrue();
        await Assert.That(path.Name).IsEqualTo("clip_live.mp4");
        await Assert.That(path.DirectoryName!.StartsWith(@"\\server", StringComparison.Ordinal)).IsFalse();
        // Its own folder under the program's directory, not the settings root:
        // wiping the settings must not wipe the captures.
        await Assert.That(Path.GetFileName(path.DirectoryName!)).IsEqualTo("output");
    }

    [Test]
    public async Task AnExtensionTheEncoderCannotWriteIsCorrected()
    {
        EncoderInfo x264 = Encoder("libx264", "mp4", "mp4");
        string dir = TempDir();
        try
        {
            // The rm container cannot carry H.264; the file becomes .mp4.
            (FileInfo path, string? from, string? to) = OutputNaming.CorrectExtension(
                new FileInfo(Path.Combine(dir, "out.rm")), x264);
            await Assert.That(from).IsEqualTo("rm");
            await Assert.That(to).IsEqualTo("mp4");
            await Assert.That(path.Name).IsEqualTo("out.mp4");

            // A container this encoder does write is left alone, including the
            // short spellings that do not match the muxer's own name.
            foreach (string writable in new[] { "out.mp4", "out.mkv", "out.avi", "out.mov", "out.flv" })
            {
                (_, _, string? corrected) = OutputNaming.CorrectExtension(
                    new FileInfo(Path.Combine(dir, writable)), x264);
                await Assert.That(corrected).IsNull().Because(writable);
            }

            // No extension at all is the same question with the same answer.
            (_, _, string? none) = OutputNaming.CorrectExtension(
                new FileInfo(Path.Combine(dir, "out")), x264);
            await Assert.That(none).IsEqualTo("mp4");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task ACorrectedNameNeverOverwritesWhatNobodyNamed()
    {
        EncoderInfo x264 = Encoder("libx264", "mp4", "mp4");
        string dir = TempDir();
        try
        {
            // out.mp4 already exists, and the user named out.rm: the capture
            // must not land on the file it was never told to write.
            await File.WriteAllTextAsync(Path.Combine(dir, "clip.mp4"), "existing");
            (FileInfo taken, _, _) = OutputNaming.CorrectExtension(
                new FileInfo(Path.Combine(dir, "clip.rm")), x264);
            await Assert.That(taken.Name).IsEqualTo("clip (2).mp4");

            // The source is avoided for the same reason, even when nothing sits
            // at that name yet.
            (FileInfo bumped, _, _) = OutputNaming.CorrectExtension(
                new FileInfo(Path.Combine(dir, "take.rm")), x264,
                Path.Combine(dir, "take.mp4"));
            await Assert.That(bumped.Name).IsEqualTo("take (2).mp4");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Mp4WithH264PrefersAac()
    {
        await Assert.That(EncoderMath.PrefersAac("mp4", "libx264")).IsTrue();
        await Assert.That(EncoderMath.PrefersAac("mp4", "libopenh264")).IsTrue();
        await Assert.That(EncoderMath.PrefersAac("MP4", "LIBX264")).IsTrue();

        // A different muxer, or a codec from another era, keeps its own answer.
        await Assert.That(EncoderMath.PrefersAac("matroska", "libx264")).IsFalse();
        await Assert.That(EncoderMath.PrefersAac("mp4", "mpeg4")).IsFalse();
        await Assert.That(EncoderMath.PrefersAac("flv", "libopenh264")).IsFalse();
    }

    [Test]
    public async Task TheAudioLeftBehindByAnotherContainerIsReplacedAtStart()
    {
        // MP3 muxes into mp4, so SupportsContainer is happy and nothing else
        // would ever replace the leftover from a 2007 FLV run.
        var carried = new Config { AudioEncoder = "libmp3lame" };
        Cui.DefaultAudioFor(carried, Encoder("libx264", "mp4", "mp4"), ["aac", "libmp3lame"]);
        await Assert.That(carried.AudioEncoder).IsEqualTo("aac");

        // A deliberate "no audio" is not a default and stays.
        var none = new Config { AudioEncoder = null };
        Cui.DefaultAudioFor(none, Encoder("libopenh264", "mp4", "mp4"), ["aac"]);
        await Assert.That(none.AudioEncoder).IsNull();

        // A build without AAC leaves the working codec alone rather than
        // leaving the run without audio.
        var build = new Config { AudioEncoder = "libmp3lame" };
        Cui.DefaultAudioFor(build, Encoder("libx264", "mp4", "mp4"), ["libmp3lame"]);
        await Assert.That(build.AudioEncoder).IsEqualTo("libmp3lame");

        // And the rule is about mp4 + H.264, not about H.264 anywhere.
        var avi = new Config { AudioEncoder = "libmp3lame" };
        Cui.DefaultAudioFor(avi, Encoder("libx264", "avi", "avi"), ["aac", "libmp3lame"]);
        await Assert.That(avi.AudioEncoder).IsEqualTo("libmp3lame");
    }
}
