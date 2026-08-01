# Importmatrix — was je Client übernommen wird, und was nicht

Stand: WP4.3 (M4). Gegenstand sind die vier Clientparser unter
`src/Bifrost.Core/Importing/Providers/` und der generische Parser aus WP4.1.

Diese Matrix nennt **auch die Felder, die nicht unterstützt werden**. Eine Matrix, die nur die
unterstützten Felder aufzählt, beantwortet die einzige Frage nicht, die vor einem Import zählt:
*Was ist nach dem Import anders als vorher?*

## Legende

| Zeichen | Bedeutung |
| --- | --- |
| **voll** | Das Feld wird eins zu eins übernommen. |
| **teilweise** | Das Feld wird übernommen, aber die Bedeutung verschiebt sich. Es gibt dazu einen Befund. |
| **nicht** | Das Feld wird nicht übernommen. Es gibt dazu einen Befund (`BFR-IMP-0200` clientexklusiv oder `BFR-IMP-0003` unbekannt). |

Kein Feld wird **still** verworfen. Wo „nicht" steht, steht im Plan ein Befund mit Ort und
Handlungsanweisung. Das ist der Kern dieses Pakets: Ein Import, der etwas wegwirft, ohne es zu
sagen, erzeugt eine Konfiguration, die anders ist als die Quelle — und niemand weiß, worin.

---

## 0. Wo ein Befund hinzeigt

Jeder Befund nennt seinen Ort im **Quelldokument**, nicht in einem gedachten Normalformat. Der
Sammelname ist je Client ein anderer, und das steht auch so im Pfad:

| Format | Datei | Ort eines Servers |
| --- | --- | --- |
| `mcp` (generisch) | `.mcp.json` | `mcpServers/<name>` beziehungsweise `servers/<name>` |
| `claude` (Projekt, Desktop) | `.mcp.json`, `claude_desktop_config.json` | `mcpServers/<name>` |
| `claude` (Benutzerdatei) | `~/.claude.json` | `projects/<projektpfad>/mcpServers/<name>` |
| `cursor` | `.cursor/mcp.json` | `mcpServers/<name>` |
| `vscode` (eigene Datei) | `.vscode/mcp.json` | `servers/<name>` |
| `vscode` (Editoreinstellungen) | `settings.json` | `mcp/servers/<name>` |
| `codex` | `config.toml` (als JSON) | `mcp_servers/<name>` |

Bis WP4.3 setzte die zentrale Nachbearbeitung den Ort **aller** von ihr erzeugten Befunde fest auf
`mcpServers/<name>` — und das sind die meisten Befunde überhaupt: Risiko, Zugangsdaten,
Normalisierung entstehen alle dort. Bei drei der sieben Zeilen oben zeigte das auf eine Stelle, die
es in der Quelldatei nicht gibt. **Ein Ort, der nicht stimmt, ist schlechter als keiner:** Er
schickt jemanden an die falsche Zeile, und wer dort nichts findet, glaubt eher, den Befund
missverstanden zu haben. Der Ort kommt jetzt vom Parser
(`ImportCandidate.SourcePath`); `ImportFindingLocationTests` prüft ihn über alle
Beispielkonfigurationen.

### Zwei Sorten Befund — und warum der Unterschied im Vertrag steht

`ImportFinding.Scope` sagt, **wen** ein Befund betrifft:

| Bereich | Was das heißt | Beispiele |
| --- | --- | --- |
| `Document` (Vorgabe) | Betrifft die ganze Datei. Ein Fehler dieser Sorte hält den **ganzen** Plan an. | kaputtes JSON (`BFR-IMP-0001`), TOML statt JSON (`BFR-IMP-0006`), unbekanntes oder mehrdeutiges Format (`BFR-IMP-0002`), VS Codes `sandbox` auf oberster Ebene (Risiko) |
| `Entry` | Betrifft genau den Eintrag unter `Path`. Ein Fehler dieser Sorte nimmt **diesen einen** Kandidaten heraus. | fehlender Transport, `command` und `url` zugleich, unbrauchbare Adresse, doppelter Servername (`BFR-IMP-0004`), Slug-Kollision (`BFR-IMP-0005`) |

