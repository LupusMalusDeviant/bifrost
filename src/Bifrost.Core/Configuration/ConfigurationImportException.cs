namespace Bifrost.Core.Configuration;

/// <summary>
/// Ein Export lässt sich nicht lesen, nicht entschlüsseln, nicht anwenden — oder er wurde
/// angewendet und musste zurückgenommen werden.
/// <para>
/// Eigener Typ, damit ein Adapter (CLI, API, UI) den Fall vom unerwarteten Fehler unterscheiden und
/// auf den vereinbarten Exit-Code abbilden kann, ohne die Meldung zu lesen.
/// </para>
/// </summary>
public sealed class ConfigurationImportException : Exception
{
    public ConfigurationImportException()
    {
    }

    public ConfigurationImportException(string message)
        : base(message)
    {
    }

    public ConfigurationImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
