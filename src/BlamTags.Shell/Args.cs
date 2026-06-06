namespace BlamTags.Shell;

/// <summary>
/// Minimal argument list with option/flag extraction. Options may be
/// <c>--name value</c> or <c>--name=value</c>; what remains after pulling
/// options/flags are the positionals, in order.
/// </summary>
public sealed class Args
{
    private readonly List<string> _items;

    public Args(IEnumerable<string> items) => _items = new List<string>(items);

    /// <summary>Pull a boolean flag (e.g. <c>--json</c>); returns whether present.</summary>
    public bool TakeFlag(params string[] names)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (Array.IndexOf(names, _items[i]) >= 0)
            {
                _items.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Pull an option value (<c>--name value</c> or <c>--name=value</c>),
    /// or null if absent.</summary>
    public string? TakeOption(params string[] names)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            string item = _items[i];
            foreach (var name in names)
            {
                if (item == name)
                {
                    if (i + 1 >= _items.Count)
                        throw new CliError($"option {name} requires a value");
                    string value = _items[i + 1];
                    _items.RemoveAt(i + 1);
                    _items.RemoveAt(i);
                    return value;
                }
                if (item.StartsWith(name + "=", StringComparison.Ordinal))
                {
                    _items.RemoveAt(i);
                    return item[(name.Length + 1)..];
                }
            }
        }
        return null;
    }

    /// <summary>Remaining positional arguments, in order.</summary>
    public IReadOnlyList<string> Positionals => _items;

    /// <summary>The positional at <paramref name="index"/>, or null.</summary>
    public string? Positional(int index) => index < _items.Count ? _items[index] : null;

    /// <summary>Fail if any unconsumed <c>--option</c> tokens remain.</summary>
    public void EnsureNoUnknownOptions()
    {
        var unknown = _items.FirstOrDefault(i => i.StartsWith("--", StringComparison.Ordinal));
        if (unknown is not null)
            throw new CliError($"unknown option {unknown}");
    }
}