**Die Vorgabe ist `Document`, und das ist Absicht.** Ohne diese Angabe müsste ein planweiter Befund
an seinem fehlenden Pfad erkannt werden — und ein neuer Befund, der versehentlich einen Pfad trägt,
wäre stillschweigend zu einem Einzelbefund verharmlost. So herum blockiert ein vergessener Bereich
zu viel statt zu wenig.

### Teilimport: ein kaputter Eintrag hält die übrigen nicht auf

Bis WP4.3 galt `ImportPlan.CanApply` planweit: Ein einziger kaputter Eintrag machte eine Datei mit
dreißig Servern unanwendbar. Jetzt gilt:

- `ImportPlan.IsApplicable(kandidat)` — die maßgebliche Auskunft je Eintrag: kein planweiter Fehler,
  kein eigener Fehler, kein Planbefund, der über seinen Pfad diesen Eintrag meint.
- `ImportPlan.ApplicableCandidates` / `BlockedCandidates` — was geht und was nicht.
- `ImportPlan.CanApply` heißt jetzt „**etwas** geht", nicht mehr „alles geht". Wer weiterhin
  „alles oder nichts" braucht, vergleicht die beiden Anzahlen.
- `ImportPlan.ConfirmationsFor(auswahl)` — bestätigt wird, was angelegt wird. Wer drei von dreißig
  Servern übernimmt, bestätigt die Risiken dieser drei und die planweiten; nicht die der
  siebenundzwanzig, die er gerade nicht anlegt.

**Woran ein Betreiber den Unterschied sieht:**

| Weg | Anzeige |
| --- | --- |
| API-Vorschau | `candidates[].canApply` je Eintrag, `blockingFindings` für die planweiten Fehler |
| API-Übernahme | `imported` (angelegt) **und** `skipped` (übergangen, je mit Ort und Befundcodes) |
| CLI `import preview` | `— NICHT ANWENDBAR` hinter dem Eintrag, Zusammenfassung „Teilweise anwendbar: 1 von 2 Servern" |
| CLI `import apply` | zusätzlich der Block „Uebergangen (nicht anwendbar)" nach der Übernahme |

**Die Ausnahme, an der der Teilimport aufhört:** Wer einen gesperrten Server **ausdrücklich** in
`servers` (beziehungsweise `--only`) benennt, bekommt eine Absage (400) statt eines stillen
Auslassens. Ein Import, der genau das übergeht, was jemand benannt hat, ist die unangenehmste Sorte
Überraschung.

---

## 1. Erkennung

Der Importer wählt den Parser über die gemeldete Sicherheit. Liegt der Abstand zwischen dem besten
und dem zweitbesten Treffer unter `ConfigurationImporter.AmbiguityMargin` (0,10), meldet er einen
Fehler, **statt zu wählen**. Deshalb liegen alle Clientwerte mindestens 0,10 über dem stärksten Wert
des generischen Parsers (0,60).

| Parser | Wert | Woran erkannt |
| --- | --- | --- |
| `mcp` (generisch, WP4.1) | 0,60 / 0,50 | `mcpServers` / `servers` |
| `claude` | **0,95** | `enabledMcpjsonServers`, `disabledMcpjsonServers`, `enableAllProjectMcpServers` oder die Karte `projects` mit `mcpServers` darunter |
| `claude` | **0,80** | `mcpServers` **und** die Ersetzungsform `${VAR:-vorgabe}` — von den vier Clients schreibt sie nur Claude Code |
| `cursor` | **0,90** | `mcpServers` mit einem `auth`-Block (VS Code nennt das `oauth`) |
| `cursor` | **0,85** | `mcpServers` **und** Cursors Ersetzungen `${env:…}`, `${userHome}`, `${workspaceFolder}`, `${pathSeparator}`, `${/}` |
| `cursor` | **0,80** | `mcpServers` mit `envFile` an einem Eintrag |
| `vscode` | **0,92** | der Block `mcp` in einer `settings.json`, darunter `servers` |
| `vscode` | **0,90** | `servers` **und** ein VS-Code-Merkmal: `inputs`, `sandbox`, `envFile`, `dev`, `sandboxEnabled`, `gallery` oder `${input:…}` |
| `codex` | **0,90** | der Sammelname `mcp_servers` in Schlangenschrift |

