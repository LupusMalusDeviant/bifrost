# Produktreife — Fortschrittsprotokoll

Arbeitsstand des Programms aus Lastenheft 0002, Pflichtenheft 0004 und dem Opus-Runbook. Die drei
Planungsdokumente selbst liegen unter `docs/produktreife/` und sind **bewusst nicht versioniert**:
Sie kommen von außen und werden dort gepflegt.

**Regel für diese Datei:** Ein Paket steht erst auf `done`, wenn sein Ergebnis implementiert,
getestet, dokumentiert, integriert und durch einen **reproduzierbaren Nachweis** belegt ist. Eine
Agentenmeldung, ein grüner Unit-Test oder ein kompilierender Branch ist keine Abnahme.

## Aktueller Meilenstein: M0 — Baseline

| WP | Status | Nachweis | Blocker | Nächster Schritt |
|---|---|---|---|---|
| WP0.1 CI grün | `done` | `./build.sh verify-rust` grün (fmt, clippy `-D warnings`, `cargo test`) | – | – |
| WP0.2 Release-/Doku-Konsistenz | `done` | README, SECURITY.md, `CHANGELOG.md` gegen Ist-Stand abgeglichen | – | Release-Kanal-Frage (siehe unten) |
| WP0.3 Lokaler Verifier | `done` | `build.ps1` / `build.sh` mit `verify-fast`, `verify-dotnet`, `verify-rust`, `verify-container`; `verify-fast` und `verify-rust` lokal ausgeführt | – | CI ruft die Skripte noch nicht auf |
| WP0.4 Prozesslebenszyklus | `done` | `ProcessLifecycleTests` (2 Tests) plus Handprobe; Lücke in `HttpConnectorIntegrationTests` geschlossen | – | – |

## Befunde aus M0

### WP0.1 — der Formatfehler war Folgeschaden der Umbenennung

`cargo fmt --check` war rot, weil die Kiste von `mcpmcp_wasi_component_spike` auf
`bifrost_wasi_component_spike` umbenannt wurde: Der Import sortiert seitdem vor `ed25519_dalek`.
Kein inhaltlicher Fehler, aber ein Beleg dafür, dass eine repo-weite Textersetzung Gates verschiebt,
die niemand dabei im Blick hat.

### WP0.2 — drei Aussagen widersprachen dem Ist-Stand

| Aussage | Vorher | Tatsächlich |
|---|---|---|
| Image-Größe im README | `< 300 MB` | **315 MB** gemessen (v0.11.0), CI-Gate steht auf `< 350 MB` |
| Release-Status im README | „Pre-release v0.11.0" | v0.11.0 ist als **Release** markiert (siehe offene Frage) |
| `SECURITY.md` | kennt `v0.11.0` nicht | Tabelle ergänzt |

`CHANGELOG.md` gab es nicht; sie ist jetzt angelegt und beginnt bewusst bei `0.11.0` statt
rückwirkend erfunden zu werden.

### WP0.4 — genau ein Startpfad lag außerhalb der Prozess-Hygiene

Alle Testserver werden über den **Produktpfad** gestartet (`StdioTransportOptions` →
`StdioUpstreamConnector`), der unter Windows ein Job-Objekt mit `KILL_ON_JOB_CLOSE` herstellt —
stirbt der Testhost, räumt das Betriebssystem die Kinder ab.

Eine Ausnahme: `HttpConnectorIntegrationTests.StartHttpServer` rief `Process.Start` direkt auf, ohne
`ProcessHygiene.EnsureInitialized()`. Ein hart abgebrochener Lauf ließ diesen Prozess stehen; er
hielt danach seinen Port **und seine eigene Programmdatei**, sodass der nächste Build mit „wird von
einem anderen Prozess verwendet" scheiterte — mit einer Ursache zwei Läufe früher. Behoben.

**Der Nachweis** ist der eigentliche DoD, nicht die Korrektur. Der normale Weg war längst geprüft
(`SupervisorIntegrationTests.Dispose_leaves_no_zombie_processes`); der interessante Fall ist der
Prozess, der gar nicht mehr zum Aufräumen kommt.

Dafür gibt es jetzt `ProcessLifecycleTests` mit einem eigenen Wirt (`tests/Bifrost.TestServers/OrphanHost`):
Er startet über den **Produktpfad** einen echten stdio-Upstream, meldet dessen Prozess-Id und wartet.
Der Test schießt **nur den Wirt** hart ab — bewusst ohne `entireProcessTree`, sonst prüfte er seine
eigene Aufräumarbeit statt der Hygiene, die das Produkt herstellt — und erwartet, dass das Kind
stirbt. Ein Test kann seinen eigenen Testhost nicht töten; deshalb der zweite Prozess.

Von Hand gegengeprüft: Wirt `13592` hart beendet, Kind `11376` war danach weg. Der Negativfall ist
ebenfalls belegt, allerdings unfreiwillig — ein `BulkServer` überlebte heute seinen Elternprozess
und blockierte den nächsten Build, weil er außerhalb des Job-Objekts gestartet worden war.

## Offene Entscheidung für den Product Owner

**Release-Kanal.** Das Lastenheft verlangt in FR-P007: *Pre-Releases werden als solche markiert;
`latest` wird erst für stabile Releases verwendet.* Am 2026-07-31 wurde `v0.11.0` auf Anweisung
ausdrücklich zum normalen Release befördert, weil GitHub sonst weiterhin `v0.5.0` als „Latest"
bewarb — die beiden Schalter sind bei GitHub nicht unabhängig (`Latest release cannot be draft or
prerelease`).

Damit steht die Anforderung gegen den Ist-Zustand. Zwei Wege:

1. **FR-P007 anpassen** — die `0.x`-Linie trägt die Aussage „nicht stabil" bereits in der
   Versionsnummer, und die Startseite soll den aktuellen Stand zeigen.
2. **Ist-Zustand anpassen** — `v0.11.0` wieder als Pre-Release markieren und in Kauf nehmen, dass
   GitHub bis zum ersten stabilen Release einen veralteten Stand als „Latest" führt.

Bis zur Entscheidung beschreibt die Dokumentation den **Ist-Zustand**, nicht die Zielregel.

## Laufender Meilenstein: M1 — Distribution

Vertrag eingefroren in [`m1-distribution-contract.md`](m1-distribution-contract.md). Vier Pakete
laufen parallel mit disjunkten Dateizonen; `release.yml` hat genau einen Schreiber.

