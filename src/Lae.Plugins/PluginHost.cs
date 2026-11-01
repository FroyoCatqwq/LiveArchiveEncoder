using System.Diagnostics;
using System.Globalization;
using Lae.Core;
using Lae.Core.Localization;
using Lae.Core.Plugins;
using Lae.Core.Settings;
using Lae.Media;

namespace Lae.Plugins;

/// <summary>
/// Runs plugin hops: find the executable, ask it what it is, call it, check what it
/// produced.
///
/// <para>Everything the contract says about a misbehaving plugin lands here as an
/// outcome, never as an exception. A plugin is someone else's program reached
/// through a pipe: it can be missing, hang, print nothing, print the wrong thing,
/// exit 2 because the host built a bad command line, or exit 0 having written a
/// truncated file. All eight are messages; none of them is a stack trace.</para>
///
/// <para>Handshakes are cached for the life of this object. One process may run
/// several encodes (<c>repl</c>, and any future menu), and asking a plugin to
/// describe itself before every hop would make a three-hop chain pay three
/// startups for an answer that cannot have changed.</para>
/// </summary>
public sealed class PluginHost : IPluginRunner
{
    /// <summary>
    /// How long a plugin gets to describe itself.
    ///
    /// <para>Ten seconds: long enough for a script that imports a library, short
    /// enough that a wedged plugin does not look like a hang. The contract's number,
    /// and part of the protocol rather than a tuning knob.</para>
    /// </summary>
    public const int HandshakeTimeoutSeconds = 10;

    private readonly PluginFinder _finder;
    private readonly IReadOnlyList<PluginRegistration> _registrations;
    private readonly PluginVerifier _verifier;
    private readonly IMessages _messages;
    private readonly Action<string>? _warn;
    private readonly TimeSpan _handshakeTimeout;

    private readonly Dictionary<string, HandshakeVerdict> _handshakes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public PluginHost(
        PluginFinder finder,
        IEnumerable<PluginRegistration>? registrations,
        PluginVerifier verifier,
        IMessages messages,
        Action<string>? warn = null,
        TimeSpan? handshakeTimeout = null)
    {
        _finder = finder;
        _registrations = registrations is null ? [] : [.. registrations];
        _verifier = verifier;
        _messages = messages;
        _warn = warn;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(HandshakeTimeoutSeconds);
    }

    public PluginStepOutcome Run(PluginCall request, CancellationToken token)
    {
        string id = request.Plugin;

        if (Registration(id) is { Enabled: false })
            return PluginStepOutcome.Reject(_messages.Format("plugin.error.disabled", new { id }));

        PluginLookup lookup = _finder.Find(id, Registration(id));

        if (lookup.Error is { } discoveryError) return PluginStepOutcome.Reject(discoveryError);
        if (lookup.Plugin is not { } plugin)
            return PluginStepOutcome.Reject(_messages.Format("plugin.error.notFound", new { id }));

        HandshakeVerdict verdict = Handshake(plugin, token);

        if (!verdict.Ok)
        {
            return PluginStepOutcome.Reject(
                _messages.Format("plugin.error.handshake", new
                {
                    id,
                    detail = string.Join("; ", verdict.Problems),
                }),
                [.. verdict.Warnings]);
        }

        PluginHandshake handshake = verdict.Handshake!;

        PluginStageDeclaration? stage = handshake.Stages
            .FirstOrDefault(s => string.Equals(s.Id, request.Stage, StringComparison.Ordinal));

        if (stage is null)
        {
            return PluginStepOutcome.Reject(_messages.Format("plugin.error.noSuchStage", new
            {
                id,
                stage = string.IsNullOrWhiteSpace(request.Stage) ? "-" : request.Stage,
                known = string.Join(", ", handshake.Stages.Select(s => s.Id)),
            }));
        }

        ParamResolution parameters = PluginParams.Resolve(stage, request.Parameters, _messages);

        if (!parameters.Ok)
            return PluginStepOutcome.Reject(string.Join("; ", parameters.Problems), [.. verdict.Warnings]);

        return Invoke(plugin, request, parameters, verdict.Warnings, token);
    }

