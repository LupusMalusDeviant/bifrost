using System.Diagnostics;
using System.Text.Json;

using AwesomeAssertions;

using Bifrost.Abstractions.Setup;
using Bifrost.Security.Tests.Infrastructure;
using Bifrost.Web;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Bifrost.Security.Tests;

/// <summary>
/// Der gefuehrte Erstaufbau (WP4.4) am Mithoerer.
///
/// <para>
/// <b>Warum dieser Weg einen eigenen Test braucht.</b> Der Wizard liest eine <em>fremde</em>
/// Konfigurationsdatei ein — also genau die Sorte Eingabe, die Zugangsdaten in jedem Feld tragen
/// kann: in Umgebungsvariablen, in Kommandozeilenargumenten, im Query-Teil einer Adresse, im
/// Benutzerteil einer URL. Der eingelesene Plan haelt diese Werte im Klartext, weil er sie zum
/// Anlegen braucht. Ab da gibt es genau zwei Stellen, an denen sie herauskommen koennten: die
/// Anzeige und das Protokoll. Dieser Test steht an beiden.
/// </para>
///
/// <para>
/// <b>Vorbild ist <see cref="LogOutputLeakTests"/>:</b> derselbe Negativkorpus, dieselbe Suche nach
/// Bruchstuecken, dieselbe Gegenprobe, dass ueberhaupt mitgeschrieben wurde. Neu ist nur die Frage
/// — nicht was der Dienst schreibt, sondern was der Assistent <em>zeigt</em>.
/// </para>
/// </summary>
public sealed class SetupWizardLeakTests : IClassFixture<SecurityGatewayFixture>, IDisposable
{
    private readonly SecurityGatewayFixture _gateway;
    private readonly CapturingTraceListener _trace = new();

    public SetupWizardLeakTests(SecurityGatewayFixture gateway)
    {
        _gateway = gateway;
        Trace.Listeners.Add(_trace);
    }

