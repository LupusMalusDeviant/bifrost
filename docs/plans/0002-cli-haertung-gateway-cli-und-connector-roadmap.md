# CLI-Härtung, Gateway-CLI und Connector-Roadmap

- **Stand:** 2026-07-24
- **Ausgangspunkt:** `main`, `edadab39b47836f4c82edc1814c8ac4550cf02ad`
- **CLI-Basis:** `61199ab08b1f1bf811c7423588d75e905157d5fd`
- **Status:** Phase-0-Befund; Umsetzung in einzeln prüfbaren Schritten

## Umsetzungsstand dieses Arbeitsstands

- Phase 1 umgesetzt und durch Core-/API-Regressionstests belegt.
- Phase 2 für den direkten Hostprozess umgesetzt; Container-/WASI-Isolation bleibt gemäß
  ADR-0017/0018 ein eigener Meilenstein.
- Offizielle Gateway-CLI umgesetzt und separat getestet.
- Capability-, Connector-, WASI-, Native-Isolation- und Task/Event-ADRs sowie Spikes/Matrix
  angelegt. OpenRPC, gRPC, GraphQL, AsyncAPI und A2A sind bewusst noch nicht produktiv implementiert.
- M4 ist als ausführbarer Wasmtime-47-Spike fortgeschritten: WIT-Component-Reflection,
  deny-by-default Imports, Hash, Fuel/Epoch, Memory-/Output-Limits und Windows-/Linux-CI sind
  implementiert. Publisher-Signatur und produktiver WASI-Host bleiben offen.

## Scope und Begriffe

Drei voneinander unabhängige CLI-Richtungen dürfen nicht vermischt werden:

1. **Gateway → CLI-Programm:** ADR-0014 und `CliUpstreamConnector` bilden einen Prototyp.
2. **CLI-Client → Gateway → Capability:** Eine offizielle Gateway-CLI existiert noch nicht.
3. **Admin-CLI → Gateway-Konfiguration:** Außer Recovery- und Health-Flags existiert keine
   allgemeine Admin-CLI.

Die breite Connector-Roadmap folgt erst auf den Sicherheits- und Datenmodellen, von denen sie
abhängt. Insbesondere werden AsyncAPI und A2A nicht vor einem persistenten Task-/Event-Modell
implementiert.

## Befunde

### Kritisch

1. **CLI-Secrets werden von Admin-Antworten nicht maskiert.**
   `ApiEndpoints.RedactConfig` berücksichtigt stdio-Environment, HTTP-Header und
   OpenAPI-Credentials, aber nicht `Cli.EnvironmentVariables`. Jede Version einer CLI-Konfiguration
   kann dadurch über `/api/v1/servers/{id}/history` im Klartext ausgegeben werden.
2. **CLI-Secrets werden aus Connection-Test-Fehlern nicht entfernt.**
   `UpstreamConnectionTester.Secrets` kennt `Cli.EnvironmentVariables` nicht. Von Connectoren oder
   Betriebssystemen erzeugte Fehlertexte gelangen so unverändert in API/UI und können anschließend
   auch in Logs oder Auditdetails kopiert werden.
3. **CLI-Secrets gehen beim Bearbeiten verloren oder werden als Maskenwert gespeichert.**
   `UpstreamConfigMerge` übernimmt CLI-Environment nicht. Die bisherige API-Rekonfiguration führt
   außerdem gar keinen zentralen Secret-Merge aus. Eine aus der Historien-API zurückgesendete Maske
   ist nicht von einem echten Secret-Wechsel unterschieden.

### Hoch

4. **Das Output-Limit schützt den Speicher nicht während des Lesens.**
   stdout und stderr werden parallel, aber jeweils vollständig mit `ReadToEndAsync` gepuffert.
   `MaxOutputBytes` wird erst danach auf `string.Length` angewandt und zählt damit UTF-16-Zeichen
   statt Bytes. Endlose oder sehr große Ausgabe kann bis zum Timeout unbeschränkt Speicher belegen.
5. **Fehlerausgabe ist unvollständig und nicht maschinenlesbar.**
   Bei regulärem Prozessende gewinnt stdout vollständig gegen stderr. Truncation existiert nur als
   Textsuffix; getrennte Streams, Bytezahlen und Truncation-Flags fehlen.
