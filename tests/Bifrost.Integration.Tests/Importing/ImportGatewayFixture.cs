using System.Net;

using Bifrost.Integration.Tests.Gateway;
using Bifrost.Server.Bootstrap;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace Bifrost.Integration.Tests.Importing;

/// <summary>
/// Derselbe Host wie in den übrigen E2E-Tests, mit genau einer Zutat: Die Gegenstelle einer Anfrage
/// lässt sich setzen.
///
/// <para>
/// <b>Warum das nötig ist.</b> Der Setup-Weg des Imports ist ein <em>lokaler</em> Weg — er antwortet
/// nur, wenn die Anfrage vom Rechner des Gateways kommt. Über den In-Memory-Transport von
/// <c>WebApplicationFactory</c> hat eine Anfrage überhaupt keine Adresse, und das ist kein
/// Testartefakt, das man wegdrücken darf: Der Dienst behandelt „keine Adresse" fail-closed als
/// <em>nicht lokal</em>. Ohne diese Zutat ließe sich also nur der Ablehnungsfall prüfen — und ein
/// Test, der nur ablehnende Antworten sieht, wäre auch dann grün, wenn der Endpunkt gar nichts
/// könnte.
/// </para>
///
/// <para>
/// Die Adresse kommt <b>je Anfrage</b> aus einem Kopf, nicht aus einem Feld der Fixture: Ein
/// veränderlicher Zustand an einer geteilten Fixture machte die Tests von ihrer Reihenfolge
/// abhängig.
/// </para>
/// </summary>
public class ImportGatewayFixture : GatewayFixture
{
    /// <summary>Der Kopf, mit dem ein Test die Gegenstelle vorgibt. Existiert nur im Testaufbau.</summary>
    public const string RemoteAddressHeader = "X-Test-Remote-Ip";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, RemoteAddressFilter>());
    }

    /// <summary>Das ausstehende Setup-Token aus der Übergabedatei — oder <c>null</c>.</summary>
    public async Task<string?> PendingSetupTokenAsync()
    {
        var bootstrap = Services.GetRequiredService<IBootstrapService>();
        var status = await bootstrap.GetStatusAsync(TestContext.Current.CancellationToken);
        if (!status.IsPending || status.HandoverPath is null || !File.Exists(status.HandoverPath))
        {
            return null;
        }

        var handover = await File.ReadAllTextAsync(
            status.HandoverPath, TestContext.Current.CancellationToken);
        return handover
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith(BootstrapToken.Prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Bringt den Erstzugang hinter sich — der Zustand, in dem die DoD gilt: „Kein Setup-/Importendpoint
    /// ist nach Abschluss anonym erreichbar."
    /// </summary>
    public async Task CompleteBootstrapAsync()
    {
        if (await PendingSetupTokenAsync() is not { } token)
        {
            return;
        }

        var result = await Services.GetRequiredService<IBootstrapService>().RedeemAsync(
            token, "wp43-betreiber", "ein-langes-passwort", TestContext.Current.CancellationToken);
        if (result.Outcome is not BootstrapOutcome.Redeemed)
        {
            throw new InvalidOperationException(
                $"Der Erstzugang liess sich nicht einloesen: {result.Outcome} — {result.Description}");
        }
    }

    /// <summary>
    /// Setzt die Gegenstelle aus dem Testkopf, ganz vorn in der Kette. Ohne Kopf bleibt sie
    /// <c>null</c> — genau die Lage, die der Dienst als „nicht lokal" behandelt.
    /// </summary>
    private sealed class RemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(async (context, following) =>
                {
                    if (context.Request.Headers.TryGetValue(RemoteAddressHeader, out var value)
                        && IPAddress.TryParse(value.ToString(), out var address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    await following(context);
                });

                next(app);
            };
    }
}
