using System.CommandLine;
using System.CommandLine.Help;
using Lae.Core;
using Lae.Core.Localization;

namespace Lae.App;

/// <summary>
/// The command tree. This is the single source of truth for three things that
/// used to be written three times:
///
/// <list type="number">
/// <item>what the program accepts,</item>
/// <item>the <c>--help</c> text,</item>
/// <item>the completion candidates — both the shell scripts and the REPL menu
/// read this tree, so they cannot drift apart.</item>
/// </list>
///
/// <para>v7 kept a hand-written 112-entry switch in CliOptions.cs and a
/// hand-written 69-entry completion table in Completion.cs; 43 options were
/// usable but never suggested. That failure is structurally impossible here.
/// </para>
///
/// <para>Commands are added as they are implemented. There are no placeholder
/// commands: a command that exists but does nothing is worse than a missing one.
/// </para>
/// </summary>
public static class CommandTree
{
    /// <summary>Builds the root command with every implemented subcommand attached.</summary>
    public static RootCommand Build(AppHost host)
    {
        IMessages messages = host.Messages;

        var root = new RootCommand(messages["root.desc"]);

        var versionOption = new Option<bool>("--version")
        {
            Description = messages["version.opt.version"],
        };

        // System.CommandLine installs its own --version, which prints the
        // assembly version with a "+<commit hash>" suffix ("1.0.0+2854e8c...").
        // That is fine for a library demo and wrong for this tool: scripts and
        // the self-updater read this value, and a hash suffix makes the compare
        // ambiguous. The generated one is dropped so LaeVersion.Current stays
        // the single source.
        foreach (VersionOption generated in root.Options.OfType<VersionOption>().ToArray())
            root.Options.Remove(generated);

        root.Options.Add(versionOption);

        // The help option is the library's, so its sentence is the library's --
        // in English, inside an otherwise Chinese --help. It is one property to
        // set, and leaving it would make the translated screen look half done.
        foreach (HelpOption help in root.Options.OfType<HelpOption>())
            help.Description = messages["root.opt.help"];

        // No subcommand: print help and succeed. v7 opened its interactive menu
        // here; v8 has no menu, so the honest answer is the usage text.
        root.SetAction((ParseResult parseResult) =>
        {
            if (parseResult.GetValue(versionOption)) return PrintVersion(host);
            new HelpAction().Invoke(parseResult);
            return ExitCodes.Ok;
        });

        root.Subcommands.Add(VersionCommand(host, messages));
        root.Subcommands.Add(BuildRepl(root, messages));
        root.Subcommands.Add(Commands.ConfigCommand.Build(host.Settings, host.Terminal, messages));
        root.Subcommands.Add(Commands.PresetCommand.Build(host.Settings, host.Terminal, messages));
        root.Subcommands.Add(Commands.SetupCommand.Build(host.Settings, host.Terminal, messages));
        root.Subcommands.Add(Commands.ToolchainCommand.Build(host.Settings, host.Terminal, messages));
        root.Subcommands.Add(Commands.EncodeCommand.Build(host.Settings, host.Terminal, host.History, messages));
        root.Subcommands.Add(Commands.HistoryCommand.Build(host.History, host.Terminal, messages));

        return root;
    }

    private static Command VersionCommand(AppHost host, IMessages messages)
    {
        var command = new Command("version", messages["version.desc"]);
        command.SetAction(_ => PrintVersion(host));
        return command;
    }

    /// <summary>
    /// <c>lae repl</c> — an interactive prompt with highlighting and an inline
    /// completion menu.
    ///
    /// <para>It is registered as a real subcommand rather than checked for in
    /// argv: System.CommandLine rejects unknown commands before any handler
    /// runs, so an unregistered "repl" exits with "unrecognized command" — a
    /// message that sends you looking in entirely the wrong place. This was
    /// found the hard way while building the preflight probe.</para>
    ///
    /// <para>A redirected stdin or stdout means nobody is watching a prompt, so
    /// it exits with <see cref="ExitCodes.Usage"/> instead of hanging. This is
    /// the v7 <c>Cui.Ask()</c> lesson: on EOF the menu loop repainted on every
    /// read and produced 96 MB of text in seconds.</para>
    /// </summary>
    private static Command BuildRepl(RootCommand root, IMessages messages)
    {
        var command = new Command("repl", messages["repl.desc"]);

        command.SetAction(async (ParseResult _, CancellationToken cancellationToken) =>
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.Error.WriteLine(messages["repl.needsTerminal"]);
                return ExitCodes.Usage;
            }

            return await Repl.RunAsync(root, cancellationToken);
        });

        return command;
    }

    private static int PrintVersion(AppHost host)
    {
        host.Terminal.Line(LaeVersion.Current);
        return ExitCodes.Ok;
    }
}