6. **CLI-Prozesse erben standardmäßig das vollständige Gateway-Environment.**
   `ProcessStartInfo.Environment` wird nicht geleert. Gateway-Datenbank-, OTel-, Bootstrap-,
   Proxy- oder andere Host-Credentials können implizit an ein Tool gelangen.
7. **Executable- und Working-Directory-Policy fehlen.**
   Relative Programme dürfen über `PATH` aufgelöst werden; Pfade werden weder kanonisiert noch
   gegen erlaubte Wurzeln geprüft. Symlinks/Reparse Points und relative Working Directories sind
   unberücksichtigt.
8. **Freie Argumentlisten sind standardmäßig aktiv.**
   `CliToolSpec.AllowCallerArguments` ist per Default `true`. Shell-Injection wird zwar durch
   `ArgumentList` verhindert, semantisch gefährliche Programmargumente bleiben aber untypisiert und
   unklassifiziert.
9. **Parallelität und Prozessanzahl sind unbeschränkt.**
   Es gibt weder ein Limit pro Upstream noch pro Kommando. Timeout/Caller-Cancellation töten zwar
   best effort den Prozessbaum, aber Shutdown, Kill-Fehler und Orphans sind nicht nachgewiesen.

### Mittel

10. **CLI-Validierung ist nur prototypisch.**
    Doppelte oder syntaktisch ungültige Toolnamen, ungültige Output-/Parallelitätslimits,
    widersprüchliche Parameter und nicht erlaubte Pfade werden nicht abgewiesen.
11. **Risk Classification ist nicht im CLI-Manifest verankert.**
    Read/write/destructive/privileged fehlen; eine sichere Default-Approval-Regel für destructive
    und privileged ist damit nicht ausdrückbar.
12. **Versionsangaben laufen auseinander.**
    MCP ServerInfo nutzt `0.4.0`, OpenTelemetry `1.1.0`, während Dokumentation/Releasehistorie auch
    `0.5.0` nennt. Assembly-, Package-, Protokoll- und Telemetrieversion haben keine gemeinsame
    Quelle.
13. **Audit ist ausschließlich best effort.**
    Der Channel zählt Drops; der Batch-Writer loggt Datenbankfehler und verwirft den gesamten Batch.
    Das Verhalten ist dokumentiert, aber ein Compliance-Modus, Readiness-Signal und Retry-/Spool-
    Vertrag fehlen.

### Architektur

14. `IUpstreamConnector`, `IUpstreamConnection`, `UpstreamInventory` und Deskriptoren kapseln das
    MCP-SDK sauber, modellieren aber primär synchrone Tools/Resources/Prompts. Task, Event,
    Subscription, Streaming, Artifact, strukturierter Fehler und transportneutrale Capability-
    Eigenschaften fehlen.
15. Es existiert kein versionierter, isolierbarer Drittanbieter-Connectorvertrag. Die aktuelle
    additive DI-Architektur ist ein guter interner Erweiterungspunkt, aber noch kein SDK-,
    Packaging-, Trust- oder Kompatibilitätsvertrag.

## Betroffene Dateien und Verträge

- `Bifrost.Abstractions/Upstream.cs`: gespeicherter JSON-Vertrag für CLI-Optionen und Manifeste.
- `Bifrost.Core/Upstreams/UpstreamConfigValidator.cs`: zentrale Konfigurationsgrenze.
- `Bifrost.Core/Upstreams/UpstreamConnectionTester.cs`: Fehlertext-Redaction vor UI/API.
- `Bifrost.Core/Upstreams/UpstreamConfigMerge.cs`: Patch-/Carry-over-Semantik für Secrets.
- neue zentrale Config-Redaction im Core; `Bifrost.Server/ApiEndpoints.cs` konsumiert sie.
- `Bifrost.Upstream/Cli/CliUpstreamConnector.cs`: Prozess-, Stream-, Environment- und Lifecycle-
  Grenze.
- `Bifrost.Server/Program.cs` und Build-Properties: Runtime-Policy, DI und gemeinsame Version.
- `Bifrost.Web/Components/Pages/Servers.razor`: sichere Editiersemantik und später CLI-Manifest-UI.
- Core-, Upstream- und Integrationstests: Sicherheitsregressionen und OS-Matrix.