| WP | Status | Nachweis |
|---|---|---|
| WP1.1 Multi-Arch-Image | `done` | `actionlint` sauber, amd64-Build und Smoke lokal grün, kein `latest`, alle Actions SHA-gepinnt |
| WP1.2 SBOM/Provenance/Signatur | `done` | Syft/Trivy/Cosign containerisiert ausgeführt, Action-SHAs stichprobenartig gegen die API geprüft |
| WP1.3 Native CLI-Artefakte | `done` | 12 CLI-Tests grün, `--version` trägt den echten HEAD-Commit |
| WP1.4 Compose-/Installationspfade | `done` | drei `docker compose config`-Läufe grün, Volume-Name nachgeprüft |
| WP1.5 Release-Automation | `done` | sechs Jobs zusammengeführt, `actionlint` auf `release.yml` ohne Befund, 783 Tests grün |

**M1 ist am 2026-08-01 abgenommen.** `v0.12.0` ist veröffentlicht: Image unter
`ghcr.io/lupusmalusdeviant/bifrost:0.12.0`, 13 Release-Anhänge (fünf CLI-Archive, sechs SBOMs,
Prüfsummen, Signatur-Bündel), keyless signiert, sechs Attestationen, Trivy-Gates auf Image und
CLI-Artefakten grün. Der Signatur-Selbsttest lief in **beide** Richtungen — der Negativtest verlangt
ausdrücklich, dass Falsches durchfällt; ein Verifikationsschritt, der nur bestätigt, bestätigt auch
Unsinn.

### Was der erste Lauf gekostet hat: neun Befunde

Drei Trockenläufe und drei Tag-Läufe. Keiner dieser Punkte wäre ohne echten Lauf sichtbar geworden.

| # | Wo | Art |
|---|---|---|
| 1 | Push-Protection | erfundener Slack-Token im Negativkorpus, nicht aufgeteilt — Push blockiert |
| 2 | Secret-Gate | Baseline kannte zwei Werte aus WP3.3 nicht |
| 3 | Versionstest | Literal `"0.11.0"`, brach beim Versionssprung |
| 4 | **Bootstrap** | **Produktfehler:** bei zwei gleichzeitigen Einlösungen verloren beide |
| 5 | **WP0.4-Nachweis** | funktionierte unter Linux **noch nie** — 15-Zeichen-Grenze von `/proc/*/comm` |
| 6 | WP0.4-Nachweis, zweiter Anlauf | Namenszählung auch danach unzuverlässig |
| 7 | `supply-chain` | im Trockenlauf **immer** rot — der Trockenlaufmodus war selbst nie gelaufen |
| 8 | `release` | `dist/*` erfasste Unterverzeichnisse; Release angelegt, Anhänge fehlten |
| 9 | Backup-Test | Nebenläufigkeitsnachweis hing am Planer, nicht am Produkt |

Der schärfste ist **Nummer 5**. Ausgerechnet das Paket, dessen ganzer Sinn „Nachweis statt
Behauptung" war, hat auf einer ganzen Plattform nichts geprüft und trotzdem grün gemeldet. Er galt
als belegt, weil er nur unter Windows gelaufen war — seit seiner Entstehung war nichts gepusht
worden.

