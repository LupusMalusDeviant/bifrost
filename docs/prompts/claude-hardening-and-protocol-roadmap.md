# Agentenanweisung: CLI-Härtung, Gateway-CLI und Connector-Roadmap

> Stand der Anweisung: 2026-07-24  
> Ausgangspunkt: `main`, HEAD `edadab39b47836f4c82edc1814c8ac4550cf02ad`  
> CLI-Basiscommit: `61199ab08b1f1bf811c7423588d75e905157d5fd`

## Arbeitsstand und Übergabe – 2026-07-24

> Dieser Abschnitt dokumentiert den tatsächlich belegten Stand nach der Bearbeitung. Die
> nachfolgende ursprüngliche Anweisung bleibt als Soll und Priorisierung erhalten.

### Legende

- **ABGESCHLOSSEN:** Code, Dokumentation und lokale Tests vorhanden.
- **TEILWEISE:** Architektur oder Spike vorhanden, aber die vollständige Phase ist noch nicht
  produktiv umgesetzt.
- **OFFEN:** Noch nicht implementieren; Abhängigkeiten und Priorisierung dieser Datei beachten.

### Phasenstatus

| Phase | Status | Belegter Stand |
|---|---|---|
| 0 – Bestandsaufnahme | **ABGESCHLOSSEN** | Befund, Risiken, Dateiplan, Testplan und Commit-Aufteilung in `docs/plans/0002-cli-haertung-gateway-cli-und-connector-roadmap.md`. |
| 1 – Secret-Fixes | **ABGESCHLOSSEN** | Zentrale Redaction, CLI-Secret-Scrubbing, sichere Carry-over-/Wechsel-/Reset-Semantik und API-/Core-Regressionstests. |
| 2 – CLI-Prozesshärtung | **ABGESCHLOSSEN für Trusted Host Process** | Streaming-Bytecaps, getrenntes stdout/stderr, minimale Umgebung, Pfad-/Root-/Hash-Policy, Lifecycle, Prozessbaum-Kill, Parallelitätslimits, typisierte Manifeste und Approval-Defaults. Direkter Hostmodus bleibt ausdrücklich **keine Sandbox**. |
| 3 – Gateway-CLI | **ABGESCHLOSSEN** | Offizielle `mcp-mcp`-CLI ausschließlich über öffentliche HTTP-Verträge, inklusive JSON-Modus, Pipelines, Exitcodes und Administration. |
| 4 – Capability-Modell | **TEILWEISE** | ADR-0015 sowie additive Risk-/Approval-/Truncation-Metadaten vorhanden. Vollständige Queries, Events, Streams, Tasks, Artifacts und Delegation fehlen. |
| 5 – Connector-/Plugin-Vertrag | **TEILWEISE; Laufzeitvertrag steht** | Der WASI-Host erfuellt den Laufzeitteil von ADR-0016: Handshake mit Capability-Flags, Correlation-Id, normierte Fehlerhuelle, Discovery mit Schema-Normalisierung, bestaetigte Cancellation, Readiness getrennt von Liveness, Lifecycle bis drain/stop. Offen bleibt der Paketteil: signiertes Manifest, Packaging, Installation, Update/Rollback in Quarantaene, Vertrauensstufen. |
| 6 – WASI/Component Model | **WEITGEHEND; Streams zurückgestellt** | Produktionshost im Image, Publisher-Trust-Store mit sofortigem Entzug, feingranulare WASI-P2-Grants (deny-before-instantiation), Modul- und Platten-Cache, IPC-Vertrag v4 mit Korrelation, Nebenläufigkeit und bestätigtem Abbruch. Aufrufbreite deckt alle WIT-Typen ab **außer** `future`/`stream` — die sind am 2026-07-25 zurückgestellt worden, weil ein dynamischer Host sie nur für fest einkompilierte Payload-Typen lesen kann. ADR-0017 akzeptiert, ADR-0020 akzeptiert. |
| 7 – Container-Isolation | **TEILWEISE** | ADR-0018, Mindestpolicy und reproduzierbarer OCI-Startupvergleich vorhanden. Kein Container-Runtimeadapter. |
| 8 – OpenRPC | **TEILWEISE / Design-Spike** | Import-/Security-Spike und Roadmap vorhanden; kein Connector. |
| 9 – gRPC | **TEILWEISE / Design-Spike** | Unary-/Reflection-/Descriptor-Set-Spike und Roadmap vorhanden; kein Connector. |
| 10 – GraphQL | **OFFEN** | Nur Entscheidungsmatrix/Roadmap. |
| 11 – Task-/Event-Modell | **TEILWEISE; entschieden** | ADR-0019 seit 2026-07-25 akzeptiert: Task verallgemeinert die Freigabe, Polling ist der Vertrag, Abbruch persistiert, `input-required` ist Elicitation über Tasks. Persistenz und API fehlen weiterhin; EventV1 mit Zustellzusage ist ausdrücklich vertagt. |
| 12 – AsyncAPI | **OFFEN** | Bewusst blockiert, bis Phase 11 stabil umgesetzt ist. |
| 13 – A2A | **OFFEN** | Bewusst blockiert, bis Task-, Delegations-, Budget- und Loop-Semantik stabil sind. |
| 14 – SOAP/WSDL | **BEWERTET / ZURÜCKGESTELLT** | Architekturkompatibilität in der Matrix; keine Implementierung ohne validierte Nachfrage. |
| 15 – Audit/Versionierung/Enterprise | **TEILWEISE** | Versionen auf `0.5.0` vereinheitlicht; Best-effort-/Compliance-Audit, Retry/Backpressure und Readiness umgesetzt. Vollständige Metriken/Alarmierung und Enterprise-Themen bleiben offen. |

