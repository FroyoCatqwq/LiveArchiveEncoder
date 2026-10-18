using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// The two string tables and the display-width helpers.
///
/// The parity test exists because a missing key renders as the key itself or
/// falls back to English, which in a label list silently breaks the column
/// alignment -- a bug that already shipped once.
/// </summary>
[TestFixture]
public sealed class LocalizationTests
{
    private string _original = "";

    [SetUp]
    public void SetUp() => _original = L.Lang;

    [TearDown]
    public void TearDown() => L.SetLanguage(_original);

    [Test]
    public void EveryKeyExistsInEveryLanguage()
    {
        foreach (string language in L.Languages)
        {
            IReadOnlyCollection<string> missing = L.AllKeys.Except(L.KeysFor(language)).ToList();
            Assert.That(missing, Is.Empty,
                $"{language} is missing: {string.Join(", ", missing)}");
        }
    }

    [Test]
    public void EveryKeyHasBothTranslations()
    {
        IReadOnlyCollection<string> english = L.KeysFor(L.English);
        IReadOnlyCollection<string> chinese = L.KeysFor(L.Chinese);

        Assert.That(chinese, Is.EquivalentTo(english));
    }

    [Test]
    public void SetLanguageNormalisesTheCommonSpellings()
    {
        Assert.That(L.SetLanguage("zh"), Is.EqualTo(L.Chinese));
        Assert.That(L.SetLanguage("ZH"), Is.EqualTo(L.Chinese));
        Assert.That(L.SetLanguage("zh-CN"), Is.EqualTo(L.Chinese));
        Assert.That(L.SetLanguage("zh_Hans"), Is.EqualTo(L.Chinese));
        Assert.That(L.SetLanguage("中文"), Is.EqualTo(L.Chinese));
        Assert.That(L.SetLanguage("en"), Is.EqualTo(L.English));
        Assert.That(L.SetLanguage("klingon"), Is.EqualTo(L.English));
    }

    [Test]
    public void UnknownKeyRendersAsTheKeyInsteadOfFailing()
    {
        L.SetLanguage(L.English);
        Assert.That(L.T("no.such.key"), Is.EqualTo("no.such.key"));
    }

    [Test]
    public void SubstitutesPositionalArguments()
    {
        L.SetLanguage(L.English);

        Assert.That(L.T("banner.legacycount", 35), Is.EqualTo("35 detected from ffmpeg -encoders"));
    }

    [Test]
    public void YesNoFollowsTheLanguage()
    {
        L.SetLanguage(L.English);
        Assert.That(L.YesNo(true), Is.EqualTo("yes"));

        L.SetLanguage(L.Chinese);
        Assert.That(L.YesNo(true), Is.EqualTo("是"));
    }

    /// <summary>
    /// CJK glyphs occupy two console columns, so padding by character count
    /// leaves the colons of a label list ragged.
    /// </summary>
    [Test]
    public void DisplayWidthCountsCjkAsTwoColumns()
    {
        Assert.That(L.DisplayWidth("abc"), Is.EqualTo(3));
        Assert.That(L.DisplayWidth("界面语言"), Is.EqualTo(8));
    }

    [Test]
    public void PadTargetsDisplayWidthNotCharacterCount()
    {
        Assert.That(L.DisplayWidth(L.Pad("abc", 10)), Is.EqualTo(10));
        Assert.That(L.DisplayWidth(L.Pad("界面语言", 10)), Is.EqualTo(10));
        Assert.That(L.Pad("界面语言", 2), Is.EqualTo("界面语言"),
            "padding never truncates");
    }
}

/// <summary>Pure helpers that the whole app leans on.</summary>
[TestFixture]
public sealed class CoreTests
{
    [TestCase("00:00:00", 0.0)]
    [TestCase("00:01:02", 62.0)]
    [TestCase("00:01:02.500", 62.5)]
    [TestCase("01:02:03", 3723.0)]
    public void Timecode_ParsesHgMmsSs(string text, double expected) =>
        Assert.That(Timecode.Parse(text), Is.EqualTo(expected).Within(0.001));

    [TestCase("00:99:00")]
    [TestCase("nonsense")]
    [TestCase("")]
    public void Timecode_RejectsOutOfRangeAndGarbage(string text) =>
        Assert.That(() => Timecode.Parse(text), Throws.InstanceOf<FormatException>());

    [Test]
    public void Timecode_FormatsBackToTheSameString() =>
        Assert.That(Timecode.Format(3723.0), Is.EqualTo("01:02:03"));

    [Test]
    public void Timecode_FormatsNullAsEndOfSource() =>
        Assert.That(Timecode.Format(null), Is.EqualTo("end of source"));

    /// <summary>
    /// These codecs have no rate control: FFmpeg silently ignores -b:a, so
    /// emitting it only misleads whoever reads the command.
    /// </summary>
    [TestCase("adpcm_ima_amv")]
    [TestCase("adpcm_ima_wav")]
    [TestCase("pcm_s16le")]
    [TestCase("roq_dpcm")]
    public void AudioIgnoresBitrate_TrueForUncompressedCodecs(string encoder) =>
        Assert.That(EncoderMath.AudioIgnoresBitrate(encoder), Is.True);

    [TestCase("libmp3lame")]
    [TestCase("aac")]
    [TestCase("ac3")]
    [TestCase(null)]
    public void AudioIgnoresBitrate_FalseWhereRateControlExists(string? encoder) =>
        Assert.That(EncoderMath.AudioIgnoresBitrate(encoder), Is.False);
}
