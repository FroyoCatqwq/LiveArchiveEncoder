using Lae.Core.Plugins;
using Lae.Plugins;

namespace Lae.App;

/// <summary>
/// Assembles the plugin host for one run.
///
/// <para>The one place that knows all three pieces -- the finder, the verifier and
/// the host -- and the one place that decides a machine cannot run plugins at all.
/// Kept out of <see cref="EncodeDependencies"/>' default so a test that never
/// mentions plugins never looks for one on disk.</para>
/// </summary>
internal static class PluginRuntime
{
    /// <summary>
    /// The host, or null when this machine cannot verify a plugin's product.
    ///
    /// <para>Null without an ffprobe is deliberate. Two of the three product checks
    /// need to read the file back, and a host that skipped them would turn "the
    /// plugin exited zero" into "the product is good" -- which is the exact
    /// assumption the verification layer exists to refuse. Better to say plugins are
    /// unavailable on this machine than to run them unverified.</para>
    /// </summary>
    public static IPluginRunner? Create(PluginSetup setup)
    {
        if (setup.Ffprobe is not { Length: > 0 } ffprobe) return null;

        var search = new PluginSearch
        {
            // With no settings folder there is no user install, so the portable
            // layout beside the program is the only place left to look. It is also
            // the one that ships with the program, which makes it the right guess.
            ConfigRoot = setup.ConfigRoot ?? AppContext.BaseDirectory,
            Explicit = setup.Explicit,
        };

        return new PluginHost(
            new PluginFinder(search),
            setup.Settings.Plugins,
            new PluginVerifier(ffprobe, setup.ProbeSource, setup.Messages),
            setup.Messages,
            setup.Warn);
    }
}