### Meilensteinstatus

- [x] **M1:** CLI-Prototyp abgesichert und Secret-Leaks geschlossen.
- [x] **M2:** Offizielle Gateway-CLI umgesetzt.
- [ ] **M3:** ADRs vorhanden; das Capability-V1-Modell (ADR-0015) fehlt weiterhin. Der
  **Connector-Handshake** ist seit dem 2026-07-25 umgesetzt — der Laufzeitteil von ADR-0016 am
  WASI-Host, siehe Plan 0003. Offen bleibt der Paketteil (Manifest, Installation, Trust-Stufen).
- [x] **M4:** WASI-Component-Spike mit WIT-Discovery — erfüllt und inzwischen von M5 überholt.
- [x] **M5:** Produktionsfähiger WASI-Pluginpfad (Plan 0003, abgeschlossen 2026-07-25):
  Produktionshost im Image, Publisher-Trust-Store mit sofortigem Entzug, feingranulare WASI-P2-Grants,
  Modul- und Platten-Cache, IPC-Vertrag v4, Resources, Readiness/Lifecycle/Capability-Flags.
  **Ein Erfolgskriterium bleibt offen:** der CI-Nachweis auf Linux — der letzte CI-Lauf datiert auf
  den 2026-07-24, die Commits danach sind nicht gepusht. Das **Connector-SDK** aus dem
  M5-Titel meint nur den Laufzeitvertrag; Packaging von Fremdconnectoren ist nicht Teil davon.
- [ ] **M6–M10:** Nicht beginnen, bevor die jeweiligen Vorgänger und Sicherheitsmodelle erfüllt sind.

### Wichtige Implementierungsartefakte

- Phase-0-/Gesamtplan:
  `docs/plans/0002-cli-haertung-gateway-cli-und-connector-roadmap.md`
- Connector-Matrix und Releasefolge:
  `docs/plans/connector-entscheidung-und-release-roadmap.md`
- Secret-Redaction:
  `src/McpMcp.Core/Upstreams/UpstreamConfigRedactor.cs`
- CLI-Härtung:
  `src/McpMcp.Upstream/Cli/CliUpstreamConnector.cs`,
  `CliProcessPolicy.cs`, `CliArgumentBinder.cs`, `BoundedProcessOutput.cs`
- Gateway-CLI:
  `src/McpMcp.Cli/` und `docs/gateway-cli.md`
- Architektur:
  `docs/adr/0015-*` bis `docs/adr/0019-*`
- Ausführbarer M4-Spike:
  `spikes/wasi-component-runtime/`
- WIT-/WASI-Nachweis:
  `docs/spikes/wasi-component-discovery.md`
- WASI-/Containervergleich:
  `docs/spikes/wasi-vs-container-result-2026-07-24.md`

### Letzte lokale Verifikation

- Branch/HEAD weiterhin: `main`,
  `edadab39b47836f4c82edc1814c8ac4550cf02ad`.
- Alle Änderungen sind **uncommitted und unstaged**. Nichts resetten, auschecken oder pauschal
  bereinigen.
- `.NET`-Gesamtbuild: **0 Warnungen, 0 Fehler**.
- `.NET`-Tests: **372 bestanden, 1 planmäßig übersprungen, 0 fehlgeschlagen**.
- Rust-Spike: **5 bestanden**, `cargo fmt --check` und
  `cargo clippy --locked --all-targets -- -D warnings` grün.
- `.github/workflows/ci.yml` wurde als YAML validiert.
- `git diff --check` ist sauber; Git meldet lediglich die vorhandenen LF/CRLF-Hinweise.
- `dotnet format --verify-no-changes` war wegen bereits repositoryweit vorhandener
  Format-/Zeilenendungsabweichungen nicht grün. **Keinen kosmetischen Bulk-Format durchführen.**

Reproduzierbare Prüfkommandos:

```text
dotnet build MCPMCP.slnx --no-restore --nologo
dotnet test MCPMCP.slnx --no-build --no-restore --nologo

cd spikes/wasi-component-runtime
cargo fmt --check
cargo clippy --locked --all-targets -- -D warnings
cargo test --locked
cargo run --locked --quiet -- probe
```

### M4-Spike-Ergebnis

- Runtime: Wasmtime `47.0.2`, Rust `1.94.0`.
- WIT-Component-Hash:
  `b17cb7254db649066f580615f0d608796895bedde4a267f91ed98518ac3ec871`.
- Reflektierter Export: `mcpmcp:spike/tools@0.1.0.run`.
- Leerer Grant-Satz akzeptiert das importfreie Component.
- Nicht gewährte filesystem-/socket-Imports werden vor Instanziierung abgewiesen.
- Fuel, Epoch-Timeout, 128-KiB-Linear-Memory-Cap und 64-KiB-Output-Cap greifen.
- Lokaler Startup-Floor, 7 Samples: WASI Median `7,16 ms`; gehärteter Alpine-Container
  Median `232,40 ms`. Dies ist keine Sicherheitsäquivalenz und kein Durchsatzbenchmark.