## Kompatibilitäts- und Migrationsrisiken

- `UpstreamServerConfig` wird als verschlüsselter Blob gespeichert. Neue optionale Felder müssen
  sinnvolle sichere Defaults besitzen, damit alte Blobs weiterhin deserialisieren.
- Ein Wechsel von `AllowCallerArguments=true` auf einen sicheren Default verändert neu erzeugte
  Konfigurationen, darf gespeicherte explizite `true`-Werte aber nicht umdeuten.
- Absolute Executable-Pfade und leeres Host-Environment können bestehende CLI-Prototypen brechen.
  Deshalb braucht es einen expliziten Development-Kompatibilitätsmodus und klare Validierungsfehler.
- Secret-Patches müssen `null`/nicht geliefert (beibehalten), `"***"` (beibehalten), einen neuen
  Wert (ersetzen) und eine explizite leere Sammlung bzw. leeren Credential-Wert (löschen)
  unterscheiden. Masken dürfen nie persistiert werden.
- Ergebnisobjekte können additive stdout/stderr- und Truncation-Metadaten erhalten; bestehende
  `content`- und `isError`-Felder bleiben erhalten.
- Typisierte Manifeste werden additiv eingeführt. Das alte freie `args`-Schema bleibt nur für
  ausdrücklich migrierte Legacy-Konfigurationen/Development erhalten.
- Capability- und Connector-Verträge werden zunächst versioniert neben den bestehenden Interfaces
  eingeführt; ein Big-Bang-Ersatz würde gespeicherte Daten und Connectoren unnötig gefährden.

## Testplan

1. **Secrets:** zentrale Maskierung aller Transport-Secrets; API-Historie enthält weder exakte noch
   eingebettete CLI-Secrets; Scrubbing deckt exakte/eingebettete Werte und kurze Nicht-Secrets ab.
2. **Merge:** CLI-Carry-over; Masken-Carry-over; kompletter und partieller Wechsel; expliziter Reset;
   API-Reconfigure verwendet dieselbe Semantik.
3. **Streams:** große stdout/stderr-Daten, gleichzeitige/endlose Ausgabe, Byte-Cap vor
   Materialisierung, getrennte Streams, Nonzero-Exit, Unicode und ungültige/mehrbyte Encodings.
4. **Prozess:** Metazeichen literal, Argumentreihenfolge, Timeout vs. Caller-Cancellation,
   Prozessbaum-Kill, Dispose/Shutdown und Parallelitätsgrenzen.
5. **Policy:** Environment-Isolation und kontrollierte Basiswerte; absolute/kanonische Executables;
   erlaubte Roots; Working-Directory-Roots; Traversal und Links/Reparse Points.
6. **Manifest:** alle Parametertypen und Constraints; freie Argumente default-off; doppelte Namen;
   Konflikte/Abhängigkeiten; destructive/privileged Approval-Default.
7. **Gateway-CLI:** JSON-Golden-Tests, Exitcodes, stdin/file/env-Konfiguration, TLS-Default,
   öffentliche REST-Verträge und RBAC-/Approval-Fehler.
8. **Regression:** vollständige Core-, Upstream- und Integrationstests auf Windows und Linux.

## Kleine, logisch getrennte Commits

1. `security: redact and preserve CLI configuration secrets`
2. `security: stream and byte-cap CLI process output`
3. `security: isolate CLI environment and enforce path policy`
4. `feat: add typed risk-classified CLI manifests`
5. `feat: enforce CLI concurrency and lifecycle limits`
6. `feat: add public-contract gateway CLI client`
7. `architecture: define neutral capability and connector contracts`
8. `architecture: add WASI and native isolation ADRs`
9. `architecture: define task/event model and connector decision matrix`
10. `spike: bound WIT discovery, OpenRPC import and gRPC unary experiments`
11. `chore: unify product and telemetry versioning`
12. `audit: specify best-effort and compliance operating modes`

Die Commits sind eine geplante Review-Grenze. Änderungen werden nicht automatisch committed; fremde
Working-Tree-Dateien bleiben unberührt.
