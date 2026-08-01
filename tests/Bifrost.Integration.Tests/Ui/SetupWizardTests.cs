using System.Reflection;
using System.Text.Json;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Setup;
using Bifrost.Core;
using Bifrost.Core.Diagnostics.Upstreams;
using Bifrost.Integration.Tests.Gateway;
using Bifrost.Server.Bootstrap;
using Bifrost.Web;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

using Role = Bifrost.Abstractions.Role;

namespace Bifrost.Integration.Tests.Ui;

/// <summary>
/// Der gefuehrte Erstaufbau (WP4.4).
///
/// <para>
/// <b>Warum diese Tests gegen die Dienste laufen und nicht gegen einen Browser.</b> Die Razor-Seite
/// ist eine Huelle: Sie ruft <see cref="ISetupWizard"/>, <see cref="ISetupSessionStore"/> und
/// dieselben Anwendungsdienste, die auch <c>Servers.razor</c>, <c>Rbac.razor</c> und
/// <c>Tools.razor</c> benutzen. Was hier durchlaeuft, laeuft dort durch — und was sich hier nicht
/// pruefen laesst, waere in der Seite ebenso wenig pruefbar. Volle Browser-E2E ist in dieser CI
/// bewusst nicht vorhanden (siehe <c>WebUiTests</c>); der Augenschein ersetzt sie nicht, er
/// ergaenzt sie.
/// </para>
///
/// <para>
/// <b>Die Frage, um die es an jedem Schritt geht:</b> kommt der Nutzer weiter, oder steht er? Sie
/// ist beantwortbar, weil die Antwort in <see cref="SetupProgress"/> steht und nicht im Markup.
/// </para>
/// </summary>
public sealed class SetupWizardTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public SetupWizardTests(GatewayFixture gw) => _gw = gw;

    private ISetupWizard Wizard => _gw.Services.GetRequiredService<ISetupWizard>();

    private ISetupSessionStore Store => _gw.Services.GetRequiredService<ISetupSessionStore>();

    private IBootstrapService Bootstrap => _gw.Services.GetRequiredService<IBootstrapService>();

    // ── Der glückliche Pfad ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Alle neun Schritte, in der Reihenfolge, in der ein Mensch sie geht — bis zu einem echten
    /// Toolaufruf gegen einen echten Prozess. Der Erfolg wird am Ergebnis des Aufrufs festgemacht
    /// und an nichts sonst.
    /// </summary>
    [Fact]
    public async Task Der_glueckliche_pfad_geht_durch_alle_neun_schritte()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = Store.Start();

        // ── 1. Betriebsart ──────────────────────────────────────────────────
        var facts = await Wizard.ReadFactsAsync(ct);
        SetupProgress.BlockerFor(session, facts, signedIn: false).Should().NotBeNull(
            "ohne gewaehlte Betriebsart ist Schritt 1 nicht fertig");

        session.Mode = SetupSecurityMode.Workbench;
        SetupProgress.BlockerFor(session, facts, signedIn: false).Should().BeNull();
        session.Step = SetupProgress.Next(session);
        session.Step.Should().Be(SetupStep.AdminAccess);

        // ── 2. Zugang ───────────────────────────────────────────────────────
        var admin = await RedeemAsync(ct);
        session.Owner = admin;
        facts = await Wizard.ReadFactsAsync(ct);
        facts.Access.AnyAdmin.Should().BeTrue();
        SetupProgress.BlockerFor(session, facts, signedIn: true).Should().BeNull();
        session.Step = SetupProgress.Next(session);

        // ── 3. Schlüsselring ────────────────────────────────────────────────
        session.Step.Should().Be(SetupStep.KeyRing);
        if (!facts.KeyRing.Declared)
        {
            SetupProgress.BlockerFor(session, facts, signedIn: true).Should().NotBeNull(
                "ein unerklaerter Schluesselring haelt den Schritt an, bis jemand ihn zur Kenntnis nimmt");
            session.KeyRingAcknowledged = true;
        }

        SetupProgress.BlockerFor(session, facts, signedIn: true).Should().BeNull();
        session.Step = SetupProgress.Next(session);

        // ── 4. Quelle: eine fremde Konfiguration einlesen ───────────────────
        session.Step.Should().Be(SetupStep.Source);
        var slug = $"wiz-{Guid.NewGuid():N}"[..12];
        var outcome = Wizard.Analyse(session, EchoDocument(slug), "wizard-test.json");
        session.Source = SetupSourceKind.Import;

        outcome.AnyApplicable.Should().BeTrue(outcome.Summary);
        session.Entries.Should().ContainSingle().Which.SourceName.Should().Be(slug);
        SetupProgress.BlockerFor(session, facts, signedIn: true).Should().BeNull();
        session.Step = SetupProgress.Next(session);

        // ── 5. Befunde und Bestätigung DIESER Auswahl ───────────────────────
        session.Step.Should().Be(SetupStep.ImportReview);
        session.Selected.Should().Contain(slug, "anwendbare Eintraege sind vorausgewaehlt");

        var confirmations = Wizard.ConfirmationsFor(session);
        if (confirmations.Count > 0)
        {
            var refused = await Wizard.ApplySelectionAsync(session, admin, ct);
            refused.Refusal.Should().NotBeNull(
                "ohne Bestaetigung der Risiken dieser Auswahl wird nicht angelegt");
            refused.Created.Should().BeEmpty();
            session.RisksConfirmed = true;
        }

        var applied = await Wizard.ApplySelectionAsync(session, admin, ct);
        applied.Refusal.Should().BeNull();
        applied.Created.Should().ContainSingle().Which.Slug.Should().Be(slug);
        SetupProgress.BlockerFor(session, facts, signedIn: true).Should().BeNull();
        session.Step = SetupProgress.Next(session);

        // ── 6. Verbindung und Discovery ─────────────────────────────────────
        session.Step.Should().Be(SetupStep.Connection);
        var server = applied.Created[0].Id;

        // Ein eingelesener Server kommt AUSGESCHALTET an — das ist die Kernaussage des Imports und
        // keine Nachlaessigkeit: Ein Plan, dessen Server bereits liefen, haette den Unterschied
        // zwischen „analysiert" und „angelegt" nur noch im Namen. Der Wizard schaltet ihn deshalb
        // nicht von selbst ein; Schritt 6 hat dafuer einen Knopf.
        _gw.Supervisor.GetStatus(server)!.State.Should().Be(
            UpstreamState.Stopped, "ein Import aktiviert nichts von selbst");
        await _gw.Supervisor.SetEnabledAsync(server, true, ct);

        try
        {
            // 60 s statt der ueblichen 30: Hier startet ein echter Prozess, und im Gesamtlauf tun
            // das mehrere Testklassen gleichzeitig. Ein Wartefenster, das nur auf der leeren
            // Maschine reicht, meldet unter Last einen Fehler, den es nicht gibt — und dann sucht
            // jemand im Wizard nach einer Ursache, die im Zeitplan liegt.
            await IntegrationSupport.WaitUntilAsync(
                () => _gw.Supervisor.GetStatus(server)?.State == UpstreamState.Healthy,
                timeoutMs: 60000,
                because: $"der importierte EchoServer '{slug}' muss hochkommen");
        }
        catch (TimeoutException exception)
        {
            // Der Grund gehoert in die Fehlermeldung, nicht in den naechsten Testlauf: „kommt nicht
            // hoch" ohne den letzten Fehler ist genau die Meldung, gegen die WP4.6 angetreten ist.
            var stuck = _gw.Supervisor.GetStatus(server);
            Assert.Fail($"{exception.Message} Zustand '{stuck?.State}', letzter Fehler: "
                + (stuck?.LastError ?? "—"));
        }

        var configs = await _gw.Services.GetRequiredService<IUpstreamConfigStore>()
            .GetAllLatestAsync(ct);
        var report = await _gw.Services.GetRequiredService<IUpstreamConnectionDiagnostics>()
            .DiagnoseAsync(configs[server].Config, ct);
        report.Succeeded.Should().BeTrue(
            "die Zeitlinie aus WP4.6 laeuft denselben Weg wie das Anschliessen; sie darf hier nicht "
            + "zu einem anderen Ergebnis kommen. Erste Ursache: {0}",
            report.FirstFailure?.Check.Summary);
        report.Negotiation!.ToolCount.Should().BeGreaterThan(0);

        facts = await Wizard.ReadFactsAsync(ct);
        SetupProgress.BlockerFor(session, facts, signedIn: true).Should().BeNull();
        session.Step = SetupProgress.Next(session);

        // ── 7. Agent, Rolle, Profil ─────────────────────────────────────────
        session.Step.Should().Be(SetupStep.Agent);
        SetupProgress.BlockerFor(session, facts, signedIn: true).Should().NotBeNull(
            "ohne Identitaet ist Schritt 7 nicht fertig");

        var (identity, apiKey, agentName) = await CreateAgentAsync(facts, ct);
        session.Identity = identity;
        session.AgentName = agentName;
        SetupProgress.BlockerFor(session, facts, signedIn: true).Should().BeNull();
        session.Step = SetupProgress.Next(session);

        // ── 8. Client-Snippet ───────────────────────────────────────────────
        session.Step.Should().Be(SetupStep.Snippet);
        var snippets = ClientConfigSnippets.Build(
            new Uri("https://gateway.example.test"), agentName, apiKey);
        snippets.Should().NotBeEmpty();
        snippets.Should().AllSatisfy(snippet => snippet.Content.Should().Contain(apiKey));
        session.Step = SetupProgress.Next(session);

        // ── 9. Der echte Aufruf ─────────────────────────────────────────────
        session.Step.Should().Be(SetupStep.TestCall);
        var catalog = _gw.Services.GetRequiredService<IToolCatalog>();
        var tool = catalog.Snapshot.Single(entry =>
            entry.Kind == CatalogEntryKind.Tool && entry.Name.Value.StartsWith(slug, StringComparison.Ordinal));

        var result = await _gw.Invoker.InvokeAsync(
            new ToolInvocationRequest(
                identity,
                CallOrigin.Ui,
                tool.Name,
                JsonSerializer.Deserialize<JsonElement>("""{"message":"hallo"}"""),
                null),
            ct);

        result.Status.Should().Be(
            InvocationStatus.Success,
            "der neunte Schritt ist erst dann durch, wenn ein Agent mit eigener Identitaet wirklich "
            + "aufgerufen hat: {0}",
            result.ErrorMessage);

        // Und er steht im Audit — das ist die Zusage, die Schritt 9 dem Nutzer gibt.
        await IntegrationSupport.WaitUntilAsync(async () =>
            (await _gw.AuditQuery.QueryAsync(
                new AuditFilter(Kind: AuditEventKind.ToolCall, ToolPrefix: tool.Name.Value), ct))
                .TotalCount >= 1,
            because: "der Testaufruf gehoert ins Audit-Log");
    }

    // ── Ein Fehler an jedem Hauptschritt ────────────────────────────────────────────────────────

    /// <summary>
    /// An jedem der neun Schritte ein Fehlgriff — und je die Frage, ob der Nutzer weiterkommt.
    ///
    /// <para>
    /// <b>Die Zusage lautet nicht „nichts geht schief", sondern „es steht dran".</b> Deshalb prueft
    /// dieser Test zwei Dinge zusammen: dass der Schritt <em>nicht</em> weitergeht und dass es dafuer
    /// einen Grund im Klartext gibt. Ein Wizard, der stumm stehen bleibt, ist der Fall, in dem
    /// jemand abbricht.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(SetupStep.SecurityMode)]
    [InlineData(SetupStep.AdminAccess)]
    [InlineData(SetupStep.KeyRing)]
    [InlineData(SetupStep.Source)]
    [InlineData(SetupStep.ImportReview)]
    [InlineData(SetupStep.Connection)]
    [InlineData(SetupStep.Agent)]
    public async Task Ein_fehler_an_einem_hauptschritt_haelt_genau_dort_an_und_sagt_warum(SetupStep step)
    {
        var ct = TestContext.Current.CancellationToken;
        var facts = await Wizard.ReadFactsAsync(ct);

        // Ein Vorgang, der bis genau vor diesen Schritt alles hat — und in diesem Schritt nichts.
        var session = Broken(step);
        var signedIn = step > SetupStep.AdminAccess;

        var blocker = SetupProgress.BlockerFor(session, Situation(facts, step), signedIn);
        blocker.Should().NotBeNullOrWhiteSpace(
            "Schritt {0} ist nicht fertig und muss sagen, woran es liegt", step);
        blocker!.Length.Should().BeGreaterThan(20, "ein Grund ist ein Satz, kein Code");
    }

    /// <summary>
    /// Die beiden Schritte, die keinen eigenen Fehlgriff kennen — sie haengen an dem, was vorher
    /// passiert ist. Der Vollstaendigkeit halber ausdruecklich geprueft, statt in der Liste oben zu
    /// fehlen und wie ein Versehen auszusehen.
    ///
    /// <para>
    /// <b>Der Zugang wird hier hergestellt, nicht vorausgesetzt.</b> <see cref="SetupProgress.Normalise"/>
    /// prueft zuerst die Instanz und erst danach den Vorgang: Ohne Zugang faellt <em>jeder</em> Schritt
    /// hinter 2 auf 2 zurueck — zu Recht, ab Schritt 3 legt der Wizard Dinge an. Wer diesen Test gegen
    /// die rohen Fakten laufen laesst, prueft deshalb nicht Schritt 8, sondern nur, ob vorher zufaellig
    /// ein anderer Test derselben Fixture den Erstzugang eingeloest hat. Genau das ist im CI
    /// auseinandergegangen: Der Vorgang stand auf 2 statt auf 7, und die Begruendung sagte es
    /// woertlich („diese Installation hat noch keinen Zugang"). Dieselbe Ueberlegung wie bei
    /// <see cref="Situation"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Schritt_8_und_9_haengen_am_agenten_und_nicht_an_sich_selbst()
    {
        var ct = TestContext.Current.CancellationToken;
        var read = await Wizard.ReadFactsAsync(ct);
        var facts = read with { Access = read.Access with { AnyAdmin = true } };

        var session = Store.Start();
        session.Owner = "pruefer";
        session.Step = SetupStep.Snippet;

        // Ohne Identitaet faellt der Vorgang auf Schritt 7 zurueck — mit Begruendung.
        var (step, reason) = SetupProgress.Normalise(session, facts, signedIn: true);
        step.Should().Be(SetupStep.Agent);
        reason.Should().NotBeNullOrWhiteSpace();

        session.Identity = IdentityId.New();
        SetupProgress.Normalise(session, facts, signedIn: true).Step.Should().Be(SetupStep.Snippet);
        SetupProgress.Normalise(session, facts, signedIn: true).Reason.Should().BeNull(
            "wer nicht zurueckgesetzt wird, bekommt auch keinen Hinweis vorgesetzt");
    }

    /// <summary>
    /// Der Fehlgriff in Schritt 2, am echten Erstzugangspfad: ein falsches Token richtet nichts ein
    /// und verbrennt auch nichts. Der Nutzer steht — und darf es noch einmal versuchen.
    /// </summary>
    [Fact]
    public async Task Ein_falsches_setup_token_haelt_schritt_2_an_ohne_den_erstzugang_zu_verbrennen()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Bootstrap.GetStatusAsync(ct);

        var denied = await Bootstrap.RedeemAsync("bfsetup_gibt-es-nicht", "wer", "auch-immer-12345", ct);
        denied.Outcome.Should().BeOneOf(
            BootstrapOutcome.InvalidToken, BootstrapOutcome.NotPending, BootstrapOutcome.Expired);
        denied.Username.Should().BeNull();

        var after = await Bootstrap.GetStatusAsync(ct);
        after.IsPending.Should().Be(before.IsPending,
            "ein Tippfehler im Token darf den Erstzugang nicht entwerten — sonst stuende der "
            + "Betreiber vor einer Installation, in die niemand mehr hineinkommt");
    }

    // ── Teilimport ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Eine Datei mit einem kaputten und zwei heilen Eintraegen: zwei Server und <b>eine benannte
    /// Auslassung</b>.
    /// <para>
    /// Der kaputte Eintrag wird gar nicht erst zum Kandidaten — er taucht deshalb weder unter den
    /// Eintraegen auf noch unter den planweiten Befunden. Ohne die eigene Liste waere er unsichtbar,
    /// und aus drei Eintraegen wuerden wortlos zwei Server.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ein_kaputter_eintrag_ergibt_zwei_server_und_eine_benannte_auslassung()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = Store.Start();
        session.Mode = SetupSecurityMode.Shielded;

        var eins = $"teil-{Guid.NewGuid():N}"[..11];
        var zwei = $"teil-{Guid.NewGuid():N}"[..11];
        var document = $$"""
        {
          "mcpServers": {
            "{{eins}}": { "type": "http", "url": "https://eins.example.test/mcp" },
            "kaputt":   { "beschreibung": "hier fehlt command und url" },
            "{{zwei}}": { "type": "http", "url": "https://zwei.example.test/mcp" }
          }
        }
        """;

        var outcome = Wizard.Analyse(session, document, "teilimport.json");

        outcome.AnyApplicable.Should().BeTrue();
        session.Entries.Select(entry => entry.SourceName).Should().BeEquivalentTo([eins, zwei]);
        session.UnreadableEntries.Should().ContainSingle()
            .Which.Path.Should().Be("mcpServers/kaputt",
                "die Auslassung wird an ihrem Ort benannt, nicht gezaehlt");

        var report = await Wizard.ApplySelectionAsync(session, "pruefer", ct);

        report.Refusal.Should().BeNull();
        report.Created.Select(created => created.Slug).Should().BeEquivalentTo([eins, zwei]);
        report.Skipped.Should().ContainSingle().Which.SourceName.Should().Be("mcpServers/kaputt");
        report.Skipped[0].Reason.Should().Contain(
            "BFR-IMP-0003", "die Auslassung nennt ihren Grund mit stabilem Code");
    }

    /// <summary>
    /// Die Bestaetigung gilt der <b>Auswahl</b>, nicht dem Plan. Wer einen riskanten Eintrag
    /// abwaehlt, muss ihn nicht bestaetigen — sonst waere die Bestaetigung eine Formalie, und eine
    /// Formalie liest niemand.
    /// </summary>
    [Fact]
    public void Die_bestaetigung_folgt_der_auswahl_und_nicht_dem_plan()
    {
        var session = Store.Start();
        var document = """
        {
          "mcpServers": {
            "harmlos":  { "type": "http", "url": "https://harmlos.example.test/mcp" },
            "riskant":  { "command": "npx", "args": ["-y", "irgendein-paket"] }
          }
        }
        """;

        Wizard.Analyse(session, document, null);

        var mitBeiden = Wizard.ConfirmationsFor(session);
        mitBeiden.Should().NotBeEmpty("der npx-Eintrag laedt beim Start Code nach");

        session.Selected.Remove("riskant");
        Wizard.ConfirmationsFor(session).Should().BeEmpty(
            "wer den riskanten Eintrag abwaehlt, bestaetigt ihn nicht mit");
    }

    // ── Refresh ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ein Neuladen mitten im Ablauf verliert nichts: Der Zustand liegt im Serverprozess, im
    /// Browser steht nur die Kennung.
    /// </summary>
    [Fact]
    public void Ein_refresh_mitten_im_ablauf_verliert_nichts()
    {
        var session = Store.Start();
        session.Owner = "pruefer";
        session.Mode = SetupSecurityMode.Shielded;
        session.ContainerImage = "ghcr.io/beispiel/server:1.2";
        session.KeyRingAcknowledged = true;
        session.Step = SetupStep.ImportReview;
        Wizard.Analyse(session, """
        {
          "mcpServers": {
            "eins": { "type": "http", "url": "https://eins.example.test/mcp" },
            "zwei": { "type": "http", "url": "https://zwei.example.test/mcp" }
          }
        }
        """, "refresh.json");
        session.Selected.Remove("zwei");
        session.RisksConfirmed = true;
        Store.Touch(session);

        // Das Neuladen: derselbe Handle aus dem Cookie, ein frischer Circuit.
        var resumed = Store.Reopen(session.Handle, "pruefer");

        resumed.Reason.Should().BeNull();
        resumed.Session.Should().NotBeNull();
        resumed.Session!.Step.Should().Be(SetupStep.ImportReview);
        resumed.Session.Mode.Should().Be(SetupSecurityMode.Shielded);
        resumed.Session.ContainerImage.Should().Be("ghcr.io/beispiel/server:1.2");
        resumed.Session.KeyRingAcknowledged.Should().BeTrue();
        resumed.Session.Entries.Should().HaveCount(2);
        resumed.Session.Selected.Should().Equal("eins");
        resumed.Session.RisksConfirmed.Should().BeTrue();
        resumed.Session.Plan.Should().NotBeNull("ohne Plan gaebe es keine Auswahl je Eintrag mehr");
    }

    /// <summary>
    /// Und die Gegenrichtung: Ein unbekannter oder fremder Vorgang wird <b>mit Begruendung</b>
    /// abgewiesen, nicht wortlos.
    /// </summary>
    [Fact]
    public void Ein_unbekannter_oder_fremder_vorgang_wird_mit_begruendung_abgewiesen()
    {
        Store.Reopen("gibt-es-nicht", null).Should().Match<SetupResume>(
            resume => resume.Session == null && resume.Reason != null);

        var fremd = Store.Start();
        fremd.Owner = "eigentuemer";
        Store.Touch(fremd);

        var uebernahme = Store.Reopen(fremd.Handle, "jemand-anderes");
        uebernahme.Session.Should().BeNull();
        uebernahme.Reason.Should().NotBeNullOrWhiteSpace();

        Store.Reopen(fremd.Handle, "eigentuemer").Session.Should().NotBeNull(
            "dem Eigentuemer gehoert der Vorgang weiterhin");

        // Ohne Kennung ist es kein Fehler, sondern ein erster Besuch — und der bekommt keinen
        // Hinweis vorgesetzt.
        Store.Reopen(null, null).Should().Match<SetupResume>(
            resume => resume.Session == null && resume.Reason == null);
    }

    /// <summary>
    /// Die Route der Seite und die Konstante, auf die alles andere zeigt, sind dieselbe.
    ///
    /// <para>
    /// <b>Warum das einen Test wert ist.</b> Razor nimmt in <c>@@page</c> nur ein Literal — die
    /// Konstante kann dort nicht stehen. Damit gibt es die Adresse zweimal, und die zweite Fassung
    /// ist die, die veraltet: Ein Rueckweg nach dem Einloesen des Setup-Tokens, der auf eine Route
    /// zeigt, die es nicht mehr gibt, endet still auf dem Dashboard.
    /// </para>
    /// <para>
    /// Und die Route haengt <b>nicht</b> unter <c>/setup/</c>. Dort gilt die Zusage aus WP4.3, dass
    /// alles anonym mit einer Absage antwortet; eine Oberflaechenseite waere die Ausnahme, die die
    /// Zusage aufweicht.
    /// </para>
    /// </summary>
    [Fact]
    public void Die_route_des_assistenten_steht_nur_an_einer_stelle()
    {
        var page = typeof(UiPolicies).Assembly.GetTypes()
            .Single(type => type.Name == "SetupWizard");

        var routes = page.GetCustomAttributes<RouteAttribute>()
            .Select(route => route.Template)
            .ToList();

        routes.Should().Equal(UiNavigation.SetupWizardRoute);
        UiNavigation.SetupWizardRoute.Should().NotStartWith(
            "/setup/",
            "unterhalb von /setup/ gilt die Zusage aus WP4.3: anonym gibt es dort nur eine Absage");
    }

    // ── Helfer ──────────────────────────────────────────────────────────────────────────────────

    private async Task<string> RedeemAsync(CancellationToken ct)
    {
        var status = await Bootstrap.GetStatusAsync(ct);
        if (status.Phase is BootstrapPhase.Redeemed or BootstrapPhase.Established)
        {
            // Diese Fixture hat den Erstzugang schon hinter sich (ein anderer Test in derselben
            // Klasse). Der Wizard sieht dann genau das, was ein zweiter Browser saehe.
            var existing = await _gw.UiUsers.ListAsync(ct);
            return existing[0].Username;
        }

        var issued = await Bootstrap.IssueAsync(BootstrapOrigin.FirstStart, ct);
        issued.Token.Should().NotBeNull("eine frische Installation stellt ein Setup-Token aus");

        var name = $"wiz-admin-{Guid.NewGuid():N}"[..18];
        var redeemed = await Bootstrap.RedeemAsync(issued.Token, name, "wizard-passwort-123", ct);
        redeemed.Outcome.Should().Be(BootstrapOutcome.Redeemed, redeemed.Description);
        return redeemed.Username!;
    }

    private async Task<(IdentityId Identity, string ApiKey, string Name)> CreateAgentAsync(
        SetupFacts facts, CancellationToken ct)
    {
        var rbac = _gw.Services.GetRequiredService<IRbacManagement>();
        var name = $"wiz-agent-{Guid.NewGuid():N}"[..18];

        // Genau das, was Schritt 7 tut: je angeschlossenem Server eine Freigabe, kein Global-Grant.
        var grants = facts.Upstreams
            .Select(server => new Grant(
                new PermissionScope(server.Id, null),
                [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt]))
            .ToList();
        var role = new Role(RoleId.New(), $"{name}-rolle", grants);
        await rbac.UpsertRoleAsync(role, ct);

        var serverIds = facts.Upstreams.Select(server => server.Id).ToHashSet();
        var pinned = _gw.Services.GetRequiredService<IToolCatalog>().Snapshot
            .Where(entry => entry.Kind is CatalogEntryKind.Tool && serverIds.Contains(entry.Server))
            .Select(entry => entry.Name)
            .ToList();
        var profile = new ToolProfile(ProfileId.New(), $"{name}-profil", pinned, LazyToolsEnabled: true);
        await rbac.UpsertProfileAsync(profile, ct);

        var identity = new Identity(IdentityId.New(), name, IdentityKind.Agent, [role.Id], profile.Id);
        await rbac.UpsertIdentityAsync(identity, ct);

        var issued = await _gw.ApiKeys.IssueAsync(identity.Id, $"{name}-key", null, ct);
        return (identity.Id, issued.PlaintextKey, name);
    }

    /// <summary>Eine Konfiguration im generischen mcp-Format, die den EchoServer startet.</summary>
    private static string EchoDocument(string slug)
    {
        var document = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                [slug] = new Dictionary<string, object>
                {
                    ["command"] = TestPaths.EchoServerExecutable,
                    ["args"] = Array.Empty<string>(),
                },
            },
        };

        return JsonSerializer.Serialize(document);
    }

    /// <summary>
    /// Ein Vorgang, dem genau in <paramref name="step"/> das Noetige fehlt — und der davor
    /// vollstaendig ist. So prueft der Test den einzelnen Schritt und nicht die Summe.
    /// </summary>
    private SetupSession Broken(SetupStep step)
    {
        var session = Store.Start();
        session.Step = step;

        if (step > SetupStep.SecurityMode)
        {
            session.Mode = SetupSecurityMode.Shielded;
        }

        if (step > SetupStep.KeyRing)
        {
            session.KeyRingAcknowledged = true;
        }

        if (step > SetupStep.Source)
        {
            // Quelle gewaehlt und eingelesen, aber noch nichts uebernommen — der Fehlgriff in
            // Schritt 5.
            session.Source = SetupSourceKind.Import;
            Wizard.Analyse(
                session,
                """{ "mcpServers": { "eins": { "type": "http", "url": "https://eins.example.test/mcp" } } }""",
                null);
        }

        if (step > SetupStep.ImportReview)
        {
            session.Applied = new SetupApplyReport([], []);
        }

        return session;
    }

    /// <summary>
    /// Zwei Schritte haengen an der <b>Instanz</b> und nicht am Vorgang: Schritt 3 am erklaerten
    /// Schluesselring, Schritt 6 an einem angeschlossenen Server. Ihre Lage wird hier ausdruecklich
    /// hergestellt, statt sie vorauszusetzen — sonst waere der Test still gruen, sobald ein anderer
    /// Test derselben Fixture die Lage verschiebt.
    /// </summary>
    private static SetupFacts Situation(SetupFacts facts, SetupStep step) => step switch
    {
        SetupStep.KeyRing => facts with { KeyRing = facts.KeyRing with { Declared = false } },
        SetupStep.Connection => facts with { Upstreams = [] },
        _ => facts,
    };
}
