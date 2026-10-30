using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tomlyn.Model;

namespace Lae.Media.Settings;

/// <summary>
/// Says no to keys <see cref="Lae.Core.Settings.SettingsDocument"/> does not have.
///
/// <para>A <c>System.Text.Json</c>-style serializer ignores what it does not
/// recognise. That is right for a wire protocol and wrong for a config file: v7
/// shipped <c>fpsLevesl</c> in a document for a whole release, and every user who
/// typed it got the default ladder plus no warning. The shape of the allowed keys
/// is derived from the type by reflection rather than kept in a list here, so
/// adding a property cannot silently make its key illegal.</para>
/// </summary>
internal static class SettingsKeySchema
{
    private enum Kind
    {
        /// <summary>A scalar, or an array of scalars: nothing below it to walk.</summary>
        Value,

        /// <summary>A table: every key inside must be known too.</summary>
        Table,

        /// <summary>An array of tables (<c>[[name]]</c>): every element is walked.</summary>
        TableArray,
    }

    private sealed record Node(Kind Kind, Dictionary<string, Node> Members);

    private static readonly ConcurrentDictionary<Type, Node> Cache = new();

    /// <summary>Empty when every key is accounted for; otherwise one line per problem.</summary>
    public static IReadOnlyList<string> Validate(TomlTable root)
    {
        var problems = new List<string>();
        Walk(root, Members(typeof(Lae.Core.Settings.SettingsDocument)), "", problems);
        return problems;
    }

    private static void Walk(TomlTable table, Dictionary<string, Node> members, string path, List<string> problems)
    {
        foreach (KeyValuePair<string, object> pair in table)
        {
            if (!members.TryGetValue(pair.Key, out Node? node))
            {
                problems.Add($"unknown key '{path}{pair.Key}'");
                continue;
            }

            switch (node.Kind)
            {
                case Kind.Table when pair.Value is TomlTable nested:
                    Walk(nested, node.Members, $"{path}{pair.Key}.", problems);
                    break;

                case Kind.Table:
                    problems.Add($"'{path}{pair.Key}' is a table, but the file gives it a single value");
                    break;

                case Kind.TableArray when pair.Value is TomlTableArray items:
                    foreach (TomlTable item in items) Walk(item, node.Members, $"{path}{pair.Key}[].", problems);
                    break;

                // A list of tables with nothing in it comes out as `[]` and comes
                // back as a plain array. Refusing that would make the program
                // reject the file it just wrote -- which is exactly what happened
                // the first time this validator ran against its own output.
                case Kind.TableArray when IsEmptyArray(pair.Value):
                    break;

                case Kind.TableArray:
                    problems.Add(
                        $"'{path}{pair.Key}' is a list of tables; write it as [[{path}{pair.Key}]] blocks "
                        + "instead of one value");
                    break;

                case Kind.Value when pair.Value is TomlTable or TomlTableArray:
                    problems.Add($"'{path}{pair.Key}' takes a single value, not a table");
                    break;
            }
        }
    }

    private static bool IsEmptyArray(object? value) => value switch
    {
        TomlArray array => array.Count == 0,
        TomlTableArray tables => tables.Count == 0,
        _ => false,
    };

    private static Dictionary<string, Node> Members(Type type) => For(type).Members;

    private static Node For(Type type)
    {
        Node node = Cache.GetOrAdd(type, Build);
        return node;
    }

    private static Node Build(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (ElementType(type) is { } element)
        {
            return IsScalar(element)
                ? new Node(Kind.Value, [])
                : new Node(Kind.TableArray, Members(element));
        }

        if (IsScalar(type)) return new Node(Kind.Value, []);

        var members = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Computed members (`Any`, `Active`) are excluded from the file by the
            // same attribute the JSON writer honoured, so a key of that name is a
            // mistake worth reporting rather than something to accept.
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;

            members[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = For(property.PropertyType);
        }

        return new Node(Kind.Table, members);
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type.IsPrimitive
            || type.IsEnum;
    }

    /// <summary>The <c>T</c> of an <c>IEnumerable&lt;T&gt;</c>, or null for anything else.</summary>
    private static Type? ElementType(Type type)
    {
        if (type == typeof(string)) return null;

        foreach (Type candidate in type.GetInterfaces().Append(type))
        {
            if (!candidate.IsGenericType) continue;
            if (candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;

            return candidate.GetGenericArguments()[0];
        }

        return null;
    }
}
