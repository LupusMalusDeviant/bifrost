using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Persistence;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Freigabe per Rückfrage im Moment des Aufrufs (ADR-0012, Erweiterung).
/// <para>
/// Die Warteschlange verlangt einen Wechsel in Oberfläche oder CLI. Bei einem Werkzeug, das man
/// mehrmals täglich braucht, führt das dazu, dass jemand die Freigabepflicht abschaltet — und dann
/// schützt sie gar nicht mehr. Eine Rückfrage im laufenden Gespräch kostet einen Klick.
/// </para>
/// <para>
/// <b>Das ist keine Selbstfreigabe des Agenten:</b> Die Frage geht an den Client, die Antwort kommt
/// vom Menschen davor. Deshalb prüfen diese Tests vor allem die <em>Ablehnung</em> und den Fall
/// „Client kann nicht fragen" — bei der Zustimmung ist wenig zu verlieren, bei den anderen beiden
/// alles.
/// </para>
/// </summary>
public sealed class ApprovalElicitationTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public ApprovalElicitationTests(GatewayFixture gw) => _gw = gw;

    private ApprovalPolicyStore Policy => _gw.Services.GetRequiredService<ApprovalPolicyStore>();

    private IApprovalStore Approvals => _gw.Services.GetRequiredService<IApprovalStore>();

    /// <summary>Eigener Upstream je Test — die Freigabepflicht haengt am Tool-Namen.</summary>
    private async Task<NamespacedToolName> EchoToolAsync(string slug)
    {
        await _gw.AddEchoUpstreamAsync(slug);
        return new NamespacedToolName($"{slug}__echo");
    }

    /// <summary>
    /// Ein Client ohne die Fähigkeit darf nichts verlieren: Der Aufruf landet wie bisher in der
    /// Warteschlange, und die Meldung nennt die Id.
    /// <para>
    /// <b>Der Client ist hier bewusst auf <c>2025-11-25</c> festgenagelt.</b> „Kann nicht gefragt
    /// werden" gibt es seit der Revision 2026-07-28 nicht mehr als Zustand: Dort meldet kein Client
    /// eine Elicitation-Faehigkeit, MRTR ist das einzige Signal, und der Gateway fragt jeden, der es
    /// spricht. Auf dem alten Stand gibt es den Fall weiter — und dort muss dieser Weg stimmen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Without_the_capability_the_call_still_goes_to_the_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync($"el{Guid.NewGuid():N}"[..10]);
        await Policy.SetAsync(tool, true, ct);
        try
        {
            var (_, apiKey) = await _gw.SeedAdminAsync($"elic-ohne-{Guid.NewGuid():N}");
            await using var client = await _gw.ConnectLegacyClientAsync(apiKey);

            var result = await client.CallToolAsync(
                tool.Value, new Dictionary<string, object?> { ["message"] = "hi" },
                cancellationToken: ct);

            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("ApprovalRequired");
            (await Approvals.ListAsync(ApprovalState.Pending, ct)).Should().NotBeEmpty(
                "ohne Rückfragemöglichkeit ist die Warteschlange der Weg — nicht der Papierkorb");
        }
        finally
        {
            await Policy.SetAsync(tool, false, ct);
        }
    }

    /// <summary>
    /// Der Kern: Eine erteilte Freigabe gilt für <b>genau einen</b> Aufruf. Sonst wäre ein einmal
    /// gegebener Klick ein Dauerrecht.
    /// </summary>
    [Fact]
    public async Task An_approval_is_consumed_by_a_single_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync($"el{Guid.NewGuid():N}"[..10]);
        await Policy.SetAsync(tool, true, ct);
        try
        {
            // Alter Stand ohne Formular-Handler: Dieser Test handelt von der Warteschlange und vom
            // Verbrauch der Freigabe, nicht von der Rueckfrage. Ein Client, der gefragt werden
            // kann, wuerde hier gefragt — und der Test pruefte etwas anderes als er behauptet.
            var (_, apiKey) = await _gw.SeedAdminAsync($"elic-einmal-{Guid.NewGuid():N}");
            await using var client = await _gw.ConnectLegacyClientAsync(apiKey);
            var args = new Dictionary<string, object?> { ["message"] = "einmal" };

            var first = await client.CallToolAsync(tool.Value, args, cancellationToken: ct);
            first.IsError.Should().BeTrue("der erste Aufruf legt die Anfrage an");

            // Nach dem WERKZEUG suchen, nicht die letzte nehmen: Der vorige Test laesst eine
            // wartende Anfrage liegen, und welche "die letzte" ist, haengt von der Reihenfolge ab.
            // Genau daran ist der Test auf Windows gescheitert, waehrend er lokal durchlief.
            var pending = (await Approvals.ListAsync(ApprovalState.Pending, ct))
                .Single(r => r.Tool == tool);
            await Approvals.DecideAsync(pending.Id, approved: true, ct);

            var second = await client.CallToolAsync(tool.Value, args, cancellationToken: ct);
            var third = await client.CallToolAsync(tool.Value, args, cancellationToken: ct);

            second.IsError.Should().NotBe(true, "die Freigabe war für genau diesen Aufruf da");
            third.IsError.Should().BeTrue(
                "sie ist verbraucht — ein Klick darf kein Dauerrecht werden");
        }
        finally
        {
            await Policy.SetAsync(tool, false, ct);
        }
    }

    /// <summary>
    /// Eine abgelehnte Freigabe muss den Aufruf endgültig stoppen — und darf ihn nicht
    /// stillschweigend wieder in die Warteschlange legen, wo ihn jemand später doch erteilt.
    /// </summary>
    [Fact]
    public async Task A_denied_approval_stops_the_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync($"el{Guid.NewGuid():N}"[..10]);
        await Policy.SetAsync(tool, true, ct);
        try
        {
            // Alter Stand ohne Formular-Handler — siehe oben: Hier geht es um die Wirkung einer
            // Ablehnung im Store, nicht um den Weg zum Menschen.
            var (_, apiKey) = await _gw.SeedAdminAsync($"elic-nein-{Guid.NewGuid():N}");
            await using var client = await _gw.ConnectLegacyClientAsync(apiKey);
            var args = new Dictionary<string, object?> { ["message"] = "nein" };

            await client.CallToolAsync(tool.Value, args, cancellationToken: ct);
            // Nach dem WERKZEUG suchen, nicht die letzte nehmen: Der vorige Test laesst eine
            // wartende Anfrage liegen, und welche "die letzte" ist, haengt von der Reihenfolge ab.
            // Genau daran ist der Test auf Windows gescheitert, waehrend er lokal durchlief.
            var pending = (await Approvals.ListAsync(ApprovalState.Pending, ct))
                .Single(r => r.Tool == tool);
            await Approvals.DecideAsync(pending.Id, approved: false, ct);

            var afterDenial = await client.CallToolAsync(tool.Value, args, cancellationToken: ct);

            afterDenial.IsError.Should().BeTrue();
            (await Approvals.ListAsync(ApprovalState.Approved, ct))
                .Should().NotContain(r => r.Id == pending.Id,
                    "eine abgelehnte Anfrage darf nicht als freigegeben enden");
        }
        finally
        {
            await Policy.SetAsync(tool, false, ct);
        }
    }
}