Der Test `ProviderRecognitionTests.Jeder_erkennungswert_ueberstimmt_den_generischen_parser_deutlich`
liest diese Zahlen aus den Konstanten und würde bei einem neuen, zu schwachen Wert rot.

### Die Grenze, die keine Zahl behebt

**Eine schlichte `.mcp.json` ist bei Claude Code und Cursor zeichengleich.** Findet ein Clientparser
kein clienteigenes Merkmal, meldet er `0` — dann übernimmt der generische Parser, und die
Herkunftsangabe lautet `mcp` statt `claude`. Das ist der richtige Ausgang: „aus Claude" wäre eine
Behauptung, die das Dokument nicht hergibt. Betroffen sind unter anderem:

- `claude_desktop_config.json` mit `mcpServers` und `globalShortcut` (Fixture `claude/02`),
- `.cursor/mcp.json` ohne Ersetzung, `auth` oder `envFile`,
- jede von Hand geschriebene `.mcp.json` ohne Eigenheiten.

Trägt ein Dokument die Merkmale **zweier** Clients, wird es nicht zugeordnet, sondern abgewiesen
(`BFR-IMP-0002`, Fehler) — belegt durch
`ProviderRecognitionTests.Ein_dokument_mit_zwei_dialekten_wird_nicht_geraten`.

---

## 2. Claude (Claude Code, Claude Desktop)

Datei: `.mcp.json` (Projekt), `~/.claude.json` (Benutzer, mit `projects`),
`claude_desktop_config.json` (Desktop). Parser: `ClaudeImportProvider`.

### Serverebene

| Feld | Status | Was passiert |
| --- | --- | --- |
| `command` | voll | |
| `args` | teilweise | Zahlen und Wahrheitswerte werden zu Text (`BFR-IMP-0201`, Info); Objekte und Listen fallen weg, die Reihenfolge verschiebt sich — mit Befund. |
| `env` | voll | Werte bleiben wörtlich stehen. |
| `type: "stdio"` | voll | |
| `type: "http"` | voll | |
| `type: "sse"` | teilweise | Übernommen als Streamable HTTP mit erlaubtem Rückfall auf SSE (`AllowLegacySse`). Befund `BFR-IMP-0201`. |
| `url` | voll | Nur absolute `http`/`https`-Adressen. |
| `headers` | voll | Werte bleiben wörtlich stehen; Zugangsdaten werden zentral eingeordnet. |
| `cwd` | **nicht** | Gehört nicht zum dokumentierten Claude-Schema. Es zu übernehmen hieße, den Server hier woanders zu starten als in der Quelle. Befund `BFR-IMP-0200`. |
| alles andere | **nicht** | Befund `BFR-IMP-0003` mit Ort. |

### Ersetzungen `${VAR}` und `${VAR:-vorgabe}`

| Ort | Status | Was passiert |
| --- | --- | --- |
| `command`, `args`, `env`, `headers` | teilweise | Der Wert bleibt **wörtlich** stehen und wird nicht aufgelöst; ein Befund nennt jede Fundstelle. Aufzulösen hieße, die Umgebung dieser Instanz für die der Quellmaschine zu halten. |
| `url` | **nicht** | Eine Adresse, die erst nach der Ersetzung eine Adresse ist, wird abgewiesen (Fehler). Eine halbe Adresse liefe durch jede Prüfung und scheiterte erst am Netz — mit einer Meldung, die nach einem Netzproblem aussieht. |

### Oberste Ebene

| Feld | Status | Was passiert |
| --- | --- | --- |
| `mcpServers` | voll | |
| `projects` | teilweise | Gelesen werden **nur** die `mcpServers` je Projekt. Freigaben, Verlauf und Modellwahl bleiben liegen (Befund je Projekt); Projekte ohne Server werden gezählt gemeldet. |
| `enabledMcpjsonServers`, `disabledMcpjsonServers`, `enableAllProjectMcpServers` | **nicht** | Befund: Welche Server in der Quelle wirklich liefen, muss vor dem Einschalten abgeglichen werden. |
| `globalShortcut`, `permissions`, `hooks`, `model`, `env`, `apiKeyHelper`, `statusLine`, `outputStyle`, `includeCoAuthoredBy`, `cleanupPeriodDays`, `autoUpdates`, `forceLoginMethod`, `theme`, `mcpContextUris`, `sandbox` | **nicht** | Einstellungen des Quellclients, erhalten als Befund `BFR-IMP-0200`. |
| alles andere | **nicht** | Befund `BFR-IMP-0003`. |