- CI ist für `windows-latest` und `ubuntu-latest` konfiguriert, wurde in dieser lokalen Session
  aber nicht extern ausgeführt.

### Verbindlicher Startauftrag für die nächste Session

1. Zuerst `git status`, Branch und HEAD prüfen. Den vorhandenen Working Tree als zusammengehörigen
   Arbeitsstand behandeln; keine fremden oder untracked Dateien löschen.
2. ADR-0017, ADR-0018, `docs/spikes/wasi-component-discovery.md` und den Rust-Spike vollständig
   lesen.
3. **M4 abschließen, nicht zu M5 oder OpenRPC springen:**
   - detached Publisher-Signatur und administrativ gepinnte Publisher-Keys für Component-Bytes;
   - echte WASI-P2-filesystem-/socket-Import-Fixtures statt nur passend benannter
     Component-Imports;
   - explizites Grant-Modell für Preopens, Netzwerk, Environment und Secret-Capabilities,
     Standard jeweils deny;
   - Auditdatensatz für Modulhash, Publisher, Runtimeversion und erteilte Grants;
   - Negativtests für Traversal/Symlink-Ausbruch, nicht erlaubte Sockets und nicht gewährte
     Secrets;
   - externen Windows-/Linux-CI-Lauf auswerten, sobald der Arbeitsstand committed/gepusht werden
     darf.
4. Erst wenn diese M4-Kriterien belegt sind, ADR-0017 neu bewerten und M5
   (WASI-Pluginpfad/Connector-SDK) planen.
5. Keine Produktionsreife behaupten: Der aktuelle WASI-Code ist ein separates Spike-Projekt,
   kein Server-Runtimeadapter. Der native CLI-Hostmodus ist gehärtet, aber nicht sandboxed.
6. Keine Commits, Staging-, Push- oder PR-Aktionen ohne ausdrücklichen Auftrag des Benutzers.

## Rolle und Arbeitsweise

Du arbeitest als Senior Software Architect und Security Engineer im Repository MCPMCP.

Der CLI-Upstream aus ADR-0014 ist bereits als Prototyp implementiert. Baue ihn nicht noch einmal von Grund auf. Prüfe den aktuellen Stand und härte ihn systematisch.

Arbeite testgetrieben, in kleinen überprüfbaren Änderungen und ohne kosmetische Großumbauten. Erhalte bestehende öffentliche APIs und gespeicherte Daten, sofern keine begründete Migration erforderlich ist.

Vor jeder Änderung:

1. Prüfe `git status`, Branch und HEAD.
2. Lies vorhandene Repository-Anweisungen und ADRs.
3. Respektiere fremde Änderungen im Working Tree.
4. Verwende keine destruktiven Git-Befehle.
5. Behaupte keine Produktionsreife, die nicht durch Code, Tests und Betriebsnachweise belegt ist.

## Produktziel

MCPMCP soll sich zu einer selbst gehosteten **Tool and Agent Control Plane** entwickeln:

- einheitliche Discovery für MCP-, API-, CLI- und Plugin-Tools;
- zentrale Identitäten, RBAC, Policies und Guardrails;
- Freigaben für riskante Aktionen;
- Secret-Schutz, Rate-Limits, Audit und Observability;
- sichere oder isolierte Ausführung;
- protokollneutrale Tool-, Task- und Event-Modelle;
- erweiterbar über stabile Connectoren, ohne den Core in ein Protokoll-Sammelsurium zu verwandeln.

Der Produktwert ist nicht die bloße Zahl unterstützter Protokolle. Der Produktwert ist, dass Fähigkeiten unabhängig von ihrer Herkunft auffindbar, kontrollierbar, freigabepflichtig, beobachtbar und auditierbar werden.

## Phase 0 – Bestandsaufnahme und Befund — ABGESCHLOSSEN

Lies mindestens:

- `docs/adr/0014-cli-programme-als-upstream-transport.md`
- `src/McpMcp.Upstream/Cli/CliUpstreamConnector.cs`
- `src/McpMcp.Abstractions/Upstream.cs`
- `src/McpMcp.Core/Upstreams/UpstreamConfigValidator.cs`
- `src/McpMcp.Core/Upstreams/UpstreamConnectionTester.cs`
- `src/McpMcp.Core/Upstreams/UpstreamConfigMerge.cs`
- `src/McpMcp.Server/ApiEndpoints.cs`
- `src/McpMcp.Web/Components/Pages/Servers.razor`
- Audit-Sink und Audit-Batch-Writer
- Versionsangaben und ServerInfo in `Program.cs`
- bestehende Core-, Upstream- und Integrationstests

Unterscheide ausdrücklich drei CLI-Richtungen:

1. **Gateway → CLI-Programm:** als ADR-0014-Prototyp vorhanden.
2. **CLI-Client → Gateway → beliebiges Tool:** noch nicht als offizielle Gateway-CLI vorhanden.
3. **Admin-CLI → Gateway-Konfiguration:** bis auf Recovery-/Health-Flags nicht vorhanden.

Liefere vor der Implementierung:

1. Befunde nach Schweregrad;
2. betroffene Dateien und Verträge;
3. Kompatibilitäts- und Migrationsrisiken;
4. Testplan;
5. Aufteilung in kleine, logisch getrennte Commits.

## Phase 1 – Unmittelbare Secret-Fixes — ABGESCHLOSSEN

