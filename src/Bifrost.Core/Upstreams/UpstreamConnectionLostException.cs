namespace Bifrost.Core.Upstreams;

/// <summary>Signalisiert dem Run-Loop den Verlust einer zuvor gesunden Verbindung (Health-Ping fehlgeschlagen).</summary>
public sealed class UpstreamConnectionLostException : Exception
{
    public UpstreamConnectionLostException()
    {
    }

    public UpstreamConnectionLostException(string message)
        : base(message)
    {
    }

    public UpstreamConnectionLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