---

## 3. Cursor

Datei: `~/.cursor/mcp.json`, `.cursor/mcp.json`. Parser: `CursorImportProvider`.

| Feld | Status | Was passiert |
| --- | --- | --- |
| `command`, `args`, `env` | voll / teilweise (`args` wie oben) | |
| `url`, `headers` | voll | |
| `type` | teilweise | `sse` → Streamable HTTP mit Rückfall (Befund). `stdio` an einem Eintrag mit `url` wird gemeldet und der Transport aus den Feldern abgeleitet. |
| `env` an einem **entfernten** Server | **nicht** | Ein HTTP-Upstream startet hier kein Programm; es gibt keine Umgebung, in die die Werte gehören. Befund mit dem Hinweis auf `headers`. |
| `envFile` | **nicht** | Die Datei wird **ausdrücklich nicht gelesen**. Gemeldet wird, dass dort Werte liegen (`ImportSecret` ohne Wert) — sonst sähe der Import vollständig aus, obwohl die halbe Umgebung fehlt. |
| `auth` (`CLIENT_ID`, `CLIENT_SECRET`, `scopes`) | **nicht** | Erhalten als Befund. `CLIENT_SECRET` wird **trotzdem als Zugangsdatum eingeordnet**: Es steht an einer Stelle, die dieses Gateway nicht übernimmt — die zentrale Risikoprüfung sähe es also nie. |
| `disabled` | **nicht** | Befund. Hier kommt ohnehin jeder Server abgeschaltet an. |
| `cwd` | **nicht** | Nicht im dokumentierten Cursor-Schema; Befund. |
| Ersetzungen `${env:…}`, `${userHome}`, `${workspaceFolder}`, `${workspaceFolderBasename}`, `${pathSeparator}`, `${/}` | teilweise | Bleiben wörtlich stehen, jede Fundstelle wird benannt. |
| alles andere | **nicht** | Befund `BFR-IMP-0003`. |

---

## 4. VS Code

Datei: `.vscode/mcp.json`, `mcp.json` auf Benutzerebene, Block `mcp` in `settings.json`.
Parser: `VsCodeImportProvider`.

| Feld | Status | Was passiert |
| --- | --- | --- |
| `servers` | voll | Auch unter `mcp` in einer `settings.json`. |
| `type` (`stdio`, `http`, `sse`) | teilweise | `sse` wie oben. |
| `command`, `args`, `env` | voll / teilweise (`args`) | `null` als Wert einer Umgebungsvariablen wird **nicht** übernommen: VS Code setzt dafür seinen eigenen Wert ein, den es hier nicht gibt (Befund). |
| `cwd` | **voll** | VS Code ist das einzige der vier Formate mit einem dokumentierten Arbeitsverzeichnis. |
| `url`, `headers` | voll | |
| `inputs` | **nicht** (als Befund erhalten) | Ein `${input:id}` ist kein Wert, sondern eine Frage, die VS Code beim Start stellt. Dieses Gateway fragt niemanden. Jede Verwendung wird als fehlender Wert gemeldet (`BFR-IMP-0202`); bei `password: true` zusätzlich als `ImportSecret` **ohne Wert**. Rekonstruiert wird nichts. |
| `sandbox` (oberste Ebene) | **nicht** | **Risikobefund** (`BFR-IMP-0200`, Severity `Risk`, verlangt Bestätigung): Die Quelle hat den Server auf Pfade und Domänen beschränkt. Diese Grenze reist nicht mit — aus einem eingehegten Server wird ein freier. |
| `sandboxEnabled` (Serverebene) | **nicht** | Befund mit demselben Grund. |
| `envFile` | **nicht** | Wird nicht gelesen; `ImportSecret` ohne Wert. |
| `dev` (`watch`, `debug`) | **nicht** | Befund. |
| `oauth` | **nicht** | Dieses Gateway führt seine eigene OAuth-Anbindung. |
| `gallery`, `version` | **nicht** | Herkunft aus VS Codes Serverkatalog; Befund. |
| Ersetzungen `${workspaceFolder}`, `${env:…}`, `${userHome}` | teilweise | Bleiben wörtlich stehen, jede Fundstelle wird benannt. |
| alles andere | **nicht** | Befund `BFR-IMP-0003`. |

