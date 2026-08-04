namespace Bifrost.ConnectorKit;

/// <summary>
/// Optionen der Form <c>--name wert</c>.
/// <para>
/// <see cref="EnsureNoRest"/> ist der Grund, warum diese Klasse existiert: Ein vertippter Schalter
/// soll ein Bedienfehler sein und nicht stillschweigend ignoriert werden. Wer <c>--kez</c> schreibt
/// und ein Paket bekommt, das mit einem anderen Schlüssel signiert ist, merkt es erst beim
/// Einspielen.
/// </para>
/// </summary>
internal sealed class KitOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<string> _rest = [];

    public KitOptions(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var current = arguments[i];
            if (current.StartsWith("--", StringComparison.Ordinal) && i + 1 < arguments.Count
                && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                _values[current] = arguments[++i];
                continue;
            }

            _rest.Add(current);
        }
    }

    public string? Value(string name) => _values.GetValueOrDefault(name);

    /// <summary>
    /// Nimmt genau die Schalter an, die der Befehl kennt — alles andere ist ein Bedienfehler.
    /// <para>
    /// <b>Befund aus dem eigenen Test:</b> Die erste Fassung prüfte nur „übrig gebliebene" Wörter.
    /// Ein vertippter Schalter <em>mit Wert</em> (<c>--ausgabe paket.mcpkg</c>) sah dabei aus wie
    /// ein gültiges Paar und wurde stillschweigend geschluckt — der Aufrufer bekam ein Paket an
    /// einem Ort, den er nicht gemeint hatte. Die Liste der erlaubten Namen steht deshalb am
    /// Aufrufort, nicht im Parser.
    /// </para>
    /// </summary>
    public void EnsureOnly(params string[] allowed)
    {
        var unknown = _values.Keys.Where(name => !allowed.Contains(name, StringComparer.Ordinal))
            .Concat(_rest)
            .ToList();

        if (unknown.Count > 0)
        {
            throw new ConnectorKitException(
                $"Unbekannte Angabe: {string.Join(' ', unknown)}. Erlaubt: {string.Join(' ', allowed)}",
                ConnectorKitExit.UsageError);
        }
    }
}
