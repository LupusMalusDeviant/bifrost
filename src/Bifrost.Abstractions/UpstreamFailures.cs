namespace Bifrost.Abstractions;

/// <summary>
/// Die Gegenstelle hat die Anmeldung abgelehnt, oder es liegt nichts vor, womit man sich anmelden
/// könnte (fehlendes oder nicht erneuerbares OAuth-Token).
/// <para>
/// Ein eigener Typ, weil der Supervisor daran die Frist festmacht: Eine abgelehnte Anmeldung geht
/// nicht von selbst vorüber. Sie im Sekundentakt zu wiederholen wäre eine Schleife gegen den
/// Schlüssel — beim Upstream sieht das aus wie ein Angriff, und manche sperren daraufhin.
/// </para>
/// <para>
/// Die Meldung formuliert der Werfende selbst. Sie geht in Status, Audit und Log und darf deshalb
/// nichts enthalten, was die Gegenstelle geschickt hat.
/// </para>
/// </summary>
public sealed class UpstreamAuthenticationException : Exception
{
    public UpstreamAuthenticationException()
    {
    }

    public UpstreamAuthenticationException(string message)
        : base(message)
    {
    }

    public UpstreamAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Der Upstream kennt die Sitzung nicht mehr: HTTP 404 auf eine Anfrage, die eine
/// <c>Mcp-Session-Id</c> trug.
/// <para>
/// <b>Das ist der eine Fehler mitten im Aufruf, bei dem feststeht, dass nichts ausgeführt wurde</b>
/// — der Server hat die Anfrage abgewiesen, statt sie zu bearbeiten. Nur deshalb darf der Aufruf
/// nach dem Neuaufbau der Sitzung einmal wiederholt werden. Bei einer abgerissenen Verbindung oder
/// einer Zeitüberschreitung ist das nicht zu wissen, und ein zweites <c>create_issue</c> ist
/// schlimmer als ein gemeldeter Fehler.
/// </para>
/// <para>
/// Ein 404 <b>ohne</b> Sitzung ist kein Fall für diesen Typ: Auf der Spec-Revision 2026-07-28
/// beantwortet ein Server auch ein unbekanntes Verfahren mit HTTP 404.
/// </para>
/// </summary>
public sealed class UpstreamSessionLostException : Exception
{
    public UpstreamSessionLostException()
    {
    }

    public UpstreamSessionLostException(string message)
        : base(message)
    {
    }

    public UpstreamSessionLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Plan 0005 I3: Eine Katalogabfrage hat vor ihrer nächsten Anfrage (<c>resources/list</c>,
/// <c>prompts/list</c>) aufgehört, weil sie nicht mehr weitermachen durfte — der Upstream wird
/// gestoppt. Bewusst keine <see cref="OperationCanceledException"/>: Die gilt am Rand der Verbindung
/// als abgebaute Verbindung. Hier ist nichts abgebaut, es ging nur nichts mehr hinaus.
/// </summary>
public sealed class UpstreamCatalogWithheldException : Exception
{
    public UpstreamCatalogWithheldException()
    {
    }

    public UpstreamCatalogWithheldException(string message)
        : base(message)
    {
    }

    public UpstreamCatalogWithheldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