---

## 5. Codex

Datei: `~/.codex/config.toml`, `.codex/config.toml`. Parser: `CodexImportProvider`.

> **Die wichtigste Zeile dieser Matrix:** Codex schreibt **TOML**. Der Importweg dieses Gateways
> nimmt **JSON** entgegen. Eine echte `config.toml` kommt hier **nicht** an — sie wird abgewiesen,
> bevor überhaupt ein Parser gefragt wird. Gelesen wird die **JSON-Umschrift** desselben Aufbaus,
> und **jeder Plan sagt das** (`BFR-IMP-0002`, Warnung).
>
> Seit WP4.3 sagt die Absage auch **warum**: Ein Dokument, das kein JSON ist, aber die Form von TOML
> hat (Abschnittsüberschriften in eckigen Klammern, Zuweisungen mit `=`), bekommt
> `BFR-IMP-0006 UnsupportedSourceFormat` statt `BFR-IMP-0001 NotJson` — samt der nächsten Handlung:
> `[mcp_servers.<name>]` wird zu `{ "mcp_servers": { "<name>": { … } } }`. Das ist **keine**
> TOML-Unterstützung; es ist die Meldung, die einen Codex-Betreiber davon abhält, in einer heilen
> Datei nach einem Syntaxfehler zu suchen, den es nicht gibt. Siehe Abschnitt 8.

| Feld | Status | Was passiert |
| --- | --- | --- |
| `mcp_servers.<id>.command`, `args`, `env` | voll / teilweise (`args`) | |
| `mcp_servers.<id>.cwd` | voll | |
| `mcp_servers.<id>.url` | voll | |
| `mcp_servers.<id>.http_headers` | voll | Auf `HttpTransportOptions.Headers` abgebildet. |
| `mcp_servers.<id>.tool_timeout_sec` | voll | Auf `CallTimeout` abgebildet; dass ein fremdes Zeitlimit übernommen wurde, steht als Befund da. Ein unbrauchbarer Wert wird gemeldet, nicht ausgelegt. |
| `mcp_servers.<id>.startup_timeout_sec` | **nicht** | Keine Entsprechung; Befund. |
| `mcp_servers.<id>.enabled` | **nicht** | Befund. Auch `enabled = true` ändert nichts: Jeder Kandidat kommt abgeschaltet an. |
| `mcp_servers.<id>.bearer_token_env_var` | **nicht** | Nennt nur den **Namen** einer Umgebungsvariablen. Der Wert steht nirgends und wird **nicht erraten**: `ImportSecret` ohne Wert plus Befund `BFR-IMP-0202` mit der Anweisung, den Wert hier als `Authorization`-Kopfzeile zu hinterlegen. |
| alle übrigen Schlüssel der CLI (`model`, `provider`, Sandbox, Freigaben …) | **nicht** | Befund `BFR-IMP-0200`. |
| TOML-Kommentare, Reihenfolge, TOML-eigene Zahl- und Datumsformate | **nicht** | Gehen bereits beim Umschreiben nach JSON verloren; der Parser sieht sie nie. Genannt im Plan. |

---

## 6. Rückweg ins Clientformat (Export)

Verlustfrei heißt: **kein einziger Befund**. Alles andere wird benannt (`BFR-IMP-0201`).

| Aus dem Gateway | Claude | Cursor | VS Code | Codex |
| --- | --- | --- | --- | --- |
| Format des Ergebnisses | JSON (`mcpServers`) | JSON (`mcpServers`) | JSON (`servers`) | **TOML** (`[mcp_servers.…]`) |
| stdio: `command`, `args`, `env` | verlustfrei | verlustfrei | verlustfrei | verlustfrei |
| stdio: `WorkingDirectory` | **verlustbehaftet** (kein `cwd` im Schema) | **verlustbehaftet** | verlustfrei (`cwd`) | verlustfrei (`cwd`) |
| HTTP: `Endpoint`, `Headers` | verlustfrei (`type: "http"`) | verlustfrei (Cursor leitet den Typ aus der Adresse ab) | verlustfrei (`type: "http"`) | verlustfrei (`url`, `http_headers`) |
| `CallTimeout` | **verlustbehaftet** (kein Feld) | **verlustbehaftet** | **verlustbehaftet** | verlustfrei (`tool_timeout_sec`) |
| `Http.OAuth` | **verlustbehaftet** | **verlustbehaftet** | **verlustbehaftet** | **verlustbehaftet** |
| `AllowLegacySse` | wird nicht geschrieben | wird nicht geschrieben | wird nicht geschrieben | wird nicht geschrieben |
| CLI-, WASI-, OpenAPI- und OpenRPC-Upstreams | **nicht möglich** (Fehler) | **nicht möglich** | **nicht möglich** | **nicht möglich** |

