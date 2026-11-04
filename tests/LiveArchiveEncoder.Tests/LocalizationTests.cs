using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// The two string tables and the display-width helpers.
///
/// The parity test exists because a missing key renders as the key itself or
/// falls back to English, which in a label list silently breaks the column
/// alignment -- a bug that already shipped once.
///
/// [NotInParallel] because every method flips the process-wide language and
/// restores it afterwards: under NUnit's sequential fixtures that was safe,
/// and TUnit's default parallelism would race two of them.
/// </summary>
[NotInParallel]
public sealed partial class LocalizationTests
{
    private string _original = "";

    [Before(Test)]
    public void SetUp() => _original = L.Lang;

    [After(Test)]
    public void TearDown() => L.SetLanguage(_original);

    [Test]
    public async Task EveryKeyExistsInEveryLanguage()
    {
        foreach (string language in L.Languages)
        {
            IReadOnlyCollection<string> missing = L.AllKeys.Except(L.KeysFor(language)).ToList();
            await Assert.That(missing).IsEmpty().Because($"{language} is missing: {string.Join(", ", missing)}");
        }
    }

    [Test]
    public async Task EveryKeyHasBothTranslations()
    {
        IReadOnlyCollection<string> english = L.KeysFor(L.English);
        IReadOnlyCollection<string> chinese = L.KeysFor(L.Chinese);

        await Assert.That(chinese).IsEquivalentTo(english);
    }

    [Test]
    public async Task SetLanguageNormalisesTheCommonSpellings()
    {
        await Assert.That(L.SetLanguage("zh")).IsEqualTo(L.Chinese);
        await Assert.That(L.SetLanguage("ZH")).IsEqualTo(L.Chinese);
        await Assert.That(L.SetLanguage("zh-CN")).IsEqualTo(L.Chinese);
        await Assert.That(L.SetLanguage("zh_Hans")).IsEqualTo(L.Chinese);
        await Assert.That(L.SetLanguage("中文")).IsEqualTo(L.Chinese);
        await Assert.That(L.SetLanguage("en")).IsEqualTo(L.English);
        await Assert.That(L.SetLanguage("klingon")).IsEqualTo(L.English);
    }

    [Test]
    public async Task UnknownKeyRendersAsTheKeyInsteadOfFailing()
    {
        L.SetLanguage(L.English);
        await Assert.That(L.T("no.such.key")).IsEqualTo("no.such.key");
    }

    /// <summary>
    /// v7.9.8.6: the parity tests above compare the two tables with each other, so
    /// a key that is missing from <em>both</em> passes them and then renders as its
    /// own name on screen. That is exactly what happened to "sub.fonts.missing":
    /// the subtitle font check printed the dotted key for any font the file asked
    /// for and the machine did not have. This walks the source for literal keys.
    /// </summary>
    [Test]
    public async Task EveryKeyTheSourceAsksForExists()
    {
        HashSet<string> asked = LiteralKeys();
        await Assert.That(asked.Count).IsGreaterThan(50).Because("the scan should find the call sites");

        foreach (string language in L.Languages)
        {
            string[] missing = [.. asked.Where(k => !L.KeysFor(language).Contains(k)).Order(StringComparer.Ordinal)];
            await Assert.That(missing).IsEmpty()
                .Because($"{language} has no text for: {string.Join(", ", missing)}");
        }
    }

    [GeneratedRegex("L\\.T\\(\\s*\"([a-z][a-z0-9._]*)\"")]
    private static partial Regex KeyLiteral();

