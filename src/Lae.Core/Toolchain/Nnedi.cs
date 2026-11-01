namespace Lae.Core.Toolchain;

/// <summary>
/// The NNEDI3 weights FFmpeg's <c>nnedi</c> filter loads by path.
///
/// <para>Not shipped with the program: the file is 13 MB, it is not this
/// project's to redistribute, and upstream revises it with the plugin. FFmpeg's
/// own documentation points at <c>sekrit-twc/znedi3</c>, which is the canonical
/// home of <c>nnedi3_weights.bin</c>. The filter reads fixed offsets out of the
/// blob and refuses to initialise without it, so the file has to be on disk
/// before an encode rather than during one.</para>
///
/// <para>Same layout rule as the FFmpeg installs: under the settings folder, not
/// next to the executable -- v8 publishes as a self-contained single file that
/// unpacks into a temporary location, so anything placed beside it is gone by the
/// next run. A folder of its own means "remove the weights" is one delete.</para>
/// </summary>
public static class Nnedi
{
    public const string Repo = "sekrit-twc/znedi3";

    public const string WeightsName = "nnedi3_weights.bin";

    /// <summary>
    /// The raw file on the default branch. There is no release asset to pin: the
    /// weights are a single versioned blob, not a build product.
    /// </summary>
    public const string Source = "https://raw.githubusercontent.com/" + Repo + "/master/" + WeightsName;

    /// <summary>
    /// Smallest plausible weights file. A block page, an HTML interstitial and a
    /// truncated transfer are all orders of magnitude smaller, and telling them
    /// apart by size is the only check that works without a hash from upstream.
    /// </summary>
    public const long MinimumBytes = 1L << 20;

    /// <summary>The folder under the settings root that holds the weights.</summary>
    public const string FolderName = "nnedi";

    public static string Root(string configRoot) => Path.Combine(configRoot, FolderName);

    /// <summary>Where the weights go, whether or not they are there.</summary>
    public static string Weights(string configRoot) => Path.Combine(Root(configRoot), WeightsName);

    /// <summary>Where the download accumulates, so an interrupted run can resume by rename.</summary>
    public static string Partial(string configRoot) => Path.Combine(Root(configRoot), "." + WeightsName + ".part");

    /// <summary>What the installed copy is, so "already current" needs no hash of 13 MB.</summary>
    public static string Record(string configRoot) => Path.Combine(Root(configRoot), "install.json");
}