    private PluginRegistration? Registration(string id)
        => _registrations.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Call the plugin and decide what its answer means.
    ///
    /// <para>The order matters: run it, look at the exit code, and only then look at
    /// the product. Checking the product of a plugin that exited non-zero would
    /// report "the part is missing" about a plugin that already said why it failed.</para>
    /// </summary>
    private PluginStepOutcome Invoke(
        FoundPlugin plugin, PluginCall request, ParamResolution parameters,
        IReadOnlyList<string> warnings, CancellationToken token)
    {
        List<string> command = Command(plugin, request, parameters);

        ChildProcess child;
        try
        {
            child = ChildProcess.Start(command, new ChildSpec
            {
                // The plugin's own folder, not a throwaway one. A manifest may name
                // a relative script -- ["python3", "run.py"] -- and that name is a
                // path only from where the manifest was read. The scratch space the
                // contract talks about is the plugin's business; what it cannot be
                // given is a working directory that makes its own body unfindable.
                WorkingDirectory = plugin.Folder,

                // Forwarded, not swallowed: a plugin's standard error is how it
                // reports progress and warnings, and FFmpeg taught this program the
                // same lesson. Prefixed so a line from a plugin is never mistaken
                // for one from the encoder.
                OnErrorLine = line => _warn?.Invoke(
                    _messages.Format("plugin.log.prefix", new { id = plugin.Id, line })),
            });
        }
        catch (Exception exc) when (exc is InvalidOperationException
                                        or System.ComponentModel.Win32Exception
                                        or IOException
                                        or UnauthorizedAccessException)
        {
            return PluginStepOutcome.Reject(
                _messages.Format("plugin.error.cannotStart", new { id = plugin.Id, detail = exc.Message }),
                [.. warnings]);
        }

        using (child)
        {
                try
                {
                    child.WaitOrCancel(request.Budget,
                        _messages.Format("plugin.error.timeout", new
                        {
                            id = plugin.Id,
                            seconds = Math.Round(request.Budget.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                        }),
                        token);
                }
                catch (OperationCanceledException)
                {
                    // Already killed by WaitOrCancel; the runner discards the part and
                    // the interrupt travels up as exit 130.
                    throw;
                }
                catch (InvalidOperationException exc)
                {
                    return PluginStepOutcome.Reject(exc.Message, [.. warnings]);
                }

                int code = child.ExitCode;
                string diagnostics = child.Diagnostics();

                // 2 first, because it is the one exit code that is about this
                // program rather than the plugin. Reporting it as "the plugin
                // failed" starts the search in the wrong codebase.
                if (code == 2)
                {
                    return PluginStepOutcome.Reject(
                        _messages.Format("plugin.error.usageExit", new
                        {
                            id = plugin.Id,
                            detail = diagnostics.Length > 0 ? diagnostics : "-",
                        }),
                        [.. warnings]);
                }

                if (code != 0)
                {
                    return PluginStepOutcome.Reject(
                        _messages.Format("plugin.error.exitCode", new
                        {
                            id = plugin.Id,
                            code,
                            detail = diagnostics.Length > 0 ? diagnostics : "-",
                        }),
                        [.. warnings]);
                }

                string? unverified = _verifier.Check(plugin.Id, request.InputPath, request.PartPath, token);

                return unverified is not null
                    ? PluginStepOutcome.Unverified(unverified)
                    : PluginStepOutcome.Ok([.. warnings]);
        }
    }

    /// <summary>
    /// The command line, from the contract's shape.
    ///
    /// <para>Every value is one argument. Paths go through
    /// <c>ProcessStartInfo.ArgumentList</c>, which means a recording called
    /// <c>閹存垹娈?瑜版洖鍎?avi</c> needs no quoting and no shell is involved at any point --
    /// which is also why a plugin cannot be asked to parse a string.</para>
    ///
    /// <para>Parameters are sorted. A dictionary's order is not a promise, and an
    /// order that changes between two identical runs would make a plugin's own
    /// logging look non-deterministic.</para>
    /// </summary>
    private static List<string> Command(FoundPlugin plugin, PluginCall request, ParamResolution parameters)
    {
        List<string> command =
        [
            .. plugin.Command,
            PluginProtocol.RunCommand,
            PluginProtocol.StageOption, request.Stage,
            PluginProtocol.InputOption, request.InputPath,
            PluginProtocol.OutputOption, request.PartPath,
        ];

        foreach ((string key, string value) in parameters.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            command.Add(PluginProtocol.ParamOption);
            command.Add($"{key}={value}");
        }

        return command;
    }

    private HandshakeVerdict Handshake(FoundPlugin plugin, CancellationToken token)
    {
        lock (_gate)
        {
            if (_handshakes.TryGetValue(plugin.Id, out HandshakeVerdict? cached)) return cached;
        }

        HandshakeVerdict verdict = AskForHandshake(plugin, token);

        // Failures are cached too. A plugin that refused once will refuse again in
        // the same run, and asking a second time costs another ten-second timeout on
        // a chain that is already going to stop.
        lock (_gate) _handshakes[plugin.Id] = verdict;

        return verdict;
    }

    private HandshakeVerdict AskForHandshake(FoundPlugin plugin, CancellationToken token)
    {
        TimeSpan budget = _handshakeTimeout;
        var clock = Stopwatch.StartNew();

        ChildProcess child;
        try
        {
            child = ChildProcess.Start(
                [.. plugin.Command, PluginProtocol.HandshakeFlag],
                new ChildSpec { ReadOutput = true, WorkingDirectory = plugin.Folder });
        }
        catch (Exception exc) when (exc is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Failed(_messages.Format("plugin.error.cannotStart", new { id = plugin.Id, detail = exc.Message }));
        }

        using (child)
        {
            string line = "";

            if (child.Output is { } standardOutput)
            {
                try
                {
                    using var reader = new StreamReader(standardOutput);

                    // One line, with a deadline. Waiting on the child instead would
                    // deadlock on a plugin that prints its handshake and then keeps
                    // running, which is exactly what a badly written one does.
                    Task<string?> read = reader.ReadLineAsync(CancellationToken.None).AsTask();

                    if (!read.Wait(budget))
                    {
                        child.KillTree();
                        Forget(read);
                        return Failed(_messages.Format("plugin.error.handshakeTimeout", new
                        {
                            id = plugin.Id,
                            seconds = Math.Round(budget.TotalSeconds, 1),
                        }));
                    }

                    line = read.IsCompletedSuccessfully ? read.Result ?? "" : "";
                }
                catch (Exception exc) when (exc is IOException or ObjectDisposedException)
                {
                    // The pipes closed under us. The exit code below still has
                    // something to say, and an empty line gets its own sentence.
                }
            }

            TimeSpan left = budget - clock.Elapsed;

            if (left <= TimeSpan.Zero)
            {
                child.KillTree();
                return Failed(_messages.Format("plugin.error.handshakeTimeout", new
                {
                    id = plugin.Id,
                    seconds = Math.Round(budget.TotalSeconds, 1),
                }));
            }

            try
            {
                child.WaitOrCancel(left, _messages.Format("plugin.error.handshakeTimeout", new
                {
                    id = plugin.Id,
                    seconds = Math.Round(budget.TotalSeconds, 1),
                }), token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                return Failed(_messages.Format("plugin.error.handshakeTimeout", new
                {
                    id = plugin.Id,
                    seconds = Math.Round(budget.TotalSeconds, 1),
                }));
            }

            if (child.ExitCode != 0)
            {
                string diagnostics = child.Diagnostics();
                return Failed(_messages.Format("plugin.error.handshakeExit", new
                {
                    id = plugin.Id,
                    code = child.ExitCode,
                    detail = diagnostics.Length > 0 ? diagnostics : "-",
                }));
            }

            return HandshakeReader.Read(line, plugin.Id, _messages);
        }
    }

    /// <summary>
    /// Stop caring about a read that was abandoned.
    ///
    /// <para>Killing the child closes the pipe, so the pending read faults a moment
    /// later with nobody waiting for it. An unobserved task exception is not fatal,
    /// but it surfaces at an unrelated moment as a first-chance exception in a debugger
    /// and sends the next person looking in the wrong place.</para>
    /// </summary>
    private static void Forget(Task<string?> read)
        => _ = read.ContinueWith(
            finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static HandshakeVerdict Failed(string problem) => new() { Problems = [problem] };
}