`AllowLegacySse` beschreibt, ob **dieses Gateway** auf den abgelösten SSE-Transport zurückfällt. Das
ist eine Eigenschaft seiner Verbindung und keine Angabe über den Server; `"type": "sse"` in eine
fremde Konfiguration zu schreiben hieße, dem Zielclient den alten Transport vorzuschreiben.

**Der Codex-Rückweg ist bewusst asymmetrisch:** Er schreibt TOML, weil das Codex' Format ist — und
genau dieses Ergebnis liest der Importer oben nicht wieder ein. Ein JSON-Ausschnitt wäre eine Datei,
die Codex nicht lädt; er sähe nur so aus, als hätte er geholfen.

---

## 7. Herkunft der Beispielkonfigurationen

Unter `tests/Bifrost.Core.Tests/Importing/Fixtures/<client>/`. Jede Datei trägt im Kopf, woher ihr
Aufbau stammt und was daran nachgebildet ist.

| Client | belegt | nachgebildet |
| --- | --- | --- |
| Claude | Sammelname `mcpServers`, Typen `stdio`/`http`/`sse`, Ersetzung `${VAR}` und `${VAR:-vorgabe}` in `command`, `args`, `env`, `url`, `headers`; Einstellungsschlüssel `enabledMcpjsonServers`, `disabledMcpjsonServers`, `enableAllProjectMcpServers`; `projects`-Karte; `globalShortcut` (Desktop) | Servernamen, Pfade und Werte; die übrigen Felder unter einem Projekt (`allowedTools`, `history`) |
| Cursor | Sammelname `mcpServers`; `command`, `args`, `env`, `url`, `headers`, `type`, `envFile`, `disabled`, `auth` mit `CLIENT_ID`/`CLIENT_SECRET`/`scopes`; die sechs Ersetzungsformen | Servernamen, Pfade und Werte |
| VS Code | `servers`, `inputs` (mit `type`/`id`/`description`/`password`), `sandbox` mit `filesystem`/`network`, Serverfelder `type`, `command`, `args`, `cwd`, `env`, `envFile`, `dev`, `sandboxEnabled`, `url`, `headers`, `oauth`; Block `mcp` in `settings.json`; das `inputs`-Beispiel stammt aus der Dokumentation | Servernamen, Pfade, die Editoreinstellungen daneben |
| Codex | Abschnitt `[mcp_servers.<id>]` mit `command`, `args`, `env`, `cwd`, `startup_timeout_sec`, `tool_timeout_sec`, `enabled`, `url`, `bearer_token_env_var`, `http_headers` | **die Schreibweise selbst**: Alle Codex-`*.json`-Beispiele sind JSON-Umschriften der dokumentierten TOML-Form. `codex/04-echtes-toml.toml` ist die Ausnahme — echtes TOML, absichtlich mit dieser Endung, damit die Sammeltests (`*.json`) sie nicht aufgreifen. |

Keine Datei enthält ein echtes Zugangsdatum. Wo ein Klartextgeheimnis gezeigt wird, steht dort ein
sprechender Beispielwert — echt aussehende Tokenformen (`ghp_…`, `sk-…`) stehen bewusst **nicht** in
den Fixtures, damit die Geheimnissuche der Lieferkette nicht auf einen Testwert anschlägt.

---

## 8. Was nicht geht — und was das für den Import-Endpunkt heißt

