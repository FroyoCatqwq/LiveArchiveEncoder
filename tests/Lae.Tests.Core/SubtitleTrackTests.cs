using Lae.Core;

namespace Lae.Tests.Core;

/// <summary>
/// An embedded subtitle track: whether it can be drawn, and how it is unwrapped.
///
/// <para>libass takes a file name; a stream is not one. Everything here is about
/// that gap -- which codecs survive being re-wrapped as ASS, which cannot be
/// drawn at all, and the one flag the unwrap must <em>not</em> get.</para>
/// </summary>
public sealed class SubtitleTrackTests
{
    [Test]
    [Arguments("subrip")]
    [Arguments("ass")]
    [Arguments("mov_text")]
    [Arguments("webvtt")]
    public async Task TextTracksCanBeReWrappedAndDrawn(string codec)
    {
        await Assert.That(SubtitleTrack.IsText(codec)).IsTrue();
        await Assert.That(SubtitleTrack.Problem(codec, 2)).IsNull();
    }

    [Test]
    [Arguments("hdmv_pgs_subtitle")]
    [Arguments("dvd_subtitle")]
    [Arguments("dvb_subtitle")]
    public async Task BitmapTracksAreRefusedByName(string codec)
    {
        await Assert.That(SubtitleTrack.IsBitmap(codec)).IsTrue();

        string? problem = SubtitleTrack.Problem(codec, 4);
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!).Contains("bitmap");
        await Assert.That(problem!).Contains("track 4");
    }

    /// <summary>
    /// An unknown codec is let through: a newer FFmpeg may know a text format this
    /// list does not, and its own refusal is more accurate than a guess.
    /// </summary>
    [Test]
    public async Task AnUnknownCodecIsNotRefusedInAdvance()
    {
        await Assert.That(SubtitleTrack.IsText("something_new")).IsFalse();
        await Assert.That(SubtitleTrack.IsBitmap("something_new")).IsFalse();
        await Assert.That(SubtitleTrack.Problem("something_new", 1)).IsNull();
    }

    /// <summary>
    /// The unwrap reads the whole file, starting at the beginning, because the
    /// burn shifts the timeline by the trim start and that only lines up while the
    /// track still carries the source's own timestamps.
    /// </summary>
    [Test]
    public async Task TheUnwrapDoesNotSeek()
    {
        List<string> command = SubtitleTrack.ExtractCommand("ffmpeg", "/media/in.mkv", 3, "/tmp/out.ass");

        await Assert.That(command[0]).IsEqualTo("ffmpeg");
        await Assert.That(command.Contains("-ss")).IsFalse();
        await Assert.That(command.Contains("-i")).IsTrue();
        await Assert.That(command[command.IndexOf("-i") + 1]).IsEqualTo("/media/in.mkv");

        // The map is the absolute stream index the user typed, on input 0.
        await Assert.That(command[command.IndexOf("-map") + 1]).IsEqualTo("0:3");
        await Assert.That(command[command.IndexOf("-c:s") + 1]).IsEqualTo("ass");
        await Assert.That(command[^1]).IsEqualTo("/tmp/out.ass");
    }

    /// <summary>
    /// The unwrapped copy goes beside the output, not beside the source: a
    /// recording on a read-only share is normal, and the output folder is the one
    /// folder the run is about to write to.
    /// </summary>
    [Test]
    public async Task TheTemporaryCopyGoesBesideTheOutput()
    {
        string path = SubtitleTrack.TemporaryPath("/media/done/out.avi", 2, "abcd1234");

        await Assert.That(Path.GetFileName(path)).IsEqualTo("lae-subs-2-abcd1234.ass");
        await Assert.That(Path.GetDirectoryName(path)).IsEqualTo(Path.GetDirectoryName("/media/done/out.avi"));
    }
}
