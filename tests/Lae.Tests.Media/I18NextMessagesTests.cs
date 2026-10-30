using Lae.Core.Localization;
using Lae.Media.Localization;

namespace Lae.Tests.Media;

/// <summary>
/// The translation pipeline, from embedded resource to sentence.
///
/// <para>These assertions are about behaviour a missing resource would break
/// silently: a language that loads nothing shows keys instead of words, and a
/// placeholder that is not filled shows <c>{{path}}</c> in the middle of a
/// sentence. Both look like "not done yet" to a user, and neither fails a build.</para>
/// </summary>
public sealed class I18NextMessagesTests
{
    [Test]
    public async Task EnglishIsTheDefaultAndSaysWhatTheResourceSays()
    {
        IMessages messages = I18NextMessages.Create(null);

        await Assert.That(messages.Language).IsEqualTo(LanguageChoice.English);
        await Assert.That(messages["history.empty"]).IsEqualTo("No runs recorded yet.");
    }

    [Test]
    public async Task ChineseSpeaksChinese()
    {
        IMessages messages = I18NextMessages.Create("zh");

        await Assert.That(messages.Language).IsEqualTo(LanguageChoice.Chinese);
        await Assert.That(messages["history.empty"]).IsEqualTo("还没有任何运行记录。");
        await Assert.That(messages["history.limit"]).Contains("正整数");
    }

    /// <summary>
    /// Placeholders are filled from the object, not concatenated by the caller: the
    /// number and the path sit in different places in the two languages, and only
    /// the resource should know where.
    /// </summary>
    [Test]
    public async Task PlaceholdersAreFilledInBothLanguages()
    {
        string english = I18NextMessages.Create("en")
            .Get("history.total", new { runs = 3, path = "/home/x/history.db" });
        string chinese = I18NextMessages.Create("zh")
            .Get("history.total", new { runs = 3, path = "/home/x/history.db" });

        await Assert.That(english).IsEqualTo("3 run(s) recorded at /home/x/history.db");
        await Assert.That(chinese).Contains("3");
        await Assert.That(chinese).Contains("/home/x/history.db");
        await Assert.That(chinese.Contains("{{", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// An untranslated key is the key, not an exception and not an empty line. A
    /// missing translation is a gap in the resource file, never a reason for a
    /// command to fail.
    /// </summary>
    [Test]
    public async Task AMissingKeyComesBackAsTheKey()
    {
        IMessages messages = I18NextMessages.Create("zh");

        await Assert.That(messages["history.doesNotExist"]).IsEqualTo("history.doesNotExist");
    }

    /// <summary>
    /// A language with no resources at all still has to produce sentences: the
    /// fallback is configured, not assumed.
    /// </summary>
    [Test]
    public async Task AnUnshippedLanguageFallsBackToEnglish()
    {
        IMessages messages = I18NextMessages.Create("fr");

        // The constructor normalises an unsupported tag to English, so the value
        // reported and the words spoken agree.
        await Assert.That(messages.Language).IsEqualTo(LanguageChoice.English);
        await Assert.That(messages["history.empty"]).IsEqualTo("No runs recorded yet.");
    }
}
