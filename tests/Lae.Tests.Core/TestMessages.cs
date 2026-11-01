using Lae.Core.Localization;
using Lae.Media.Localization;

namespace Lae.Tests.Core;

/// <summary>
/// The real English strings, for the tests that assert what a user reads.
///
/// <para>From the shipped resource file rather than a stub that echoes keys, and
/// rather than a copy of the sentences: a copy would drift, and a test asserting
/// wording the program no longer produces is worse than no test. Imported into
/// each test file with <c>using static</c>, so a call site reads
/// <c>EncodePlan.Create(request, source, encoder, En)</c>.</para>
/// </summary>
internal static class TestMessages
{
    internal static IMessages En { get; } = I18NextMessages.Create("en");
}
