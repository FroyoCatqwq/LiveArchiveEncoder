namespace Lae.Core.Localization;

/// <summary>
/// The strings a person reads.
///
/// <para>An interface here rather than a call into the translation library, for the
/// same reason <c>ISettingsStore</c> is here: reading a translation file is I/O and
/// this assembly does not do I/O. It also means the decision "what language is
/// this run in" is testable without loading a single resource.</para>
///
/// <para><b>Keys, not sentences.</b> A key such as <c>history.empty</c> is stable
/// and greppable; an English sentence as a key breaks the moment someone fixes a
/// typo in it, and the resource file is the only place a sentence should live.</para>
/// </summary>
public interface IMessages
{
    /// <summary>The locale actually in use, e.g. <c>en</c> or <c>zh</c>.</summary>
    string Language { get; }

    /// <summary>The text for a key, or the key itself when it is not translated.</summary>
    string this[string key] { get; }

    /// <summary>
    /// The text for a key, with <c>{{name}}</c> placeholders filled from
    /// <paramref name="values"/> (an anonymous object).
    /// </summary>
    string Get(string key, object? values = null);
}

/// <summary>
/// Echoes the key back. What a build with no resources loaded shows, and what the
/// unit tests use when the wording is not the subject.
///
/// <para>Echoing rather than throwing is deliberate: a missing translation must
/// never be the reason a command fails.</para>
/// </summary>
public sealed class KeyMessages(string language = "en") : IMessages
{
    public static KeyMessages English { get; } = new();

    public string Language => language;

    public string this[string key] => key;

    public string Get(string key, object? values = null) => key;
}

/// <summary>
/// Which language a run speaks, decided in one place.
///
/// <para>Order: <c>LAE_LANG</c>, then <c>ui.language</c> from the settings, then
/// English. The environment variable is what makes a regression test possible --
/// the alternative is writing a settings file into the developer's own profile,
/// which is the mistake v7's end-to-end suite spent a release recovering from.</para>
/// </summary>
public static class LanguageChoice
{
    public const string English = "en";
    public const string Chinese = "zh";

    /// <summary>The languages this build actually has resources for.</summary>
    public static IReadOnlyList<string> Supported { get; } = [English, Chinese];

    /// <summary>
    /// Normalises <c>zh-CN</c>, <c>zh_CN</c>, <c>ZH</c> and friends to the tag this
    /// build has resources for. Anything unknown becomes English -- a config value
    /// someone typed by hand must not leave the program speechless.
    /// </summary>
    public static string Pick(string? fromEnvironment, string? fromSettings)
    {
        foreach (string? candidate in new[] { fromEnvironment, fromSettings })
        {
            if (Normalise(candidate) is { } language) return language;
        }

        return English;
    }

    /// <summary>Null when the value is absent or not a language we ship.</summary>
    public static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string head = value.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return Supported.Contains(head) ? head : null;
    }
}