Behandle die unvollständige CLI-Secret-Behandlung als Sicherheitsfehler.

### Management-API

Erweitere die Konfigurationsmaskierung:

- `Cli.EnvironmentVariables` müssen in Konfigurationshistorien und Admin-Antworten maskiert sein.
- Kein Secret darf allein deshalb sichtbar werden, weil ein neuer Transporttyp ergänzt wurde.
- Zentralisiere die Secret-Maskierung, wenn dadurch zukünftige Transporttypen nicht erneut vergessen werden.

### Connection-Test und Fehlermeldungen

Erweitere `UpstreamConnectionTester.ScrubSecrets`:

- Werte aus `Cli.EnvironmentVariables` wie stdio-Environment behandeln;
- fremde Fehlermeldungen vor UI, API, Logs und Audit bereinigen;
- Tests mit exakten Secret-Werten, eingebetteten Werten und kurzen Nicht-Secrets ergänzen.

### Konfigurationsänderungen

Erweitere `UpstreamConfigMerge`:

- CLI-Secrets beim Bearbeiten sicher übernehmen;
- leere, maskierte und explizit gelöschte Werte eindeutig unterscheiden;
- ein Speichern über UI oder API darf Secrets nicht versehentlich verlieren oder offenlegen.

### Regressionstests

Ergänze Tests für:

- API-Konfigurationshistorie;
- Connection-Test-Fehler;
- Secret-Carry-over;
- expliziten Secret-Wechsel und Secret-Reset.

## Phase 2 – CLI-Prozesshärtung — ABGESCHLOSSEN FÜR TRUSTED HOST PROCESS

Überarbeite den vorhandenen Connector, ohne seine additive Connector-Architektur unnötig zu ersetzen.

### 2.1 Output und Speicher

- stdout und stderr nicht vollständig mit unbeschränktem `ReadToEndAsync` puffern.
- Während des Streamings begrenzen.
- Klar definieren, ob Bytes oder Zeichen gezählt werden.
- `MaxOutputBytes` muss tatsächlich Bytes begrenzen oder korrekt umbenannt werden.
- stdout und stderr getrennt erfassen.
- Bei Fehlern nicht versehentlich den relevanten stderr-Text verwerfen.
- Truncation maschinenlesbar kennzeichnen.
- Pipe-Deadlocks verhindern.
- Endlose Ausgabe bis zum Timeout darf den Gateway-Speicher nicht erschöpfen.
- Nicht-ASCII-Ausgabe und Encoding korrekt behandeln.

### 2.2 Environment

- Standardmäßig nicht das vollständige Host-Environment erben.
- Eine minimale, dokumentierte Basisumgebung explizit definieren.
- Zusätzliche Werte nur aus kontrollierter Konfiguration oder Secret-Referenzen.
- PATH-Verhalten ausdrücklich regeln.
- Gateway-interne Credentials dürfen nicht implizit an Tools weitergereicht werden.
- Environment-Namen und Plattformunterschiede validieren.

### 2.3 Executable-Policy

- Produktionsmodus verlangt standardmäßig absolute, kanonisierte Pfade.
- Erlaubte Executable-Wurzeln konfigurierbar machen.
- Path Traversal, Symlinks und Windows Reparse Points berücksichtigen.
- PATH-Auflösung höchstens als ausdrücklich unsicherer Development-Modus.
- Optionales Hash-, Signatur- oder Publisher-Pinning als Erweiterung vorsehen.
- Die Aussage „festes Binary = Allowlist“ nicht überdehnen.

### 2.4 Working Directory und Dateipfade

- Pfade kanonisieren.
- Erlaubte Wurzeln erzwingen.
- Relative Pfade nicht still gegen sensible Gateway-Verzeichnisse auflösen.
- Schreibbare und nur lesbare Pfade unterscheiden.

### 2.5 Ressourcen und Lifecycle

- Parallelitätslimit pro CLI-Upstream und Kommando.
- Prozessanzahl begrenzen.
- Timeout und Caller-Cancellation korrekt unterscheiden.
- Prozessbaum zuverlässig beenden.
- Orphan-Prozesse verhindern.
- Verhalten beim Gateway-Shutdown testen.
- CPU-, RAM-, PID- und Dateisystemlimits für isolierte Runtimes vorbereiten.

### 2.6 Typisierte CLI-Manifeste

Das generische `args: string[]` ist ein Prototyp, kein sicherer Endzustand.

Unterstütze ein typisiertes Manifest mit:

- Parametername und Beschreibung;
- Typ: string, integer, number, boolean, enum, path, secret-reference;
- Position oder festes Flag;
- Pflichtfeld und Default;
- erlaubte Werte;
- Pattern und Wertebereich;
- Pfadregeln;
- Wiederholbarkeit;
- sensitive Kennzeichnung;
- maximale Länge;
- Konflikte und Abhängigkeiten.

Freie Argumentlisten standardmäßig deaktivieren.

Definiere pro Kommando:

- `read`;
- `write`;
- `destructive`;
- `privileged`.

Destruktive und privilegierte Kommandos müssen standardmäßig approval-pflichtig sein. Klassifiziere nicht allein anhand des Programmnamens.

Der Validator muss unter anderem abweisen:

- doppelte Toolnamen;
- leere oder ungültige Namen;
- widersprüchliche Parameter;
- ungültige Timeouts und Limits;
- nicht erlaubte Executables und Working Directories.

