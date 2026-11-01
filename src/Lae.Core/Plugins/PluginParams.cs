using System.Globalization;
using Lae.Core.Localization;

namespace Lae.Core.Plugins;

/// <summary>The parameters one plugin stage will be called with, or why not.</summary>
public sealed record ParamResolution
{
    /// <summary>Exactly what goes on the command line, defaults included.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Why the values cannot be used. Non-empty means do not run.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Turns the strings in a settings file into the parameters a plugin declared, or
/// says why it cannot.
///
/// <para>This is a planning step, not a calling step: it runs before the encode
/// starts, so a typo in <c>[passes.params]</c> is a message within a millisecond
/// rather than a plugin failure twenty minutes into a recording. The same
/// reasoning as <c>EncodePlan.Create</c> -- everything that can be decided from
/// what is on hand gets decided before anything is spawned.</para>
///
/// <para>Defaults are filled here rather than by the plugin, because the plugin
/// never learns which parameters were omitted: the host builds one command line
/// and hands it over. A plugin left to apply its own defaults would work when
/// called by hand and not when called from here.</para>
/// </summary>
public static class PluginParams
{
    /// <summary>
    /// Merge <paramref name="given"/> over the declared defaults and check
    /// everything against the declaration.
    ///
    /// <para>Culture-invariant throughout. A plugin written in any language parses
    /// the text it was handed, and <c>0,25</c> from a machine whose culture uses a
    /// decimal comma is a different number to a program expecting <c>0.25</c> --
    /// or no number at all.</para>
    /// </summary>
    public static ParamResolution Resolve(
        PluginStageDeclaration stage,
        IReadOnlyDictionary<string, string>? given,
        IMessages messages)
    {
        var problems = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        // Declared order, so a report about two bad parameters reads in the order
        // the handshake listed them rather than the order a dictionary happened to
        // store them in.
        foreach (PluginParamDeclaration? declared in stage.Params ?? [])
        {
            if (declared is null) continue;
            if (string.IsNullOrWhiteSpace(declared.Name)) continue;

            string? supplied = null;
            if (given is not null)
            {
                foreach ((string key, string value) in given)
                {
                    if (!string.Equals(key, declared.Name, StringComparison.Ordinal)) continue;
                    supplied = value;
                    break;
                }
            }

            if (supplied is null)
            {
                if (declared.DefaultValue is { } fallback) values[declared.Name] = fallback;
                continue;
            }

            if (Check(declared, supplied, messages) is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            values[declared.Name] = supplied;
        }

        // A parameter nobody declared is a configuration mistake that would be
        // silently ignored by the command line -- the plugin would run with its
        // default and the user would believe they had set something.
        if (given is not null)
        {
            foreach (string key in given.Keys)
            {
                if ((stage.Params ?? []).Any(p => p is not null && string.Equals(p.Name, key, StringComparison.Ordinal)))
                    continue;

                problems.Add(messages.Format("plugin.error.paramUnknown", new
                {
                    stage = stage.Id,
                    name = key,
                }));
            }
        }

        return new ParamResolution { Values = values, Problems = problems };
    }

    /// <summary>Null when the value is legal; the sentence to print when it is not.</summary>
    private static string? Check(PluginParamDeclaration declared, string value, IMessages messages)
    {
        if (declared.Kind is not { } kind) return null;   // rejected at handshake time

        double? number = kind switch
        {
            PluginParamType.Float => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                ? f
                : null,
            PluginParamType.Int => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
                ? l
                : null,
            _ => null,
        };

        if (kind is PluginParamType.Bool && !bool.TryParse(value, out _))
            return messages.Format("plugin.error.paramBool", new { name = declared.Name, value });

        if (kind is PluginParamType.Float or PluginParamType.Int && number is null)
        {
            return messages.Format(
                kind is PluginParamType.Int ? "plugin.error.paramInteger" : "plugin.error.paramNumber",
                new { name = declared.Name, value });
        }

        if (number is { } parsed)
        {
            if (declared.Min is { } min && parsed < min)
            {
                return messages.Format("plugin.error.paramRange", new
                {
                    name = declared.Name,
                    value,
                    min = min.ToString("G", CultureInfo.InvariantCulture),
                    max = declared.Max?.ToString("G", CultureInfo.InvariantCulture) ?? "-",
                });
            }

            if (declared.Max is { } max && parsed > max)
            {
                return messages.Format("plugin.error.paramRange", new
                {
                    name = declared.Name,
                    value,
                    min = declared.Min?.ToString("G", CultureInfo.InvariantCulture) ?? "-",
                    max = max.ToString("G", CultureInfo.InvariantCulture),
                });
            }
        }

        return null;
    }
}
