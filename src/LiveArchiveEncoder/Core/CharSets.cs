using System.Buffers;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// v7.9.9: cached <see cref="SearchValues{T}"/> sets (.NET 8+). Building the
/// vectorised search table once and reusing it on a span is cheaper than
/// allocating a char[] at every <c>IndexOfAny</c> call, which used to happen in
/// URL parsing and version comparison on every FFmpeg argument build.
/// </summary>
public static class CharSets
{
    /// <summary>Where a URL's query or fragment starts.</summary>
    public static readonly SearchValues<char> UrlQuery = SearchValues.Create(['?', '#']);

    /// <summary>Where a version string's pre-release / build suffix starts.</summary>
    public static readonly SearchValues<char> VersionSuffix = SearchValues.Create(['-', '+', ' ']);

    /// <summary>CR/LF, for trimming a captured line.</summary>
    public static readonly SearchValues<char> NewLine = SearchValues.Create(['\r', '\n']);
}