    private static HashSet<string> LiteralKeys([CallerFilePath] string thisFile = "")
    {
        string source = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "src", "LiveArchiveEncoder"));
        if (!Directory.Exists(source))
            throw new InvalidOperationException($"the source tree is not readable at {source}");

        return [.. Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => KeyLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)];
    }

    [Test]
    public async Task SubstitutesPositionalArguments()
    {
        L.SetLanguage(L.English);

        await Assert.That(L.T("banner.legacycount", 35)).IsEqualTo("35 detected from ffmpeg -encoders");
    }

    /// <summary>
    /// v7.9.8.10: key parity is not enough. A template with two slots called with
    /// one argument throws FormatException inside string.Format; in the CUI that
    /// was swallowed as "Invalid selection" after the state had already changed.
    /// Walk every template in both languages with exactly the arguments its
    /// highest slot asks for, and make sure none of them disappear.
    /// </summary>
    [Test]
    public async Task EveryPlaceholderFormatsInEveryLanguage()
    {
        foreach (string language in L.Languages)
        {
            L.SetLanguage(language);
            foreach (string key in L.AllKeys)
            {
                string template = L.T(key);          // zero args returns the raw text
                MatchCollection slots = Regex.Matches(template, @"\{(\d+)");
                if (slots.Count == 0) continue;
                int highest = slots.Cast<Match>().Max(m => int.Parse(m.Groups[1].Value));
                object?[] args = [.. Enumerable.Range(0, highest + 1)
                    .Select(i => (object?)$"<{i}>")];
                string rendered = L.T(key, args);   // throws on a malformed template
                for (int i = 0; i <= highest; i++)
                    await Assert.That(rendered).Contains($"<{i}>")
                        .Because($"{language}/{key} lost argument {i}");
            }
        }
    }

    private static int[] Slots(string template) =>
        [.. Regex.Matches(template, @"\{(\d+)").Cast<Match>()
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order()];

    /// <summary>
    /// A Chinese template that quietly loses a {1} renders fine in English and
    /// throws FormatException in the CUI. Key parity alone cannot see this.
    /// </summary>
    [Test]
    public async Task BothLanguagesUseTheSamePlaceholders()
    {
        L.SetLanguage(L.English);
        Dictionary<string, int[]> english = L.AllKeys.ToDictionary(k => k, k => Slots(L.T(k)));
        L.SetLanguage(L.Chinese);
        foreach (string key in L.AllKeys)
        {
            int[] chinese = Slots(L.T(key));
            await Assert.That(chinese).IsEquivalentTo(english[key])
                .Because($"{key}: en=[{string.Join(",", english[key])}] zh=[{string.Join(",", chinese)}]");
        }
    }

    [Test]
    public async Task YesNoFollowsTheLanguage()
    {
        L.SetLanguage(L.English);
        await Assert.That(L.YesNo(true)).IsEqualTo("yes");

        L.SetLanguage(L.Chinese);
        await Assert.That(L.YesNo(true)).IsEqualTo("是");
    }

    /// <summary>
    /// CJK glyphs occupy two console columns, so padding by character count
    /// leaves the colons of a label list ragged.
    /// </summary>
    [Test]
    public async Task DisplayWidthCountsCjkAsTwoColumns()
    {
        await Assert.That(L.DisplayWidth("abc")).IsEqualTo(3);
        await Assert.That(L.DisplayWidth("界面语言")).IsEqualTo(8);
    }

    [Test]
    public async Task PadTargetsDisplayWidthNotCharacterCount()
    {
        await Assert.That(L.DisplayWidth(L.Pad("abc", 10))).IsEqualTo(10);
        await Assert.That(L.DisplayWidth(L.Pad("界面语言", 10))).IsEqualTo(10);
        await Assert.That(L.Pad("界面语言", 2)).IsEqualTo("界面语言").Because("padding never truncates");
    }
}

/// <summary>Pure helpers that the whole app leans on.</summary>
public sealed class CoreTests
{
    [Test]
    [Arguments("00:00:00", 0.0)]
    [Arguments("00:01:02", 62.0)]
    [Arguments("00:01:02.500", 62.5)]
    [Arguments("01:02:03", 3723.0)]
    public async Task Timecode_ParsesHgMmsSs(string text, double expected) =>
        await Assert.That(Timecode.Parse(text)).IsEqualTo(expected).Within(0.001);

    [Test]
    [Arguments("00:99:00")]
    [Arguments("nonsense")]
    [Arguments("")]
    public async Task Timecode_RejectsOutOfRangeAndGarbage(string text) =>
        await Assert.That(() => Timecode.Parse(text)).Throws<FormatException>();

    [Test]
    public async Task Timecode_FormatsBackToTheSameString() =>
        await Assert.That(Timecode.Format(3723.0)).IsEqualTo("01:02:03");

    [Test]
    public async Task Timecode_FormatsNullAsEndOfSource() =>
        await Assert.That(Timecode.Format(null)).IsEqualTo("end of source");

    /// <summary>
    /// These codecs have no rate control: FFmpeg silently ignores -b:a, so
    /// emitting it only misleads whoever reads the command.
    /// </summary>
    [Test]
    [Arguments("adpcm_ima_amv")]
    [Arguments("adpcm_ima_wav")]
    [Arguments("pcm_s16le")]
    [Arguments("roq_dpcm")]
    public async Task AudioIgnoresBitrate_TrueForUncompressedCodecs(string encoder) =>
        await Assert.That(EncoderMath.AudioIgnoresBitrate(encoder)).IsTrue();

    [Test]
    [Arguments("libmp3lame")]
    [Arguments("aac")]
    [Arguments("ac3")]
    [Arguments(null)]
    public async Task AudioIgnoresBitrate_FalseWhereRateControlExists(string? encoder) =>
        await Assert.That(EncoderMath.AudioIgnoresBitrate(encoder)).IsFalse();
}
