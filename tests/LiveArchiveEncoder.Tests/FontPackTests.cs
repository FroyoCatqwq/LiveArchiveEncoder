using LiveArchiveEncoder.PostProcess;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>v7.9.8.13: sidecar discovery and font-pack selection.</summary>
public sealed class FontPackTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae_fp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task SidecarsAreFoundByTheVideosStem()
    {
        string dir = NewDir();
        try
        {
            string video = Path.Combine(dir, "[DBD] Show [05].mkv");
            await File.WriteAllTextAsync(video, "");
            foreach (string name in new[]
                     {
                         "[DBD] Show [05].sc.ass", "[DBD] Show [05].tc.ass",
                         "[DBD] Show [05].srt", "[DBD] Show [06].ass", "notes.txt",
                     })
                await File.WriteAllTextAsync(Path.Combine(dir, name), "");

            IReadOnlyList<string> found = SubtitleDiscovery.Find(video);
            await Assert.That(found.Count).IsEqualTo(3);
            // ASS/SSA outrank SRT, and sc sorts before tc.
            await Assert.That(Path.GetFileName(found[0])).IsEqualTo("[DBD] Show [05].sc.ass");
            await Assert.That(found.Any(f => f.EndsWith("[06].ass", StringComparison.Ordinal))).IsFalse();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task ANeighbouringEpisodeIsNotAttached()
    {
        string dir = NewDir();
        try
        {
            string video = Path.Combine(dir, "clip.mkv");
            await File.WriteAllTextAsync(video, "");
            await File.WriteAllTextAsync(Path.Combine(dir, "clip2.ass"), "");
            await File.WriteAllTextAsync(Path.Combine(dir, "clip.ass"), "");

            IReadOnlyList<string> found = SubtitleDiscovery.Find(video);
            await Assert.That(found.Count).IsEqualTo(1);
            await Assert.That(Path.GetFileName(found[0])).IsEqualTo("clip.ass");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task OnlyTheThreeArchiveFormatsAreAccepted()
    {
        await Assert.That(FontPack.IsArchive("Fonts.rar")).IsTrue();
        await Assert.That(FontPack.IsArchive("Fonts.7z")).IsTrue();
        await Assert.That(FontPack.IsArchive("Fonts.ZIP")).IsTrue();
        await Assert.That(FontPack.IsArchive("Fonts.tar.gz")).IsFalse();
        await Assert.That(FontPack.InstallerUrl.Contains("7z2605-x64.exe")).IsTrue();
    }

    [Test]
    public async Task AssStyleNamesMatchRealPackedFileNames()
    {
        // Measured 2026-11-07 from the user's Fonts.rar / .sc.ass: spaces and
        // hyphens differ between an ASS Fontname and the file it ships in.
        string[] fonts =
        [
            "FOT-MatisseProN-B.otf", "方正准圆_GBK.TTF", "Re0.ttf", "Unrelated.otf",
        ];
        string[] required = ["FOT-Matisse ProN B", "方正准圆_GBK", "Re0"];

        List<string> picked = FontPack.Recommended(fonts, required);
        await Assert.That(picked.Count).IsEqualTo(3);
        await Assert.That(picked.Contains("Unrelated.otf")).IsFalse();
        await Assert.That(FontPack.Normalize("FOT-Matisse ProN B"))
            .IsEqualTo(FontPack.Normalize(
                Path.GetFileNameWithoutExtension("FOT-MatisseProN-B.otf")));
    }

    [Test]
    public async Task TheSelectionAnswerMeansWhatItSays()
    {
        string[] fonts = ["a.ttf", "b.ttf", "c.ttf"];
        string[] suggested = ["b.ttf"];

        await Assert.That(string.Join(",", SubtitleSetup.ParseSelection("", fonts, suggested)))
            .IsEqualTo("b.ttf");
        await Assert.That(string.Join(",", SubtitleSetup.ParseSelection("*", fonts, suggested)))
            .IsEqualTo("a.ttf,b.ttf,c.ttf");
        await Assert.That(string.Join(",", SubtitleSetup.ParseSelection("1,3", fonts, suggested)))
            .IsEqualTo("a.ttf,c.ttf");
        // Nonsense falls back to the safe suggestion instead of importing nothing.
        await Assert.That(string.Join(",", SubtitleSetup.ParseSelection("wat", fonts, suggested)))
            .IsEqualTo("b.ttf");
    }
}
