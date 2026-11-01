using System.CommandLine;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Localization;
using Lae.Core.Settings;
using Lae.Core.Toolchain;
using Lae.Media.Toolchain;

namespace Lae.App.Commands;

/// <summary>
/// <c>lae toolchain</c> — install, list, remove and choose the FFmpeg this program
/// uses.
///
/// <para>This is the command <c>lae setup</c> has been telling people to run since
/// v8 started, and did not exist. A tool that needs FFmpeg to do anything, and
/// then makes the user go and find a build, unpack it, and hand-edit a path, has a
/// first-run experience made of other people's documentation.</para>
///
/// <para>It is deliberately <b>not</b> part of <c>setup</c>. <c>setup</c> reports
/// and touches nothing: a diagnostic that downloads 80 MB is not a diagnostic, and
/// a command that silently starts a network transfer fails at the worst possible
/// moment. <c>install</c> says what it is going to do before doing it.</para>
///
/// <para>The installs live beside the settings, each distribution in its own
/// folder, so switching is instant and a bad download cannot damage a working one.
/// Nothing is placed on <c>PATH</c> and no environment variable is changed: the
/// program finds its own copy, and leaves the machine as it found it.</para>
/// </summary>
public static class ToolchainCommand
{
    public static Command Build(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var command = new Command("toolchain", messages["toolchain.desc"]);
        command.Subcommands.Add(ListCommand(store, terminal, messages));
        command.Subcommands.Add(InstallCommand(store, terminal, messages));
        command.Subcommands.Add(RemoveCommand(store, terminal, messages));
        command.Subcommands.Add(UseCommand(store, terminal, messages));
        command.Subcommands.Add(PathCommand(store, terminal, messages));
        command.Subcommands.Add(NnediCommand(store, terminal, messages));
        return command;
    }

    /// <summary>
    /// The NNEDI3 weights that the <c>nnedi</c> deinterlacer loads by path.
    ///
    /// <para>A subcommand of its own rather than an id under <c>install</c>: the
    /// weights are not a distribution, they carry no executables, and listing them
    /// beside builds that can encode would suggest a choice between them. They are
    /// also the one thing this command fetches that v7 could fetch and v8 could
    /// not, which made <c>deinterlace = "nnedi"</c> a setting that only ever
    /// produced a refusal.</para>
    /// </summary>
    private static Command NnediCommand(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var proxyOption = new Option<string?>("--proxy")
        {
            Description = messages["toolchain.install.opt.proxy"],
        };

        var frontOption = new Option<string?>("--front")
        {
            Description = messages["toolchain.install.opt.front"],
        };

        var noFrontOption = new Option<bool>("--no-front")
        {
            Description = messages["toolchain.install.opt.noFront"],
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = messages["toolchain.nnedi.opt.force"],
        };

        var removeOption = new Option<bool>("--remove")
        {
            Description = messages["toolchain.nnedi.opt.remove"],
        };

        var command = new Command("nnedi", messages["toolchain.nnedi.desc"])
        {
            proxyOption,
            frontOption,
            noFrontOption,
            forceOption,
            removeOption,
        };

        command.SetAction((ParseResult parseResult) =>
        {
            SettingsDocument settings = store.Load();
            var installer = new NnediInstaller(ConfigRoot(store), messages);

            if (parseResult.GetValue(removeOption))
            {
                if (installer.Remove() is { } removeError)
                {
                    terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = removeError }));
                    return ExitCodes.Failed;
                }

                terminal.Line(messages.Format("toolchain.nnedi.removed", new { path = installer.Weights }));
                return ExitCodes.Ok;
            }

            string? front = parseResult.GetValue(frontOption);
            if (parseResult.GetValue(noFrontOption)) front = GitHubFront.Off;

