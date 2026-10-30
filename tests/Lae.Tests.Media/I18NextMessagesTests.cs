using System.Text.Json;
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
        await Assert.That(messages["history.limitWarn"]).Contains("正整数");
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
            .Format("history.total", new { runs = 3, path = "/home/x/history.db" });
        string chinese = I18NextMessages.Create("zh")
            .Format("history.total", new { runs = 3, path = "/home/x/history.db" });

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

    /// <summary>
    /// Both resource files carry the same keys.
    ///
    /// <para>This is the failure the fallback mechanism hides: a key added to
    /// <c>en</c> and forgotten in <c>zh</c> does not throw, does not print a key
    /// name, and does not look broken -- it prints an English sentence in the
    /// middle of a Chinese one. Nobody notices that in review, and nobody notices
    /// it in a test that only checks the keys it remembers.</para>
    /// </summary>
    [Test]
    public async Task BothLocalesCarryTheSameKeys()
    {
        HashSet<string> english = Keys("en");
        HashSet<string> chinese = Keys("zh");

        List<string> missingInChinese = [.. english.Except(chinese).Order(StringComparer.Ordinal)];
        List<string> missingInEnglish = [.. chinese.Except(english).Order(StringComparer.Ordinal)];

        await Assert.That(missingInChinese.Count).IsEqualTo(0)
            .Because("zh is missing: " + string.Join(", ", missingInChinese));
        await Assert.That(missingInEnglish.Count).IsEqualTo(0)
            .Because("en is missing: " + string.Join(", ", missingInEnglish));
        await Assert.That(english.Count).IsGreaterThan(50);
    }

    /// <summary>
    /// Every dotted path that ends in a string, which is what a translator sees as
    /// one row. Objects are walked; anything else at a leaf is a structural
    /// mistake and is reported as the leaf it is.
    /// </summary>
    private static HashSet<string> Keys(string language)
    {
        Stream stream = typeof(I18NextMessages).Assembly
            .GetManifestResourceStream($"lae.locales.{language}.translation.json")
            ?? throw new InvalidOperationException($"no embedded resources for '{language}'");

        using var document = JsonDocument.Parse(stream);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        Walk(document.RootElement, "", keys);
        return keys;
    }

    private static void Walk(JsonElement element, string prefix, HashSet<string> keys)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";

            if (property.Value.ValueKind == JsonValueKind.Object) Walk(property.Value, path, keys);
            else keys.Add(path);
        }
    }
}