**Nummer 8** ist die unangenehmste Sorte: Das Release wurde angelegt, die Anhänge fehlten — genau
der Zustand, den der Kommentar über diesem Job vermeiden will („eine Zusage ohne Deckung"). Beim
Beheben sind zwei Torposten dazugekommen, die es vorher nicht gab: leere Dateiliste bricht ab,
doppelte Basisnamen brechen ab (`gh` hängt unter dem Basisnamen an — zwei gleichnamige Dateien
überschrieben einander lautlos).

**Der Satz „implementiert, aber nicht abgenommen" stand seit drei Meilensteinen im Protokoll und
klang nach Formalie. Er war keine.**

### Der ursprüngliche Vermerk (Stand bis 2026-07-31)

Alle fünf Pakete sind implementiert und lokal geprüft, aber **kein
einziger Releaselauf hat stattgefunden**. Was erst der erste echte Tag zeigt: arm64-Build unter
QEMU, GHCR-Login und Push, die Form von `steps.push.outputs.digest`, Attestation und Signatur mit
den gesetzten Berechtigungen, und ob die fünf CLI-Runner-Labels für dieses Repository verfügbar
sind. Nach der Abschlussregel des Pflichtenhefts (Kapitel 20, Punkt 6) muss der Nachweis **auf dem
gebauten Releaseartefakt** erfolgen — M1 ist damit implementiert, aber nicht abgenommen.

### WP1.4 — Prüfergebnis des Lead

Nachgeprüft und bestätigt: alle Compose-Kombinationen gültig, Volume löst auf
`mcpmcp_bifrost-data` auf, kein `latest`-Tag, Image zeigt auf die Registry aus dem Vertrag.

**Der wichtigste Fund betrifft eigene frühere Arbeit.** Der Umbenennungs-Commit `c7cb446` hat in
`docker-compose.yml` auch das Volume mitgezogen (`mcpmcp-data` → `bifrost-data`). Beim Deploy auf
Badwolf war das aufgefallen und dort umgangen — die Datei im Repository blieb aber die Falle, und
niemand hätte es gemerkt: Eine Bestandsinstallation bekäme ein leeres Volume und meldete sich
fehlerfrei als „bereit". Der Umstiegsweg steht jetzt in `docs/operations.md`.

Zwei Punkte gehen als Entscheidung an den Lead bzw. weiter:

1. **Der Standardweg ist bis zum ersten Release-Lauf kaputt.** `docker compose up -d` zieht ein
   Image, das es noch nicht gibt — vorher wurde lokal gebaut. Das ist die vom Pflichtenheft
   verlangte Richtung (WP1.4), aber die Lücke muss WP1.5 schließen: Der Vorgabewert der Version in
   `docker-compose.yml` und `.env.example` gehört an den ersten veröffentlichten Tag gekoppelt.
2. ~~**Das Key-Ring-Passwort lässt sich nicht als Datei-Secret zuführen**~~ — **erledigt in WP3.3.**
   Der Befund stand hier seit M1: `Program.cs` las nur die Umgebungsvariable, das PFX ging als
   Compose-Secret, das Passwort landete in `.env` und damit in `docker inspect`. FR-P048 war
   dadurch nur halb erfüllt.

   Gelöst über `…_FILE` als **allgemeine Regel**, nicht als Einzelfall. Gegen `AddKeyPerFile`
   sprach, dass es ein ganzes Verzeichnis als Konfigurationsquelle einhängt — eine zweite, anders
   geformte Oberfläche neben dem dokumentierten `BIFROST_*`-Vertrag, die alles liest, was jemand
   hineinlegt. Sind Wert und `…_FILE` beide gesetzt, bricht der Start ab: Eine Rangfolge zwischen
   zwei Quellen desselben Secrets wäre eine Regel, die man im Zweifel falsch erinnert.
   `docker-compose.yml` führt den zweiten Secret-Block jetzt.

## Laufender Meilenstein: M4 — Onboarding und Bedienbarkeit

Vertrag in `src/Bifrost.Abstractions/Importing.cs`. Die Einfrierung der ersten Welle ist
**aufgehoben**; Änderungen werden im Vertrag selbst begründet.

| WP | Status |
|---|---|
| 4.1 Providerneutrales Importmodell | `implementiert` |
| 4.2 Parser (Claude, Cursor, VS Code, Codex) | `implementiert` |
| 4.3 Setup- und Import-API | `implementiert` |
| 4.5 Basic/Advanced-Informationsarchitektur | `implementiert` |
| 4.6 Einheitliche Upstream-Diagnose | `implementiert` |
| 4.7 Dokumentation, i18n, Contributor-Basis | `implementiert` |
| 4.4 Geführter Setup-Wizard | `offen` — setzt auf 4.3 auf |

### Vier Befunde aus der Abnahme von 4.1–4.3 — behoben

Ein Audit gegen den Code (nicht gegen die Häkchen) hat vier Sachen gefunden. Alle vier sind
behoben; die ersten beiden verlangten eine Vertragsänderung in `Importing.cs`.

**1. Teilimport.** `ImportPlan.CanApply` galt planweit: Ein einziger kaputter Eintrag machte eine
Datei mit dreißig Servern unanwendbar. Für den geführten Erstaufbau (WP4.4) war das die
Einschränkung, an der die Sache scheitert — wer dreißig Server mitbringt, hat mit ziemlicher
Sicherheit einen darunter, der nicht mehr stimmt.

`ImportFinding` trägt jetzt einen `Scope` (`Document` oder `Entry`), **Vorgabe `Document`**: Ein
Befund gilt für alles, bis jemand ausdrücklich hinschreibt, dass er nur eine Stelle betrifft. So
herum blockiert ein vergessener Bereich zu viel statt zu wenig — die Verharmlosung eines planweiten
Fehlers zu einem Einzelbefund wäre der teure Irrtum. `ImportPlan.IsApplicable(kandidat)` fasst die
drei Bedingungen zusammen; `CanApply` heißt jetzt „etwas geht", nicht mehr „alles geht".

Bestätigungen gelten seitdem der **Auswahl** (`ConfirmationsFor`): Wer drei von dreißig Servern
übernimmt, bestätigt die Risiken dieser drei und die planweiten. Eine Bestätigung, die pauschal für
alles gilt, wird zur Formalie.

**Die Stelle, an der der Teilimport aufhört:** Wer einen gesperrten Server ausdrücklich in `servers`
(beziehungsweise `--only`) benennt, bekommt eine Absage statt eines stillen Auslassens. Ohne
Auswahl werden die anwendbaren übernommen und die übrigen in der Antwort unter `skipped` genannt —
ein Teilimport, der die Differenz verschweigt, sieht aus wie ein vollständiger.

**2. Der Ort in den zentralen Befunden war falsch.** Die zentrale Nachbearbeitung setzte den Pfad
aller von ihr erzeugten Befunde fest auf `mcpServers/<name>` — und das sind die meisten Befunde
überhaupt: Risiko, Zugangsdaten und Normalisierung entstehen alle dort. Bei Claudes
`projects`-Karte, bei VS Code (`servers/…`, `mcp/servers/…`) und bei Codex (`mcp_servers/…`) zeigte
das auf eine Stelle, die es in der Quelldatei nicht gibt. Ein Ort, der nicht stimmt, ist schlechter
als keiner: Er schickt jemanden an die falsche Zeile, und wer dort nichts findet, glaubt eher, den
Befund missverstanden zu haben. `ImportCandidate.SourcePath` kommt jetzt vom Parser;
`ImportFindingLocationTests` prüft das über alle Beispielkonfigurationen.

**3. TOML: die Entscheidung ist, es draußen zu lassen.** Zur Wahl stand, die Parser *vor* der
JSON-Prüfung nach Zuständigkeit zu fragen. Das hätte an der Sache nichts geändert — ohne TOML-Leser
beansprucht kein Parser eine `config.toml`, und ein TOML-Leser ist weder vorhanden noch ohne
Rückfrage zu ziehen. Gekostet hätte es die Schärfe der Meldung „kaputtes JSON". Stattdessen ist die
**Absage** deutlicher: `BFR-IMP-0006` sagt „das ist TOML, dieser Weg liest JSON, so schreibst du es
um" statt „Syntaxfehler in Zeile 1". Wer Codex wirklich unterstützen will, braucht einen TOML-Leser
— das ist gemeldet, nicht umgangen.

**4. Falschpositiv in der Maskenerkennung.** `LooksMasked` erkannte eine Verweisform nur als
*ganzen* Wert; `"Authorization": "Bearer ${env:TOKEN}"` galt deshalb als Klartextgeheimnis. Jetzt
zählt eine Verweisform auch mitten im Wert — aber nur, wenn nach ihrem Entfernen nichts Wertartiges
übrig bleibt. `Bearer ${env:TOKEN}` hinterlässt ein Schemawort und ist maskiert; `sk-abc${SUFFIX}`
hinterlässt ein halbes Geheimnis und bleibt ein Klartextfund.

### Entscheidung: Der Setup-Endpunkt bleibt auf Loopback beschränkt

WP4.3 hat `/setup/import/preview` auf Loopback begrenzt und dazu angemerkt, dass ein Betreiber, der
die Einrichtung vom Laptop aus öffnet, darüber nicht durchkommt.

**Entschieden: Die Beschränkung bleibt.** Der Endpunkt ist der einzige, der ohne Anmeldung
erreichbar ist — er hängt allein am Erstzugangs-Token, und dieses Token liegt in einer Datei auf dem
Server. Ihn übers Netz zu öffnen hieße, den Erstzugang zu einem Ratespiel für jeden zu machen, der
den Port erreicht. Loopback ist hier kein Komfortverlust, sondern die zweite Hälfte der Absicherung.

Die Setup-Oberfläche braucht ihn nicht: Sie ist Blazor Interactive Server und läuft **im**
Serverprozess. Sie ruft `IConfigurationImporter` und die Vorschauprojektion direkt auf, ohne den
Umweg über HTTP. Der HTTP-Weg bleibt für lokale Werkzeuge — genau dafür ist er gebaut.

Das ist eine Auflage für WP4.4, keine Einschränkung: Wer den Wizard über HTTP gegen den
Setup-Endpunkt baut, hat ihn falsch gebaut.

### Drei Wächter für dieselbe Regel — und drei Pakete, die je einen übersahen

Der Architekturtest aus ADR-0025 E4 („jeder Weg, der eine `UpstreamServerConfig` baut, ist
eingeordnet") existiert **dreifach**, weil er drei Assemblies prüft:

| Test | Assembly |
|---|---|
| `HostExecutionArchitectureTests` | `Bifrost.Core.Tests` |
| `HostExecutionPolicyContractTests` | `Bifrost.Security.Tests` |
| `HostExecutionServerArchitectureTests` | `Bifrost.Integration.Tests` |

WP4.1 prüfte nur Core und riss den in Security. WP4.3 prüfte Core und Security — und riss den in
Integration. Beide Male hat der Wächter getan, was er soll; beide Male fiel es erst im Gesamtlauf
auf.

**Der Fehler liegt in den Aufgabenkarten, nicht bei den Paketen.** Sie zählten Testprojekte auf,
und eine Aufzählung ist genau das, was diese Tests selbst vermeiden. Ab sofort lautet die
Pflichtprüfung in jeder Karte `./build.sh verify-dotnet` — der Gesamtlauf — statt einer Liste von
Projekten. Der Preis sind sieben Minuten je Paket; der Preis der Liste war zweimal ein roter
Gesamtlauf und eine Nachbesserung durch den Lead.

## Abgeschlossener Meilenstein: M3 — Sichere Vorgaben

**Alle sechs Pakete implementiert**, 1271 Tests grün. Die drei Rückstände sind abgeräumt:

| Rückstand | Erledigt |
|---|---|
| wasmtime 47.0.2 mit zwei RUSTSEC-Meldungen | auf 47.0.3; `cargo audit` meldet nichts mehr, 110 Rust-Tests und die zwei Real-Host-Tests grün |
| `.trivyignore.yaml` fehlte, `release.yml` verwies darauf | angelegt und **bewusst leer** — Ausnahmen laufen über das befristete Register, nicht über eine Ignorierliste, die niemand liest |
| CODEOWNERS fehlte vollständig | angelegt, kurz gehalten: Gates, Release-Pipeline, `CryptographicNames.cs`, ADRs |

**Einschränkung zu CODEOWNERS, die nicht untergehen darf:** Die Datei allein erzwingt nichts. In
den Branch-Protection-Regeln für `main` muss „Require review from Code Owners" eingeschaltet sein,
sonst ist der Eintrag ein Vorschlag. Das ist eine Repository-Einstellung und lässt sich nicht
mitliefern; sie steht als Hinweis im Kopf der Datei. Bis dahin bleibt die PO-Freigabepflicht
dokumentiert und **nicht durchgesetzt**.

Zum wasmtime-Sprung: Beide Meldungen waren LOW und hätten kein Gate blockiert. Betroffen war aber
ausgerechnet die WASI-Sandbox selbst — eine Schwachstelle in genau der Komponente, deren Aufgabe die
Isolation ist, verdient keine Einstufung nach Punktzahl. Die Real-Host-Tests wurden gesondert
gefahren, weil `verify-dotnet` sie ausschließt und ein veralteter Release-Stand dort hängt statt
fehlzuschlagen.

### Verlauf

Entscheidung vorab in [ADR-0025](../adr/0025-host-ausfuehrung-verbieten-und-bestehende-instanzen-migrieren.md),
Vertrag eingefroren in [`m3-secure-defaults-contract.md`](m3-secure-defaults-contract.md).

### WP3.6 hat zwei SSRF-Lücken gefunden — beide nachgeprüft, beide echt

Das Paket sollte Regressionstests für bekannte Fehlerklassen bauen. Es hat dabei zwei **neue**
Lücken gefunden und liefert sie als **rote** Tests ab, statt sie grün zu reden. Das ist genau das
gewünschte Verhalten.

**F1 — MCP-über-HTTP ist der einzige Transport ganz ohne Zielprüfung.**
`HttpTransportOptions` (`src/Bifrost.Abstractions/Upstream.cs:66`) trägt **kein**
`AllowPrivateTargets`; `OpenApiTransportOptions`, `OpenRpcTransportOptions` und
`UpstreamOAuthOptions` tragen es. `StreamableHttpUpstreamConnector` reicht `options.Endpoint`
direkt in den Transport — nachgeprüft: Die einzige Zielprüfung in der Datei betrifft den
**OAuth-Issuer**, nicht den Endpunkt des Upstreams. Ein Administrator, der
`http://169.254.169.254/…` oder einen Verwaltungsport auf `127.0.0.1` einträgt, bekommt genau den
Abruf, den `RemoteSpecFetcher` für OpenAPI und OpenRPC verhindert.

Strukturelle Ursache: `RemoteSpecFetcher` ist `internal` zu `Bifrost.Upstream` — `Bifrost.Core` und
`Bifrost.Server` können ihn gar nicht wiederverwenden. Eine zentrale Prüfung, die man nicht
erreichen kann, ist keine zentrale Prüfung.

**F2 — Der OAuth-Connect-Endpunkt probt, bevor er prüft.**
`src/Bifrost.Server/UpstreamOAuthEndpoints.cs:161` ruft `probe.GetAsync(endpoint, ct)` gegen die
vom Betreiber genannte Adresse. **Drei Zeilen später** wird `oauth.AllowPrivateTargets` an jede
Discovery-Anfrage weitergereicht. Der Code kennt den Schalter also und benutzt ihn unmittelbar
danach — der Probe-Aufruf davor wurde schlicht übersehen.

**Beide behoben.** F2 war eine übersehene Zeile: `OAuthDiscovery.EnsureTargetAllowedAsync` läuft
jetzt vor der Sonde, mit demselben Schalter, den die Zeile darunter ohnehin weiterreicht. Der Weg
dorthin ist der eigentliche Fix — `RemoteSpecFetcher` bleibt `internal`, bekommt aber einen
öffentlichen Zugang über `OAuthDiscovery`, damit `Bifrost.Server` die zentrale Prüfung überhaupt
erreichen kann.

F1 verlangte eine Entscheidung, weil dort Verhalten kippt. `HttpTransportOptions` hat jetzt
`bool? AllowPrivateTargets`, und **`null` heißt „nicht entschieden", nicht „verboten"**:

- Eine Bestandsinstanz hat den Schalter nie gesetzt. Ein MCP-Server im eigenen Netz ist bei diesem
  Produkt der Regelfall, nicht die Ausnahme — die produktive Instanz auf Badwolf spricht selbst
  einen Upstream unter `192.168.178.61` an.
- Sie beim nächsten Neustart abzuklemmen wäre genau die stille Verhaltensänderung, die
  [ADR-0025 E3](../adr/0025-host-ausfuehrung-verbieten-und-bestehende-instanzen-migrieren.md)
  für die Hostausführung ausdrücklich ablehnt. Dieselbe Frage, dieselbe Antwort.
- Ausdrückliches `false` weist private Ziele ab.

**Die Lücke ist damit nicht geschlossen, sondern verschoben**, und das gehört so gesagt: Solange die
Erzeugungswege (Formular, API, Paketimport) den Wert nicht **setzen**, bleibt er `null` und damit
erlaubt — auch bei neuen Konfigurationen. Der Schalter existiert, die Prüfung greift, aber die
Vorgabe für Neuanlagen fehlt. Das gehört zu WP3.2, wo die Erzeugungswege ohnehin angefasst werden.

### Stand M3

| WP | Status | Nachweis |
|---|---|---|
| 3.1 Host-Execution-Policy + Bestandsübernahme | `implementiert` | 607 Core-Tests, Architekturtest über Reflexion und IL |
| 3.2 Container als Standard | `implementiert` | ein Startmodell für stdio und CLI, echte Container-Tests, +50 Tests |
| 3.3 Key-Ring-Setup | `implementiert` | drei Betriebsmodi, Verlusterkennung mit zwei Zeugen, FR-P048 erledigt |
| 3.4 Bootstrap statt Log-Credentials | `implementiert` | Log-Mithörer belegt: kein Secret im Log; bestehende Admins bleiben |
| 3.5 Security- und Supply-Chain-Gates | `implementiert` | Negativnachweis je Gate außer Containerscan |
| 3.6 Sicherheitsinvarianten | `implementiert` | 62 Tests, zwei echte SSRF-Funde |

### Der Abbruch von WP3.2

Beide Agenten der zweiten Welle endeten an einem **Kontolimit**, nicht an einem Fehler im Code.
WP3.3 kam nicht über die Pflichtlektüre hinaus. WP3.2 war mitten in der Umbenennung — und ein
halb vollzogener Rename ist der ungünstigste Abbruchzeitpunkt, den dieses Paket hat: Der Vertrag in
`Bifrost.Abstractions` kannte nur noch `IsolationOptions`, drei Aufrufstellen in `Bifrost.Upstream`
und `Bifrost.Core` noch `CliIsolationOptions`. **Die Lösung baute nicht.**

Die Teilarbeit liegt vollständig auf `wip/wp3.2-isolation` — inklusive eines neuen Ordners
`src/Bifrost.Upstream/Isolation/` mit vier Dateien, der neben dem alten
`Cli/ContainerLaunchPolicy.cs` steht. Welcher von beiden bleibt, ist die unerledigte Kernfrage des
Pakets: Zwei Launchmodelle wären genau die zwei Wahrheiten, die der Auftrag verhindern sollte.

`main` wurde auf `3ceb5e3` zurückgesetzt und baut. Nichts von der Teilarbeit ist verloren, und
nichts davon ist in einem Zustand, in dem man darauf aufbauen sollte, ohne den Rename zu Ende zu
führen.

**Nachtrag: neu geschnitten, nicht übernommen.** Der zweite Anlauf lief von `main` aus, mit der
Auflage, in jederzeit bauenden Schritten zu arbeiten — der halbe Rename war die Lehre aus dem
Abbruch, nicht nur sein Ergebnis. Vom Zweig übernommen wurden geprüfte Bausteine
(`ImageReference`, `ContainerIdentity`, `ContainerMountPolicy`); vier Dinge wurden dabei korrigiert,
darunter ein Pfad in beiden Mount-Listen, der zwei `--volume` auf dasselbe Ziel erzeugt hätte
(von der Runtime abgelehnt), und ein fehlender Lebenszyklus — ohne ihn lief ein Kommando nach einer
Zeitüberschreitung im Container weiter, denn den Client zu töten reicht nicht.

Die Kernfrage ist entschieden: **ein** Startmodell. `Cli/ContainerLaunchPolicy.cs` ist gelöscht,
stdio und CLI gehen durch dieselbe Mindestpolicy und unterscheiden sich nur in der Lebensdauer
(`PerInvocation` gegen `Session`) und darin, ob stdin offen bleibt.

### WP3.4: Ein Widerspruch in der Aufgabenkarte, offengelegt statt aufgelöst

Die Karte verlangte in Auftrag 3 die Ausgabe „an eine interaktive CLI oder in eine restriktive
Bootstrapdatei" und verbot in der Stop-Bedingung „ein Token im Klartext in irgendeiner Ablage, auch
nicht kurz". Beides zusammen geht nicht. Der Agent hat den Widerspruch benannt, seine Lesart
begründet (dauerhafte Ablage trägt nur den Hash, die Übergabedatei ist kein Speicher) und die Stelle
markiert, die zurückzudrehen wäre — statt sich still für eine Seite zu entscheiden.

Bestätigt: Die Bootstrapdatei stammt aus dem Pflichtenheft, die Stop-Bedingung meinte die dauerhafte
Ablage. Das Restrisiko — eine Sicherung *innerhalb* der Frist trägt die Datei mit — steht in
`docs/operations.md`.

**Der Nachweis zum DoD ist der wertvollste Teil.** Er holt das *tatsächlich ausgestellte* Token aus
der Übergabedatei und prüft, dass weder es noch eines seiner Acht-Zeichen-Bruchstücke in irgendeinem
Logkanal auftaucht — nicht ein erfundenes Token gegen einen erfundenen Pfad. Davor steht der
Nachweis, dass überhaupt mitgeschrieben wurde und überhaupt ein Token existierte; ohne den wäre der
Vergleich auch dann grün, wenn nichts passiert ist.

**Nachtrag des Lead:** Der `README`-Abschnitt zum Erstzugang schickte Betreiber weiterhin ins Log
nach einem Passwort, das dort bewusst nicht mehr steht — die schlimmste Sorte veralteter Doku, weil
sie wie ein Fehler im Produkt aussieht. Nachgezogen.

### WP3.3: Der Verlust wird jetzt erkannt, statt überschrieben

Der Kern war nicht die Zertifikatsverwaltung, sondern ein Ausfallmodus, den dieses Projekt schon
einmal getroffen hat: Fehlt der Key-Ring, ist jeder gespeicherte Geheimtext unlesbar — **und der
Dienst startet trotzdem und meldet sich als bereit**. Beim v0.11.0-Umstieg hat dieselbe Kombination
an der Datenbank zugeschlagen.

Erkannt wird der Verlust über **zwei unabhängige Zeugen**, und das ist die eigentliche Arbeit:

1. `config/keyring.json` hält Anzahl und Ids der zuletzt gesehenen Schlüssel fest. Zeuge da,
   Verzeichnis leer → Verlust. Zeuge **unlesbar** → ebenfalls Verlust, nie „frische Instanz".
2. Geheimtext in der Datenbank. Der erste Zeuge liegt im selben Volume wie der Ring — verschwinden
   beide zusammen, trägt die Datenbank die Beweislast. Genau der Fall eines umbenannten Volumes.

Folge: Exit-Code 78, Critical-Log, Audit-Eintrag, **kein neuer Ring**. Ein vollständig
ausgetauschter Ring blockiert dagegen nicht — so sieht auch eine legitime Wiederherstellung aus —,
wird aber laut protokolliert. Die Prüfung sitzt bewusst *nach* den Recovery-Kommandos, damit
`--reset-ui-admin` erreichbar bleibt.

Eine ungeschriebene Annahme hat der Agent dabei selbst offengelegt: `config/keyring.json` reist
**nicht** im Backup mit, weil `BackupSections.Config` nur `instance.json` sichert. Für die
Verlusterkennung ist das richtig — ein zurückgespielter Zeuge ohne Ring wäre ein Falschbefund —,
aber ein künftiger Ausbau des Config-Bereichs könnte es still kippen.

### Der Blindfleck, den WP3.2 selbst gemeldet hat

`ServerDiagnosticService` zählte für die Runtime-Bereitschaft nur `Config.Cli?.Isolation`. Eine
Instanz mit ausschließlich stdio-Container-Upstreams hätte damit „keine Runtime nötig" gemeldet —
eine Diagnose, die genau den Fall verschweigt, für den sie da ist. Nachgeprüft und behoben; beide
nativen Transporte zählen jetzt.

### WP3.5 hat einen M1-Fehler gefunden, keinen M3-Fehler

`release.yml` rief die Trivy-Action mit `trivyignores: .trivyignore.yaml` auf — **diese Datei gibt
es nicht** (nachgeprüft im committeten Stand, Zeile 666). Die Action bricht bei fehlendem
Ignorefile ab. Der allererste Releaselauf wäre daran gescheitert, und zwar nicht an einem
Scanergebnis, sondern an einem Pfad; `supply-chain.md` §4.2 behauptet an der Stelle das Gegenteil.

Das ist der zweite Beleg für dasselbe Muster: M1 gilt als implementiert und nicht abgenommen, weil
kein Releaselauf stattgefunden hat — und jedes Mal, wenn jemand genau hinsieht, findet er etwas,
das erst ein echter Lauf gezeigt hätte.

Weitere Funde:

- **`dotnet list package --vulnerable` endet mit Exit 0**, auch bei High-Funden. Das naheliegende
  Ein-Zeilen-Gate wäre eines gewesen, das nie rot werden kann. Die Tabellenausgabe ist zudem
  lokalisiert (`Schweregrad` statt `Severity`) — ein `grep` hätte hier zufällig funktioniert.
- **wasmtime 47.0.2: RUSTSEC-2026-0222 und -0223.** Beide LOW, blocken also nicht — betroffen ist
  aber die WASI-Sandbox selbst. Empfehlung ≥ 47.0.3.
- **Der Nachweis für das Containerfilesystem-Gate fehlt** und wird ausdrücklich nicht als erfüllt
  gemeldet: Es gibt kein Release-Image, an dem sich zeigen ließe, dass das Gate scheitern kann.

`actionlint` unabhängig nachgeprüft: nur die zwei vorbestehenden Warnungen in `ci.yml`.

### Ein Zeitlimit, das die Maschine maß statt das Produkt

Der erste Gesamtlauf nach der Zusammenführung war rot: Zwei `RestFacadeTests` liefen nach je 17 s in
eine Zeitschranke, während sie auf einen stdio-Testserver warteten. Einzeln laufen dieselben Tests
12/12 grün in 10 s, der Wiederholungslauf war vollständig grün.

Ursache ist die gewachsene Last — die Suite hat mit `Bifrost.Security.Tests` und
`Bifrost.Upgrade.Tests` zwei Projekte bekommen, die selbst Prozesse und Container starten. Die
Schranke stand auf 15 s und maß damit die Auslastung der Maschine, nicht das Verhalten des Produkts.

Angehoben auf 30 s, und die Meldung nennt jetzt die tatsächlich verstrichene Zeit. Ein Zeitlimit
heraufzusetzen ist die schwächste aller Antworten; sie ist hier trotzdem richtig, weil ein Test, der
gelegentlich grundlos rot wird, mehr kostet als er sichert — man gewöhnt sich an rote Läufe. Die
Schranke bleibt scharf genug für den Fall, für den sie da ist: ein Upstream, der gar nicht hochkommt.

### Was WP3.6 sonst gebaut hat

Die Tests zählen nirgends die heute bekannten Fälle auf. Die Endpunktmatrix liest die Routen aus
der `EndpointDataSource` des laufenden Hosts und ist fail-closed — alles gilt als Management, außer
es steht ausdrücklich auf der offenen Liste. Der Auditvollständigkeitstest liest die Ausgänge aus
`InvocationStatus`; ein neuer Enum-Wert ohne Test macht ihn rot. Und die Verallgemeinerung der
OpenRPC-Lehre von heute: Statt über Transporte läuft der Korpustest über **jede** Eigenschaft im
Konfigurationsmodell, deren Name nach Geheimnis aussieht — ein neues `ClientSecret` an einem
bestehenden Transport wird gefunden, nicht nur ein neuer Transport.

Als Gegenprobe wurde der historische Fehler nachgestellt (`OpenRpcCredential` aus dem Korpus
entfernt) — der Test wird rot und nennt die Stelle.

## Abgeschlossener Meilenstein: M2 — Wiederherstellbarkeit

Entscheidung vorab in [ADR-0024](../adr/0024-backup-restore-und-migrationssicherheit.md), Vertrag
eingefroren in [`m2-recoverability-contract.md`](m2-recoverability-contract.md).

**Vermerk zur Reihenfolge.** Das Pflichtenheft macht M2 von einem **abgenommenen** M1 abhängig.
Abgenommen ist M1 nicht — dazu fehlt ein echter Releaselauf, den der Product Owner für diesen
Durchgang ausdrücklich ausgeschlossen hat („keinen Release taggen"). M2 läuft daher auf Anweisung
vor der Abnahme. Das ist keine stille Umgehung der Regel, sondern eine bewusst getroffene
Reihenfolgeentscheidung, die hier festgehalten wird, damit sie bei der Abnahme sichtbar bleibt.

| WP | Status | Nachweis |
|---|---|---|
| WP2.1 Backup-Format und -Erzeugung | `implementiert` | 26 Backup-Tests; SQLite vollständig, **PostgreSQL nicht** |
| WP2.2 Restore mit Vorprüfung | `implementiert` | Staging, Pre-Backup vor `Replace`, Zip-Slip/Symlink/Bombe abgewiesen |
| WP2.3 Migrationssicherheit | `implementiert` | 80/80 im Namensraum `Persistence`, SQLite **und** Postgres |
| WP2.4 Diagnosedienst (`doctor`) | `implementiert` | 17 Codes, 107 Tests; Sonden noch nicht verdrahtet |
| WP2.5 Konfigurationsexport/-import | `implementiert` | 20 Tests, Secret-Negativkorpus je 8-Zeichen-Bruchstück |
| WP2.6 Upgrade-Kompatibilitätsmatrix | `implementiert` | 43 Tests, 15 Migrationsstände × 2 Provider; **hat E6 widerlegt** |
| WP2.7 Adapter (CLI, API, UI) | `implementiert` | 7 Befehlsgruppen, API hinter Global-Grant, Sonden verdrahtet |

Gesamtlauf nach der Zusammenführung: `./build.sh verify-dotnet` → **948 Tests grün, 0 Fehler**
(M1-Stand: 783). Kein Paket hat eine fremde Zone angefasst, `Operations.cs` blieb während der
gesamten Welle unverändert.

### Der Fund, der keinem Paket gehörte

WP2.5 meldete ihn nebenbei, und er ist der schwerwiegendste Einzelbefund dieser Welle:
`UpstreamConfigRedactor` maskierte das Credential von `OpenRpcTransportOptions` **nicht**. Der
Redactor sitzt in `ApiEndpoints.cs` vor der Upstream-Liste — das Zugangsdatum eines OpenRPC-Servers
ging damit im Klartext an Oberfläche und API.

Beim Nachprüfen kam eine zweite Lücke derselben Familie dazu, in die Gegenrichtung:
`UpstreamConfigMerge.CarryOverSecrets` kannte weder WASI-Secrets noch das OpenRPC-Credential. Der
Redactor blendete sie aus, die Übernahme holte sie nicht zurück — wer einen solchen Upstream in der
Oberfläche bearbeitete und speicherte, schrieb die Maske `***` als echten Wert in die Datenbank.
Der Upstream lief bis zum nächsten Neustart weiter und scheiterte dann an einem Zugangsdatum, das
wörtlich aus drei Sternen bestand.

Beide behoben. Der eigentliche Fehler lag aber im Test: `Every_transport_secret_is_masked…` prüfte
genau **einen** Transport und trug die Zusicherung für alle im Namen. Der Ersatz führt einen
Negativkorpus über alle sechs Transporte und hat einen Wächter, der rot wird, sobald ein siebter
dazukommt — das Versäumnis war nicht, `OpenRpc` zu vergessen, sondern keine Stelle zu haben, an der
das Vergessen auffällt.

### Lead-Entscheidungen zu den Rückfragen

| Frage | Entscheidung |
|---|---|
| Journaltabelle per Roh-DDL statt EF-Migration (WP2.3) | **bestätigt** — genau das Verfahren, mit dem EF seine eigene Historientabelle anlegt; eine EF-Migration war ausdrücklich verboten, ein Sidecar würde bei PostgreSQL die falsche Antwort geben |
| Codebereiche `BFR-DB-0001…0099` (WP2.4) / `0100…0199` (WP2.3) | **bestätigt**, Überschneidungsfreiheit nachgeprüft: 26 Codes, jeder genau einmal vergeben |
| `Skipped` im Exit-Code (WP2.4) | **neutral, also `0`.** Ein übersprungener Check ist keine Warnung — er steht sichtbar mit Begründung im Bericht, und das ist die Aussage |
| Schreibprobe legt `.bifrost-doctor-*.tmp` an (WP2.4) | **zulässig.** Einen Ordner rein lesend auf Beschreibbarkeit zu prüfen, geht auf keinem der beiden Betriebssysteme verlässlich |
| Ids beim Import erhalten (WP2.5) | **bestätigt** — neu vergebene Ids ließen jeden mitgelieferten Grant ins Leere zeigen, und Default-Deny meldet das nicht, sondern erlaubt nur nichts |
| `.gitignore`-Negation für `Backup/` (WP2.1) | **bestätigt**, Wirkung nachgeprüft (`git check-ignore`, 41 Dateien sichtbar). Die VS-Vorlagenregel `Backup*/` hätte eine ganze Quellcodezone unsichtbar gemacht |

### Vertragslücke — gefunden, entschieden, behoben

Zwei Pakete sind unabhängig voneinander auf denselben Fehler in `Operations.cs` gestoßen: Weder
`RestorePlan` noch `ConfigurationImportPlan` trug einen Verweis auf seine Nutzlast. Beide behalfen
sich mit einer `ConditionalWeakTable` und wiesen einen fremden Plan ab, statt zu raten — die
richtige Notlösung, aber sie bindet Planung und Anwendung an **dieselbe Objektidentität**. Für die
CLI trägt das. Für die REST-API nicht: Dort geht der Plan als JSON hinaus und kommt als neues
Objekt zurück, ein Restore über die API wäre grundsätzlich nicht anwendbar gewesen.

Dass zwei Pakete ohne Kenntnis voneinander dieselbe Stelle melden, war die Aussage — ein Fehler,
keine Auslegungsfrage. Die Welle war beendet, die Einfrierung damit aufgehoben; beide Pläne tragen
jetzt ein Handle mit 30-Minuten-Geltung, einmaliger Verwendung und ohne Passphrase im Plan. Details
im [Vertrag](m2-recoverability-contract.md#nachtrag-nach-der-welle-der-plan-trägt-ein-handle).

Der Nachweis ist ein Test, der den Plan tatsächlich durch `JsonSerializer` schickt und danach
anwendet — vorher war das genau der Fall, der scheiterte.

### WP2.6 hat das Rückwärts-Tor aus E6 widerlegt

Der Upgrade-Harness sollte belegen, dass ein Archiv aus einer neueren Version abgelehnt wird. Er
belegt das Gegenteil, und der Test steht jetzt als Feld 8 der Matrix.

`RestoreService` vergleicht die **selbst behauptete** `minimumRestoreVersion` aus dem Manifest. Die
stammt aus `BackupOptions.MinimumRestoreVersion`, und das ist die Konstante
`BackupLayout.DefaultMinimumRestoreVersion = "0.11.0"` — sie wird von keiner Version angehoben.
Ein Archiv aus einer späteren Version trägt also dieselbe Angabe wie eines von heute und wird
**eingespielt**. Aufgehalten wird es erst danach vom `DatabaseInitializer` mit `BFR-DB-0102` — also
nachdem geschrieben wurde, was E6 gerade verhindern sollte.

Der Fehler ist nicht die Konstante, sondern das Kriterium: Eine Versionsangabe, die das Archiv über
sich selbst macht, kann nicht das Tor bewachen. Der Restore muss den **Migrationsstand** aus dem
Manifest gegen die Migrationen prüfen, die dieser Build kennt — kennt er sie nicht, ist das Archiv
neuer, ganz ohne Versionsbuchhaltung. Das ist vor WP2.6 niemandem aufgefallen, weil der Test dazu
fehlte; der zuständige Agent hat gegen die eigene Zusage getestet und sie widerlegt.

**Behoben.** Der Restore prüft jetzt den Migrationsstand aus dem Manifest gegen die Migrationen, die
dieser Build kennt (`KnownMigrations.For`, gelesen aus der Migrations-Assembly, **ohne**
Datenbankverbindung — der Regelfall des Restore ist eine leere Instanz, eine Prüfung mit
Verbindungsbedarf fiele genau dann aus, wenn sie gebraucht wird). Kennt der Build den Stand nicht,
stammt das Archiv aus einer neueren Version, ganz ohne Versionsbuchhaltung.

Fehlt die Menge der bekannten Migrationen, meldet das Tor eine **Warnung** statt zu schweigen: Ein
Schutz, der still ausfällt, ist schlimmer als keiner. Drei Tests belegen die drei Fälle —
abgelehnt, angewendet (dasselbe Archiv, nur bekannter Stand), und ungeprüft mit Ansage.

### Vier Bezeichner, an denen jeder gespeicherte Geheimtext hängt — ungetestet

Ebenfalls von WP2.6 gemeldet und nachgeprüft:

| Bezeichner | Ort |
|---|---|
| `McpMcp.UpstreamConfig.v1` | `EfUpstreamConfigStore.cs:18` |
| `McpMcp.UpstreamOAuthToken.v1` | `UpstreamOAuthTokenStore.cs:21` |
| `McpMcp.Webhook.Secret.v1` | `WebhookStore.cs:18` |
| `SetApplicationName("MCPMCP")` | `Program.cs:97` |

Alle vier gehen in die Schlüsselableitung ein; eine Umbenennung macht **jeden** gespeicherten
Geheimtext unlesbar, ohne Fehlermeldung beim Start. Gesichert sind sie heute ausschließlich durch
Kommentare — kein Test greift darauf zu.

Das ist genau die Regression, die der Umbenennungs-Commit `c7cb446` beinahe ausgelöst hätte, und
sie ist zugleich die einzige, die der Upgrade-Harness prinzipiell nicht fangen kann: Er benutzt
seinen eigenen Anwendungsnamen und würde eine repo-weite Umbenennung stillschweigend mitmachen.

**Behoben.** Alle vier stehen jetzt als Konstanten in `CryptographicNames`, und
`CryptographicNamesTests` prüft sie gegen ihre Werte. Ein Test, der eine Konstante gegen ihren
eigenen Wert prüft, sieht sinnlos aus — er ist hier die einzige Stelle im Repository, an der eine
Umbenennung *auffällt*. Wer ihn rot macht, hat zwei Möglichkeiten: die Änderung zurücknehmen oder
einen Migrationslauf schreiben, der alles entschlüsselt und neu verschlüsselt. Den Test anzupassen
ist keine dritte, und das steht so im Test.

### Offen aus dieser Welle

- **PostgreSQL-Backup fehlt.** Der Weg lehnt laut ab statt still Zeilen zu exportieren (ADR-0024 E2),
  aber FR-P020 ist für Postgres damit nicht erfüllt. **Das ist die größte offene Lücke aus M2**, und
  sie zieht eine zweite nach sich: Auf PostgreSQL läuft jedes Upgrade ohne Rückweg, weil E7 dort
  keine Sicherung erzeugen kann (`WhenAvailable` statt `Always` — `Always` wäre dort ein
  Startverbot, keine Zusage).
- **E7 ist für SQLite erfüllt**, für PostgreSQL und Datenbanken im Arbeitsspeicher nicht.
- **`IAssetStore` kann keinen einzelnen Skill entfernen** und `CreateAsync` erhält die exportierte
  `AssetId` nicht. `RemoveSkillAsync` wirft deshalb; der Rückstand landet sichtbar in der
  Rückstandsliste der Kompensation, statt als „vollständig zurückgenommen" gemeldet zu werden. Ein
  zur Hälfte angewendeter Skill-Import bleibt Handarbeit.
- **`GuardOptions` sind ein Start-Singleton aus der Umgebung.** Beim Import lässt sich nur der
  Freigabe-Vorgabeweg übernehmen; abweichende Guard-Schalter werden protokolliert, nicht gesetzt.
- **„Instanz-Id" heißt zwei verschiedene Dinge.** `GatewayIdentity.InstanceId` ist ein frischer GUID
  je Prozess (für den Federations-Loop-Header), die neue stabile Id steht in `config/instance.json`.
  Für sich genommen beides richtig, zusammen eine Falle.
- **Publisher-Trust fehlt im Konfigurationsexport.** Ohne ihn ist ein WASI-Upstream auf der
  Zielinstanz nicht ladbar — der inhaltlich dringendste Kandidat für Exportformat v2.
- **Alte Daten in alter Form sind nicht prüfbar.** Der Upgrade-Harness fährt echte alte Schemata,
  aber mit dem heutigen schreibenden Code. Eine Regression im *Serialisierungsformat* eines früheren
  Builds findet er prinzipiell nicht; das ist seine schwerwiegendste Lücke, und sie steht in
  `docs/upgrade-matrix.md`.
