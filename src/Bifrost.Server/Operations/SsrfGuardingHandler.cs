using Bifrost.Upstream.OAuth;

namespace Bifrost.Server.Operations;

/// <summary>
/// Schickt jedes Ziel durch dieselbe SSRF-Prüfung wie ein Schemaimport, <b>bevor</b> die Verbindung
/// aufgebaut wird (Invariante 3).
/// <para>
/// <b>Gefunden hat diese Lücke der Wächter, nicht ich.</b> Der erste Entwurf der
/// Freigabe-Benachrichtigung baute sich seinen eigenen Client und rief die vom Betreiber genannte
/// Adresse ungeprüft — genau die Stelle, an der ein Gateway zum Werkzeug wird, um interne Dienste
/// zu erreichen: Cloud-Metadaten unter <c>169.254.169.254</c>, ein Admin-Port auf
/// <c>127.0.0.1</c>, ein Nachbar im Firmennetz.
/// </para>
/// <para>
/// Dass die Adresse aus der Konfiguration kommt, ändert daran wenig: Sie wird einmal gesetzt und
/// danach von niemandem mehr angesehen. <see cref="AllowPrivateTargets"/> ist der ausdrückliche
/// Schalter für den Fall, dass ein Ziel wirklich im eigenen Netz steht — dieselbe Bauform wie bei
/// OAuth-Upstreams.
/// </para>
/// </summary>
public sealed class SsrfGuardingHandler(bool allowPrivateTargets) : DelegatingHandler(new SocketsHttpHandler
{
    // Kein automatisches Folgen: Ein 302 fuehrte die Signatur und den Deep-Link an eine Adresse,
    // die nie geprueft wurde.
    AllowAutoRedirect = false,
})
{
    public bool AllowPrivateTargets { get; } = allowPrivateTargets;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is { } target)
        {
            // Derselbe zentrale Weg wie bei Schemaimport und OAuth-Discovery. Die Ausnahme wirft,
            // und der Zusteller verbucht das als Fehlversuch mit Grund.
            await OAuthDiscovery.EnsureTargetAllowedAsync(target, AllowPrivateTargets, cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
