# Plan 0004 — Umbenennung auf Faolan und Rückkanal-Projektion

**Stand:** 2026-11-16 · **Status:** ADRs und Verträge am 2026-11-16 vom Nutzer abgenommen; Umsetzung noch nicht begonnen · **Phase 1 von 3** (nur Bestandsaufnahme und Plan, kein Code geändert)
**Ausgangsstand:** `main` @ `4de594f`, Version 0.12.0

Marken: `[Claude-prüfbar]` erledige ich vollständig mit Tests. `[braucht Nutzer]` steht am Ende in der Prüfliste.

---

## 1. Befunde der Bestandsaufnahme

### 1.1 Der Name: Faolan

**Entschieden am 2026-11-16: Das Produkt heißt künftig Faolan** (irisch *faolán*, „kleiner Wolf"). CLI und Binary `faolan`, Namensraum und Paketpräfix `Faolan.*`, Variablenpräfix `FAOLAN_`.

Stand der Prüfung für Faolan:

| Wo | Ergebnis |
|---|---|
| npm, PyPI, crates.io | Paket `faolan` frei, npm-Suche ohne Treffer |
| nuget.org | `Faolan`, `Faolan.Cli`, `Faolan.Core`, `Faolan.Abstractions`, `Faolan.ConnectorKit` frei. Die Suche findet neun Pakete eines **Autors** namens Faolan (Godot- und Netzwerkbibliotheken), keine Paket-Id |
| GitHub | 93 Repos mit dem Namen, größtes 20 ★ (Spielserver-Emulator); nichts zu MCP, Agenten oder Gateways. Das Konto `github.com/faolan` gehört einem fremden Nutzer — das Repo bleibt unter `LupusMalusDeviant` |
| Docker Hub | Namensraum `faolan` gehört einem fremden Nutzer — das Image liegt ohnehin auf GHCR |
| Websuche | kein Produkt und keine Firma dieses Namens gefunden |
| **nicht geprüft** | Markenregister (DPMA, EUIPO), Domains |

Der ursprünglich gewünschte Name Heimdall wurde verworfen. Der Anlass der Umbenennung ist die Namensgleichheit mit `maximhq/bifrost`, und für Heimdall gilt dasselbe, nur mit kleineren Projekten:

| Projekt | Was es ist |
|---|---|
| `shinzo-labs/heimdall` (npm `@shinzolabs/heimdall`) | MCP-Server, der andere MCP-Server durchreicht und Werkzeuge je Client freigibt — dieselbe Produktkategorie |
| `enmanuelmag/heimdall-mcp` (npm `@cardor/heimdall-mcp`) | durchsichtiger MCP-Proxy mit OpenTelemetry-Traces |

Dazu kommt außerhalb von MCP das verbreitete Self-Hosting-Dashboard „Heimdall" (linuxserver.io).

Die folgenden Tabellen halten fest, was auf dem Weg zu Faolan geprüft und verworfen wurde.

#### Namensprüfung vom 2026-11-16

Geprüft: exakte Paket-Id auf npm, nuget.org, PyPI und crates.io, die NuGet-Suche, GitHub-Repositories nach Namen und nach „Name + mcp", dazu eine Websuche. **Nicht geprüft:** Markenregister, Domains, Docker Hub und GHCR.

| Kandidat | Register | GitHub | Urteil |
|---|---|---|---|
| **Himinbjorg** | alle vier frei | 7 Repos, alle 0 ★, keins zu MCP | frei |
| **Schlagbaum** | alle vier frei | 1 fremde Android-App | frei |
| **Durchreiche** | alle vier frei | 1 fremdes Bastelprojekt | frei |
| Hallinskidi | alle vier frei | nur ein Profil-Repo | frei, aber als Wort unbekannt |
| Gullintanni | alle vier frei | ein ruhender Merge-Bot (14 ★) | frei |
| Schleuse | alle vier frei | 0-★-Repo „selbst gehostetes API-Gateway" | nahe Nachbarschaft |
| Torhaus | alle vier frei | 0-★-Repo „secure host for untrusted AI agents" | nahe Nachbarschaft |
| Hlidskjalf | npm belegt | 32 kleine Repos | schwer zu schreiben |
| Gjallarbru | npm belegt | 8 kleine Repos | — |
| Bilrost | npm, PyPI, crates belegt | Rust-Kodierung (48 ★), OIDC-Operator | zu nah an Bifrost |
| Modgud | PyPI belegt, NuGet `Modgud.*` | **MCP-Auth-Proxy** `dbtlr/modgud`, Identity-Provider | vergeben |
| Ratatoskr | alle vier belegt | 233 Repos, darunter ein MCP-Proxy | vergeben |
| Gjallarhorn | NuGet-Id belegt | .NET-Bibliothek (120 ★) | vergeben |
| Hermod, Skirnir, Draupnir, Gleipnir, Vegvisir, Mimir, Sluice, Lockkeeper, Garm | überwiegend belegt | teils AI-Gateways oder MCP-Projekte | vergeben |
| Skald | alle vier belegt, NuGet-Id `Skald` | `skaldlabs/skald` (564 ★, „context layer platform", eigener MCP-Server) | vergeben, und im selben Themenfeld |
| **Thulr** | alle vier frei | 3 Kleinst-Repos, zwei davon KI-nah (2 ★, 0 ★) | frei |
| **Snorri** | alle vier frei | nichts Einschlägiges | frei |
| Heiti | alle vier frei | nichts Einschlägiges | frei, aber Suchtreffer führen zu chinesischen Schriften |
| Vardr, Lawspeaker | alle vier frei | nichts Einschlägiges | frei |
| Sigyn | npm belegt, NuGet frei | zwei IRC-Bots (41 ★, 17 ★) | für .NET brauchbar |
| Kenning | PyPI belegt | ML-Framework (150 ★), ein MCP-Wissensserver | vergeben |
| Galdr, Bragi, Lofn, Vakt, Huskarl, Skjold, Egil | teils belegt | u. a. ABAC-Bibliothek, OAuth-Crate, .NET-Autor mit dem Handle `egil` | vergeben oder irreführend |

Tiernamen (62 geprüft, gleiche Methode):

| Kandidat | Register | GitHub | Urteil |
|---|---|---|---|
| **Rorqual** (Furchenwal) | alle vier frei | 15 Kleinst-Repos, größtes 4 ★ (Musik-Client) | frei |
| **Sitta** (Kleiber) | npm, NuGet, crates frei; PyPI belegt | Bildverarbeitungs-Paper (61 ★), nichts zu MCP | für .NET frei |
| **Anhinga** (Schlangenhalsvogel) | alle vier frei | 52 Kleinst-Repos, größtes 4 ★ | frei |
| Phalarope, Linsang, Meles, Garrulus | alle vier frei | nichts Einschlägiges | frei |
| Culpeo, Lysmata | alle vier frei | je ein KI-nahes Kleinprojekt (LLM-Studio 3 ★, OpenClaw-Client 32 ★) | nahe Nachbarschaft |
| Gulo, Wrasse, Whimbrel, Lycaon, Corsac | nur npm belegt | nichts Einschlägiges | für .NET frei |
| Hoopoe | drei belegt | **MCP-Proxy, der Werkzeuge filtert** | vergeben |
| Pilotfish, Minke, Ratel, Numbat, Remora, Tenrec, Godwit | überwiegend belegt | KI-Werkzeuge mit 136 bis 1165 ★ | vergeben |
| Stoat, Shrike, Dipper, Oxpecker, Aardwolf, Ermine, Marmota, Quoll | NuGet-Id belegt | — | für .NET vergeben |
| übrige 30 | überwiegend belegt | — | vergeben |

Wolf, Technik, Fantasy und Torwächter (86 geprüft, gleiche Methode, Favoriten zusätzlich nach „Name + mcp / ai agent / gateway"):

| Kandidat | Register | GitHub und Web | Urteil |
|---|---|---|---|
| **Makami** (japanische Wolfsgottheit, Schutz vor Dieben und Feuer) | alle vier frei | nichts Einschlägiges | frei |
| **Faolan** (irisch „kleiner Wolf") | alle vier frei | nichts Einschlägiges | frei |
| **Isengrim** (der Wolf aus Reineke Fuchs) | alle vier frei | 1 Profil-Repo | frei; in der Fabel der Übertölpelte |
| Maugrim (Wolf und Torwächter in Narnia) | alle vier frei | 1 Profil-Repo | frei; Figur aus einem geschützten Werk |
| Carcharoth (Wolf am Tor von Angband) | alle vier frei | 4 Kleinst-Repos | frei; Tolkien-Name |
| Vilkas (litauisch „Wolf") | alle vier frei | finnische Softwarefirma Vilkas Group | Firmenname |
| Lupercal, Nighteyes, Rougarou | alle vier frei | Warhammer-Name bzw. Romanfigur bzw. KI-Server-OS (1 ★) | Vorbehalt |
| Bandpass | alle vier frei | „Context compiler for AI agents" (1 ★) | nahe Nachbarschaft |
| Hackle | alle vier frei | koreanische SaaS-Firma mit eigenem MCP-Server | vergeben |
| Noisegate, Winnow, Decant, Distillate, Cupel, Sallyport, Ostiary, Cardea, Homunculus | überwiegend belegt | jeweils ein Kontext-, Token- oder Agenten-Werkzeug | vergeben, selbe Nische |
| Kerf, Squelch, Pawl, Penstock, Escapement, Postern, Zener, Durin, Orthrus, Wyrm, Castellan, Seneschal und übrige | überwiegend belegt | — | vergeben |

### 1.2 Wo der Name heute steht

763 der 879 versionierten Dateien enthalten `bifrost` in irgendeiner Schreibweise, 53 noch `mcpmcp`. Es ist die **zweite** Umbenennung; die erste (`McpMcp` → `Bifrost`, Commit `c7cb446`, 2026-07-31) hat die Muster hinterlassen, an die dieser Plan anschließt: `LegacyEnvironment`, `CryptographicNames` samt Test, die Doctor-Prüfung `BFR-CFG-0002`.

**Klasse A — mechanisch, ohne Wirkung nach außen**

- 25 Projekte, `Bifrost.slnx`, Ordner `src/Bifrost.*` und `tests/Bifrost.*`
- Namensräume `Bifrost.*`, Typen `BifrostDbContext` (203 Stellen), `BifrostDbOptions` (54), `BifrostProductInfo` (23), `UseBifrostDatabase` (22)
- 66 Migrationsdateien je Anbieter (Namensraum und `[DbContext(typeof(...))]`; die Migrations-Ids selbst tragen den Namen nicht)
- Prosa in README, `docs/operations.md`, `docs/en/*`, `SECURITY.md`, `.github/*`

**Klasse B — der Betreiber sieht es, eine Brücke ist Pflicht**

- rund 60 Betriebsvariablen `BIFROST_*` (plus `_FILE`-Varianten), vier CLI-Variablen (`BIFROST_ENDPOINT`, `_TOKEN`, `_CONFIG`, `_IDENTITY`)
- Binärnamen `bifrost`, `bifrost-connector`, `bifrost-wasi-host`, Einstieg `Bifrost.Server.dll`, Release-Artefakte `bifrost-cli-*`
- Image `ghcr.io/lupusmalusdeviant/bifrost`, Compose-Dienst `bifrost`, **Volume `bifrost-data`**
- Datenbankdatei `bifrost.db` (und davor `mcpmcp.db`)
- Metriken `bifrost.tool_calls`, `bifrost.tool_call_duration`, Meter und ActivitySource `Bifrost.Gateway`, Span-Attribute `bifrost.*`
- MCP `serverInfo.name = "bifrost"`, Text der Server-Instructions

**Klasse C — steht auf dem Draht oder in abgelegten Daten; umbenennen heißt alte Daten unlesbar machen**

| Bezeichner | Fundstelle | Warum heikel |
|---|---|---|
| `bifrost.db`, `bifrost.dump` als Archiveinträge | `BackupLayout.cs:28,40`, `RestoreService.cs:653,678` | ältere Sicherungen müssen sich weiter zurückspielen lassen |
| `__BifrostMigrationJournal` | `MigrationJournal.cs:65` | Tabellenname in jeder bestehenden Datenbank |
| `bifrost.connector.v1` | `ConnectorPackages.cs:121` | steht im **signierten** Manifest ausgelieferter Pakete |
| `bifrost://assets/` | `Assets.cs:10` | Clients halten die URI |
| `${bifrost:secret/…}` | `ConfigurationSecretScrubber.cs:26` | steht in Konfigurationsexporten und Policy Packs |
| `X-Bifrost-Signature`, `X-Bifrost-Timestamp` | `WebhookSignature.cs:17` | fremde Absender signieren damit |
| `X-Bifrost-Instance`, `X-Bifrost-Setup-Token` | `Upstream.cs:557`, `ImportEndpoints.cs:44` | Vertrag mit Upstreams und Einrichtungsskripten |
| `_bifrost_truncated` | `Invocation.cs:75` | Agenten und Tests erkennen daran eine Kürzung |
| `MCPMCP`, `McpMcp.*.v1` | `CryptographicNames.cs` | Schlüsselableitung — **bleibt**, wie schon beim letzten Mal |
| `Bifrost.Server.ApprovalElicitation.RequestState.v1` | `ApprovalElicitation.cs:57` | DataProtection-Zweck, heute nicht von `CryptographicNamesTests` gedeckt |
| `CN=bifrost-keyring` | `KeyRingCertificates.cs:56` | Subjekt bestehender Zertifikate |
| Diagnosecodes `BFR-*` | `DiagnosticCodes.cs` | stehen in Doku, Support-Bundles und Suchmaschinen |
| WIT-Paket `mcpmcp` | vier `.wasm`-Fixtures | in Binärdateien einkompiliert |

**Klasse D — außerhalb des Repos**

GitHub-Repo `LupusMalusDeviant/bifrost`, GitLab `ai-tools/bifrost`, das GHCR-Paket, die Instanz auf Badwolf, der Connector-Name in den MCP-Clients.

**Klasse E — Geschichte, wird nicht umgeschrieben**

CHANGELOG-Einträge bis 0.12.0, ADR-0001 bis 0025, `docs/acceptance/*`, ältere Pläne, `gitleaks-baseline.json`. Dort war der Name damals richtig.

### 1.3 Der Rückweg von `tools/call` heute

```
Upstream
  └─ IUpstreamConnection.CallToolAsync            SdkUpstreamConnection.cs:127
       SDK-CallToolResult → sofort wieder JsonElement (eine Hin- und Rückwandlung)
  └─ ToolInvoker.CallUpstreamAsync                ToolInvoker.cs:240–264     ← der einzige Weg
       1. ResultCompressor.Compress   (FR-16, global, Vorgabe aus)
       2. Guard(…, Inbound)           (blockt oder beobachtet; maskiert nicht)
       3. ToolInvocationResult(Success, gekürzt, …, Truncation)
  └─ ToolInvoker.Audit                            ToolInvoker.cs:458–482
  └─ Fassaden
       MCP        GatewayMcpHandlers.ToCallToolResult   GatewayMcpHandlers.cs:414–438
       Meta-Tool  invoke_tool / invoke_sensitive_tool   MetaToolService.cs:294
       REST       /tools/{name}/invoke, /capabilities/{id}/invoke   ApiEndpoints.cs:88,111
       UI         Tools.razor:166, SetupWizard.razor:1529
       Webhook    WebhookEndpoint.cs:63
```

Alle fünf Fassaden laufen durch `IToolInvoker`. Eine Projektion an dieser einen Stelle wirkt also überall gleich — das ist die gute Nachricht. `resources/read` und `prompts/get` laufen **nicht** durch den Invoker (direkter Passthrough) und sind von diesem Plan nicht erfasst.

**Vier Befunde, die den Entwurf bestimmen:**

1. **Das Audit kennt das Original heute nicht.** Abgelegt werden Status, maskierte Argumente, Dauer und `ResponseBytes`. Die Nutzlast selbst nur mit `BIFROST_AUDIT_DEBUG_PAYLOADS`, und dann die bereits gekürzte Fassung. Die Vorgabe „Audit muss die ungekürzten oder sauber referenzierten Originaldaten kennen" ist also eine **neue** Fähigkeit, keine zu bewahrende.
2. **`ResponseBytes` misst die gekürzte Fassung, in Zeichen.** `ToolInvoker.cs:478` nimmt `result.Content.GetRawText().Length`, und `result.Content` ist an dieser Stelle schon das Ergebnis der Kürzung. Der Kommentar in Zeile 245 sagt das Gegenteil („das Audit soll die tatsächlich gelieferte Größe festhalten, nicht die gekürzte"). Sobald FR-16 eingeschaltet ist, steht im Audit die falsche Größe.
3. **Die heutige Kürzung zerstört die Hülle.** Ein Upstream-Ergebnis ist ein Objekt `{content:[…], isError}`. `ResultCompressor` behandelt es als „Objekt und alles Übrige" und liefert `{_bifrost_truncated, partialJson:"…"}`. Danach findet `ToCallToolResult` kein `content` mehr und gibt das Ganze als JSON-Text aus — `isError` und die Blockstruktur sind weg, der Agent bekommt abgeschnittenes JSON in einer Zeichenkette.
4. **Die Audit-Kette hat eine feste Feldfolge** (`AuditChain.Canonicalize`, `audit-chain-v1`). Jedes neue Audit-Feld verlangt eine neue Kettenfassung; die alte muss nachrechenbar bleiben.

**Was es schon gibt und woran angeschlossen wird, statt es ein zweites Mal zu bauen:**

- `ResultCompressor` / `ResultTruncation` (FR-16) — geht in der neuen Pipeline als letzte Stufe auf
- `SecretGuard` mit 15 kuratierten Regeln, Keyword-Vorfilter, `NonBacktracking` und Zeitgrenze (ADR-0011)
- `GuardRuleStore`, `RedactionRuleStore` — das Muster für datenbankgestützte, zur Laufzeit tauschbare Regeln (`Changed`-Ereignis, atomarer Austausch)
- `ConfigurationExportService` und Policy Packs — **der eine** Importweg (M6-Vertrag §2); JSON
- `UsageCockpit` — rechnet Kontextkosten bereits in Zeichen
- Messvorrichtung in `tests/Bifrost.Performance.Tests` hinter `BIFROST_RUN_BENCHMARK`; **kein** BenchmarkDotNet, **keine** YAML-Bibliothek in `Directory.Packages.props`
- `tools/list` reicht kein `outputSchema` durch (`ToolDescriptor` kennt nur Name, Beschreibung, Eingabeschema). Eine Projektion von `structuredContent` kann deshalb heute keine Client-Validierung brechen — das muss so bleiben oder mitprojiziert werden.

---

## 2. Reihenfolge

1. **Verträge und ADRs zuerst** (Dokumente, Abnahme durch den Nutzer) — für beide Vorhaben.
2. **Umbenennung** als eigener, verhaltensneutraler Zug.
3. **Projektion, Cutter, Profiler** auf den neuen Namen.

Die Umbenennung vor dem Feature, weil sonst jede neue Datei sofort wieder angefasst wird. Bleibt der Name (1.1) länger offen, dreht sich 2 und 3 um; der Engine-Vertrag hängt nur an drei Bezeichnern (Schema-Id, Variablen-Präfix, Markername).

---

## 3. Teil R — Umbenennung

### R0 Name festlegen — erledigt, Rest `[braucht Nutzer]`
Entschieden: Faolan (siehe 1.1). Offen bleibt die Prüfung im Markenregister (DPMA, EUIPO) und, falls gewünscht, eine Domain.

### R1 Vertrag und ADR `[Claude-prüfbar]`, Abnahme `[braucht Nutzer]`
- `docs/adr/0026-umbenennung-auf-faolan.md` (über die Skill `adr-writer`)
- `docs/contracts/umbenennung-faolan.md` mit der Tabelle aus 1.2 als bindender Zuordnung: je Bezeichner „umbenennen", „umbenennen mit Brücke" oder „bleibt".

**Stand 2026-11-16:** [ADR-0026](../adr/0026-umbenennung-auf-faolan.md) und der [Umbenennungsvertrag](../contracts/umbenennung-faolan.md) sind geschrieben und vom Nutzer abgenommen. Der Vertrag weicht in zwei Zeilen von der Tabelle unten ab, und es gilt der Vertrag: Die Archiveinträge `database/bifrost.db` und `database/bifrost.dump` **bleiben** (Formatvertrag der Archivfassung 1), und die ausgehende Freigabe-Benachrichtigung trägt **beide** Headerpaare, solange die Brücke besteht.

Mein Vorschlag für die Zuordnung der Klasse C:

| Bezeichner | Vorschlag |
|---|---|
| Archiveinträge, DB-Datei | neuer Name für Neues; Lesen akzeptiert `faolan.*`, `bifrost.*`, `mcpmcp.db` |
| `__BifrostMigrationJournal` | **bleibt** — ein Tabellenname ist kein Markenname |
| `bifrost.connector.v1` | Leser akzeptiert beide; `faolan-connector` schreibt die neue Id |
| `bifrost://assets/`, `${bifrost:secret/` | Lesen akzeptiert beide, Schreiben nutzt die neue Form |
| `X-Bifrost-*` eingehend | beide angenommen; ausgehend der neue Name |
| `_bifrost_truncated` | entfällt mit Teil P zugunsten des neuen Nachweises (Bruch, im CHANGELOG benannt) |
| Kryptografische Namen | **bleiben**, `ApprovalElicitation`-Zweck wandert in `CryptographicNames` und unter den Test |
| `CN=bifrost-keyring` | bleibt für bestehende Zertifikate; neue bekommen den neuen Namen |
| `BFR-*` | **bleiben** — undurchsichtige Kennungen, kein Markenname |
| Metriken, Meter, Span-Attribute | umbenennen ohne Doppelausgabe; Bruch im CHANGELOG (Dashboards müssen nachziehen) |

### R2 Mechanische Umbenennung `[Claude-prüfbar]`
Ein Commit, ausschließlich Klasse A: `git mv` der Ordner, Projekte, Solution; Namensräume, Typnamen, Projektverweise, Dockerfile-Pfade, Workflow-Pfade. Kein Verhalten ändert sich; Nachweis ist die unveränderte Testzahl. Als Skript in PowerShell, damit der Lauf wiederholbar ist (kein Python).

### R3 Brücken `[Claude-prüfbar]`
- `LegacyEnvironment` wird zur Kette `MCPMCP_` → `BIFROST_` → `FAOLAN_`; der jüngste gesetzte Name gewinnt, die Startwarnung nennt jede übernommene Variable. Gilt auch für `_FILE`-Varianten und die vier CLI-Variablen.
- `ResolveSqliteFile`: `faolan.db`, sonst `bifrost.db`, sonst `mcpmcp.db`. Umbenannt wird weiterhin nichts.
- Leser für Archive, Connector-Manifeste, Asset-URIs, Secret-Referenzen und Header nach der Vertragstabelle.
- Doctor: `BFR-CFG-0002` meldet beide alten Präfixe.

### R4 Betreiber-Artefakte `[Claude-prüfbar]`
Binärnamen `faolan`, `faolan-connector`, `faolan-wasi-host`; Image `ghcr.io/lupusmalusdeviant/faolan`; Compose, `.env.example`, `build.ps1`/`build.sh`, sieben Workflows, `.claude/launch.json`.

**Die Volume-Falle:** Ein Compose-Dienst, der statt `bifrost-data` plötzlich `faolan-data` einhängt, startet mit einem **leeren** Datenverzeichnis und meldet „bereit" — genau der stille Ausfall, vor dem `LegacyEnvironment` warnt, nur eine Ebene tiefer, wo der Prozess ihn nicht erkennen kann. Das mitgelieferte Compose bekommt deshalb `FAOLAN_DATA_VOLUME` (Vorgabe `faolan-data` für Neuinstallationen), und die Upgrade-Anleitung nennt den einen Wert, den Bestandsinstallationen setzen müssen.

### R5 Dokumentation und Migrationshinweis `[Claude-prüfbar]`
- `docs/upgrade-faolan.md` (+ englische Fassung): was sich ändert, was von selbst weiterläuft, was der Betreiber einmal tun muss (Volume, Image-Referenz, Dashboards, Client-Konfiguration wegen `serverInfo.name`).
- README, Betriebsdoku, `SECURITY.md`, `.github/*` auf den neuen Namen. Klasse E bleibt, bekommt wo nötig eine Kopfzeile „hieß bis 0.12.0 B.I.F.R.O.S.T".
- CHANGELOG „Unreleased" mit den Brüchen (Governance-Test verlangt den Abschnitt).

### R6 Nachweis `[Claude-prüfbar]`
- Alle Testprojekte der Solution, nicht nur die berührten (M6-Vertrag §1), geprüft wie in der CI.
- `Upgrade.Tests` erweitert: eine Instanz mit `BIFROST_*`-Umgebung, `bifrost.db` und einem 0.12.0-Archiv startet und stellt unter dem neuen Namen wieder her; dasselbe mit `MCPMCP_*` und `mcpmcp.db`.
- `LegacyEnvironmentTests` für die Kette, `CryptographicNamesTests` um den vierten Zweck.
- Governance-Tests ziehen mit (`Every_environment_option_is_documented…` sucht heute hart nach `BIFROST_`).

### R7 Außerhalb des Repos `[braucht Nutzer]`
Repos auf GitHub und GitLab umbenennen, GHCR-Paket, Instanz auf Badwolf, Connector-Namen in den Clients. Reihenfolge und Befehle liefere ich als Prüfliste.

---

## 4. Teil P — Projektion und Cutter

### 4.1 Wo die Pipeline sitzt

```
Upstream-Ergebnis (UTF-8)
  ├─ Maß nehmen: Zeichen, SHA-256 des Originals          → Nachweis, Audit
  ├─ Abgriff für den Profiler (Stichprobe, nie blockierend)
  ├─ Stufe 1  Projektion      nur erlaubte Pfade, Tabellenform, Elementgrenzen
  ├─ Stufe 2  Cutter          entfernen → maskieren → falten → Budget
  ├─ Stufe 3  globale Obergrenze (das heutige FR-16, jetzt hüllenerhaltend)
  └─ Guard eingehend          prüft, was den Gateway wirklich verlässt
```

Ohne Regel für ein Werkzeug bleibt das Ergebnis bytegleich. Die Projektion steht **vor** dem Guard, wie heute die Kürzung: Ein Feld, das ohnehin nicht ausgeliefert wird, soll keinen Aufruf blockieren, und ein 2-MB-Ergebnis, das auf 3 KB projiziert wird, soll nicht an der Prüfgrenze scheitern. Der Guard bleibt die letzte Instanz über dem Ausgelieferten.

**Eine Regel, die den Entwurf zusammenhält:** *Maskiert wird, bevor irgendetwas abgelegt wird. Projektion, Faltung und Budget wirken nur auf das, was ausgeliefert wird.*

### 4.2 Was „das Ergebnis" ist

Die Engine arbeitet auf der `CallToolResult`-Hülle, nicht auf rohem JSON:

- `structuredContent` wird direkt projiziert.
- Ein `text`-Block, dessen Inhalt JSON ist (der Normalfall — Server serialisieren ihr Ergebnis in den Text), wird gelesen, projiziert und neu geschrieben.
- Ein `text`-Block ohne JSON überspringt Stufe 1 und geht in Stufe 2.
- Bild-, Audio- und Ressourcenblöcke bleiben in der Vorgabe unangetastet; je Regel `keep`, `drop` oder `placeholder`.
- `isError: true` wird nie projiziert — nur maskiert und budgetiert. Eine Fehlermeldung, aus der das Entscheidende herausgefiltert wurde, ist schlimmer als eine lange.

### 4.3 Regelformat

JSON, Schema-Id `faolan.projection/v1`, eine Regel je Server und Werkzeug (`"tool": "*"` als Server-Vorgabe für Stufe 2):

```json
{
  "schema": "faolan.projection/v1",
  "server": "github",
  "tool": "search_issues",
  "mode": "enforce",
  "select": ["total_count", "items[].number", "items[].title", "items[].state", "items[].user.login"],
  "shape": { "items": { "format": "table", "maxItems": 50 } },
  "text": {
    "strip": ["base64", "tracking-params", "ansi"],
    "fold": { "stacktrace": { "head": 5, "tail": 3 }, "repeatedLines": true },
    "budget": { "default": 2000, "fields": { "items[].body": 400 }, "total": 12000 }
  },
  "nonText": "keep"
}
```

- **Pfadsprache:** Punkt-Segmente, `[]` für „jedes Element", `*` für „jeder Schlüssel". Bewusst kein JSONPath: keine Bibliothek, keine Filterausdrücke, in einem Durchlauf entscheidbar.
- **`mode`:** `off`, `observe` (rechnet und protokolliert die Ersparnis, liefert aber das Original aus), `enforce`. Der Probelauf ist der Weg, eine Regel einzuführen, ohne einem Agenten blind etwas wegzunehmen — dieselbe Idee wie `GuardMode.Observe`.
- **Tabellenform:** spaltenweises JSON, `{"cols":["number","title","state"],"rows":[[1,"…","open"],…]}`. Die Spalten ergeben sich aus der Regel, nicht aus den Daten, sind also je Regel konstant. Es bleibt gültiges JSON und funktioniert damit auch in `structuredContent`. Eine Textvariante (kopfzeilenbasiert, noch kompakter) lässt sich später hinter demselben Schalter ergänzen.
- **Budgets in Zeichen**, wie überall im Projekt (`Invocation.cs:80` begründet das). Token erscheinen nur als Schätzung in Anzeige und Bericht.

**Ablage:** Tabelle `ProjectionRules` (Server, Werkzeug, Regel-JSON, Hash, Modus, Zeitstempel), zur Laufzeit tauschbar wie die Guard-Regeln. Die Datei-Form ist der bestehende Konfigurationsexport und das Policy Pack — **kein** zweiter Importweg. Für YAML müsste eine neue Abhängigkeit durch die Lieferketten-Gates; mein Vorschlag ist JSON allein.

### 4.4 Cutter, feste Reihenfolge

1. **Entfernen:** Base64-Läufe ab einer Mindestlänge (`[base64, 12,3 KB entfernt]`), `data:`-URIs, Tracking-Parameter in URLs, ANSI-Sequenzen.
2. **Maskieren:** Die Guard-Regeln bekommen einen dritten Modus `Mask` neben `Observe` und `Block` — **ein** Regelsatz, nicht zwei. Maskiert wird je Textwert, nicht auf dem rohen JSON: Die PEM-Regel reicht mit `[\s\S]{64,}?` über Zeichenkettengrenzen hinweg, eine Ersetzung im Rohtext könnte das Dokument zerbrechen. Neue eingebaute Regeln: Bearer-Header, Verbindungszeichenketten (`Password=`, `Pwd=`), Zugangsdaten in URLs. Das ergänzt ADR-0011 und braucht deshalb einen ADR.
3. **Falten:** Stacktraces (.NET, Java, Python, JavaScript) auf die ersten und letzten Frames, optional mit einer Präfixliste „eigener" Frames; aufeinanderfolgende gleiche Logzeilen (gleich nach Abzug des Zeitstempels) zu einer Zeile mit Zähler.
4. **Budget:** je Feld und gesamt, mit sichtbarer Marke `…[gekürzt: 18 432 → 2 000 Zeichen]`.

Die festen Muster des Cutters (Frame-Zeilen, Zeitstempel, ANSI, URL-Parameter) über `[GeneratedRegex]`, rückverfolgungsarm geschrieben, mit Zeitgrenze. Guard-Regeln — eingebaute wie Freitext-Muster von Admins — bleiben bei `NonBacktracking` zur Laufzeit (ADR-0011, E2). *Gemessen am 2026-11-16:* Der Quellgenerator erzeugt für `RegexOptions.NonBacktracking` keinen eigenen Code; `[GeneratedRegex]` liefert dort ein gewöhnliches `Regex`, ohne Warnung beim Bau. Er lohnt sich also nur für die festen Muster. Die Zahlen stehen im [Projektionsvertrag](../contracts/rueckkanal-projektion.md), Abschnitt 13.

**Nachtrag aus dem Vertrag:** Die Prüfung in Schritt 2 läuft je Textwert gegen **alle** eingehenden Regeln, nicht nur gegen `Mask` — sonst könnte das Budget in Schritt 4 ein Token halbieren, das danach keine Regel mehr erkennt. Der Guard am Ende der Pipeline bleibt als zweites Netz (Vertrag 6.2).

### 4.5 Schnittstellen (Entwurf für den Vertrag)

In `Faolan.Abstractions`:

```csharp
public interface IResponseShaper            // synchron, ohne E/A, im Hot Path
{
    ShapedResponse Shape(in ShapingContext context, JsonElement upstreamResult);
}

public interface IProjectionRuleStore       // wie IGuardRuleStore
{
    IReadOnlyList<ProjectionRule> All { get; }
    ProjectionRule? Find(ServerId server, string tool);
    Task UpsertAsync(ProjectionRule rule, CancellationToken ct);
    Task RemoveAsync(ServerId server, string tool, CancellationToken ct);
    event EventHandler? Changed;
}

public interface IResponseObserver          // Abgriff des Profilers; blockiert nie, wie IAuditSink
{
    void Observe(in ObservedResponse sample);
}

public sealed record ShapingReport(
    int OriginalChars, int DeliveredChars, string OriginalSha256,
    string? RuleHash, ProjectionMode Mode,
    int FieldsDropped, int ValuesMasked, int BlocksFolded, int ValuesTruncated);
```

In `Faolan.Core/Projection`: `ProjectionPath` (Parser), `CompiledProjection` (Präfixbaum, einmal je Regeländerung gebaut), `JsonProjector` (`Utf8JsonReader` → `Utf8JsonWriter` in einen gepoolten Puffer; verworfene Teilbäume werden mit `Skip()` übersprungen und nie materialisiert), `TableWriter`, `TextCutter` mit den vier Stufen, `ResponseShaper` als Orchestrierung über der Hülle.

`ToolInvoker` bekommt `IResponseShaper?` als weiteren optionalen Konstruktorparameter und ruft ihn dort, wo heute `ResultCompressor.Compress` steht. `ToolInvocationResult.Truncation` wird zu `ShapingReport` erweitert; die Fassaden reichen ihn weiter (MCP in `_meta`, REST im vorhandenen `truncation`-Feld).

### 4.6 Audit

- Neue Felder am Ereignis: `OriginalResponseChars`, `OriginalResponseSha256`, `DeliveredResponseChars`, `ProjectionRuleHash`, `ProjectionMode` und die vier Zähler. Damit ist je Aufruf belegt, **was ankam, welche Regel es verändert hat und was hinausging** — ohne die Nutzlast abzulegen.
- `ResponseBytes` wird korrigiert (Befund 2) und bekommt seine Bedeutung im Vertrag festgeschrieben.
- `audit-chain-v2` für die neue Feldfolge; der Prüfer rechnet beide Fassungen, bestehende Abschnitte bleiben gültig.
- Der Debug-Modus legt künftig das **Original nach Maskierung** ab, nicht die gekürzte Fassung — wer Regeln entwickelt, braucht genau das.
- Migration für SQLite und PostgreSQL, jeweils mit Rückweg (Governance-Tests verlangen beides).

**Optional, als eigenes Paket P7:** eine inhaltsadressierte, verschlüsselte Ablage der Originale (`data/originals/<sha256>`, Aufbewahrung wie das Audit, Größengrenze), auf die der Hash im Audit zeigt — und ein Meta-Tool, mit dem ein Agent ein weggefiltertes Feld gezielt nachholen kann. Das macht eine zu strenge Regel reparierbar statt blind. Vorgabe wäre aus: Ergebnisse dauerhaft abzulegen widerspricht NFR-04 und will ausdrücklich eingeschaltet werden.

### 4.7 Leistung und Messung

Ziele, die gemessen und nicht versprochen werden:

| Fall | Ziel |
|---|---|
| Werkzeug ohne Regel | keine zusätzliche Allokation, Aufschlag im einstelligen Mikrosekundenbereich |
| 100 KB JSON, Projektion auf ~5 % | unter 1 ms, Allokation ≤ 1,5 × Ausgabegröße |
| Cutter auf 50 KB Logtext | unter 1 ms |

Nebenbei fällt weg, dass die Pipeline heute bis zu dreimal `GetRawText()` über die ganze Nutzlast ruft (Kürzung, Guard, Audit) — die Engine arbeitet einmal auf den UTF-8-Bytes.

Zwei Arten Nachweis:
- **Allokationstests in `Core.Tests`** über `GC.GetAllocatedBytesForCurrentThread()` — deterministisch, laufen in jeder CI.
- **Zeitmessung** entweder in der bestehenden Vorrichtung hinter `FAOLAN_RUN_BENCHMARK` oder mit BenchmarkDotNet in einem eigenen, nicht ausgelieferten Projekt. Letzteres ist eine neue Entwicklungsabhängigkeit und damit Ihre Entscheidung.

### 4.8 Arbeitspakete

| Nr. | Inhalt | Marke |
|---|---|---|
| P0 | [ADR-0027](../adr/0027-rueckkanal-projektion-im-invoker.md) (Rückkanal-Projektion), [ADR-0028](../adr/0028-maskieren-als-dritter-guard-modus.md) (`Mask`, ergänzt ADR-0011) und Vertrag [`docs/contracts/rueckkanal-projektion.md`](../contracts/rueckkanal-projektion.md): Schnittstellen, Regelschema, Audit-Felder, Kettenfassung, Nachweisformat — **abgenommen am 2026-11-16** | `[Claude-prüfbar]`, Abnahme `[braucht Nutzer]` |
| P1 | Pfadsprache, `CompiledProjection`, `JsonProjector`; Tests gegen Fixtures echter Antwortformen, Allokationstests | `[Claude-prüfbar]` |
| P2 | Tabellenform und Elementgrenzen | `[Claude-prüfbar]` |
| P3 | Cutter: entfernen, falten, Budget; je Stufe Positiv-, Negativ- und Grenzfälle | `[Claude-prüfbar]` |
| P4 | `GuardMode.Mask`, neue eingebaute Regeln, Maskierung je Textwert | `[Claude-prüfbar]` |
| P5 | Einbau in `ToolInvoker`, Hüllenerhalt, FR-16 als letzte Stufe, `ShapingReport` durch alle fünf Fassaden | `[Claude-prüfbar]` |
| P6 | Audit-Felder, `audit-chain-v2`, Migrationen beider Anbieter, Export | `[Claude-prüfbar]` |
| P7 | *(optional)* Original-Ablage und Nachhol-Werkzeug | `[Claude-prüfbar]` |
| P8 | Regel-Ablage, REST `/api/v1/projection/rules`, Aufnahme in Konfigurationsexport und Policy Pack | `[Claude-prüfbar]` |
| P9 | CLI `faolan projection list\|show\|set\|remove\|test`; `test` rechnet eine Regel offline gegen eine Beispieldatei und zeigt vorher/nachher | `[Claude-prüfbar]` |
| P10 | Oberfläche: Regeln, Ersparnis je Werkzeug, Probelauf-Ergebnisse | `[Claude-prüfbar]`, Sichtprüfung `[braucht Nutzer]` |
| P11 | Zeitmessung, Ergebnis nach `docs/acceptance/performance.md`; Lastenheft und Traceability-Matrix um die neuen Anforderungen | `[Claude-prüfbar]`, Lauf auf Referenz-Hardware `[braucht Nutzer]` |

---

## 5. Teil A — Auto-Profiler

**Was er tut:** Er sieht echten Antworten zu und schlägt Regeln vor. Er setzt nie selbst eine in Kraft.

- **Abgriff:** `IResponseObserver` im Shaper, Stichprobe mit Rate und Obergrenze je Werkzeug, begrenzter Kanal, bei Überlauf wird verworfen. Ein Aufruf wartet nie auf den Profiler.
- **Was abgelegt wird:** je Werkzeug eine Formskizze — je Pfad (Arrays zu `[]` zusammengefasst) Häufigkeit, Typen, Bytes gesamt und im Mittel, Anteil an der Nutzlast, ob der Wert über alle Stichproben konstant ist, Längenverteilung, Kennzeichen (Base64, URL, Stacktrace, Zeitstempel). **Keine Werte**, nur Pfade, Zahlen und gesalzene Hashes. Tabelle `ResponseProfiles`, übersteht einen Neustart.
- **Entwurf:** deterministische Regeln über der Skizze. Streichkandidaten sind Pfade mit hohem Byteanteil und wenig Aussage (konstant, fast immer leer, URL-Vorlagen, Base64, undurchsichtige Ids). Flache Objekt-Arrays ab einer Mindestlänge bekommen Tabellenform, lange Texte ein Budget, gesehene Stacktraces die Faltung. Heraus kommt ein Dokument `faolan.projection/v1` im Modus `observe`, dazu ein Bericht mit Begründung je Entscheidung und geschätzter Ersparnis über die Stichprobe.
- **Bedienung:** `FAOLAN_PROFILER=on|off`, `FAOLAN_PROFILER_SAMPLE_RATE`; CLI `faolan profile status|draft <server> [<werkzeug>] [-o datei]|reset`; REST `/api/v1/projection/profiles`; in der Oberfläche Entwurf, Vergleich mit der geltenden Regel und Übernahme per Klick.

**Die Grenze, die im Vertrag stehen muss:** Der Profiler sieht, was aufgebläht ist — nicht, was ein Agent braucht. Ein Feld, das groß und langweilig aussieht, kann genau das sein, worauf ein Arbeitsschritt wartet. Der Entwurf ist deshalb vorsichtig gebaut (er behält alles, was nicht eindeutig Ballast ist), kommt im Probelauf an und wird von einem Menschen in Kraft gesetzt.

| Nr. | Inhalt | Marke |
|---|---|---|
| A1 | Vertrag [`docs/contracts/antwort-profiler.md`](../contracts/antwort-profiler.md): Skizzenformat, Heuristiken mit Schwellen, was nie abgelegt wird — **abgenommen am 2026-11-16** | `[Claude-prüfbar]`, Abnahme `[braucht Nutzer]` |
| A2 | Abgriff, Kanal, Skizzenrechnung, Ablage; Test, dass kein Wert die Tabelle erreicht | `[Claude-prüfbar]` |
| A3 | Entwurfsgenerator; Tests gegen aufgezeichnete Antwortformen mit erwarteter Regel | `[Claude-prüfbar]` |
| A4 | CLI, REST, Oberfläche | `[Claude-prüfbar]`, Sichtprüfung `[braucht Nutzer]` |
| A5 | Lauf gegen die echten Upstreams der Badwolf-Instanz, Entwürfe durchsehen | `[braucht Nutzer]` |

---

## 6. Risiken

| Risiko | Gegenmaßnahme |
|---|---|
| Eine Regel filtert weg, was der Agent braucht | Probelauf als Einführungsweg; Nachweis im Ergebnis; optional P7 |
| Stiller Datenverlust beim Upgrade (Volume, Variablen, DB-Datei) | Brücken R3, Volume-Schalter R4, Upgrade-Tests R6 |
| Neue Audit-Felder brechen die Kettenprüfung | eigene Kettenfassung, Prüfer rechnet beide; Test mit gemischtem Bestand |
| Maskierung verändert, was der Guard sieht | Reihenfolge und Zuständigkeit in ADR-0027; `Block` gewinnt gegen `Mask` bei derselben Regel-Id |
| Projektion macht `structuredContent` schemawidrig | `outputSchema` wird weiterhin nicht durchgereicht; Test, der das festhält |
| Regex-Kosten im Hot Path | Keyword-Vorfilter wie im `SecretGuard`, Zeitgrenze, Allokationstests |
| Der neue Name kollidiert doch | Register und GitHub sind geprüft (1.1); Markenregister vor R2 |

---

## 7. Offene Entscheidungen

1. ~~**Name**~~ — entschieden: Faolan.
2. ~~**Originale im Audit**~~ — entschieden mit der Abnahme am 2026-11-16: nur Hash und Größe; P7 bleibt offen.
3. ~~**Regelformat**~~ — entschieden: JSON allein.
4. ~~**Zeitmessung**~~ — entschieden: bestehende Vorrichtung plus Allokationstests, kein BenchmarkDotNet.
5. ~~**Metriken**~~ — entschieden: hart umbenennen, keine Doppelausgabe.
6. **Reihenfolge** — Umbenennung zuerst (Vorschlag). Noch offen, weil die mechanische Umbenennung (R2) mit laufender Arbeit an `src/Bifrost.*` kollidiert (Plan 0005) und die Prüfung im Markenregister aussteht.

Die weiteren mit der Abnahme bestätigten Punkte stehen im Projektionsvertrag (Abschnitt 15) und im Profiler-Vertrag (Abschnitt 8).

## 8. Prüfliste für den Nutzer

1. Faolan im Markenregister (DPMA, EUIPO) gegenprüfen (R0).
2. ~~ADR-0026 und den Umbenennungsvertrag abnehmen (R1).~~ — abgenommen am 2026-11-16.
3. ~~ADR-0027, ADR-0028 und den Projektionsvertrag abnehmen (P0) — samt der sieben Punkte in dessen Abschnitt 15; danach den Profiler-Vertrag (A1) mit den vier Punkten in dessen Abschnitt 8.~~ — abgenommen am 2026-11-16.
4. Repos, GHCR-Paket, Badwolf-Instanz und Client-Connectoren umstellen (R7) — Befehle kommen mit R5.
5. Oberfläche für Regeln und Profiler ansehen (P10, A4).
6. Zeitmessung auf Referenz-Hardware laufen lassen (P11).
7. Profiler gegen die echten Upstreams laufen lassen und die Entwürfe beurteilen (A5).