### 2.7 CLI-Testmatrix

Teste mindestens:

- Shell-Metazeichen werden literal übergeben;
- Argumentreihenfolge;
- typisierte Parameter;
- deaktivierte freie Argumente;
- Environment-Isolation;
- Secret-Redaction;
- sehr große stdout- und stderr-Ausgaben;
- endlose Ausgabe;
- Nonzero-Exit mit stdout und stderr;
- Timeout und Prozessbaum-Kill;
- Caller-Cancellation;
- Shutdown;
- doppelte Toolnamen;
- ungültige Pfade;
- Parallelitätsgrenzen;
- Unicode und Encoding;
- Verhalten auf unterstützten Betriebssystemen.

## Phase 3 – Offizielle Gateway-CLI — ABGESCHLOSSEN

Baue nicht bloß Curl-Beispiele. Erstelle einen schlanken offiziellen CLI-Client, der ausschließlich öffentliche Gateway-Verträge verwendet.

Zieloberfläche:

```text
mcp-mcp status
mcp-mcp tools search <query>
mcp-mcp tools describe <tool>
mcp-mcp tools invoke <tool> --json '{...}'
mcp-mcp tools invoke <tool> --file args.json
mcp-mcp servers list
mcp-mcp servers add --file server.json
mcp-mcp servers enable <id>
mcp-mcp servers disable <id>
mcp-mcp servers remove <id>
mcp-mcp approvals list
mcp-mcp approvals approve <id>
mcp-mcp approvals deny <id>
mcp-mcp audit tail
```

Anforderungen:

- menschenlesbare Ausgabe und stabiler `--json`-Modus;
- verlässliche Exitcodes;
- stdin/stdout-Pipelines;
- keine Secrets in Prozessargumenten empfehlen;
- Token über Environment, Secret Store oder stdin;
- Endpoint und Identität über Konfigurationsdatei oder Environment;
- TLS-Fehler nicht standardmäßig ignorieren;
- keine direkte Datenbank- oder interne Store-Nutzung;
- identische RBAC-, Approval- und Auditregeln wie andere Clients;
- MCP-, OpenAPI- und CLI-Upstreams einheitlich suchen, beschreiben und aufrufen;
- Skriptstabilität und dokumentierte Kompatibilitätsregeln.

## Phase 4 – Protokollneutrales Capability-Modell — TEILWEISE

Prüfe, ob die bestehenden Abstraktionen langfristig transportneutral genug sind:

- `IUpstreamConnector`
- `IUpstreamConnection`
- `UpstreamInventory`
- `ToolDescriptor`
- `ResourceDescriptor`
- `PromptDescriptor`
- Invocation und Result
- Notifications
- Supervisor und Lifecycle

Das Zielmodell soll mindestens unterscheiden:

### Capability-Arten

- Tool/Command;
- Query;
- Mutation;
- Resource;
- Prompt;
- Event;
- Subscription;
- Long-running Task;
- Agent Delegation.

### Invocation-Eigenschaften

- synchron oder asynchron;
- idempotent oder nicht idempotent;
- read-only, write, destructive oder privileged;
- Cancellation;
- Streaming;
- Binärdaten;
- Fortschritt;
- Retry-Semantik;
- erwartete Laufzeit;
- Seiteneffekte;
- Approval-Anforderung.

### Schema und Identität

- JSON-Schema für den Gateway-Katalog;
- möglichst verlustarme Abbildung nativer Typen;
- Herkunft und Version des Schemas;
- stabile Capability-ID;
- Connector- und Protokollversion;
- technischer Name getrennt vom Anzeigenamen.

### Ergebnisse

- strukturierte Daten;
- Text;
- Binärdaten oder Artifact-Referenz;
- strukturierte Fehler;
- Task-ID;
- Event-/Stream-Referenz;
- Pagination;
- Truncation-Metadaten.

Reduziere nicht jedes Protokoll zwanghaft auf einen ausschließlich synchronen Tool-Call.

## Phase 5 – Stabile Connector-/Plugin-Schnittstelle — TEILWEISE

MCPMCP soll keine monolithische Sammlung fest eingebauter Adapter werden. Entwickle einen versionierten Connector-Vertrag.

Der Vertrag benötigt:

- Discovery;
- Schema-Normalisierung;
- Invocation;
- Cancellation;
- Health und Readiness;
- Lifecycle;
- Secret-Referenzen;
- Auth-Konfiguration;
- Events, Streams und Tasks;
- connector-spezifische Konfigurationsschemas;
- Validierung;
- Observability;
- eindeutige Fehlersemantik;
- Capability Flags;
- Kompatibilitätsprüfung.

Kläre:

- In-Process versus isolierter Connector;
- Vertrauensmodell;
- Signierung und erlaubte Herausgeber;
- Versionierung;
- Installation, Update und Rollback;
- Absturzisolation;
- Ressourcenlimits;
- Netzwerk- und Dateisystemberechtigungen;
- Packaging und Distribution;
- Drittanbieter-Connectoren.

Kein Connector darf Governance umgehen oder direkt auf interne Datenbanken zugreifen.

## Phase 6 – WASI und WebAssembly Component Model — WEITGEHEND, STREAMS ZURÜCKGESTELLT

Behandle WASI nicht als Netzwerkprotokoll, sondern als bevorzugte sichere Ausführungsplattform für lokale Tools und externe Connectoren.

Ziel:

