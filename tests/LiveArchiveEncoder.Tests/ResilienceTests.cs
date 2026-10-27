using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8: the failures that look like success.
///
/// Every case here pins a defect that did not announce itself -- a config that
/// crashed only one of its readers, a timecode that threw the wrong exception
/// type, a shard join that would have judged a network source by the wrong
/// bitrate, and the ".part" scheme that keeps a failed run from destroying the
/// file it was replacing. None of them failed loudly, which is exactly why they
/// need a test rather than a code comment.
/// </summary>
public sealed class ResilienceTests
{
    // ---- a settings file may spell a non-nullable member as null -----------

    /// <summary>
    /// System.Text.Json honours <c>"FpsLevels": null</c> literally, so a
    /// hand-edited file put nulls in slots whose type forbids them. Only the
    /// Apply path defended itself; <c>--emit-args</c> walked straight into
    /// CliArgs.LadderSpec and died with a NullReferenceException.
    /// </summary>
    [Test]
    public async Task ANullCollectionInTheSettingsFileIsRepairedOnRead()
    {
        const string json = """
            {
              "FpsLevels": null,
              "FpsLevelKbps": null,
              "Recompress": null,
              "Subtitles": null,
              "Dither": null,
              "Deinterlace": null,
              "Language": null,
              "FfmpegSource": null
            }
            """;
        string path = Path.Combine(Path.GetTempPath(), $"lae-null-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            UserConfig? loaded = ConfigStore.TryLoad(path);
            await Assert.That(loaded).IsNotNull();

            // The ladder is never empty: the controller needs a current rung.
            await Assert.That(loaded!.FpsLevels.Count).IsEqualTo(4);
            await Assert.That(loaded.FpsLevelKbps).IsNotNull();
            await Assert.That(loaded.Recompress).IsNotNull();
            await Assert.That(loaded.Subtitles).IsNotNull();
            await Assert.That(loaded.Subtitles.Files).IsNotNull();
            await Assert.That(loaded.Dither).IsEqualTo("auto");
            await Assert.That(loaded.Deinterlace).IsEqualTo("off");
            await Assert.That(loaded.Language).IsEqualTo("en");
            await Assert.That(loaded.FfmpegSource).IsEqualTo(FfmpegCatalog.DefaultId);

            // The reader that used to crash is the real acceptance test.
            List<string> argv = CliArgs.FromUserConfig(loaded);
            await Assert.That(argv).IsNotNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// null and [] are not the same thing for the custom presets: null means
    /// "the file predates the field, keep whatever is loaded", [] means "none".
    /// Normalising must not collapse the two.
    /// </summary>
    [Test]
    public async Task AnAbsentCustomEraListStaysAbsent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lae-era-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "Language": "zh" }""");
        try
        {
            UserConfig? loaded = ConfigStore.TryLoad(path);
            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.CustomEras).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- timecode --------------------------------------------------------

    /// <summary>
    /// The hour field accepts any number of digits, so int.Parse overflowed and
    /// threw an OverflowException that every call site (they all catch
    /// FormatException only) let escape.
    /// </summary>
    [Test]
    [Arguments("99999999999:00:00")]
    [Arguments("100000:00:00")]
    public async Task AnOverlongHourFieldIsAFormatError(string text) =>
        await Assert.That(() => Timecode.Parse(text)).Throws<FormatException>();

    [Test]
    public async Task TheLongestAcceptedHourStillParses()
    {
        double seconds = Timecode.Parse("99999:00:00");
        await Assert.That(seconds).IsEqualTo(99999.0 * 3600.0);
    }

    // ---- shard-join input ------------------------------------------------

    private static SourceStream Video(int index) =>
        new(index, "video", "h264", "und", null,
            new StreamInfo { Width = 320, Height = 240, PixFmt = "yuv420p" });

    /// <summary>
    /// The container bitrate belongs to the file, not to the stream that was
    /// picked. Dropping it in Resolve made the network check fall back to the
    /// output ceiling, so a 20 Mbit/s source behind a slow link was judged as if
    /// it needed 800 kbit/s and got sharded anyway.
    /// </summary>
    [Test]
    public async Task PickingAVideoStreamKeepsTheContainerBitrate()
    {
        var source = new SourceInfo
        {
            Video = Video(0).Video!,
            Duration = 42.0,
            HasAudio = false,
            Streams = [Video(0), Video(2)],
            BitRateKbps = 20_000.0,
        };

        SourceInfo resolved = StreamSelection.Resolve(source, video: 2, audio: null);
        await Assert.That(resolved.BitRateKbps).IsEqualTo(20_000.0);
    }

    // ---- the ".part" output scheme ---------------------------------------

    [Test]
    [Arguments("/tmp/x/out.mp4", "/tmp/x/out.part.mp4")]
    [Arguments("/tmp/x/out.tar.gz", "/tmp/x/out.tar.part.gz")]
    [Arguments("/tmp/x/noext", "/tmp/x/noext.part")]
    public async Task ThePartSiblingKeepsTheExtension(string final, string expected)
    {
        // Both sides go through FileInfo: a rooted-but-drive-less path such as
        // "/tmp/x/out.mp4" is "D:\tmp\x\out.mp4" to Windows, and comparing the
        // raw strings failed on the drive letter rather than on where the
        // ".part" landed.
        string path = final.Replace('/', Path.DirectorySeparatorChar);
        string want = new FileInfo(expected.Replace('/', Path.DirectorySeparatorChar)).FullName;
        await Assert.That(OutputFile.PartPath(new FileInfo(path)).FullName).IsEqualTo(want);
    }

    /// <summary>
    /// The whole point: a finished part file replaces whatever was there, and a
    /// failed run never touches the real name.
    /// </summary>
    [Test]
    public async Task PromoteReplacesTheDestinationAndConsumesThePart()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lae-part-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var final = new FileInfo(Path.Combine(dir, "clip.mp4"));
        try
        {
            var part = OutputFile.PartPath(final);
            await File.WriteAllTextAsync(final.FullName, "old archive");
            await File.WriteAllTextAsync(part.FullName, "new archive");

            OutputFile.Promote(part, final);

            await Assert.That(await File.ReadAllTextAsync(final.FullName)).IsEqualTo("new archive");
            await Assert.That(File.Exists(part.FullName)).IsFalse();

            // An empty part is a failed encode, not a result.
            await Assert.That(() => OutputFile.Promote(new FileInfo(Path.Combine(dir, "gone.part.mp4")), final))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task DiscardIsQuietAboutAFileThatIsNotThere()
    {
        var missing = new FileInfo(Path.Combine(Path.GetTempPath(), $"lae-absent-{Guid.NewGuid():N}.part"));
        OutputFile.Discard(missing);       // must not throw
        await Assert.That(missing.Exists).IsFalse();
    }

    // ---- FileInfo.HasContent -------------------------------------------

    /// <summary>
    /// The refresh is the reason this member exists: a FileInfo built before
    /// FFmpeg wrote the file answers "does not exist" from its cached snapshot.
    /// </summary>
    [Test]
    public async Task HasContentRefreshesAStaleSnapshot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lae-content-{Guid.NewGuid():N}.bin");
        var file = new FileInfo(path);
        try
        {
            await Assert.That(file.HasContent).IsFalse();

            await File.WriteAllTextAsync(path, "x");
            // No Refresh() here on purpose: HasContent has to do it.
            await Assert.That(file.HasContent).IsTrue();

            await File.WriteAllTextAsync(path, "");
            await Assert.That(file.HasContent).IsFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- --emit-args quoting -------------------------------------------

    /// <summary>
    /// cmd.exe splits on these. A source called "clip &amp; take 2.avi" used to be
    /// printed bare, so pasting the emitted command line ran two commands.
    /// </summary>
    [Test]
    [Arguments("clip & take 2.avi")]
    [Arguments("a|b.avi")]
    [Arguments("(c).avi")]
    [Arguments("d^e.avi")]
    [Arguments("f<g>.avi")]
    public async Task ShellMetacharactersForceQuoting(string token)
    {
        string quoted = CliArgs.Quote(token);
        await Assert.That(quoted.StartsWith('"')).IsTrue().Because(quoted);
        await Assert.That(quoted.EndsWith('"')).IsTrue().Because(quoted);
    }

    [Test]
    public async Task APlainTokenStaysBare()
    {
        await Assert.That(CliArgs.Quote("clip.avi")).IsEqualTo("clip.avi");
        await Assert.That(CliArgs.Quote("")).IsEqualTo("\"\"");
    }
}
