namespace Lae.App;

/// <summary>
/// Process exit codes, defined once.
///
/// <para>This is the fix for a v7 wart: <c>Program.cs</c> had 48 bare
/// <c>return</c> statements, and the meaning of "3" (an update is available)
/// existed only in a comment. Scripts depend on these numbers, so they are a
/// public contract — do not renumber.</para>
///
/// <para><see cref="UpdateAvailable"/> is 3 rather than 1 on purpose: "there is
/// a newer build" is not an error, and a wrapper script has to be able to tell
/// it apart from a failure without parsing stdout.</para>
/// </summary>
public static class ExitCodes
{
    /// <summary>Work finished.</summary>
    public const int Ok = 0;

    /// <summary>Work started and failed.</summary>
    public const int Failed = 1;

    /// <summary>The command line itself was wrong. Nothing was attempted.</summary>
    public const int Usage = 2;

    /// <summary><c>update --check</c> found a newer build.</summary>
    public const int UpdateAvailable = 3;

    /// <summary>A plugin failed to start, exited non-zero, or broke the protocol.</summary>
    public const int PluginError = 4;

    /// <summary>A produced file failed verification; it was not promoted.</summary>
    public const int VerificationFailed = 5;

    /// <summary>Ctrl-C or an equivalent cancellation.</summary>
    public const int Interrupted = 130;
}