- portable Tools;
- WIT-basierte typisierte Imports und Exports;
- maschinenlesbare Discovery;
- capability-basierter Hostzugriff;
- kontrolliertes Dateisystem und Netzwerk;
- sichere Alternative zu beliebigen Host-Prozessen.

Prüfe einen WASI-/Component-Model-Host mit:

- WebAssembly Components;
- WIT-Discovery und Schema-Mapping;
- expliziten Preopens;
- standardmäßig deaktiviertem Netzwerk;
- Netzwerk-Allowlist;
- kontrolliertem Environment;
- expliziten Secret-Capabilities;
- Fuel-/CPU-Limits;
- Speicherlimits;
- Timeouts;
- Parallelitätslimits;
- Output-Limits;
- Modul-Hash und Signaturprüfung;
- nachvollziehbaren Capability Grants;
- Audit der erteilten Host-Capabilities;
- Modul-Caching;
- Windows-/Linux-Portabilität.

Wichtige Sicherheitsannahme:

WebAssembly stellt eine Sandbox-Grenze bereit. Die Sicherheit hängt dennoch von den freigegebenen Host-Funktionen, Verzeichnissen, Sockets und Secrets ab. Bezeichne WASI nicht pauschal als automatisch sicher.

Erstelle zunächst einen ADR und einen begrenzten technischen Spike.

Offizielle Grundlagen:

- https://webassembly.org/docs/security/
- https://component-model.bytecodealliance.org/design/wit.html

## Phase 7 – Container-Isolation für native Prozesse — TEILWEISE

WASI ersetzt nicht jede native CLI. Definiere einen getrennten Isolationspfad für CLI- und stdio-Prozesse.

Prüfe:

- Container pro Upstream oder Invocation;
- langlebiger Worker versus kurzlebiger Job;
- read-only Root-Filesystem;
- dedizierter Benutzer;
- entfernte Linux Capabilities;
- seccomp/AppArmor;
- CPU-, RAM-, PID- und Disk-Limits;
- Netzwerk standardmäßig deaktiviert;
- explizite Netzwerk-Allowlist;
- Mount-Allowlist;
- Secret-Injection ohne Persistenz;
- Kill- und Cleanup-Garantien;
- Windows-Kompatibilität;
- Betrieb ohne Container-Runtime.

Zielmodi:

1. WASI als bevorzugter isolierter Pluginpfad;
2. Container für bestehende native Programme;
3. direkter Host-Prozess nur als ausdrücklich vertrauenswürdiger oder Development-Modus.

## Phase 8 – OpenRPC/JSON-RPC — DESIGN-SPIKE, CONNECTOR OFFEN

OpenRPC ist der voraussichtlich günstigste zusätzliche Protokollgewinn.

Erste Version:

- OpenRPC-Dokument importieren;
- Methoden als Capabilities abbilden;
- Parameter und Resultate in JSON-Schema überführen;
- benannte und positionale Parameter unterscheiden;
- JSON-RPC-Fehler strukturiert abbilden;
- Request-ID-Korrelation;
- HTTP-Transport;
- Auth-Header und Secret-Referenzen;
- Timeout und Cancellation;
- `rpc.discover` optional verwenden;
- Schemaimporte gegen SSRF, Größenangriffe und zyklische Referenzen absichern.

Batch-Requests und Notifications entweder sauber spezifizieren oder zunächst ausdrücklich ausnehmen.

Nutze gemeinsame Parser- und Validierungsmuster des OpenAPI-Adapters, ohne OpenRPC künstlich in OpenAPI umzuwandeln.

Offizielle Spezifikation:

- https://spec.open-rpc.org/

## Phase 9 – gRPC — DESIGN-SPIKE, CONNECTOR OFFEN

gRPC ist für interne Enterprise-Services strategisch relevant.

Begrenze Version 1 auf Unary-RPCs.

Discovery:

- Server Reflection, wenn aktiviert;
- statische Descriptor-Sets als Alternative;
- Services, Methoden und referenzierte Protobuf-Typen.

Mapping:

- Request als Eingabeschema;
- Response als strukturiertes Ergebnis;
- Enums, `oneof`, Maps, `repeated` und Well-known Types;
- Bytes über begrenzte Daten oder Artifact-Referenzen;
- gRPC-Statuscodes und Details;
- Deadlines und Cancellation;
- Metadaten und Credentials als Secrets.

Client-, Server- und bidirektionales Streaming erst nach einem stabilen Stream-/Task-Modell.

Offizielle Reflection-Dokumentation:

- https://grpc.io/docs/guides/reflection/

## Phase 10 – GraphQL — OFFEN

Nutze Introspection zur Schemaanalyse, aber exportiere nicht jedes Schemafeld als eigenes Tool.

Ziel:

- kuratierte Queries;
- kuratierte Mutations;
- bevorzugt gespeicherte oder administrativ registrierte Operationen;
- Variablen als Eingabeschema;
- strukturierte Ergebnisse;
- Mutations standardmäßig approval-pflichtig;
- Complexity- und Depth-Limits;
- Antwortgrößenlimits;
- Introspection-Cache und Schemaänderungen;
- sichere Auth- und Secret-Header.

Vermeide:

- ein Tool pro Feld;
- freie, unkontrollierte GraphQL-Dokumente vom Agenten;
- unbegrenzte Tiefe;
- ungefilterte Introspection-Daten im Agentenkontext.

