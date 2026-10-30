using System.Reflection;
using I18Next.Net;
using I18Next.Net.Backends;
using I18Next.Net.Plugins;
using Lae.Core.Localization;

namespace Lae.Media.Localization;

/// <summary>
/// Translations from resources embedded in this assembly, through I18Next.Net.
///
/// <para><b>Embedded, not a folder next to the binary.</b> v8 publishes as a
/// self-contained single file, so a loose <c>locales/</c> folder would be one more
/// thing to ship, copy and get wrong -- and it would be the first thing missing
/// from a user's download. The JSON lives in the assembly and is read through the
/// library's own delegate backend.</para>
///
/// <para><b>i18next, not a hand-rolled dictionary.</b> The keys are nested
/// (<c>history.empty</c>), the placeholders need interpolation, and Chinese needs no
/// plural forms while English does -- all three are exactly what the library is for.
/// Rolling that by hand is how a project ends up with <c>"{0} run(s)"</c> in one
/// place and a different convention in the next.</para>
///
/// <para>Falling back to English is configured rather than assumed: a key present
/// in <c>zh</c> but missing in <c>en</c> is the rarer case, but a partly translated
/// locale is the normal one.</para>
/// </summary>
public sealed class I18NextMessages : IMessages
{
    private static readonly Assembly Own = typeof(I18NextMessages).Assembly;

    private readonly I18NextNet _i18n;

    private I18NextMessages(I18NextNet i18n, string language)
    {
        _i18n = i18n;
        Language = language;
    }

    public string Language { get; }

    public string this[string key] => _i18n.T(key);

    public string Get(string key, object? values = null)
        => values is null ? _i18n.T(key) : _i18n.T(key, values);

    /// <summary>
    /// Builds the translator for one language. Cheap enough to do per command: the
    /// resources are a few kilobytes and only the namespaces actually asked for are
    /// parsed.
    /// </summary>
    public static I18NextMessages Create(string? language)
    {
        string chosen = LanguageChoice.Normalise(language) ?? LanguageChoice.English;

        var backend = FuncBackend.FromStream((lng, ns) => Open(lng, ns));
        var i18n = new I18NextNet(backend, new DefaultTranslator(backend))
        {
            Language = chosen,
            DefaultNamespace = "translation",
        };

        // A language we do not ship, or a key only translated in one place, still
        // has to produce a sentence rather than a key name.
        i18n.SetFallbackLanguages(LanguageChoice.English);

        return new I18NextMessages(i18n, chosen);
    }

    /// <summary>
    /// <c>lae.locales.zh.translation.json</c> inside this assembly. Null for a
    /// missing language, which the backend reads as "no such namespace" and the
    /// fallback then covers.
    /// </summary>
    private static Stream? Open(string language, string @namespace)
        => Own.GetManifestResourceStream($"lae.locales.{language}.{@namespace}.json");
}
