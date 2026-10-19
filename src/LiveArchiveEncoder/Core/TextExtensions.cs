using System.Diagnostics.CodeAnalysis;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// C# 14 extension members for the text handling this tool does everywhere.
///
/// An extension <em>block</em> (<c>extension(string? value)</c>) rather than the
/// classic <c>static ... this string?</c> methods, because the two members
/// below read as properties of the value instead of calls about it, and because
/// a block can carry members an extension method cannot.
///
/// Deliberately small: these two cover the "is this usable / fall back to
/// something" question that appears dozens of times across argument parsing,
/// config loading and FFmpeg discovery. Anything more specific belongs next to
/// the code that needs it.
/// </summary>
public static class TextExtensions
{
    // [NotNullWhen] on the receiver is what lets the compiler narrow the value
    // at every `if (!x.HasText) return;` -- the same flow analysis
    // `string.IsNullOrWhiteSpace` gets from the framework.
    extension([NotNullWhen(true)] string? value)
    {
        /// <summary>
        /// True when the text carries something other than whitespace.
        ///
        /// Named for what the caller wants to know, because the negated form
        /// (<c>!string.IsNullOrWhiteSpace(x)</c>) is the one that appears in
        /// almost every branch and reads badly.
        /// </summary>
        public bool HasText => !string.IsNullOrWhiteSpace(value);

        /// <summary>The text, or <paramref name="fallback"/> when it has none.</summary>
        public string Or(string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