Subscriptions erst nach einem Event-/Subscription-Modell.

Offizielle Grundlage:

- https://spec.graphql.org/October2016/#sec-Introspection

## Phase 11 – Internes Task- und Event-Modell — TEILWEISE

Vor AsyncAPI und A2A benötigt MCPMCP persistente Modelle für asynchrone Vorgänge.

### Task

- Task-ID;
- Capability-ID;
- Identität;
- `created`, `working`, `input-required`, `completed`, `failed`, `cancelled`, `expired`;
- Fortschritt;
- Ergebnis oder Artifact;
- strukturierter Fehler;
- Cancellation;
- TTL und Cleanup;
- Berechtigungen;
- Audit-Korrelation;
- optionales Folge-Input.

### Event

- Event-ID;
- Topic/Channel;
- Quelle und Typ;
- Schema;
- Timestamp und Correlation-ID;
- Delivery-Semantik;
- Deduplizierung;
- Retry und Dead Letter;
- Subscription und Filter;
- Identität und Berechtigungen;
- Größenlimit;
- Redaction.

Kläre die Darstellung über MCP und REST, ohne bestehende synchrone Aufrufe zu brechen.

## Phase 12 – AsyncAPI — OFFEN

AsyncAPI passt zu:

- Commands als Tools;
- Events als Trigger oder Eventquellen;
- Requests/Responses als Tasks;
- Channels als kontrollierte Topics;
- empfangene Nachrichten als Events oder Resources.

Implementiere nicht sofort alle Broker.

Beginne mit:

- AsyncAPI-Dokumentimport;
- Analyse von Operations und Channels;
- Klassifikation von Commands und Events;
- internem Mapping;
- einem Referenztransport als Spike.

Behandle:

- at-most-once und at-least-once;
- Duplikate;
- Backpressure;
- Retention;
- Consumer Groups;
- Offsets;
- Dead Letters;
- Reconnect;
- Message-Größen;
- Schema Registry;
- untrusted Payloads;
- Auditvolumen;
- Kosten dauerhafter Subscriptions.

Offizielle Spezifikation:

- https://www.asyncapi.com/docs/reference/specification/v3.0.0

## Phase 13 – A2A — OFFEN

A2A erweitert den Scope von einem Tool Gateway zu einer Agent and Tool Control Plane.

Ein externer Agent ist kein gewöhnlicher synchroner RPC-Endpunkt.

Modelliere:

- Agent-/Capability-Discovery;
- delegierbare Capabilities;
- Task-ID;
- `working`, `input-required`, `completed`, `failed`;
- Folge-Nachrichten;
- Artifacts;
- Cancellation;
- Updates und Streaming;
- Authentifizierung;
- Policy-Prüfung;
- Budget-, Zeit- und Rekursionlimits;
- Loop-Erkennung;
- Audit über die Delegationskette;
- Identitätsdelegation;
- Schutz vor Confused-Deputy-Angriffen.

A2A erst implementieren, wenn:

- das Task-Modell stabil ist;
- Cancellation und Persistenz funktionieren;
- Identitätsdelegation geklärt ist;
- Agent-Loops erkannt werden;
- Artifacts unterstützt werden.

A2A-Semantik muss oberhalb seiner JSON-RPC-, REST- oder gRPC-Bindings liegen.

Offizielle Spezifikation:

- https://a2a-protocol.org/latest/specification

## Phase 14 – SOAP/WSDL — BEWERTET UND ZURÜCKGESTELLT

Nur bei validierter Nachfrage umsetzen.

Vorher lediglich Architekturverträglichkeit bewerten:

- XML Schema und Namespaces;
- SOAP-/WSDL-Varianten;
- document/literal;
- WS-Security;
- Attachments;
- herstellerspezifische Profile.

Keine halbe WS-Security-Implementierung. Vor Entwicklungsbeginn reale Zielsysteme und Testfixtures verlangen.

## Phase 15 – Audit, Versionierung und Enterprise-Fähigkeit — TEILWEISE

### Audit

- Best-effort- und Compliance-Modus explizit trennen.
- Compliance-Modus darf Ereignisse nicht still verlieren.
- Verhalten bei vollem Channel und Datenbankfehler definieren.
- Readiness, Metriken und Alarmierung ergänzen.
- Fehlgeschlagene Batches nicht kommentarlos verwerfen.

### Versionierung

- MCP ServerInfo, OpenTelemetry, Assembly/Package und Releaseversion aus einer Quelle ableiten.
- Die aktuellen Werte `0.4.0`, `0.5.0` und `1.1.0` dürfen nicht auseinanderlaufen.

### Enterprise-Roadmap

Sauber abgrenzen, nicht unkontrolliert in dieselbe Iteration ziehen:

- OIDC/SSO;
- Mandantenfähigkeit;
- HA/Cluster;
- verteilte Rate-Limits;
- gemeinsamer Approval-/Audit-Zustand;
- externe Secret Stores;
- Policy-as-Code.

## Verbindliche Priorisierung

1. Aktuelle Secret-Leaks und CLI-Prozessrisiken schließen.
2. Sichere Runtime:
   - WASI/Component Model;
   - Container-Isolation für native CLI-/stdio-Prozesse.
3. Stabiler externer Connector-Vertrag.
4. OpenRPC/JSON-RPC.
5. gRPC Unary.
6. GraphQL mit kuratierten Operationen.
7. Task-/Event-Modell.
8. AsyncAPI.
9. A2A.
10. SOAP/WSDL nur nach nachgewiesener Nachfrage.

