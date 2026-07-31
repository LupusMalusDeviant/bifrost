# Plan 0001 — B.I.F.R.O.S.T: Implementation-Plan / Pflichtenheft

| | |
|---|---|
| **Status** | Aktiv (lebendes Dokument) |
| **Datum** | 2026-07-17 |
| **Basis-PRD** | [0001-bifrost-meta-gateway.md](../prd/0001-bifrost-meta-gateway.md) (Lastenheft) |
| **Bindende ADRs** | [0001](../adr/0001-zentraler-proxy-gateway-statt-direktanbindung.md)–[0008](../adr/0008-api-mcp-bridge-als-erstklassige-fassaden.md) |
| **Rollen** | Senior PM (Scope/DoD), Senior-Tech-Specialist (Architektur/Performance) |

---

## 1. Kontext / Motivation

Das PRD definiert *was* gebaut wird (Meta-MCP-Gateway, 7 Keyfeatures), die ADRs *womit und warum* (.NET 10 + offizielles C# SDK, Proxy-Architektur, Hybrid-Token, Blazor, Supervisor-Prozessmodell, eigenes RBAC, EF Core, gemeinsamer Invocation-Kern). Dieser Plan übersetzt beides in Verträge (Interfaces), Arbeitspakete mit Definition of Done, Teststrategie und Konstruktionsregeln. Er ist das Pflichtenheft: Wer nur dieses Dokument und die ADRs liest, kann bauen.

## 2. Ziele

Identisch mit PRD Z-1 bis Z-6; Abnahme ausschließlich gegen PRD Abschnitt 8 (Akzeptanzkriterien 1–7). Keine Neudefinition hier.

## 3. Architektur-Überblick

### 3.1 Solution-Struktur

```
Bifrost.slnx
├── src/
│   ├── Bifrost.Abstractions/        # NUR Interfaces + DTOs/Records. Keine Dependencies außer BCL.
│   ├── Bifrost.Core/                # Domänenlogik: Katalog, RBAC, Profile, Invocation-Pipeline, Supervisor
│   ├── Bifrost.Upstream/            # IUpstreamConnector-Implementierungen: Stdio, StreamableHttp, OpenApi
│   ├── Bifrost.Persistence/         # EF Core: DbContext, Migrations, Repositories, Audit-Batch-Writer
│   ├── Bifrost.Server/              # ASP.NET Core Host: MCP-Endpoint, REST-Fassade, AuthN, Health
│   └── Bifrost.Web/                 # Blazor-UI (Razor Components), nutzt NUR Application-Services
├── tests/
│   ├── Bifrost.Core.Tests/          # Unit
│   ├── Bifrost.Upstream.Tests/      # Unit + Prozess-Tests
│   ├── Bifrost.Integration.Tests/   # End-to-End gegen echten Host (WebApplicationFactory + Test-MCP-Server)
│   └── Bifrost.TestServers/         # Referenz-Upstreams: EchoServer (stdio), SlowServer, CrashServer, HttpServer
└── docs/ (prd, adr, plans, security, blueprints)
```

Abhängigkeitsregel (erzwungen per Projektreferenzen): `Server → Core, Upstream, Persistence, Web` (der Host referenziert die UI-Komponenten — Hosting-Realität von Blazor); `Web → Core`; `Core/Upstream/Persistence → Abstractions`. **Nichts** referenziert `ModelContextProtocol`-Typen außer `Upstream` (Client-Seite) und `Server` (Server-Seite) — der Kern bleibt SDK-frei (ADR-0002/0008). *(Korrigiert 2026-07-17 bei WP0-Umsetzung; ursprünglich stand hier `Web → Server`.)*

### 3.2 Request-Fluss (der eine Pfad, ADR-0008)

```
Agent (MCP) ─┐
REST-Client ─┼─► AuthN (API-Key) ─► IToolInvoker-Pipeline:
Blazor-UI  ──┘      RBAC-Check ─► Arg-Validierung ─► Routing (IToolCatalog)
                    ─► Timeout/Cancellation ─► IUpstreamConnection.CallToolAsync
                    ─► Audit (immer, auch bei Deny/Fehler) ─► Ergebnis
```

## 4. Interface-first: Kernverträge (C#)

Verbindliche v1-Verträge in `Bifrost.Abstractions`. Änderungen an diesen Signaturen sind PR-Review-pflichtig und im Plan nachzuziehen. (Namespaces/Usings weggelassen; alle DTOs sind `record`s, alle Methoden `CancellationToken`-führend.)

```csharp
// ── Identität & Autorisierung (ADR-0006) ─────────────────────────────
public interface IAuthorizationService
{
    /// Einzige Autorisierungsentscheidung im System. Reine Funktion über einem Snapshot.
    AuthorizationDecision Evaluate(IdentityId identity, PermissionScope scope, ToolAction action);
    /// RBAC-gefilterte Sicht — speist tools/list, search_tools UND OpenAPI-Generierung.
    IReadOnlyList<CatalogEntry> FilterVisible(IdentityId identity, IReadOnlyList<CatalogEntry> catalog);
}
public sealed record AuthorizationDecision(bool Allowed, string? DenyReason);
public sealed record PermissionScope(ServerId? Server, NamespacedToolName? Tool); // Tool==null → Server-weit
public enum ToolAction { UseTool, ReadResource, UsePrompt }

public interface IApiKeyValidator
{
    ValueTask<IdentityId?> ValidateAsync(string presentedKey, CancellationToken ct); // Hash-Vergleich, null = invalid
}

// ── Katalog & Profile (ADR-0003) ─────────────────────────────────────
public interface IToolCatalog
{
    /// Aggregierter, namespaced Gesamtkatalog (Quelle: alle Healthy-Upstreams).
    IReadOnlyList<CatalogEntry> Snapshot { get; }
    /// Sicht einer Identität gemäß Profil (Pinned voll, Rest lazy) + RBAC-Filter.
    ProfileView GetViewFor(IdentityId identity);
    IReadOnlyList<ToolSearchHit> Search(IdentityId identity, string query, int limit);
    event EventHandler<CatalogChangedEventArgs> Changed; // löst tools/list_changed aus
}
public sealed record CatalogEntry(NamespacedToolName Name, ServerId Server, string Description,
    JsonElement InputSchema, CatalogEntryKind Kind, int EstimatedSchemaTokens);
public sealed record ProfileView(IReadOnlyList<CatalogEntry> PinnedTools, bool LazyToolsEnabled,
    int EstimatedContextTokens);

// ── Invocation-Kern (ADR-0008): der EINZIGE Weg zu einem Tool-Call ──
public interface IToolInvoker
{
    Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct);
}
public sealed record ToolInvocationRequest(IdentityId Caller, CallOrigin Origin, // Mcp | Rest | Ui
    NamespacedToolName Tool, JsonElement Arguments, TimeSpan? TimeoutOverride);
public sealed record ToolInvocationResult(InvocationStatus Status, JsonElement? Content,
    string? ErrorMessage, TimeSpan Duration);
public enum InvocationStatus { Success, UpstreamError, Denied, Timeout, ValidationFailed, ToolNotFound }

// ── Upstream-Abstraktion (ADR-0005/0008) ─────────────────────────────
public interface IUpstreamConnector          // Fabrik pro Transporttyp: Stdio | StreamableHttp | OpenApi
{
    UpstreamTransportKind Kind { get; }
    Task<IUpstreamConnection> ConnectAsync(UpstreamServerConfig config, CancellationToken ct);
}
public interface IUpstreamConnection : IAsyncDisposable
{
    ServerId Id { get; }
    Task<UpstreamInventory> DiscoverAsync(CancellationToken ct);   // Tools+Resources+Prompts
    Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct);
    Task PingAsync(CancellationToken ct);                          // Health-Probe
    event EventHandler<UpstreamNotificationEventArgs> NotificationReceived; // u.a. list_changed von unten
}

public interface IUpstreamSupervisor          // Hosted Service; besitzt alle Lifecycles (ADR-0005)
{
    IReadOnlyList<UpstreamStatus> Statuses { get; }
    Task<ServerId> AddAsync(UpstreamServerConfig config, CancellationToken ct);        // Add→Connect→Discover→Changed
    Task RemoveAsync(ServerId id, DrainPolicy drain, CancellationToken ct);
    Task SetEnabledAsync(ServerId id, bool enabled, CancellationToken ct);
    Task<ConfigVersionId> ReconfigureAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct);
    Task RollbackAsync(ServerId id, ConfigVersionId version, CancellationToken ct);
}
public sealed record UpstreamStatus(ServerId Id, UpstreamState State, string? LastError,
    int ToolCount, DateTimeOffset LastHealthyAt);
public enum UpstreamState { Starting, Healthy, Degraded, Stopped, Failed }

// ── Audit (ADR-0007) ─────────────────────────────────────────────────
public interface IAuditSink                   // Hot-Path-Seite: darf NIE blockieren
{
    void Record(AuditEvent evt);              // fire-and-forget in Channel; Batch-Writer persistiert
}
public interface IAuditQuery                  // UI-/Export-Seite
{
    Task<PagedResult<AuditEvent>> QueryAsync(AuditFilter filter, CancellationToken ct);
}
public sealed record AuditEvent(DateTimeOffset Timestamp, IdentityId? Caller, CallOrigin Origin,
    AuditEventKind Kind, ServerId? Server, string? Tool, InvocationStatus? Status,
    JsonElement? RedactedArguments, long? RequestBytes, long? ResponseBytes, TimeSpan? Duration);

public interface IRedactionService
{
    JsonElement RedactArguments(NamespacedToolName tool, JsonElement args); // Regeln pro Tool, Default-Regeln global
}

// ── Skill-/Asset-Verteilung (FR-40) ──────────────────────────────────
public interface IAssetStore
{
    Task<IReadOnlyList<AssetInfo>> ListAsync(IdentityId identity, CancellationToken ct);
    Task<AssetContent> GetAsync(AssetId id, AssetVersion? version, CancellationToken ct);
    Task<AssetVersion> PublishAsync(AssetId id, string content, CancellationToken ct);
}
```

**Vertragsregeln:** (1) `IToolInvoker` ist der einzige Aufrufpfad — MCP-Endpoint, REST-Controller und UI-Testaufruf sind Adapter. (2) `IAuthorizationService.FilterVisible` ist die einzige Sichtbarkeitsquelle. (3) Kein Interface liefert SDK-Typen nach `Core`.

**Vertrags-Änderungslog** (gemäß DO Nr. 6):
- *2026-07-17 (WP1):* `IUpstreamConnector.ConnectAsync` erhält die vom Supervisor vergebene `ServerId` als ersten Parameter (Connector kann sie nicht kennen). `IUpstreamSupervisor` erweitert um `Changed`-Event (`UpstreamChangedEventArgs`: Added/Removed/InventoryChanged/StateChanged) sowie `GetStatus`/`GetInventory`/`GetConnection` — der Katalog (WP2) konsumiert Event + Inventar, das Routing (WP4) die Guarded-Connection. Neuer Persistenz-Port `IUpstreamConfigStore` (+ `UpstreamConfigVersion`) für FR-10; WP1 liefert In-Memory-Stub, WP3 die EF-Implementierung.
- *2026-07-17 (WP2):* Neues RBAC-Datenmodell in Abstractions (`Rbac.cs`): `Identity`/`Role`/`Grant`/`RateLimit`/`ToolProfile` + IDs (`RoleId`, `ProfileId`), Lese-Port `IRbacDirectory` (mit `Version` + `Changed` für Snapshot-/Katalog-Invalidierung) und `IRateLimiter` (FR-31, konsumiert vom Invoker in WP4). Grant-Semantik: Allow-only (ADR-0006); global (beide Scope-Felder null) / Server-weit / Tool-genau, Tool-Grants binden an den Slug-Namespace. `UpstreamStatus` erweitert um `Slug` (Katalog braucht ihn fürs Namespacing, UI sowieso).
- *2026-07-17 (WP4):* `IToolCatalog.Find` (Routing-Lookup ohne RBAC — die macht der Invoker), `IUpstreamConnection.ReadResourceAsync`/`GetPromptAsync` (FR-04-Passthrough), `IUpstreamConfigStore.GetAllLatestAsync` + `UpstreamSupervisor.RestoreAsync` (Startup-Restore persistierter Server unter bestehender Id). Meta-Tool-Semantik: search/describe werden selbst auditiert, invoke_tool auditiert nur den inneren Ziel-Call (kein Doppel-Audit). Bekannte v1-Grenzen: RBAC-Deny ist von ToolNotFound im describe_tool ununterscheidbar (kein Existenz-Leak), bei tools/call aber als Denied sichtbar (dokumentiertes Existenz-Leak, Threat-Model WP7.2); SDK-`RunSessionHandler` ist experimentell (MCPEXP002 unterdrückt, SDK gepinnt); Bootstrap-Admin-Key wird beim Erststart einmalig geloggt (dokumentierte DON'T-Nr.-2-Ausnahme).
- *2026-07-18 (NACHTRAG — Lücken aus vorzeitig abgehakten WPs):* Eine Prüfung gegen den vollständigen WP-Text (statt nur gegen die DoD-Stichpunkte) hat vier geplante, aber nicht umgesetzte Punkte zutage gefördert; die Pakete waren trotzdem als ✅ markiert und beliefert worden. Nachgezogen: **(a) WP6.2 Rollback in der Server-UI** (Muss) — Konfigurations-Historie je Server mit Rollback auf eine frühere Version, auditiert; Credentials werden in der Historie nicht angezeigt. **(b) FR-40 Asset-Auslieferung** (WP6.4) — Assets erscheinen jetzt tatsächlich als MCP-Prompt und -Resource unter reserviertem Namespace `assets__…` bzw. `bifrost://assets/…`; der Slug `assets` ist für Upstreams gesperrt, damit nichts die zentrale Auslieferung überschatten kann. Assets sind bewusst für jede authentifizierte Identität sichtbar (zentrale Instruktionstexte, kein Fremdsystem-Zugriff) — per-Asset-RBAC ist nicht Teil von FR-40. **(c) FR-14 Description-Override** — persistiert, wirkt über den Katalog auf `tools/list`, `search_tools`, `describe_tool` **und die Token-Schätzung**, mit `list_changed` bei Änderung; UI im Tool-Explorer (Admin). **(d) FR-26 Metriken-Export** — bis dahin existierte nur ein `Meter` ohne Exporter; jetzt OpenTelemetry/OTLP (aktivierbar über `OTEL_EXPORTER_OTLP_ENDPOINT`, Prometheus via Collector, da dessen Exporter nicht stabil veröffentlicht ist) und die fehlende `server`-Dimension ergänzt. **Prozess-Lehre:** DoD-Stichpunkte sind Nachweise, nicht der Leistungsumfang — abgehakt wird künftig gegen den vollständigen WP-Text.
- *2026-07-18 (NACHTRAG 2 — unabhängiges FR-Audit):* Nach dem ersten Nachtrag lief eine **unabhängige Prüfung aller Muss-FRs gegen den Code** (nicht gegen Plan-Häkchen, Changelog oder Kommentare). Sie fand sechs weitere Lücken desselben Musters — Struktur vorhanden, aber nie angeschlossen. Nachgezogen: **(e) FR-24/NFR-04 (Sicherheitsdefekt)** — `MetaToolService` schrieb Argumente **ungefiltert** ins Audit; bei `invoke_tool` gingen damit die kompletten Ziel-Argumente im Klartext in die DB. Der Meta-Pfad läuft jetzt durch dieselbe Redaction wie der reguläre; die Invariante steht im Vertrag von `AuditEvent` und wird per Test gehalten. **(f) FR-22** — `AuditEventKind.ServerLifecycle` existierte nur als Enum-Wert; Zustandswechsel gingen ausschließlich in den `ILogger`. Der `UpstreamSupervisor` schreibt sie jetzt über `SetState` (den einzigen Durchgangspunkt) ins Audit, mit neuem `CallOrigin.System` und Klartext im neuen Feld `Detail`. **(g) FR-21** — Profil/Rolle des Aufrufers fehlte im Datenmodell; neu `AuditEvent.CallerRoles`, gespeist aus `IAuthorizationService.DescribeCaller` (mit dem RBAC-Snapshot gecacht, damit der Hot Path nicht teurer wird). **(h) FR-23** — die Log-UI setzte Agent- und Server-Filter nie, das Herkunft-Dropdown war ein totes Bedienelement, und das Tool-Feld war als Präfixsuche beschriftet, verglich aber exakt (`github__` → immer 0 Treffer). `AuditFilter.Tool` heißt jetzt `ToolPrefix` und vergleicht als Präfix; `Origin` ist ergänzt; die UI bietet Auswahllisten statt Guid-Eintippen. **(i) FR-33** — `McpSessionRegistry.Count` wurde nirgends konsumiert; neuer Vertrag `IActiveSessionSource` (die UI zeigt nur nach unten auf Abstractions, ADR-0004), Dashboard-Kachel mit Sessions und verbundenen Agenten. **(j) FR-24** — Redaction-Regeln pro Tool waren nur aus Tests erreichbar; jetzt persistiert (`RedactionRules`-Tabelle, Cache, Admin-UI im Tool-Explorer). Der geforderte Debug-Modus für vollständige Ergebnis-Payloads existierte gar nicht: neu `AuditOptions.CaptureResponsePayloads` über `BIFROST_AUDIT_DEBUG_PAYLOADS`, Default aus, und auch eingeschaltet läuft der Payload durch die Redaction. Schema: eine Migration `AddAuditDetailsAndRedactionRules` für beide Provider (additiv, nullable — kein Datenverlust). **Prozess-Lehre 2:** Die Selbstprüfung hat beim ersten Anlauf nur vier von zehn Lücken gefunden, weil sie gegen die eigene Erinnerung lief. Verbindlich ist der Abgleich Anforderung → Code, und zwar von außen.
- *2026-07-18 (NACHTRAG 3 — Soll-Anforderungen):* Ein zweiter unabhängiger Durchgang hat die zehn Fixes verifiziert (alle real verdrahtet, keine tote DI-Registrierung, kein wirkungsloses Bedienelement) und zusätzlich die **Soll**-Anforderungen geprüft. Nachgezogen: **FR-25** — die Retention stand fest auf 30 Tagen (`new PersistenceOptions()` ohne Binding), ist jetzt über `BIFROST_AUDIT_RETENTION_DAYS` konfigurierbar; ungültige Werte fallen auf den Default zurück. **NFR-07** — es lief der Default-Textformatter; außerhalb von `Development` schreibt der Host jetzt JSON auf stdout (lokal bleibt Text, weil dort Lesbarkeit zählt). **FR-38** — das Token-Cockpit zeigte nur Token-Last, jetzt zusätzlich den Call-Volumen-Trend über 14 Tage (eine Count-Abfrage je Tag statt Full-Scan; Balken aus CSS statt Diagramm-Bibliothek, eine Abhängigkeit weniger). Dazu die Nacharbeit, dass der Debug-Payload zwar gespeichert, aber in der Logs-UI und im CSV nicht sichtbar war. **FR-02 (SSE-Legacy-Transport) — geklärt, siehe [ADR-0009](../adr/0009-sse-legacy-transport.md):** Die Recherche hat die Frage anders beantwortet als erwartet. Upstream war SSE **bereits erfüllt**, nur unbemerkt: `HttpClientTransportOptions.TransportMode` steht im SDK per Default auf `AutoDetect` und fällt selbsttätig auf HTTP+SSE zurück — der Konnektor hat den Wert nie gesetzt. Das erste Audit hat daraus „kein SSE-Konnektor vorhanden" gelesen, was formal stimmt, aber die falsche Schlussfolgerung war. Jetzt wird `AutoDetect` **explizit** gesetzt, damit ein SDK-Upgrade die Fähigkeit nicht stillschweigend entfernt, per Server über `AllowLegacySse` abschaltbar, mit UI-Schalter. Downstream bleibt es bei Streamable HTTP: `EnableLegacySse` ist im SDK `[Obsolete]`, SSE fehlt HTTP-Backpressure, und SEP-2596 gibt HTTP+SSE die kürzeste Entfernungsfrist aller deprecateten Features (drei Monate nach Final). **Lehre:** „Klasse X existiert nicht" ist noch kein Befund — erst der Blick auf das tatsächliche Laufzeitverhalten sagt, ob eine Anforderung erfüllt ist. Fähigkeiten, die an einem Framework-Default hängen, gehören per Test festgenagelt (`LegacySseSupportTests`).
- *2026-07-18 (NACHTRAG 4 — Abnahme-Audit vor dem Release):* Drei unabhängige Durchgänge (Muss-FRs, Soll/Kann/NFRs, adversariale Testqualität) vor einem korrigierten Release. Ergebnis: keine Hard-Blocker, aber zehn Punkte — auffällig viele davon **Nachweislücken statt Funktionslücken**. Umgesetzt: **FR-08** — `Degraded` war ein Enum-Wert, den nie jemand schrieb; ein Ping-Fehler sprang direkt auf `Failed` und riss die Verbindung samt In-Flight-Calls mit. Jetzt toleriert der Supervisor einen Aussetzer als `Degraded` (Verbindung bleibt geroutet, Zustand sichtbar, auditiert) und eskaliert erst danach. **FR-34** — „Bearbeiten" fehlte in der UI komplett, `ReconfigureAsync` war nur per REST erreichbar (verletzte Z-6). Neu mit Secret-Regel: bestehende Zugangsdaten werden nicht angezeigt, leer gelassene Felder bedeuten „unverändert" (`UpstreamConfigMerge`, im Kern statt in der Razor-Seite, damit testbar). **FR-31** — das Gültigkeitsfenster war in der UI hart `null`. **FR-05** — die Loop-Erkennung versprach „direkt oder transitiv", kann aber nur den direkten Selbstbezug: der Instanz-Header wird beim Verbindungsaufbau *einmal* gesetzt und kennt den auslösenden Request nicht. Behauptung korrigiert, Grenze samt Mitigation (Call-Timeout deckelt den Schaden) im Threat-Model. **NFR-01/02** — der einzige Test, der die Lastschwellen assertiert, war über eine nie gesetzte Env-Variable permanent übersprungen: die NFRs waren faktisch ungeprüft. Jetzt eigener wöchentlicher/manueller CI-Job. **NFR-05** — arm64 wurde behauptet, aber nie gebaut (jetzt `buildx`-Schritt); `docker-compose.yml` hatte ein `depends_on` auf einen nur im Profil aktiven Service, der SQLite-Default war damit je nach Compose-Version defekt (jetzt Override-Datei statt Profile, plus `config`-Prüfung in CI). **NFR-08** — Coverage wurde nur als Artefakt hochgeladen; jetzt Gate auf 80 % Zeilen für `Bifrost.Core` (Ist: 84 %). **NFR-09** — die Protokollversion hing still am SDK; jetzt per Handshake-Test auf „2025-06-18 oder neuer" festgenagelt. **Testhärtung:** Der Postgres-Fang machte *jeden* Fehler zum grünen Skip — auf Linux-CI erzwingt `BIFROST_REQUIRE_POSTGRES` jetzt das Durchreichen, dazu ein Skip-Zähler als Backstop. Zeitabhängige Negativ-Assertions ersetzt (`StaysFalseAsync`). Neuer Reflection-Test über die Policies aller routbaren Seiten — bei Blazor Interactive Server ist das Seitenattribut die Durchsetzung, darunter prüft kein Service UI-Rollen. **Doku-Fallen entfernt:** Kommentare, die längst Umgesetztes als „folgt" auswiesen — und, sicherheitsrelevant, `IAssetStore.ListAsync` versprach eine RBAC-Filterung, die es nie gab (der `identity`-Parameter wird ignoriert); jetzt steht die Grenze samt Warnung im Vertrag. **Lehre 3:** Grün heißt nicht geprüft. Ein Test, der nie läuft, ist teurer als keiner — er erzeugt Sicherheit, die es nicht gibt.
- *2026-07-18 (NACHTRAG 5 — Kann-Anforderungen entschieden):* Die fünf Kann-Punkte hatte ich in Berichten als „v2" geführt — meine Einordnung, nicht die des Auftraggebers. Nach Vorlage von Aufwand und Nutzen je Punkt entschieden: **FR-41 umgesetzt** (Client-Config-Snippets beim Ausstellen eines Keys, also genau dann, wenn der Key einmalig sichtbar ist; Namen werden auf ASCII normalisiert, weil sie als Schlüssel in fremde Config-Dateien wandern). **FR-16 umgesetzt** (`BIFROST_MAX_RESULT_CHARS`, Default aus). Die zwei Entscheidungen, die es brauchbar machen, stecken nicht in der Kürzung selbst: Das Ergebnis bleibt **gültiges JSON** — ein roher Abschnitt wäre für den Agenten wertlos, er hätte dann gar nichts statt zu wenig — und die Kürzung ist per `_bifrost_truncated` **erkennbar**, denn verschwiegen wäre sie schlimmer als die Größe. Das Audit hält weiter die ungekürzte Größe fest. **FR-04 als Abweichung geschlossen ([ADR-0010](../adr/0010-sampling-elicitation-nicht-durchreichen.md)):** Die Machbarkeitsprüfung ergab, dass beide SDK-Enden existieren, aber die Mitte strukturell fehlt — das Protokoll trägt keine Korrelation, der SDK-Handler bekommt keinen Kontext, und die Upstream-Verbindung ist geteilt, während die Identität schon im Invoker endet. Tragfähig wäre nur ein Pool je (Server, Identität), was die 1:1-Annahme des Supervisors kippt (16–26 PT, ±40 %). Ausschlaggebend war aber nicht der Aufwand: Sampling lässt einen Upstream LLM-Inferenz auf Kosten des Agenten auslösen, und die Spezifikationszusage „ein Mensch kann immer ablehnen" kann ein Gateway zwischen headless Agent und Server nicht einlösen. Ein Feature auszuliefern, dessen Sicherheitsversprechen die Architektur nicht trägt, wäre schlechter als es wegzulassen. **FR-20 und FR-32 zurückgestellt** — bei FR-32 ist nicht die Queue das Problem, sondern der blockierende Call, der dem Timeout aus FR-09 widerspricht; das braucht ein Design, keine Implementierung. - *2026-07-20 (NACHTRAG 6 — Abhängigkeiten auf aktuellen Stand):* **.NET 10 ist LTS** (Support bis 2028-11-14); .NET 11 gibt es nur als Preview mit STS-Zyklus und ist kein Ziel. Neu `global.json` mit **Untergrenze** 10.0.200 + `rollForward: latestFeature` — ein Pin auf eine exakte Version (erst versucht: 10.0.302) bricht jede Maschine, auf der sie nicht installiert ist, und tat das hier sofort. **GitHub Actions** auf die Node-24-Majors gehoben (checkout@v7, setup-dotnet@v6, upload-artifact@v7, setup-buildx-action@v4); `upload-artifact@v5` hätte die Node-20-Warnung *nicht* gelöst, dort war Node 24 nur vorläufig. **Docker-Basis** explizit auf `10.0-noble` gepinnt: Der Default-Alias `10.0` zeigt heute auf Ubuntu 24.04, wandert aber bei einem künftigen Distro-Wechsel weiter. Chiseled bleibt ausgeschlossen, weil das Laufzeit-Image für `/data` eine Shell braucht. **FluentAssertions → AwesomeAssertions 9.5.0:** FluentAssertions steht ab v8 unter kommerzieller Xceed-Lizenz (NFR-10), auf 7.x zu bleiben wäre eine tote Linie gewesen; AwesomeAssertions ist der Apache-2.0-Fork von vor dem Lizenzwechsel und war tatsächlich drop-in — nur Paketname und Namespace, keine einzige Assertion geändert. **xUnit 2.9.3 → xunit.v3 3.2.2:** Testprojekte sind jetzt eigenständige Executables (`OutputType=Exe`), `Xunit.SkippableFact` entfällt zugunsten des eingebauten `Assert.SkipWhen`/`SkipUnless`, `IAsyncLifetime` liefert `ValueTask` und erbt von `IAsyncDisposable`. **Der Umstieg hat einen echten Defekt gefunden:** `State_changes_are_written_to_the_audit_log` bestand isoliert, fiel aber im Gesamtlauf — der Test wartete auf den Zustand und las dann das Audit, während `SetState` den Zustand *vor* dem Eintrag sichtbar macht. Ein selbstgebautes Rennen, das nur auf einer unbelasteten Maschine gewann; jetzt wird auf das Ereignis gewartet. Danach dreimal hintereinander grün. Bewusst **nicht** übernommen: `xUnit1051` verlangt an jedem Aufruf mit optionalem Token den TestContext-Token — die 198 Stellen mit ohnehin übergebenem Token sind umgestellt, die ~360 impliziten wären Lärm ohne Nutzen (die Wartehelfer haben eigene Timeouts). Nachgeprüft statt angenommen: Die TRX-Datei meldet auch unter v3 `outcome="NotExecuted"`, das CI-Gate auf übersprungene Tests greift unverändert.

- *2026-07-20 (WP9 — Secret-Guardrail, [ADR-0011](../adr/0011-secret-erkennung-als-guardrail.md)):* Neue Stufe im `ToolInvoker` — dem einzigen Weg zu einem Call, womit MCP, REST und UI in einem Zug abgedeckt sind. B.I.F.R.O.S.T bleibt der Kern; die Prüfung ist ein Filter darin, kein zweites Produkt. Verträge in Abstractions (`IContentGuard`, `GuardRule`, `GuardFinding`, `IGuardRuleStore`, `GuardOptions`), Erkennung im Core, Regeln in der DB mit Write-Through wie bei den Redaction-Mustern — also **hot-swappable ohne Neustart**. Pipeline: `SearchValues`-Multiscan (Ordinal) → Keyword je Regel → Regex mit `NonBacktracking` + Timeout. Gemessen ~0,05 ms, also unter 1 % des 7-ms-Budgets. Neuer Status **`GuardBlocked`**, bewusst nicht `Denied`: In der Ergebnis-Richtung ist der Upstream-Call bereits gelaufen, der Seiteneffekt also eingetreten — die Fehlermeldung sagt „ausgeführt, Ergebnis zurückgehalten, NICHT wiederholen", und das Audit hält den stattgefundenen Call fest. Sonst wiederholt ein Agent den Aufruf und legt dasselbe Issue zweimal an. Startregelsatz aus gitleaks (MIT; trufflehog scheidet wegen AGPL-3.0 aus, NFR-10), kuratiert statt vollständig — jede kompilierte Regel kostet ~530 KB. UI mit geführtem Editor als Standard, Freitext-Regex hinter `BIFROST_GUARD_ALLOW_CUSTOM_PATTERNS`, Validierung beim Speichern, Modus pro Regel, globaler Not-Aus. **Lehre 5:** Die schwierigste Frage war nicht die Erkennung, sondern was „blockieren" in der Ergebnis-Richtung überhaupt bedeutet — und dass eine Fehlermeldung, die den bereits ausgeführten Call verschweigt, einen doppelten Seiteneffekt provoziert.

**Lehre 4:** Scope-Entscheidungen sind keine technischen Urteile. „Kann" heißt nicht „ich entscheide das still" — jeder offene Punkt bekommt einen dokumentierten Status, sonst ist später nicht unterscheidbar, was entschieden und was vergessen wurde.
- *2026-07-18 (v1.1 — Härtung & Betrieb, WP8):* **WP8.1** Key-Ring-Schutz optional per X509 (`BIFROST_KEYRING_CERT_PATH/_PASSWORD`, `ProtectKeysWithCertificate` + `UnprotectKeysWithAnyCertificate` für Rotation); bewusst zertifikatsbasiert statt Cloud-KMS, damit self-hosted tauglich — ohne Konfiguration Startup-Warnung statt stiller Default. **WP8.2** PBKDF2 100k → 600k (OWASP); Bestandshashes bleiben verifizierbar, da die Iterationszahl im Hash-Format steckt (Test gegen beide Provider); Testsuite-Laufzeit unverändert (Hashing geht im Prozess-Overhead unter). **WP8.3** Lasttest-Harness (`PerformanceBenchmarkTests`, per `BIFROST_RUN_BENCHMARK=1` scharfgeschaltet, neuer `BulkServer` mit 100 programmatisch erzeugten Tools) — bewusst außerhalb der CI, weil geteilte Runner keine Referenz-Hardware sind; Referenzmessung in `docs/acceptance/performance.md`. **WP8.4** Recovery-Kommandos `--reset-ui-admin [user]` und `--issue-bootstrap-key`, laufen ohne Gateway-Start; Kernlogik von `WebApplication` entkoppelt, damit sie ohne Prozess-Spawn testbar ist.
- *2026-07-18 (v1.1 — Migrations-Baseline, der in WP7 vertagte Punkt):* Schema läuft jetzt über EF-Migrationen statt `EnsureCreated`. Wegen provider-spezifischem DDL (SQLite `TEXT` vs. Postgres `uuid`) **zwei Migrations-Assemblies** (`Bifrost.Persistence.Migrations.Sqlite` / `.Postgres`) mit je eigener `IDesignTimeDbContextFactory`; Auswahl über den geteilten Helper `BifrostDbOptions.UseBifrostDatabase(provider, cs)`, den Host **und** Tests nutzen. Neuer `DatabaseInitializer` mit drei Pfaden: leere DB → `CreatedFromMigrations`; **v1.0-DB ohne `__EFMigrationsHistory` → Baseline-Stamping** (Historie anlegen + Initial-Migration als angewendet eintragen, ohne DDL — Daten bleiben unangetastet) → `BaselinedLegacySchema`; sonst `Migrated`. Damit ist das v1.0→v1.1-Upgrade schrittfrei. Nachgewiesen durch Tests gegen SQLite **und** PostgreSQL (frische DB, Legacy-Upgrade inkl. Datenerhalt, Idempotenz beim zweiten Start).
- *2026-07-17 (WP7):* `GatewayIdentity` (Instanz-Kennung) + `X-Bifrost-Instance`-Header für Federations-Loop-Erkennung (FR-05); Loop → HTTP 508. Container-Healthcheck als `--healthcheck`-Self-Ping (chiseled ohne curl). Security-Härtungen aus dem Audit (kein Critical/High): Cookie `SecurePolicy=Always` außerhalb Development, OpenAPI-Spec-10-MB-Cap + CR/LF-Header-Schutz, Dummy-PBKDF2 gegen Username-Timing, `/readyz` ohne Topologie-Details. **Bewusste Abweichung EF-Migrations:** v1.0 bleibt bei `EnsureCreated` (frisches Schema, beide Provider korrekt); Migrations-Baseline erst zur ersten Schemaänderung in v1.1 — risikoärmer für einen Erst-Release ohne Bestands-DBs (Threat-Model/Acceptance dokumentiert). CI-Actions auf checkout@v5/setup-dotnet@v5 gehoben + neuer `docker`-Job (Image-Build, &lt;300-MB-Prüfung, Container-Smoke).
- *2026-07-17 (WP6):* Web-UI-Verträge in Abstractions: `IUiUserService`/`UiUserInfo`/`UiRole` (Cookie-UI-Nutzer, FR-30), `IRbacManagement` (schreibend+lesend, hält Blazor an Interfaces statt an Persistence — ADR-0004-Layering), `IUpstreamConnectionTester` ("Verbindung testen", FR-34), `IAssetStore.CreateAsync` (FR-40). **Abweichung UI-Auth:** statt vollem ASP.NET Identity ein leichtgewichtiges Cookie-Auth + eigener `UiUserService` (PBKDF2, geteilt mit API-Keys via `Pbkdf2Hasher`) — konsistent mit dem bestehenden Modell, viel weniger Schema-Ballast; drei Policies (ui-admin/ui-operator/ui-authenticated). UI-Test-Aufrufe laufen unter einer internen Agenten-Identität mit Global-Grant (`ui-internal`, IdentityKind.User), damit Origin=Ui-Calls durch den regulären Invoker+Audit gehen. Bootstrap legt bei leerer DB einen UI-Admin `admin` an und loggt das Passwort einmalig (analog Bootstrap-API-Key). Web-Projekt: `Microsoft.NET.Sdk.Razor` + `FrameworkReference Microsoft.AspNetCore.App`; Blazor Interactive Server, im Server-Host via `MapRazorComponents<App>`. **Playwright bewusst nicht in CI** (Browser-Binaries, Minuten-Overhead): WP6-DoD ist über HTTP-Auth/Authz-Tests (Rollen-Enforcement, Login-Redirect, UI-Login-Audit) und einen Referenz-Setup-Durchstich über exakt die Application-Services der Komponenten (anlegen→Key→Origin=Ui-Invoke→Audit) abgesichert.
- *2026-07-17 (WP5):* `OpenApiTransportOptions` präzisiert (`Credential` statt `CredentialReference` — im DataProtection-verschlüsselten Config-Blob abgelegt, kein separater Store nötig; `ApiKeyHeaderName`). **Bewusster Feature-Schnitt OpenAPI-Import (FR-19, eigener Parser statt `Microsoft.OpenApi`):** nur JSON-Specs (kein YAML), OpenAPI 3.x (kein Swagger 2.0), path/query/header-Parameter, `application/json`-Bodies, dokument-lokale `#/`-`$ref`s; alles außerhalb bricht **komplett** ab (kein Halbimport, DON'T Nr. 6) mit präziser Fehlermeldung → Server geht auf `Failed`. Management-API-Adminschranke: bis WP6 echte UI-Rollen bringt, gilt ein Global-Grant (`PermissionScope(null,null)` + `UseTool`) als Admin-Kriterium (`RequireAdminAsync`-EndpointFilter). REST-Fassade und Management laufen durch dieselben Kernpfade wie MCP (Invoker bzw. Application-Services); Audit-Parität MCP↔REST per Test belegt. `JsonStringEnumConverter` für die REST-JSON-Optionen (Enums als Strings in Config/Status).
- *2026-07-17 (WP3):* `IMutableRbacDirectory` (Schreibseite, hält Persistence Core-frei), `IApiKeyService`/`IssuedApiKey`/`ApiKeyInfo` (WP3.3). **Abweichung Migrations:** v1 nutzt `EnsureCreated` statt EF-Migrations — die Zwei-Provider-Migrationspflege (getrennte Migrations-Assemblies) wird erst mit der Migrations-Baseline in WP7 aufgesetzt, solange das Schema noch fließt; NFR-06-Migrationspfad bleibt gewahrt (Baseline vor v1.0). Zeitstempel werden provider-neutral als UTC-Ticks (bigint) gespeichert (SQLite kann DateTimeOffset weder sortieren noch in ExecuteDelete vergleichen). Transitive CVE-Pins in `Directory.Packages.props` (SQLitePCLRaw 2.1.12, GHSA-2m69-gcr7-jv3q). Postgres-Tests skippen ohne erreichbaren Docker (Windows-CI); der Ubuntu-Lauf trägt den Postgres-Nachweis.

## 5. Arbeitspakete (Issues)

Jedes WP ist als GitHub-Issue anlegbar (Titel = WP-Titel, Body = Schritte + DoD). Schätzung in T-Shirt-Größen (S ≤ 1 Tag, M ≤ 3 Tage, L ≤ 1 Woche). Reihenfolge = Nummerierung, Parallelität siehe Abschnitt 6.

### WP0 — Projekt-Fundament (M) ✅ *(umgesetzt 2026-07-17; DoD lokal erfüllt, CI-Lauf auf beiden OS steht bis zum ersten Push aus)*

**Schritte:**
- WP0.1 (S): Solution-Skelett gemäß 3.1, `Directory.Packages.props`, Analyzer + `TreatWarningsAsErrors`, `.editorconfig`, git init + CI (Build + Test auf Windows & Linux).
- WP0.2 (S): `Bifrost.Abstractions` vollständig anlegen (alle Verträge aus Abschnitt 4 + DTOs/IDs als strongly-typed records).
- WP0.3 (S): Test-Infrastruktur: xUnit, FluentAssertions, `Bifrost.TestServers/EchoServer` (minimaler stdio-MCP-Server via C# SDK) baubar.

**DoD:** CI grün auf beiden OS; `Abstractions` kompiliert ohne einzige externe Dependency; EchoServer beantwortet `initialize` + `tools/list` + Echo-Call, nachgewiesen durch einen ersten Integrationstest.

### WP1 — Upstream-Konnektoren & Supervisor (L) ✅ *(umgesetzt 2026-07-17; Anmerkungen: Degraded-Zustand wird v1 nur bei Teil-Discovery genutzt, Verbindungsverlust geht direkt auf Failed [DoD-konform]; Windows-Hygiene via Job Object am Gateway-Prozess, Linux via stdio-EOF-Semantik)*

**Schritte:**
- WP1.1 (M): `StdioUpstreamConnector` + `StreamableHttpUpstreamConnector` auf SDK-Basis; Discovery (Tools/Resources/Prompts) → `UpstreamInventory`.
- WP1.2 (M): `UpstreamSupervisor` als Hosted Service: Zustandsmaschine, Health-Ping-Loop, Exponential-Backoff-Restart (Policy konfigurierbar), Prozess-Hygiene (Windows Job Objects / Linux Prozessgruppen — kein Orphan überlebt Gateway-Exit).
- WP1.3 (S): Add/Remove/Reconfigure/Rollback inkl. Config-Versionierung (Persistenz-Stub in-memory, echte DB in WP3).
- WP1.4 (S): Drain-Semantik: Remove wartet konfigurierbar auf In-Flight-Calls, dann Cancel; Fault Isolation via Timeout pro Call.

**DoD:** Kill des EchoServer-Prozesses → Status `Failed` → Auto-Restart → `Healthy`, ohne dass ein parallel laufender zweiter Upstream einen Call verliert (Integrationstest CrashServer). `AddAsync` bis `Changed`-Event < 5 s. Kein Zombie-Prozess nach Host-Shutdown (OS-spezifischer Test).

### WP2 — Katalog, Profile & RBAC (L) ✅ *(umgesetzt 2026-07-17; Property-Test handgerollt mit festen Seeds statt FsCheck [keine Zusatz-Dependency]; RBAC-Matrix mit 25 Fällen; AuthorizationService 100 % Branch, ToolCatalog 98 %)*

**Schritte:**
- WP2.1 (M): `ToolCatalog`: Aggregation, Namespacing (`server__tool`), Kollisionsfreiheit, Token-Schätzung (chars/4), `Changed`-Event.
- WP2.2 (M): RBAC-Datenmodell (Identity/Role/Grant) + Snapshot-Compiler + `AuthorizationService.Evaluate` als reine Funktion; Default-Deny; Server→Tool-Vererbung.
- WP2.3 (S): Profile (Pinned + Lazy-Flag) und `GetViewFor`; `Search` mit Keyword-Score über Name+Beschreibung.
- WP2.4 (S): Rate-Limiter pro Identität (Token-Bucket, Rollen-Attribut) vor dem Invoker.

**DoD:** RBAC-Testmatrix (≥ 20 Fälle aus PRD-Kriterium 4) 100 % grün; Property-Test „nie sichtbar, was nicht erlaubt" über zufällige Grant-Kombinationen grün; Branch-Coverage der Evaluate/Filter-Pfade = 100 %; Katalog mit 100 Tools liefert `GetViewFor` < 10 ms.

### WP3 — Persistenz & Audit (M) ✅ *(umgesetzt 2026-07-17; DoD erfüllt: 1000/1000-Audit-Test, Record p99 < 50 µs, SQLite-Datei-Scan ohne Klartext-Secrets, Provider-Suite SQLite+Postgres [Postgres via Testcontainer, lokal/Windows-CI übersprungen]; Migrations-Abweichung siehe Änderungslog)*

**Schritte:**
- WP3.1 (M): EF-Core-Modell + Migrations (SQLite & PostgreSQL), Repositories für Config/RBAC/Profile; Data-Protection-Key-Ring + Secret-Verschlüsselung der Upstream-Credentials.
- WP3.2 (M): Audit-Pipeline: `Channel<AuditEvent>` + Batch-Writer (Flush ≤ 1 s / ≤ 500 Events), `IAuditQuery` mit Filter+Paging, Retention-Job, `IRedactionService` (globale Defaults: `password|token|secret|key|authorization`-Felder; per-Tool-Regeln).
- WP3.3 (S): API-Keys: Erzeugung (nur einmal im Klartext sichtbar), Hash-Speicherung (PBKDF2/Argon2), Widerruf, Gültigkeitsfenster.

**DoD:** 1000-Call-Lasttest erzeugt exakt 1000 Audit-Zeilen, korrekt attribuiert, Secrets maskiert (PRD-Kriterium 5); Integrationstests laufen in CI gegen SQLite **und** PostgreSQL (Testcontainer); `Record()` p99 < 50 µs (nicht-blockierend nachgewiesen); DB-Datei enthält keine Klartext-Secrets (Scan-Test).

### WP4 — Invocation-Kern & MCP-Endpoint (L) ✅ *(umgesetzt 2026-07-17; DoD: Hot-Swap-, RBAC-Deny+Audit- und p95≤50ms-Nachweise als E2E-Tests mit offiziellem SDK-Client gegen den echten Host; „Claude Code/Inspector verbinden sich" ist protokollseitig damit belegt — manueller Connect siehe M2-Demo)*

**Schritte:**
- WP4.1 (M): `ToolInvoker`-Pipeline gemäß 3.2 inkl. serverseitiger JSON-Schema-Validierung der Argumente (Pflicht für Lazy-Pfad, ADR-0003) und Timeout/Cancellation.
- WP4.2 (M): MCP-Server-Endpoint (Streamable HTTP, SDK): `initialize`, `tools/list` (aus `ProfileView`), `tools/call` → Invoker, `notifications/tools/list_changed` bei Katalog-/Profil-/RBAC-Änderung; Resources/Prompts-Durchreichung; Session-Verwaltung pro Identität.
- WP4.3 (S): Meta-Tools `search_tools`, `describe_tool`, `invoke_tool` als eingebaute Tools über denselben Invoker.
- WP4.4 (S): AuthN-Middleware (API-Key als Bearer), `/healthz` + `/readyz`, strukturierte Logs, OTel-Metriken (Calls/s, Fehlerquote, Latenz-Histogramme pro Server/Tool).

**DoD:** Claude Code und MCP Inspector verbinden sich erfolgreich; Hot-Swap-Nachweis aus PRD-Kriterium 3 als automatisierter Integrationstest; Latenz-Overhead ≤ 50 ms p95 im Benchmark (EchoServer, 100 parallele Calls, NFR-01/02); RBAC-Deny via MCP liefert sauberen Tool-Error und Audit-Eintrag.

### WP5 — REST-Fassade & OpenAPI-Bridge (L) ✅ *(umgesetzt 2026-07-17; DoD: curl-Roundtrip gegen EchoServer-Tool, Fehler-Mapping 403/404/400/504/502, Audit-Parität MCP↔REST als Test, RBAC-gefilterte OpenAPI-3.1-Spec pro Key, Petstore-Mini als OpenAPI-Upstream importiert + hot-swap-aufrufbar, nicht unterstützte Spec bricht komplett mit präziser Meldung ab. 189 Tests grün)*

**Schritte:**
- WP5.1 (M): REST→MCP: `POST /api/v1/tools/{name}/invoke`, `GET /api/v1/tools` (RBAC-gefiltert), Fehler-Mapping (Denied→403, NotFound→404, Timeout→504, ValidationFailed→400); Management-API für Server/RBAC/Profile (Basis für UI-Parität und FR-41).
- WP5.2 (S): Dynamische OpenAPI-3.1-Generierung pro Identität aus deren Katalog-Sicht (Cache mit Invalidierung über `Changed`).
- WP5.3 (M): API→MCP: `OpenApiUpstreamConnector` — Spec-Import (URL/Datei), Operation→Tool-Mapping, `$ref`-Auflösung, Auth-Profile (ApiKey/Bearer/Basic), harte Ablehnung nicht unterstützter Features mit präziser Fehlermeldung.

**DoD:** `curl`-Roundtrip mit API-Key gegen EchoServer-Tool grün; identischer Call via MCP und REST erzeugt bit-identische Audit-Semantik (Testvergleich); Petstore-ähnliche Referenz-Spec importiert → Operationen als Tools aufrufbar, hot-swappable wie echte Server; nicht unterstützte Spec bricht mit verständlicher Meldung ab, nie mit Halbimport.

### WP6 — Blazor Web-UI (L) ✅ *(vollständig erst 2026-07-18: Rollback-UI aus WP6.2 und die Asset-Auslieferung aus WP6.4 fehlten in der ersten Abnahme und wurden nachgezogen — siehe Änderungslog-Nachtrag. Ursprünglich 2026-07-17; DoD: Rollen-Enforcement Admin/Operator/Auditor per HTTP-Test, UI-Login + Fehlversuch auditiert, Referenz-Setup-Durchstich (anlegen→Key→Origin=Ui-Testaufruf→Log) über die Komponenten-Services. Damit auch PRD-Abnahmekriterium 1 erfüllbar. Playwright-Browser-E2E bewusst außerhalb CI — siehe Änderungslog. 197 Tests grün)*

**Schritte:**
- WP6.1 (M): UI-Gerüst, lokales Admin-Login (ASP.NET Identity, Cookie), UI-Rollen Admin/Operator/Auditor.
- WP6.2 (M): Server-Verwaltung (Formulare stdio/HTTP/OpenAPI, „Verbindung testen", Enable/Disable, Rollback) + Dashboard (Live-Health, Sessions, Call-Rate).
- WP6.3 (M): Tool-Explorer (Suche, Schema-Ansicht, Test-Aufruf über Invoker mit Origin=Ui), RBAC-Verwaltung (Identitäten, Keys, Rollen, Grants, Profile inkl. Pinning), Log-Ansicht (Filter, Export JSON/CSV), Token-Cockpit (`EstimatedContextTokens` pro Profil).
- WP6.4 (S): Skill-/Asset-Verwaltung (FR-40): CRUD + Versionen; Auslieferung als MCP-Prompts/Resources über den Katalog.

**DoD:** PRD-Abnahmekriterium 1 (Referenz-Setup komplett ohne Config-Datei) manuell durchgespielt und als Playwright-E2E-Smoke automatisiert (anlegen → Key erzeugen → Tool testen → Log sehen); Auditor-Rolle kann nachweislich nichts verändern (403-Tests); UI-Zugriffe erscheinen im Audit-Log.

### WP7 — Härtung, Performance-Nachweis & Release (M) ✅ *(umgesetzt 2026-07-17; DoD: alle 7 PRD-Abnahmekriterien dokumentiert [docs/acceptance/v1.md], Token-Ersparnis 96,7 %, Security-Audit ohne High/Critical + Findings behoben [Threat-Model], Federation+Loop-Detection, Docker chiseled + CI-Größenprüfung <300 MB. Abweichung: EF-Migrations-Baseline auf v1.1 vertagt)*

**Schritte:**
- WP7.1 (S): Lasttest-Suite (NFR-01/02: 20 Sessions, 100 In-Flight-Calls; Report ins Repo), Token-Messung des Referenz-Setups (PRD-Kriterium 2, ≥ 80 %).
- WP7.2 (S): `repo-security-audit`-Skill ausführen; Findings ≥ High fixen; Threat-Model-Kurzdoku (Gateway als Credential-Ziel, ADR-0001/0005-Risiken).
- WP7.3 (S): Dockerfile (multi-arch), docker-compose-Beispiel, Betriebs-Doku (Env-Vars, Backup, Reverse-Proxy/TLS, Retention), README + Quickstart.
- WP7.4 (S): Federation-Smoke (FR-05, Should): B.I.F.R.O.S.T als Upstream eines zweiten B.I.F.R.O.S.T, Loop-Detection via Server-Fingerprint.

**DoD:** Alle 7 PRD-Abnahmekriterien dokumentiert erfüllt (je Kriterium: Test oder protokollierter Nachweis); Docker-Image < 300 MB; Security-Audit ohne offene High/Critical; Release-Tag.

## 6. Abhängigkeiten

```
WP0 ─► WP1 ─► WP2 ─► WP4 ─► WP5 ─► WP7
        │      └───► WP3 ──┘        ▲
        └──────────────────► WP6 ───┘   (WP6 startet nach WP4; braucht Management-API-Basis aus WP5.1)
```

- WP2 und WP3 sind nach WP1 parallelisierbar (RBAC in-memory testbar, Persistenz unabhängig).
- Externe Abhängigkeiten: keine Beschaffung; einzig SDK-Releases (`ModelContextProtocol` ≥ 1.2) und .NET 10 SDK.
- Zyklenfrei geprüft.

## 7. Teststrategie

### 7.1 Unit-Tests (Projekt: `*.Tests`, Framework xUnit + FluentAssertions, keine echten Prozesse/Netz/DB)

| Bereich | Schwerpunkt | Coverage-Ziel |
|---|---|---|
| RBAC (`Evaluate`, `FilterVisible`, Snapshot-Compiler) | 20er-Matrix, Vererbung, Default-Deny, Property-Tests (FsCheck): „sichtbar ⇒ erlaubt" | 100 % Branch |
| Katalog | Namespacing, Kollisionen, Token-Schätzung, Changed-Semantik | ≥ 90 % |
| Invoker-Pipeline | Statusmatrix (jeder `InvocationStatus` erreichbar), Timeout, Schema-Validierungsfehler, Audit-genau-einmal | 100 % Branch |
| Redaction | Default-Regeln, per-Tool-Regeln, verschachtelte Objekte/Arrays, Nicht-Mutation des Originals | 100 % Branch |
| Supervisor-Zustandsmaschine | alle Übergänge, Backoff-Berechnung, Drain (mit Fake-Connection + FakeTimeProvider) | ≥ 95 % |
| Profile/Search | Pinned+Lazy-Kombinatorik, Score-Ranking, leere Treffer | ≥ 90 % |
| OpenAPI-Mapping | Operation→Tool, `$ref`, Ablehnungsfälle (multipart, OAuth-Flows) | ≥ 90 % |

Gesamtziel Kern-Bibliotheken ≥ 80 % Zeilen (NFR-08), gemessen in CI (Coverlet), PR-Gate.

### 7.2 Integrationstests (`Bifrost.Integration.Tests`, WebApplicationFactory + TestServers)

1. **Lifecycle:** Add/Remove/Reconfigure/Rollback gegen echten EchoServer-Prozess; CrashServer-Restart; Zombie-Check bei Host-Shutdown (je OS).
2. **Hot-Swap E2E:** verbundener SDK-Client erhält `list_changed`, kann neues Tool sofort rufen (PRD-Kriterium 3).
3. **RBAC E2E:** identische Matrix wie Unit, aber über echten MCP- und REST-Pfad — beweist Pfad-Konsistenz (ADR-0008).
4. **Audit E2E:** 1000 gemischte Calls (Erfolg/Deny/Timeout via SlowServer) → Zeilenzahl, Attribution, Redaction (PRD-Kriterium 5); gegen SQLite und PostgreSQL (Testcontainers).
5. **Bridge:** REST-Invoke, OpenAPI-Spec-Abruf, OpenAPI-Import-Roundtrip.
6. **Meta-Tools:** search→describe→invoke-Kette; invoke mit Schema-Fehler → `ValidationFailed`.
7. **Performance-Smoke** (nightly, nicht PR-Gate): NFR-01/02-Benchmark mit Schwellwert-Assertion.
8. **UI-Smoke** (Playwright, ab WP6): Referenz-Setup-Durchstich.

### 7.3 Testdaten & Fixtures

`Bifrost.TestServers` liefert deterministische Upstreams: **Echo** (normal), **Slow** (konfigurierbare Latenz → Timeout-Tests), **Crash** (stirbt nach N Calls → Supervisor-Tests), **Http** (Streamable-HTTP-Variante). Keine externen Live-Server in Tests.

## 8. DO's und DON'Ts (Konstruktionsregeln, PR-Review-Checkliste)

**DO:**
1. Jeder Tool-Call — egal welcher Herkunft — läuft durch `IToolInvoker`. Ohne Ausnahme.
2. Jede Sichtbarkeitsentscheidung läuft durch `IAuthorizationService.FilterVisible`. `tools/list`, `search_tools`, REST-`GET /tools` und OpenAPI-Gen teilen dieselbe Quelle.
3. Default-Deny überall: neuer Endpoint/neues Feature startet unsichtbar/verboten.
4. Jede `async`-Methode nimmt und propagiert `CancellationToken`; jeder Upstream-Call hat einen Timeout.
5. Audit zuerst designen: neue Aktion ⇒ zuerst `AuditEventKind` definieren, dann Feature bauen.
6. Interfaces in `Abstractions` ändern nur mit Plan-Update + Review (Verträge sind API).
7. Strukturierte Logs mit `ServerId`/`IdentityId`/`CorrelationId` in jedem Scope.
8. Beide DB-Provider in CI testen, bevor ein EF-Feature genutzt wird.
9. Fehler von Upstreams als Daten behandeln (Status + Message), nie als ungefangene Exception zum Client durchschlagen lassen.
10. Windows- und Linux-Prozesspfade getrennt testen (ADR-0005-Risiko).

**DON'T:**
1. Keine SDK-Typen (`ModelContextProtocol.*`) in `Core`/`Abstractions`/`Web` — nur `Upstream` und `Server` dürfen sie sehen.
2. Kein Klartext-Secret in Logs, Audit, DB, Exceptions oder UI — auch nicht „nur im Debug-Modus" (Debug loggt Payloads, aber nach Redaction).
3. Kein blockierender I/O im Hot Path — `IAuditSink.Record` bleibt synchron-nicht-blockierend, alles andere async.
4. Keine RBAC-/Sichtbarkeitslogik in UI oder Controllern duplizieren („zur Sicherheit nochmal prüfen" verboten — es gibt eine Quelle).
5. Kein `Task.Result`/`.Wait()`/`async void` (außer Event-Handlern mit Try/Catch).
6. Keine stillen Teilerfolge: OpenAPI-Import, Config-Änderung, Server-Add sind atomar — ganz oder mit Fehler zurückgerollt.
7. Kein globaler veränderlicher Zustand außerhalb der dafür vorgesehenen Snapshot-Stores (Katalog/RBAC-Snapshot sind immutable-swap).
8. Keine ungeprüfte Weitergabe von Upstream-Beschreibungstexten in die UI ohne Encoding (Tool-Descriptions sind fremder Input — XSS).
9. Keine neuen NuGet-Abhängigkeiten ohne Lizenz-Check (NFR-10) und Eintrag in `Directory.Packages.props`.
10. Keine Feature-Arbeit an einem WP beginnen, dessen vorgelagerte DoD nicht erfüllt ist.

## 9. Risiken & Mitigationen

| # | Risiko | W'keit | Impact | Mitigation |
|---|---|---|---|---|
| R1 | SDK-Abstraktionen verhindern sauberes Proxying (z. B. Notification-Durchreichung) | mittel | hoch | Spike in WP1.1 zeitlich boxen (2 Tage); Fallback: `ModelContextProtocol.Core`-Low-Level (ADR-0002 sieht das vor) |
| R2 | Windows/Linux-Prozessmanagement (Orphans, Signale) frisst Zeit | hoch | mittel | Früh in WP1.2 auf beiden OS in CI testen; Job-Object-/Prozessgruppen-Code isoliert kapseln |
| R3 | Schema-Validierung beliebiger Tool-Schemas (JSON-Schema-Dialekte) unvollständig | mittel | mittel | Etablierte Lib (JsonSchema.Net) + definierter Fallback: bei nicht validierbarem Schema durchlassen und Ereignis loggen (Draft-Vielfalt der Server) |
| R4 | Blazor-Server-Circuits + Live-Daten führen zu Memory-Leaks bei Dauerbetrieb | niedrig | mittel | Dashboard-Subscriptions über `IDisposable`-Pattern, 24-h-Soak-Test in WP7.1 |
| R5 | Token-Ersparnis-Ziel (≥ 80 %) verfehlt, weil Agenten Pinned-Tools massenhaft brauchen | niedrig | mittel | Token-Cockpit früh (WP2.1-Schätzer), Referenzmessung schon nach WP4, nicht erst WP7 |
| R6 | Scope-Creep durch Could-Features (Approval-Flows, Webhooks, Federation-Vollausbau) | hoch | mittel | Could bleibt aus v1 draußen; jede Aufnahme nur per PRD-Änderung durch Product Owner |
| R7 | Bösartiger/instabiler Upstream kompromittiert Gateway-Host (ADR-0005-Restrisiko) | niedrig | hoch | Doku-Warnung, Betrieb als non-root-Container empfohlen; v2-Pfad Container-Isolation offengehalten |
| R8 | Ein-Personen-Projekt: Bus-Faktor und Review-Lücke | hoch | mittel | Dieses Doku-Set + ADR-Pflicht + PR-Selbstreview gegen Abschnitt 8; Code-Review-Skill je WP-Abschluss |

## 10. Meilensteine

| MS | Inhalt | Nachweis | Ziel |
|---|---|---|---|
| **M1 „Skelett spricht"** | WP0–WP1 | EchoServer via Gateway von MCP Inspector aufrufbar; Crash-Restart-Demo | +3 Wochen |
| **M2 „Kontrollpunkt steht"** | WP2–WP4 | Hot-Swap-, RBAC- und Audit-Integrationstests grün; Claude Code angebunden; Latenz-Benchmark bestanden | +7 Wochen |
| **M3 „Beide Brücken tragen"** | WP5 | REST-Roundtrip + OpenAPI-Import-Demo, Pfad-Konsistenz-Test grün | +9 Wochen |
| **M4 „v1.0 abnehmbar"** | WP6–WP7 | Alle 7 PRD-Abnahmekriterien erfüllt, Docker-Release, Security-Audit clean | +13 Wochen |

Summe Schätzung ≈ 10–11 Wochen Netto-Einzelarbeit; mit 25 % Puffer ≈ 13 Wochen (Teilzeit entsprechend strecken).

## 11. Erfolgskriterien

Ausschließlich PRD Abschnitt 8 (Kriterien 1–7). Der Plan gilt als abgearbeitet, wenn jedes WP seine DoD erfüllt **und** die PRD-Abnahme dokumentiert ist (`docs/acceptance/v1.md`, entsteht in WP7).

## 12. Offene Punkte

1. PRD OQ-2 (konkrete Referenz-Server des Betreibers) — vor WP1-Ende klären, beeinflusst TestServers-Auswahl nicht, wohl aber die M2-Demo.
2. UI-Komponentenbibliothek (ADR-0004-Folgeentscheidung) — Entscheidung zu WP6-Start.
3. PRD OQ-3/OQ-4 (async REST, Skill-Datei-Sync) — bewusst auf nach-M4 vertagt.
4. GitHub-Repo/Remote noch nicht angelegt (Verzeichnis ist noch kein git-Repo) — Teil von WP0.1.