            (string? prefix, string? frontError) = GitHubFront.Normalise(front);
            if (frontError is not null)
            {
                terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = frontError }));
                return ExitCodes.Usage;
            }

            string? proxy = parseResult.GetValue(proxyOption) ?? settings.Network.Proxy;

            // The FFmpeg that would load this file: proving the download is
            // loadable is worth one frame, and a machine with no FFmpeg at all
            // still gets the file (the check is skipped).
            ToolchainProbeResult tools = ToolchainProbe.Probe(
                EncodeSession.ToolchainDirectory(settings, ConfigRoot(store)));

            NnediOutcome outcome = installer.Install(
                tools.Ffmpeg, proxy, prefix,
                parseResult.GetValue(forceOption),
                line => terminal.Line($"  {line}"),
                Interrupt.Token);

            if (!outcome.Ok)
            {
                terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = outcome.Error }));
                return ExitCodes.Failed;
            }

            terminal.Line(messages.Format(
                outcome.AlreadyCurrent ? "toolchain.nnedi.current" : "toolchain.nnedi.installed",
                new { path = outcome.Weights, mb = outcome.Bytes / (1024 * 1024) }));

            return ExitCodes.Ok;
        });

        return command;
    }

    /// <summary>
    /// What is installed, what it is, and which one is in use.
    ///
    /// <para>Prints the version line as well as the path: two installs of the same
    /// id are indistinguishable by path, and the snapshot date is the only thing
    /// that tells a user whether "re-download today's build" actually did
    /// something.</para>
    /// </summary>
    private static Command ListCommand(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var command = new Command("list", messages["toolchain.list.desc"]);

        command.SetAction(_ =>
        {
            SettingsDocument settings = store.Load();
            var installer = new ToolchainInstaller(ConfigRoot(store), messages);
            IReadOnlyList<InstalledToolchain> installed = installer.Installed();
            string selected = settings.Toolchain.FfmpegSource;

            terminal.Line(messages.Format("toolchain.list.root", new
            {
                path = ToolchainLayout.Root(ConfigRoot(store)),
            }));
            terminal.Line();

            if (installed.Count == 0)
            {
                terminal.Line(messages["toolchain.list.empty"]);
                terminal.Line();
                terminal.Line(messages.Format("toolchain.list.hint", new { ids = FfmpegCatalog.Ids }));
                return ExitCodes.Ok;
            }

            foreach (InstalledToolchain tool in installed)
            {
                bool inUse = string.Equals(tool.Id, selected, StringComparison.OrdinalIgnoreCase);
                terminal.Line($"{(inUse ? "*" : " ")} {tool.Id,-10} {tool.Ffmpeg}");
                if (tool.Version is { Length: > 0 } version) terminal.Line($"    {version}");
            }

            // PATH is a real answer and the default, so it is one of the rows.
            if (string.Equals(selected, FfmpegCatalog.PathId, StringComparison.OrdinalIgnoreCase))
            {
                terminal.Line();
                terminal.Line(messages["toolchain.list.pathSelected"]);
            }

            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command InstallCommand(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var idArgument = new Argument<string?>("id")
        {
            Description = messages["toolchain.install.arg.id"],
            DefaultValueFactory = _ => FfmpegCatalog.DefaultId,
        };

        var proxyOption = new Option<string?>("--proxy")
        {
            Description = messages["toolchain.install.opt.proxy"],
        };

        var frontOption = new Option<string?>("--front")
        {
            Description = messages["toolchain.install.opt.front"],
        };

        var noFrontOption = new Option<bool>("--no-front")
        {
            Description = messages["toolchain.install.opt.noFront"],
        };

        var archiveOption = new Option<string?>("--from")
        {
            Description = messages["toolchain.install.opt.from"],
        };

        var command = new Command("install", messages["toolchain.install.desc"])
        {
            idArgument,
            proxyOption,
            frontOption,
            noFrontOption,
            archiveOption,
        };

        command.SetAction((ParseResult parseResult) =>
        {
            SettingsDocument settings = store.Load();
            string? requested = parseResult.GetValue(idArgument);

            if (FfmpegCatalog.Find(requested) is not { } dist)
            {
                // Two different mistakes read the same to a user and want two
                // different answers: an id nobody ever had, and a real one that is
                // built for the other operating system.
                if (FfmpegCatalog.FindKnown(requested) is { } known)
                {
                    terminal.ErrorLine(messages.Format("toolchain.error.wrongPlatform", new
                    {
                        id = known.Id,
                        platform = known.Platform.ToString(),
                        current = FfmpegCatalog.Current.ToString(),
                    }));
                }
                else
                {
                    terminal.ErrorLine(messages.Format("toolchain.error.unknownSource", new
                    {
                        id = requested,
                        ids = FfmpegCatalog.Ids,
                    }));
                }

                return ExitCodes.Usage;
            }

            string? front = parseResult.GetValue(frontOption);
            if (parseResult.GetValue(noFrontOption)) front = GitHubFront.Off;

            (string? prefix, string? frontError) = GitHubFront.Normalise(front);
            if (frontError is not null)
            {
                terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = frontError }));
                return ExitCodes.Usage;
            }

            string? proxy = parseResult.GetValue(proxyOption) ?? settings.Network.Proxy;

            // Remember the route when the user named one, so the next install does
            // not have to be told again. Only when it was named: "off" and unset are
            // different answers, and writing one over the other is how a remembered
            // decision gets forgotten.
            if (front is not null && !string.Equals(settings.Network.GithubFront, front, StringComparison.Ordinal))
            {
                settings = settings with
                {
                    Network = settings.Network with { GithubFront = front },
                };

                if (store.TrySave(settings) is { } saveError)
                    terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = saveError }));
            }

            terminal.Line(messages.Format("toolchain.install.dist", new { id = dist.Id, label = dist.Label }));

            var installer = new ToolchainInstaller(ConfigRoot(store), messages);
            InstallOutcome outcome = installer.Install(
                dist,
                parseResult.GetValue(archiveOption),
                proxy,
                prefix,
                line => terminal.Line($"  {line}"),
                Interrupt.Token);

            if (!outcome.Ok)
            {
                terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = outcome.Error }));
                return ExitCodes.Failed;
            }

            terminal.Line(messages.Format("toolchain.install.done", new
            {
                path = ToolchainLayout.Tool(ConfigRoot(store), dist.Id, "ffmpeg"),
            }));

            if (outcome.Version is { Length: > 0 } version) terminal.Line($"  {version}");

            // Installing is not the same as choosing. Saying so is what keeps the
            // next encode from using a copy the user did not mean to use.
            if (!string.Equals(settings.Toolchain.FfmpegSource, dist.Id, StringComparison.OrdinalIgnoreCase))
            {
                terminal.Line();
                terminal.Line(messages.Format("toolchain.install.notSelected", new { id = dist.Id }));
            }

            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command RemoveCommand(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var idArgument = new Argument<string>("id") { Description = messages["toolchain.remove.arg.id"] };

        var command = new Command("remove", messages["toolchain.remove.desc"]) { idArgument };

        command.SetAction((ParseResult parseResult) =>
        {
            SettingsDocument settings = store.Load();
            string id = parseResult.GetValue(idArgument)!;

            if (FfmpegCatalog.Find(id) is not { } dist)
            {
                terminal.ErrorLine(messages.Format("toolchain.error.unknownSource", new
                {
                    id,
                    ids = FfmpegCatalog.Ids,
                }));

                return ExitCodes.Usage;
            }

            var installer = new ToolchainInstaller(ConfigRoot(store), messages);
            if (installer.Remove(dist.Id) is { } problem)
            {
                terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = problem }));
                return ExitCodes.Failed;
            }

            terminal.Line(messages.Format("toolchain.remove.done", new { id = dist.Id }));

            // Removing the build that is in use leaves the next encode with nothing
            // to find. Not an error -- the user may be clearing space -- but the
            // fallback has to be named, or the next command reads as a fresh bug.
            if (string.Equals(settings.Toolchain.FfmpegSource, dist.Id, StringComparison.OrdinalIgnoreCase))
            {
                terminal.Line();
                terminal.Line(messages.Format("toolchain.remove.wasSelected", new { id = dist.Id }));
            }

            return ExitCodes.Ok;
        });

        return command;
    }

    /// <summary>
    /// Choose which build an encode uses. Writes the settings and nothing else --
    /// no files move, so switching back is one more command.
    /// </summary>
    private static Command UseCommand(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var idArgument = new Argument<string>("id") { Description = messages["toolchain.use.arg.id"] };

        var command = new Command("use", messages["toolchain.use.desc"]) { idArgument };

        command.SetAction((ParseResult parseResult) =>
        {
            SettingsDocument settings = store.Load();
            string id = parseResult.GetValue(idArgument)!;

            // "path" is a legal choice but not a distribution: it means "whatever
            // the machine's own PATH offers", which is what someone with a system
            // FFmpeg and no install wants.
            if (!string.Equals(id, FfmpegCatalog.PathId, StringComparison.OrdinalIgnoreCase))
            {
                if (FfmpegCatalog.Find(id) is not { } dist)
                {
                    terminal.ErrorLine(messages.Format("toolchain.use.unknownSource", new
                    {
                        id,
                        ids = $"{FfmpegCatalog.PathId}, {FfmpegCatalog.Ids}",
                    }));

                    return ExitCodes.Usage;
                }

                // Choosing something that is not installed is a request that cannot
                // be honoured, and the honest answer is the command that fixes it --
                // not a settings file that points at nothing.
                if (new ToolchainInstaller(ConfigRoot(store), messages).FfmpegFor(dist.Id) is null)
                {
                    terminal.ErrorLine(messages.Format("toolchain.use.notInstalled", new { id = dist.Id }));
                    return ExitCodes.Usage;
                }
            }

            settings = settings with
            {
                Toolchain = settings.Toolchain with { FfmpegSource = id.ToLowerInvariant() },
            };

            if (store.TrySave(settings) is { } saveError)
            {
                terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = saveError }));
                return ExitCodes.Failed;
            }

            terminal.Line(messages.Format("toolchain.use.done", new { id }));
            return ExitCodes.Ok;
        });

        return command;
    }

    /// <summary>
    /// The ffmpeg an encode would run, or "PATH". The one question a user has after
    /// a <c>use</c>, and until now it could only be answered by running an encode
    /// and reading the note.
    /// </summary>
    private static Command PathCommand(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var command = new Command("path", messages["toolchain.path.desc"]);

        command.SetAction(_ =>
        {
            SettingsDocument settings = store.Load();
            ToolchainProbeResult probe = ToolchainProbe.Probe(
                ToolchainSource.Folder(settings.Toolchain, store.DataDirectory, p => Directory.Exists(p) || File.Exists(p)));

            if (probe.Ffmpeg is { } ffmpeg)
            {
                terminal.Line(ffmpeg);
                return ExitCodes.Ok;
            }

            // Nothing found is a real answer, and the caller is a script: exit 1 so
            // `if lae toolchain path` means what it looks like.
            terminal.ErrorLine(messages["toolchain.path.none"]);
            return ExitCodes.Failed;
        });

        return command;
    }

    /// <summary>
    /// The folder the settings live in, which is where managed installs go. A store
    /// with no disk location (the in-memory one used by tests) cannot install
    /// anything, so the temporary folder stands in: it keeps the command runnable
    /// instead of throwing, and nothing in the real program takes that branch.
    /// </summary>
    private static string ConfigRoot(ISettingsStore store)
        => store.DataDirectory ?? Path.Combine(Path.GetTempPath(), "LiveArchiveEncoder");
}


