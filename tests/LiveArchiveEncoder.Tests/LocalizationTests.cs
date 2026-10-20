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
public sealed class LocalizationTests
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

    [Test]
    public async Task SubstitutesPositionalArguments()
    {
        L.SetLanguage(L.English);

        await Assert.That(L.T("banner.legacycount", 35)).IsEqualTo("35 detected from ffmpeg -encoders");
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
