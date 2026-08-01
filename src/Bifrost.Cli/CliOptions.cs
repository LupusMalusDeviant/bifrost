namespace Bifrost.Cli;

/// <summary>
/// Sehr kleiner Optionsparser. Er kennt nur Schalter und <c>--name wert</c> — und er meldet, was er
/// nicht kennt: Ein stillschweigend verschlucktes <c>--replace</c> wäre die schlimmste Sorte
/// Tippfehler.
/// <para>
/// Er stand bis WP4.3 als private Klasse in <see cref="OperationsCli"/>. Herausgezogen, als der
/// zweite Befehlssatz ihn brauchte: Zwei Kopien eines Parsers heißen zwei Meinungen darüber, was
/// eine unbekannte Option ist — und genau die Meinung ist hier der ganze Wert.
/// </para>
/// </summary>
public sealed class CliOptions
{
    private readonly List<string> _rest;

    public CliOptions(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _rest = [.. arguments];
    }

    public string? Value(string name)
    {
        var index = _rest.IndexOf(name);
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= _rest.Count)
        {
            throw new ArgumentException($"{name} verlangt einen Wert.");
        }

        var value = _rest[index + 1];
        _rest.RemoveRange(index, 2);
        return value;
    }

    public bool Flag(string name)
    {
        var index = _rest.IndexOf(name);
        if (index < 0)
        {
            return false;
        }

        _rest.RemoveAt(index);
        return true;
    }

    public void EnsureNoRest()
    {
        if (_rest.Count > 0)
        {
            throw new ArgumentException($"Unbekannte Option(en): {string.Join(", ", _rest)}.");
        }
    }
}
