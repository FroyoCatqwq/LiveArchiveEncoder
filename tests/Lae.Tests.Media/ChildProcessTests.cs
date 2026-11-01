using Lae.Media;

namespace Lae.Tests.Media;

/// <summary>
/// The one place the program starts a process, and the two questions every
/// caller asks it: is it still running, and how did it end.
///
/// <para>These use real children, because the thing being tested is the
/// operating system's answer. A fake would only prove that the fake works.</para>
/// </summary>
public sealed class ChildProcessTests
{
    /// <summary>A child that outlives the assertions below, on either platform.</summary>
    private static IReadOnlyList<string> Sleeper() =>
        OperatingSystem.IsWindows()
            ? ["cmd", "/c", "ping", "-n", "4", "127.0.0.1"]
            : ["/bin/sh", "-c", "sleep 3"];

    /// <summary>
    /// While the child is alive the code answers -1 instead of throwing.
    ///
    /// <para>This used to be <c>Process.ExitCode</c> behind a <c>Refresh()</c>,
    /// which throws <c>InvalidOperationException</c> when the process has not
    /// exited -- so every caller had to ask <see cref="ChildProcess.HasExited"/>
    /// first, and nothing enforced it.</para>
    /// </summary>
    [Test]
    public async Task ARunningChildAnswersMinusOneInsteadOfThrowing()
    {
        using ChildProcess child = ChildProcess.Start(Sleeper(), new ChildSpec());

        await Assert.That(child.HasExited).IsFalse();
        await Assert.That(child.ExitCode).IsEqualTo(-1);
    }

    /// <summary>Once the child is gone the real code is there, and it stays there.</summary>
    [Test]
    public async Task TheExitCodeIsRememberedAfterTheChildIsGone()
    {
        IReadOnlyList<string> command = OperatingSystem.IsWindows()
            ? ["cmd", "/c", "exit", "3"]
            : ["/bin/sh", "-c", "exit 3"];

        using ChildProcess child = ChildProcess.Start(command, new ChildSpec());
        child.WaitOrCancel(TimeSpan.FromSeconds(20), "the child did not exit", CancellationToken.None);

        await Assert.That(child.HasExited).IsTrue();
        await Assert.That(child.ExitCode).IsEqualTo(3);

        // Repeatable, and cheap: the second read must not re-ask a process that
        // may already be disposed, and must not change its mind.
        await Assert.That(child.ExitCode).IsEqualTo(3);
    }

    /// <summary>
    /// Disposing a running child is a kill, not a wait, and the tap behind it no
    /// longer blocks on the way out.
    ///
    /// <para>That wait -- up to two seconds in <c>PipeLines.Dispose</c> -- was the
    /// textbook sync-over-async shape: tolerable in a console program, a deadlock
    /// in any host with a synchronization context. What this test can assert is the
    /// part that is observable: shutting down a child that is still talking is
    /// quiet, and doing it twice is not an error.</para>
    /// </summary>
    [Test]
    public async Task DisposingARunningChildIsQuietAndIdempotent()
    {
        ChildProcess child = ChildProcess.Start(Sleeper(), new ChildSpec());

        child.Dispose();

        // The kill is part of disposal, and the state is still readable afterwards
        // instead of throwing from a disposed handle.
        await Assert.That(child.HasExited).IsTrue();

        child.Dispose();   // a second call is a no-op, not a throw
    }
}
