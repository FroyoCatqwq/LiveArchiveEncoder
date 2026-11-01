using Lae.Core;
using Lae.Core.Localization;
using Lae.Media.Localization;
using Lae.Plugins;

namespace Lae.Tests.Plugins;

/// <summary>
/// The three product checks, isolated from ffprobe.
///
/// <para>The probe is a delegate, so every layer can be exercised without a media
/// file or a process. That matters most for the third layer -- "the product is a
/// tenth of the length it was given" -- because producing a genuinely truncated file
/// with FFmpeg would take seconds per case and would test FFmpeg's truncation, not
/// this code's detection of it.</para>
/// </summary>
public sealed class PluginVerifierTests
{
    private const string Id = "lae-fake";
    private static readonly IMessages En = I18NextMessages.Create("en");

    /// <summary>A product that reads as a video of this length.</summary>
    private static SourceInfo Video(double seconds) => new()
    {
        DurationSeconds = seconds,
        Width = 320,
        Height = 240,
        Streams = [new SourceStream(0, "video", "mpeg4", "yuv420p", null)],
    };

    private static PluginVerifier Verifier(
        Func<string, string, CancellationToken, (SourceInfo? Info, string? Error)> probe)
        => new("ffprobe", probe, En);

    [Test]
    public async Task AFileThatWasNeverWrittenIsReported()
    {
        string part = Path.Combine(Path.GetTempPath(), $"lae-missing-{Guid.NewGuid():N}.mp4");

        string? problem = Verifier((_, _, _) => (Video(10), null)).Check(Id, "input.mp4", part, default);

        await Assert.That(problem).Contains("wrote no output");
    }

    [Test]
    public async Task AnEmptyFileIsReportedApartFromAMissingOne()
    {
        string part = Path.Combine(Path.GetTempPath(), $"lae-empty-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(part, []);

        try
        {
            string? problem = Verifier((_, _, _) => (Video(10), null)).Check(Id, "input.mp4", part, default);

            await Assert.That(problem).Contains("empty");
        }
        finally
        {
            File.Delete(part);
        }
    }

    [Test]
    public async Task AFileFfprobeCannotReadIsReported()
    {
        string part = await Temp(64);

        try
        {
            string? problem = Verifier((_, _, _) => (null, "moov atom not found"))
                .Check(Id, "input.mp4", part, default);

            await Assert.That(problem).Contains("not a readable video");
            await Assert.That(problem).Contains("moov atom not found");
        }
        finally
        {
            File.Delete(part);
        }
    }

    [Test]
    public async Task AProductWithNoVideoStreamIsReported()
    {
        string part = await Temp(64);
        var audioOnly = new SourceInfo
        {
            DurationSeconds = 10,
            Streams = [new SourceStream(0, "audio", "mp3", null, null)],
        };

        try
        {
            string? problem = Verifier((_, _, _) => (audioOnly, null)).Check(Id, "input.mp4", part, default);

            await Assert.That(problem).Contains("no video stream");
        }
        finally
        {
            File.Delete(part);
        }
    }

    /// <summary>
    /// The layer that catches "processed the first ten percent and exited zero". The
    /// file plays, so nothing else would have noticed.
    /// </summary>
    [Test]
    public async Task AProductFarShorterThanItsInputIsReported()
    {
        string part = await Temp(64);

        try
        {
            string? problem = Verifier((_, path, _) => (Video(path == part ? 1 : 10), null))
                .Check(Id, "input.mp4", part, default);

            await Assert.That(problem).Contains("1.0s");
            await Assert.That(problem).Contains("10.0s");
        }
        finally
        {
            File.Delete(part);
        }
    }

    /// <summary>
    /// A fraction of a second is container overhead and keyframe alignment, not lost
    /// content. Rejecting it would reject correct products.
    /// </summary>
    [Test]
    public async Task ASmallDifferenceInLengthIsAccepted()
    {
        string part = await Temp(64);

        try
        {
            string? problem = Verifier((_, path, _) => (Video(path == part ? 9.6 : 10), null))
                .Check(Id, "input.mp4", part, default);

            await Assert.That(problem).IsNull();
        }
        finally
        {
            File.Delete(part);
        }
    }

    /// <summary>
    /// MPEG-PS carries no index, so a probe can report zero. "Unknown" must not be
    /// read as "wrong": a product of a source whose length nobody knows is accepted
    /// on the first two layers alone.
    /// </summary>
    [Test]
    public async Task AnUnknownInputLengthSkipsTheComparison()
    {
        string part = await Temp(64);

        try
        {
            string? problem = Verifier((_, path, _) => (Video(path == part ? 3 : 0), null))
                .Check(Id, "input.mp4", part, default);

            await Assert.That(problem).IsNull();
        }
        finally
        {
            File.Delete(part);
        }
    }

    private static async Task<string> Temp(int bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"lae-part-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, new byte[bytes]);
        return path;
    }
}
