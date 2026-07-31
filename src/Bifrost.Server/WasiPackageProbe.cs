using Bifrost.Abstractions;
using Bifrost.Core.Packaging;
using Bifrost.Upstream.Wasi;

namespace Bifrost.Server;

/// <summary>
/// Die Probe für Connector-Pakete mit WASI-Laufzeit (ADR-0016 „parallel in Quarantäne validieren,
/// Health/Discovery testen").
/// <para>
/// Sie startet den Host wirklich, lädt das Component aus der Quarantäne, prüft die Signatur gegen
/// die gepinnten Herausgeber und fragt den Katalog ab. Eine Probe, die nur Dateien anschaut, hätte
/// genau die Fehler nicht gefunden, wegen derer man probt: ein Component, das nicht instanziiert,
/// eine Signatur, die nicht zum Inhalt passt, ein Katalog, der leer bleibt.
/// </para>
/// <para>
/// Geprobt wird mit <b>genau den Grants</b>, die die Vertrauensstufe erteilt hat — nicht mit
/// weniger. Ein Component, das <c>wasi:cli/environment</c> importiert, startet ohne diesen Grant
/// gar nicht; eine Probe mit weniger Rechten als der Betrieb lieferte also Fehlschläge, die nichts
/// über das Paket aussagen. Und nicht mit mehr, aus dem offensichtlichen Grund.
/// </para>
/// </summary>
internal static class WasiPackageProbe
{
    /// <summary>Wie lange die Probe höchstens dauern darf — inklusive Kompilierung des Components.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static ConnectorProbe Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return async (context, ct) =>
        {
            if (context.Manifest.Transport is not UpstreamTransportKind.Wasi)
            {
                throw new ConnectorPackageException(
                    $"Pakete mit Transport '{context.Manifest.Transport}' sind noch nicht "
                    + "installierbar — bisher gibt es nur den WASI-Pfad (ADR-0016/0020).");
            }

            var hostExecutable = Environment.GetEnvironmentVariable("BIFROST_WASI_HOST")
                ?? throw new ConnectorPackageException(
                    "Kein WASI-Host konfiguriert (BIFROST_WASI_HOST). Ohne ihn ließe sich das Paket "
                    + "nicht proben, und ungeprobt wird nichts aktiv.");

            var connector = new WasiRuntimeConnector(services.GetRequiredService<IPublisherTrustStore>());
            var config = new UpstreamServerConfig(
                $"probe-{context.Manifest.Id}", context.Manifest.DisplayName,
                UpstreamTransportKind.Wasi, Enabled: true,
                Wasi: new WasiTransportOptions(
                    hostExecutable,
                    context.EntryPointPath,
                    context.SignaturePath,
                    // Der Trust-Store ist die Vertrauensquelle; die Liste hier bleibt leer und wird
                    // vom Connector aus dem Store gefüllt.
                    PinnedPublishers: [],
                    Grants: ToGrants(context.GrantedCapabilities)));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            await using var connection = await connector
                .ConnectAsync(new ServerId(Guid.NewGuid()), config, timeout.Token)
                .ConfigureAwait(false);
            var inventory = await connection.DiscoverAsync(timeout.Token).ConfigureAwait(false);
            if (inventory.Tools.Count == 0)
            {
                throw new ConnectorPackageException(
                    "Der Connector startet, meldet aber kein einziges Tool. Ein leerer Katalog wäre "
                    + "ein Upstream, der nichts kann — das fällt besser jetzt auf als im Betrieb.");
            }
        };
    }

    /// <summary>
    /// Übersetzt die erteilten Zugriffe in WASI-Grants. Secrets bleiben außen vor: Ihre Werte
    /// stehen in der Upstream-Konfiguration, nicht im Paket — die Probe kennt sie nicht und soll
    /// sie auch nicht kennen.
    /// </summary>
    private static WasiCapabilityGrants ToGrants(IReadOnlyList<string> granted)
    {
        List<string> preopens = [], network = [], environment = [];
        foreach (var grant in granted)
        {
            var separator = grant.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var value = grant[(separator + 1)..];
            switch (grant[..separator])
            {
                case "fs-read" or "fs-write":
                    preopens.Add(value);
                    break;
                case "network":
                    network.Add(value);
                    break;
                case "env":
                    environment.Add(value);
                    break;
            }
        }

        return new WasiCapabilityGrants(
            preopens.Count == 0 ? null : preopens,
            network.Count == 0 ? null : network,
            environment.Count == 0 ? null : environment);
    }
}
