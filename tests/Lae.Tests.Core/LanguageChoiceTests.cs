using Lae.Core.Localization;

namespace Lae.Tests.Core;

/// <summary>
/// Which language a run speaks.
///
/// <para>A one-line decision with more edge cases than it looks: a hand-edited
/// <c>zh-CN</c>, a stale <c>ui.language</c> naming a language this build does not
/// ship, and an environment variable that must beat both without writing anything
/// to disk.</para>
/// </summary>
public sealed class LanguageChoiceTests
{
    [Test]
    public async Task TheEnvironmentVariableBeatsTheSettingsFile()
    {
        await Assert.That(LanguageChoice.Pick("zh", "en")).IsEqualTo(LanguageChoice.Chinese);
        await Assert.That(LanguageChoice.Pick("en", "zh")).IsEqualTo(LanguageChoice.English);
    }

    [Test]
    public async Task WithoutTheVariableTheSettingsDecide()
    {
        await Assert.That(LanguageChoice.Pick(null, "zh")).IsEqualTo(LanguageChoice.Chinese);
        await Assert.That(LanguageChoice.Pick("", "zh")).IsEqualTo(LanguageChoice.Chinese);
    }

    [Test]
    public async Task NothingAnywhereMeansEnglish()
    {
        await Assert.That(LanguageChoice.Pick(null, null)).IsEqualTo(LanguageChoice.English);
        await Assert.That(LanguageChoice.Pick("", "")).IsEqualTo(LanguageChoice.English);
    }

    /// <summary>
    /// A language this build has no resources for is not an error and must not leave
    /// the program dumb: it falls through to English.
    /// </summary>
    [Test]
    public async Task AnUnknownLanguageFallsThroughToEnglish()
    {
        await Assert.That(LanguageChoice.Pick("fr", null)).IsEqualTo(LanguageChoice.English);
        await Assert.That(LanguageChoice.Pick(null, "klingon")).IsEqualTo(LanguageChoice.English);
    }

    /// <summary>
    /// Region and case do not matter: <c>zh-CN</c>, <c>zh_CN</c> and <c>ZH</c> all
    /// name the same resources.
    /// </summary>
    [Test]
    public async Task RegionalTagsNormaliseToTheLanguage()
    {
        foreach (string tag in new[] { "zh-CN", "zh_CN", "ZH", " zh-TW " })
        {
            await Assert.That(LanguageChoice.Pick(tag, null)).IsEqualTo(LanguageChoice.Chinese)
                .Because($"'{tag}' should resolve to zh");
        }
    }

    [Test]
    public async Task NormaliseRejectsWhatIsNotALanguage()
    {
        await Assert.That(LanguageChoice.Normalise(null)).IsNull();
        await Assert.That(LanguageChoice.Normalise("   ")).IsNull();
        await Assert.That(LanguageChoice.Normalise("fr-FR")).IsNull();
        await Assert.That(LanguageChoice.Normalise("zh-Hans")).IsEqualTo(LanguageChoice.Chinese);
    }
}
