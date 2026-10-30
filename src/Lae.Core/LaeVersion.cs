namespace Lae.Core;

/// <summary>
/// The one place the version number lives.
///
/// <para>v7 had the same idea in Configuration/Constants.cs, but the release
/// notes and the update checker each ended up with their own copy of the string.
/// v8 keeps exactly one: the self-updater, the <c>--version</c> output and the
/// release tag all read this constant.</para>
///
/// <para>Anything after <c>-</c> is a prerelease tag; <see cref="IsPrerelease"/>
/// is what the update checker uses to decide whether a published build is newer.
/// </para>
/// </summary>
public static class LaeVersion
{
    /// <summary>The build's own version, without a leading "v".</summary>
    public const string Current = "8.0.0-m2-canary";

    /// <summary>True while this is a milestone build rather than a release.</summary>
    public static bool IsPrerelease => Current.Contains('-', StringComparison.Ordinal);
}
