namespace Lae.Core.Plugins;

/// <summary>
/// The optional <c>plugin.json</c> that sits beside a plugin and says how to run it.
///
/// <para>Optional because the common case needs nothing: a folder named after the
/// plugin containing an executable of that name is self-describing. The manifest
/// exists for the cases where it is not -- a Python module, a JVM jar, anything
/// whose launcher is not the plugin's own name.</para>
///
/// <para>Only the command is read here. Identity, version and the stage list come
/// from the handshake, which is the one source that cannot go stale: a manifest can
/// be edited without touching the program it describes, and then two files disagree
/// about what the plugin does.</para>
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Ignored on read: the folder name is the id discovery already matched.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// Program and arguments: <c>["python3", "-m", "lae_damage"]</c>. A single
    /// element is a plain executable.
    /// </summary>
    public IReadOnlyList<string>? Command { get; init; }
}
