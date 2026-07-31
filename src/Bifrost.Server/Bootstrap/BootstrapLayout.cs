namespace Bifrost.Server.Bootstrap;

/// <summary>
/// Wo die beiden Dateien des Erstzugangs liegen (WP3.4).
/// <para>
/// <b>Zwei Dateien, zwei Aufgaben.</b> Die <see cref="StatePathFor">Zustandsdatei</see> ist die
/// dauerhafte Ablage — sie trägt <b>nur den Hash</b> des Setup-Tokens und überlebt jeden Neustart.
/// Die <see cref="HandoverPathFor">Übergabedatei</see> ist der Ausgabeweg für genau einen Menschen:
/// Sie enthält den Klartext, steht auf 0600 beziehungsweise einer ACL ohne Vererbung, und sie wird
/// beim Einlösen oder beim Ablauf <b>gelöscht</b>. Sie ist die Alternative zum Logeintrag, nicht
/// dessen Kopie.
/// </para>
/// <para>
/// Beide liegen unter <c>config/</c>, also neben <c>instance.json</c>: dieselbe Sorte Datei, die
/// den Zustand einer Installation beschreibt und nicht ihre Nutzdaten.
/// </para>
/// </summary>
public static class BootstrapLayout
{
    /// <summary>Die dauerhafte Ablage — Hash, Fristen, Zustand. Nie Klartext.</summary>
    public static string StatePathFor(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(dataDirectory, "config", "bootstrap.json");
    }

    /// <summary>Der kurzlebige Ausgabeweg — Klartext, restriktive Rechte, wird wieder entfernt.</summary>
    public static string HandoverPathFor(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(dataDirectory, "config", "bootstrap-token.txt");
    }
}
