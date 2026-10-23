using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Subtitle fonts (v7.9.5.1). An ASS file names the fonts it needs in its style
/// table; whether the machine has them decides between "renders" and "renders
/// boxes". The parsing, the matching and the filter argument are all pure, so
/// they are tested here; the real font files are only read for the family-name
/// test, which skips itself when the machine has none.
/// </summary>
public sealed class SubtitleFontTests
{
    private static readonly string[] Ass =
    [
        "[Script Info]",
        "ScriptType: v4.00+",
        "",
        "[V4+ Styles]",
        "Format: Name, Fontname, Fontsize, PrimaryColour",
        "Style: Default,微软雅黑,20,&H00FFFFFF",
        "Style: Alt,SimHei,18,&H00FFFFFF",
        "",
        "[Events]",
        "Format: Layer, Start, End, Style, Name",
        "Dialogue: 0,0:00:00.50,0:00:02.00,Default,,0",
    ];

    [Test]
    public async Task TheStyleTableIsReadByColumnName()
    {
        // The Fontname column is found through the Format line, so a file whose
        // columns are in another order still works.
        List<string> families = SubtitleFonts.ParseAssFamilies(Ass);

        await Assert.That(families).IsEquivalentTo(new[] { "微软雅黑", "SimHei" });
    }

    [Test]
    public async Task OldSsaAndEmbeddedFontsAreUnderstood()
    {
        string[] ssa =
        [
            "[V4 Styles]",
            "Format: Name, Fontname, Fontsize",
            "Style: Default,Georgia,20",
            "",
            "[Fonts]",
            "fontname: AR PL UKai CN",
        ];

        List<string> families = SubtitleFonts.ParseAssFamilies(ssa);

        await Assert.That(families).Contains("Georgia");
        await Assert.That(families).Contains("AR PL UKai CN");
    }

    [Test]
    public async Task SrtsNameNoFontAtAll()
    {
        // Which is the "没有指定" case: only a fallback family can apply.
        await Assert.That(SubtitleFonts.RequiredFamilies(["a.srt"])).IsEmpty();
        await Assert.That(SubtitleFonts.RequiredFamilies(["a.vtt"])).IsEmpty();
    }

    [Test]
    public async Task MatchingUnderstandsTranslationsAndFaces()
    {
        var installed = new List<FontFace>
        {
            new("Microsoft YaHei", @"C:\fonts\msyh.ttc", true),
            new("SimSun", @"C:\fonts\simsun.ttc", true),
            new("Arial", @"C:\Windows\Fonts\arial.ttf", false),
        };

        // Chinese name -> English family, and vice versa.
        await Assert.That(SubtitleFonts.Match("微软雅黑", installed)?.Family).IsEqualTo("Microsoft YaHei");
        await Assert.That(SubtitleFonts.Match("宋体", installed)?.Family).IsEqualTo("SimSun");
        await Assert.That(SubtitleFonts.Match("simsun", installed)?.Family).IsEqualTo("SimSun");
        // Spacing and case do not matter.
        await Assert.That(SubtitleFonts.Match("microsoft yahei", installed)?.Family).IsEqualTo("Microsoft YaHei");
        // The portable copy wins over the system one.
        await Assert.That(SubtitleFonts.Match("arial", installed)?.Portable).IsFalse();
        // Something nobody has stays missing rather than matching the first font.
        await Assert.That(SubtitleFonts.Match("FZShuTi", installed)).IsNull();
    }

    [Test]
    public async Task ASameFamilyDifferentFaceStillMatches()
    {
        // "Microsoft YaHei UI" is the same design; a release naming it must not
        // fall back to boxes.
        var installed = new List<FontFace> { new("Microsoft YaHei", "x.ttc", true) };
        await Assert.That(SubtitleFonts.Match("Microsoft YaHei UI", installed)?.Family)
            .IsEqualTo("Microsoft YaHei");
    }

    [Test]
    public async Task TheBurnFilterCarriesFontsdirAndTheForcedFamily()
    {
        string vf = SubtitleTrack.AppendBurn("scale=320:240", ["a.ass"], null, "SimHei");

        await Assert.That(vf).StartsWith("scale=320:240,");
        await Assert.That(vf).Contains("subtitles='a.ass'");
        await Assert.That(vf).Contains("force_style='FontName=SimHei'");
        // The forcing sits inside the same filter call as the file, so there is
        // exactly one subtitles= per file.
        await Assert.That(vf.Split("subtitles=").Length).IsEqualTo(2);
    }

    [Test]
    public async Task NoFallbackMeansNoForceStyle()
    {
        string vf = SubtitleTrack.AppendBurn("", ["a.srt"]);
        await Assert.That(vf).DoesNotContain("force_style");
        await Assert.That(vf).StartsWith("subtitles='a.srt'");
    }

    [Test]
    public async Task AFontFileReportsItsOwnFamily()
    {
        // Read one real font, if this machine has any. The point is the name
        // table, not the file name: msyh.ttc says "Microsoft YaHei".
        string dir = FontResolver.FontDirectory;
        string? sample = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            : null;
        if (sample is null)
        {
            Skip.Test("no font file on this machine");
            return;
        }

        List<string> families = SubtitleFonts.ReadFamilies(sample);
        await Assert.That(families).IsNotEmpty().Because(sample);
        // A family name is never the file extension or an empty string.
        foreach (string family in families)
        {
            await Assert.That(family.Trim()).IsNotEmpty();
            await Assert.That(family).DoesNotContain(".tt");
        }
    }

    [Test]
    public async Task GarbageIsNotAFont()
    {
        string path = Path.Combine(Path.GetTempPath(), "lae_not_a_font_" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04]);
        try
        {
            // A truncated or corrupt file answers "no families" instead of
            // throwing out of the middle of an encode.
            await Assert.That(SubtitleFonts.ReadFamilies(path)).IsEmpty();
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task TheSettingsFileKeepsTheFontChoices()
    {
        // A fallback family is part of the run: without it the same file renders
        // differently on a machine with other fonts.
        var config = new Config
        {
            Subtitles = new SubtitleConfig
            {
                Mode = "burn",
                Files = ["a.ass"],
                FontDir = @"D:\fonts",
                FallbackFont = "SimHei",
            },
        };

        UserConfig captured = ConfigStore.Capture(config);
        await Assert.That(captured.Subtitles.FallbackFont).IsEqualTo("SimHei");
        await Assert.That(captured.Subtitles.FontDir).IsEqualTo(@"D:\fonts");

        var again = new Config();
        ConfigStore.Apply(captured, again, []);
        await Assert.That(again.Subtitles.FallbackFont).IsEqualTo("SimHei");
    }
}