Keine Phase überspringen, wenn die folgende Phase auf ihren Sicherheits- oder Datenmodellen aufbaut.

## Entscheidungsmatrix pro Connector

Bewerte jeden Connector vergleichbar:

- Zielmarkt;
- typische Anwender;
- reale Beispielsysteme;
- Discovery-Qualität;
- Schema-Qualität;
- Implementierungsaufwand;
- laufender Wartungsaufwand;
- Security-Risiko;
- Isolation;
- Auth-Komplexität;
- Streaming;
- Cancellation;
- Long-running Operations;
- Binärdaten;
- Events;
- Fehlersemantik;
- Testbarkeit;
- Abhängigkeit von optionaler Discovery;
- Produktnutzen;
- Differenzierung;
- Core, offizielles Plugin, Community-Plugin oder nicht umsetzen.

## Zentrale Governance-Regel

Unabhängig vom Protokoll muss jede Invocation denselben Kontrollpfad verwenden:

```text
Identity
→ RBAC
→ Eingabevalidierung
→ Risk Classification
→ Guardrails
→ Approval
→ Rate-/Concurrency-Limit
→ Connector Invocation
→ Output-Limit
→ Secret Redaction
→ Audit
→ Result / Task / Event
```

Kein Connector erhält einen Bypass.

## Erwartete Architekturartefakte

Vor einer breiten Protokollimplementierung:

1. ADR: Protocol-neutral Capability Model
2. ADR: Connector/Plugin Contract
3. ADR: WASI Component Runtime
4. ADR: Native Process and Container Isolation
5. ADR: Long-running Task and Event Model
6. Entscheidungsmatrix der Protokolle
7. kleine technische Spikes für:
   - WIT/WASI Component Discovery;
   - OpenRPC-Import;
   - gRPC Unary via Reflection oder Descriptor Set.
8. Core-/Official-Plugin-/Community-Entscheidung
9. Sicherheitsanalyse pro Runtime und Connector
10. realistische, einzeln releasefähige Roadmap

## Vorgeschlagene Meilensteine

- **M1:** CLI-Prototyp absichern und Secret-Leaks schließen.
- **M2:** Offizielle Gateway-CLI für Suche, Beschreibung, Invocation und Administration.
- **M3:** Protokollneutrales Capability-Modell und versionierter Connector-Vertrag.
- **M4:** WASI-Component-Spike mit WIT-Discovery.
- **M5:** Produktionsfähiger WASI-Pluginpfad und Connector-SDK.
- **M6:** OpenRPC als erster externer Protokolladapter.
- **M7:** gRPC Unary.
- **M8:** GraphQL mit kuratierten Operationen.
- **M9:** Persistentes Task-/Event-Modell und danach AsyncAPI.
- **M10:** A2A nach stabiler Delegationssemantik.
- **SOAP/WSDL:** kein fester Meilenstein ohne Interessenten oder Kunden.

## Definition of Done

- Keine CLI-Secrets in API, UI, Logs, Fehlern, Tests oder Auditdaten.
- Das Output-Limit schützt den Speicher während des Lesens.
- Host-Environment wird nicht unkontrolliert vererbt.
- CLI-Aufrufe sind manifest- und policygebunden.
- Destruktive Kommandos sind standardmäßig approval-pflichtig.
- Eine offizielle Gateway-CLI kann MCP-, OpenAPI- und CLI-Tools einheitlich bedienen.
- Der Core kennt keine unnötigen protokollspezifischen Sonderfälle.
- Drittanbieter-Connectoren können versioniert und isoliert betrieben werden.
- Connectoren deklarieren ihre benötigten Capabilities.
- WASI-Module erhalten standardmäßig weder Netzwerk, Dateisystem noch Secrets.
- Native Prozesse haben einen dokumentierten Isolationspfad.
- Kein Connector umgeht RBAC, Approval, Guardrails oder Audit.
- AsyncAPI wird nicht vor dem Task-/Event-Modell implementiert.
- A2A wird nicht als einfacher synchroner Tool-Call modelliert.
- GraphQL erzeugt keinen unkontrollierten Schema-Bloat.
- gRPC v1 beschränkt sich auf Unary-RPCs.
- SOAP/WSDL bleibt nachfragegetrieben.
- Versionsangaben stammen aus einer Quelle.
- Bestehende Tests bleiben grün und relevante Security-/Integrationstests kommen hinzu.
- Dokumentation nennt den CLI-Connector erst produktionsgehärtet, wenn diese Kriterien erfüllt sind.
- Keine Behauptung „jede Werkzeug-Art sicher unterstützt“, solange native Prozesse ohne belastbare Isolation laufen.

## Erwartetes Vorgehen ab jetzt

1. Liefere Befund, Dateiplan, Risiken, Testplan und Commit-Aufteilung.
2. Beginne anschließend mit Phase 1.
3. Härte danach den CLI-Connector aus Phase 2.
4. Setze die Gateway-CLI als getrennte, öffentliche Clientoberfläche um.
5. Erstelle die Architektur-ADRs und begrenzten Spikes.
6. Implementiere nicht sämtliche Protokolle in einer einzigen Iteration.
7. Halte die Roadmap für einen Soloselbstständigen langfristig wartbar.
