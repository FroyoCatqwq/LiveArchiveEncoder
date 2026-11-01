namespace Lae.Core.Localization;

/// <summary>
/// An argument-level refusal that already knows which sentence to use.
///
/// <para>Most of what this program says is produced where the strings are: a
/// command has the <see cref="IMessages"/> it was built with, so it can just ask
/// for a sentence. Two places cannot: the argument builders in the middle of the
/// plan, and the encoder arithmetic. They are deep inside pure functions whose job
/// is to build a list of tokens, and threading a translation table through every
/// one of them would put a language parameter on a dozen signatures for three
/// sentences.</para>
///
/// <para>So those three throw this instead of <see cref="ArgumentException"/>. It
/// carries a key and the values, and the planner -- which does have the messages --
/// turns it into the sentence the user reads. A plain
/// <see cref="ArgumentException"/> still means what it always meant: a bug in this
/// program, not a request that cannot be honoured.</para>
/// </summary>
public sealed class RefusalException(string key, object? values = null) : Exception(key)
{
    public string Key { get; } = key;

    public object? Values { get; } = values;

    /// <summary>The sentence, in whatever language the caller is speaking.</summary>
    public string Resolve(IMessages messages) => messages.Format(Key, Values);
}