1. **Codex' echtes Format (TOML) erreicht diesen Weg nicht — und das bleibt so.**
   `IConfigurationImporter.Plan` nimmt ein JSON-Dokument; `ConfigurationImporter` weist alles andere
   vorher ab.

   **Die Entscheidung und ihre Begründung.** Zur Wahl stand, den Importer die Parser *vor* der
   JSON-Prüfung fragen zu lassen, ob einer das Dokument beansprucht — dann könnte ein Parser ein
   eigenes Format lesen. **Das ändert an dieser Zeile nichts:** Ohne TOML-Leser könnte kein Parser
   eine `config.toml` beanspruchen, und ein TOML-Leser ist hier nicht vorhanden. Ihn zu schreiben
   hieße, einen Parser für ein Format mit Datumsliteralen, mehrzeiligen Zeichenketten und
   Punkt-Schlüsseln in genau den Weg zu setzen, über den fremde Dateien hereinkommen; ihn zu ziehen
   hieße, eine neue Abhängigkeit an derselben Stelle. Gekostet hätte die Umstellung dagegen etwas
   Reales: Die Meldung „kaputtes JSON" entstünde erst, wenn sich niemand zuständig fühlt, und wäre
   damit unschärfer als heute.

   **Also: TOML bleibt draußen, und die Absage wird deutlicher.** `BFR-IMP-0006` sagt „das ist TOML,
   dieser Weg liest JSON, so schreibst du es um" statt „Syntaxfehler in Zeile 1". Belegt durch
   `ConfigurationImporterTests.Eine_echte_config_toml_wird_als_toml_abgewiesen_und_nicht_als_kaputtes_json`
   gegen die Beispieldatei `codex/04-echtes-toml.toml` — die einzige Fixture des Repos, die
   absichtlich kein JSON ist.

   Wer `codex` wirklich unterstützen will, braucht **beides**: einen TOML-Leser (neue Abhängigkeit
   oder eigener Parser — beides ist eine Entscheidung, die nicht in diesem Paket fällt) und die
   Umstellung der Formaterkennung. Das ist gemeldet und **nicht umgangen**.
2. **Der Quellpfad ist eine Angabe über die Herkunft, kein Leseauftrag.** Kein Parser öffnet ihn;
   `originPath` landet unverändert in `ImportSource.OriginPath`. Ein `envFile` wird ebenfalls nicht
   gelesen. Ein Gateway, das den in einer fremden Konfiguration genannten Pfad selbst ausliest, wäre
   ein Weg, beliebige Dateien des Rechners über eine Remote-API zu lesen.
3. **Kein Parser liefert eine eingeschaltete Konfiguration.** Das steht je Parser unter Test und
   nicht nur zentral in `ImportNormalization`.
4. **Zugangsdaten in `env` und `headers` ordnet die vorhandene zentrale Erkennung ein**
   (`ImportRiskScanner` + `ImportSecretDetection`); die Parser bauen sie nicht nach. Sie ergänzen
   ausschließlich die Stellen, die der zentrale Weg **nicht sehen kann**, weil sie gar nicht
   übernommen werden: Cursors `auth.CLIENT_SECRET`, Codex' `bearer_token_env_var`, VS Codes
   `${input:…}` mit `password: true` und jedes `envFile`.
5. **Eine Verweisform gilt auch dann als Verweis, wenn sie nicht der ganze Wert ist.** Bis WP4.3
   erkannte `ImportSecretDetection.LooksMasked` `${…}`, `$FOO` und `%FOO%` nur als *vollständigen*
   Wert — `"Authorization": "Bearer ${env:TOKEN}"`, also die Form, in der ein Autorisierungsheader
   tatsächlich geschrieben wird, galt damit als Klartextgeheimnis. Der Irrtum ging in die sichere
   Richtung, war aber ein Falschpositiv, und Falschpositive in einer Liste, die ein Mensch
   durchgehen soll, kosten genau die Aufmerksamkeit, die die echten Funde bräuchten.

   Die Grenze steht dort, wo es teuer würde: Als maskiert gilt nur, was nach dem Entfernen aller
   Verweisformen **nichts Wertartiges** übrig lässt. `Bearer ${env:TOKEN}` hinterlässt das
   Schemawort `Bearer` und damit nichts; `sk-abc${SUFFIX}` hinterlässt `sk-abc` und bleibt ein
   Klartextfund — ein halbes Geheimnis als „maskiert" abzustempeln wäre der Irrtum in die andere,
   teurere Richtung.
