using Lae.Core.Localization;
using Lae.Media.Localization;

namespace Lae.Tests.Encoding;

/// <summary>
/// The real English strings, for the tests that assert what a user reads. See the
/// note on <c>Lae.Tests.Core.TestMessages</c>: the sentences come from the shipped
/// resource file, not from a copy.
/// </summary>
internal static class TestMessages
{
    internal static IMessages En { get; } = I18NextMessages.Create("en");
}