    public void Dispose()
    {
        Trace.Listeners.Remove(_trace);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Eine Quelldatei, die in jedem wertetragenden Feld ein Zugangsdatum traegt. Nichts davon darf
    /// in der Anzeige des Wizards stehen, in seinem Protokoll oder im Audit.
    /// </summary>
    [Fact]
    public void Keine_zeile_des_wizards_traegt_einen_wert_aus_der_fremden_konfiguration()
    {
        // Der Host muss laufen, bevor Dienste aufgeloest werden — die Fabrik baut ihn erst beim
        // ersten Zugriff, und ohne Start gaebe es den Wizard-Dienst nicht.
        using var anonymous = _gateway.CreateApiClient(null);
        var wizard = _gateway.Services.GetRequiredService<ISetupWizard>();
        var store = _gateway.Services.GetRequiredService<ISetupSessionStore>();

        var document = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                // stdio: Zugangsdatum in der Umgebung UND im Argument — die beiden Stellen, die
                // eine Negativliste nicht kennt.
                ["lokal"] = new Dictionary<string, object>
                {
                    ["command"] = "/usr/bin/beispiel",
                    ["args"] = new[] { "--api-key", SecretCorpus.ToolArgument },
                    ["env"] = new Dictionary<string, string> { ["TOKEN"] = SecretCorpus.StdioEnv },
                },
                // HTTP: Zugangsdatum im Header, im Query-Teil und im Benutzerteil der Adresse.
                ["entfernt"] = new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = $"https://nutzer:{SecretCorpus.OpenApiCredential}@api.example.test/mcp"
                        + $"?token={SecretCorpus.OAuthToken}",
                    ["headers"] = new Dictionary<string, string>
                    {
                        ["Authorization"] = SecretCorpus.HttpHeader,
                    },
                },
            },
        });

        var session = store.Start();
        session.Owner = "leck-pruefer";
        var outcome = wizard.Analyse(session, document, "leck-probe.json");

        // Gegenprobe zuerst: Der Plan MUSS die Werte tragen — sonst prueft alles Folgende einen
        // Text, in dem ohnehin nichts steht.
        session.Plan.Should().NotBeNull();
        session.Entries.Should().NotBeEmpty("sonst ist die folgende Suche eine Suche im Leeren");

        // 1. Alles, was die Oberflaeche aus diesem Vorgang zeigen kann.
        SecretCorpus.FirstLeakIn(Visible(session, outcome)).Should().BeNull(
            "die Anzeige entsteht aus der Positivliste der Vorschauprojektion — ein Wert, den die "
            + "Erkennung nicht fuer ein Geheimnis hielt, ist immer noch ein Wert");

        // 2. Die Fundstelle muss trotzdem benannt sein: Ort und Erkennungsgrund, nie der Wert.
        session.Entries.SelectMany(entry => entry.Secrets).Should().NotBeEmpty(
            "der Wizard soll sagen, DASS die Quelle Zugangsdaten mitbringt");

        // 3. Und die Protokollkanaele — einschliesslich der Kurzfassung des Vorgangs selbst, die
        //    ein einzelnes LogDebug('{Session}') ins Protokoll schriebe.
        SecretCorpus.FirstLeakIn(session.ToString()).Should().BeNull(
            "SetupSession ist bewusst kein record: Ein erzeugtes ToString() gaebe den Plan mit aus");

        _gateway.Log.Count.Should().BeGreaterThan(0, "sonst prueft der folgende Vergleich einen leeren Text");
        SecretCorpus.FirstLeakIn(_gateway.Log.Text).Should().BeNull();
        SecretCorpus.FirstLeakIn(_trace.Text).Should().BeNull();
    }

    /// <summary>
    /// Die Kennung des Vorgangs ist kein Zugangsdatum — aber sie gehoert trotzdem nicht in ein
    /// Protokoll, das Adressen mitschreibt. Deshalb steht sie in einem Cookie und nicht in der
    /// Adresszeile; dieser Test haelt die Zusage fest, indem er sie gegen die Route prueft.
    /// </summary>
    [Fact]
    public async Task Die_kennung_des_vorgangs_reist_nicht_in_der_adresszeile()
    {
        using var anonymous = _gateway.CreateApiClient(null);
        var store = _gateway.Services.GetRequiredService<ISetupSessionStore>();
        var session = store.Start();

        using var page = await anonymous.GetAsync(
            new Uri(UiNavigation.SetupWizardRoute, UriKind.Relative),
            TestContext.Current.CancellationToken);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        html.Should().NotContain(session.Handle,
            "die Kennung kommt aus dem Cookie und wird nie in die Seite geschrieben — was in der "
            + "Seite steht, steht danach im Verlauf und im Referrer");

        // Und sie steht auch in keinem Set-Cookie: Geschrieben wird sie im Browser, nachdem der
        // Circuit sie vergeben hat. Der Name taucht im eingebetteten Schnipsel auf — das ist der
        // Leser, nicht der Wert.
        page.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(value =>
            value.Contains("bifrost-setup", StringComparison.Ordinal));
    }

    /// <summary>
    /// Alles, was der Wizard aus einem eingelesenen Vorgang anzeigen kann — an einer Stelle
    /// zusammengezogen. Wer dem Vorgang ein Feld hinzufuegt, das die Oberflaeche zeigt, ergaenzt es
    /// <b>hier</b>; sonst prueft dieser Test es nicht.
    /// </summary>
    private static string Visible(SetupSession session, SetupImportOutcome outcome)
    {
        var parts = new List<string>
        {
            outcome.Summary,
            session.ImportSource?.Provider ?? string.Empty,
            session.ImportSource?.OriginPath ?? string.Empty,
        };

        foreach (var entry in session.Entries)
        {
            parts.Add(entry.SourceName);
            parts.Add(entry.Slug);
            parts.Add(entry.DisplayName);
            parts.Add(entry.Kind);
            parts.Add(entry.Transport);
            parts.Add(entry.SourcePath ?? string.Empty);
            parts.AddRange(entry.Findings.Select(f => $"{f.Code} {f.Summary} {f.Path} {f.Remediation}"));
            parts.AddRange(entry.Blockers.Select(f => $"{f.Code} {f.Summary} {f.Path} {f.Remediation}"));
            parts.AddRange(entry.Secrets.Select(s => $"{s.Location} {s.Looked} {s.ValuePresent}"));
        }

        parts.AddRange(session.BlockingFindings.Select(f => $"{f.Code} {f.Summary} {f.Path} {f.Remediation}"));
        parts.AddRange(session.UnreadableEntries.Select(f => $"{f.Code} {f.Summary} {f.Path} {f.Remediation}"));

        return string.Join('\n', parts);
    }
}
