using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.Update;

/// <summary>
/// A release tag seen as a number: <c>v7.9.8.5</c> becomes <c>[7,9,8,5]</c>.
///
/// <para>Why not <see cref="Version"/>: a release tag is not a
/// <see cref="Version"/>. <c>7.9</c> parses, <c>v7.9.8.5</c> does not, and a tag
/// with four components would be rejected outright -- while this project has
/// shipped four-component tags since v7.9.8.1. Missing components compare as
/// zero, so <c>7.9.8</c> and <c>7.9.8.0</c> are the same version, and
/// <c>7.9.8.5</c> is newer than both.</para>
///
/// <para>A pre-release suffix (<c>-rc1</c>, <c>+build</c>) is dropped rather
/// than ordered: releases here are cut from a linear tag and a suffix has never
/// been used, so pretending to understand SemVer precedence would be more
/// machinery than the problem has.</para>
/// </summary>
public sealed class VersionTag : IComparable<VersionTag>, IEquatable<VersionTag>
{
    /// <summary>A version that compares below every real one.</summary>
    public static readonly VersionTag Zero = new([]);

    private readonly int[] _parts;

    private VersionTag(int[] parts) => _parts = parts;

    /// <summary>Null for anything that is not a dotted number, so a bad feed cannot be read as "newest".</summary>
    public static VersionTag? Parse(string? text)
    {
        if (!text.HasText) return null;

        string work = text.Trim();
        if (work.Length > 0 && (work[0] == 'v' || work[0] == 'V')) work = work[1..];

        int cut = work.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) work = work[..cut];
        if (work.Length == 0) return null;

        string[] fields = work.Split('.');
        var parts = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            string field = fields[i].Trim();
            if (field.Length == 0) return null;
            if (!int.TryParse(field, out int value) || value < 0) return null;
            parts[i] = value;
        }
        return new VersionTag(parts);
    }

    public int CompareTo(VersionTag? other)
    {
        if (other is null) return 1;
        int length = Math.Max(_parts.Length, other._parts.Length);
        for (int i = 0; i < length; i++)
        {
            int mine = i < _parts.Length ? _parts[i] : 0;
            int theirs = i < other._parts.Length ? other._parts[i] : 0;
            if (mine != theirs) return mine < theirs ? -1 : 1;
        }
        return 0;
    }

    /// <summary>True when this tag names a build later than <paramref name="other"/>.</summary>
    public bool IsNewerThan(VersionTag other) => CompareTo(other) > 0;

    public bool Equals(VersionTag? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is VersionTag tag && Equals(tag);

    public override int GetHashCode()
    {
        int hash = 17;
        // Trailing zeros are not significant (7.9.8 == 7.9.8.0), so they must not
        // reach the hash either.
        int last = _parts.Length - 1;
        while (last >= 0 && _parts[last] == 0) last--;
        for (int i = 0; i <= last; i++) hash = unchecked(hash * 31 + _parts[i]);
        return hash;
    }

    public override string ToString() => string.Join('.', _parts);

    // Null sorts lowest, which is the same thing CompareTo(null) says. Leaving
    // these out is a CA1036 warning, and the warning is right: a type that
    // answers "which is bigger" should also answer "is this bigger".
    public static bool operator ==(VersionTag? left, VersionTag? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(VersionTag? left, VersionTag? right) => !(left == right);

    public static bool operator <(VersionTag? left, VersionTag? right) => Rank(left, right) < 0;

    public static bool operator >(VersionTag? left, VersionTag? right) => Rank(left, right) > 0;

    public static bool operator <=(VersionTag? left, VersionTag? right) => Rank(left, right) <= 0;

    public static bool operator >=(VersionTag? left, VersionTag? right) => Rank(left, right) >= 0;

    private static int Rank(VersionTag? left, VersionTag? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);
}
