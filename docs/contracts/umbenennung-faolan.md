# Vertrag: Umbenennung auf Faolan

**Stand:** 2026-11-23 · **Status:** Abgenommen vom Nutzer am 2026-11-16, Nachtrag (Container-Etiketten bleiben, UI-Cookies mit Bruch, Postgres-Volume über Variable, Volume-Messung) abgenommen am 2026-11-22; zweiter Nachtrag (Cache-Kennung und Sperrschlüssel bleiben) abgenommen am 2026-11-22; dritter Nachtrag (WASI-Hostname als Brücke, Vorgaben des Images) abgenommen am 2026-11-22; vierter Nachtrag (Befunde des zweiten Reviews) abgenommen am 2026-11-22; fünfter Nachtrag (Befunde des vierten Reviews) abgenommen am 2026-11-23 · **Gilt für:** Plan 0004, R1 – R7
**Grundlage:** [ADR-0026](../adr/0026-umbenennung-auf-faolan.md),
[Plan 0004](../plans/0004-umbenennung-und-rueckkanal-projektion.md) Abschnitt 1.2

Dieser Vertrag legt **je Bezeichner** fest, was mit ihm geschieht. Weicht die Umsetzung ab, wird
zuerst diese Tabelle geändert und erneut abgenommen.

## 1. Die drei Klassen

| Klasse | Regel | Prüffrage |
|---|---|---|
| **Umbenennen** | alter Name verschwindet | Hat ihn nie jemand außerhalb des Repos geschrieben? |
| **Brücke** | neuer Name gilt; der alte wird weiter gelesen und beim Lesen gemeldet | Hat ein Betreiber oder ein fremdes System ihn geschrieben? |
| **Bleibt** | unverändert, auf Dauer | Ist er Teil einer Schlüsselableitung, eines versionierten Formats oder eine undurchsichtige Kennung? |

**Für jede Brücke gilt:** Der neueste gesetzte Name gewinnt. Jede Übernahme eines alten Namens
erzeugt eine Warnung beim Start und erscheint in `faolan doctor`. Umbenannt oder verschoben wird auf
dem Datenträger des Betreibers **nichts**.

## 2. Bindende Zuordnung

### 2.1 Quelltext und Build — Umbenennen

| Alt | Neu |
|---|---|
| `Bifrost.slnx` | `Faolan.slnx` |
| Ordner und Projekte `src/Bifrost.*`, `tests/Bifrost.*`, `tests/**/Bifrost.*` (25 Projekte) | `Faolan.*` |
| Namensräume `Bifrost.*` | `Faolan.*` |
| `BifrostDbContext`, `BifrostDbOptions`, `BifrostProductInfo`, `UseBifrostDatabase` | `FaolanDbContext`, `FaolanDbOptions`, `FaolanProductInfo`, `UseFaolanDatabase` |
| Migrations-Assemblies `Bifrost.Persistence.Migrations.{Sqlite,Postgres}` | `Faolan.Persistence.Migrations.{Sqlite,Postgres}` |
| `Authors`, `Product` in `Directory.Build.props` | `Faolan Project`, `Faolan` |
| Logger-Kategorien `Bifrost.Server.*` | `Faolan.Server.*` |
| Rust-Crate `bifrost-wasi-component-spike` | `faolan-wasi-component-spike` |
| Testschalter `BIFROST_RUN_*`, `BIFROST_REQUIRE_*`, `BIFROST_UPDATE_*`, `BIFROST_PERF_*`, `BIFROST_SOAK_*`, `BIFROST_E2E_*` | `FAOLAN_*`, **ohne Brücke** — Workflows und Tests wechseln im selben Commit |
| Testdaten `bifrost-test-realm.json` und Verwandte | `faolan-*` |

Die **Migrations-Ids bleiben**, wie sie sind: Sie stehen in `__EFMigrationsHistory` jeder
bestehenden Datenbank und tragen den Produktnamen nicht. Geändert werden Namensraum und
`[DbContext(typeof(…))]` der Migrationsklassen, nicht ihre `[Migration("…")]`-Kennung.

### 2.2 Was der Betreiber setzt — Brücke

| Alt | Neu | Brücke |
|---|---|---|
| alle Betriebsvariablen `BIFROST_*` (Liste in 2.6) samt `_FILE`-Varianten | `FAOLAN_*` | Kette `MCPMCP_` → `BIFROST_` → `FAOLAN_`, vor dem Bau der Konfiguration |
| CLI-Variablen `BIFROST_ENDPOINT`, `_TOKEN`, `_CONFIG`, `_IDENTITY` | `FAOLAN_*` | dieselbe Kette, im CLI-Prozess |
| Datenbankdatei `bifrost.db` | `faolan.db` für Neuinstallationen | vorhandene Datei gewinnt: `faolan.db`, sonst `bifrost.db`, sonst `mcpmcp.db` |
| Compose-Variablen `BIFROST_IMAGE`, `BIFROST_VERSION`, `BIFROST_HTTP_PORT` | `FAOLAN_*` | keine technische — Compose kennt keine Kette; steht in der Upgrade-Anleitung |

**Die Kette steht an einer Stelle.** `LegacyEnvironment` wird von der Sonderregel für ein Präfix zu
einer geordneten Liste von Generationen. Das CLI-Projekt hat keine Projektverweise; es bindet
dieselbe Quelldatei per `<Compile Include>` ein, statt die Regel ein zweites Mal zu schreiben.

**Vorgaben des Images** (dritter Nachtrag 2026-11-22, Review-Befund H2). Das Image setzt
`FAOLAN_DATA_DIR` und `FAOLAN_WASI_HOST` selbst. Nach „der neueste Name gewinnt" verdeckte diese
Vorgabe ein `BIFROST_DATA_DIR` des Betreibers still — bis 0.12.0 war es umgekehrt. Deshalb nennt das
Image seine Vorgaben in `FAOLAN_IMAGE_DEFAULTS` (`NAME=Wert;…`). Trägt ein dort genannter Name genau
den Vorgabewert und ist ein alter Name mit **anderem** Wert gesetzt, bricht der Start ab (Exit-Code 2)
und nennt beide Variablen. Gleicht der alte Wert der Vorgabe, oder trägt der neue Name einen anderen
Wert, gilt die gewöhnliche Regel. Ein Test hält Dockerfile und Deklaration übereinander.
Ergänzt nach dem zweiten Review (vierter Nachtrag 2026-11-22): Die Datei-Variante
(`BIFROST_DATA_DIR_FILE`) ist dieselbe Einstellung, ihr Inhalt wird ebenso verglichen; eine
unlesbare Datei und eine unlesbare Deklaration brechen den Start ebenfalls ab. Die Meldung nennt nur
Namen, nie Werte. Nennt der alte Wert genau den bis 0.12.0 mitgelieferten Pfad
`/usr/local/bin/bifrost-wasi-host`, rät sie zum Entfernen der alten Variable statt zum Umbenennen;
jeder andere Pfad gilt als eigenes Programm. Nach dem dritten Review außerdem: Sind direkter Wert und
`_FILE` derselben alten Generation beide gesetzt, bricht der Start ab. Die Deklaration wird streng
gelesen — jeder Name nur aus `FAOLAN_` plus `A–Z`, `0–9`, `_`, und eine gesetzte Deklaration ohne
gültigen Eintrag gilt als kaputt.
Nach dem vierten Review (fünfter Nachtrag 2026-11-23): Auch eine ausdrücklich leer gesetzte
Deklaration (`FAOLAN_IMAGE_DEFAULTS=`) gilt als kaputt, sonst schaltete sie die Prüfung still ab.
Steht der alte Wert nicht fest (unlesbare Datei, direkter Wert und Datei zugleich), rät die Meldung
beim WASI-Host zuerst zum Entfernen der alten Variablen, falls sie den mitgelieferten Pfad nannten.
„Nur direkt setzen, nicht als `…_FILE`" gilt allein für `FAOLAN_DATA_DIR` und `FAOLAN_WASI_HOST`;
für jede andere Vorgabe, etwa ein Geheimnis eines abgeleiteten Images, bleibt `…_FILE` der Rat.

### 2.3 Was auf dem Draht oder in Dateien steht — Brücke

| Bezeichner | Lesen | Schreiben |
|---|---|---|
| Connector-Manifest `bifrost.connector.v1` | beide Schema-Ids | `faolan.connector.v1` |
| Asset-URI `bifrost://assets/` | beide Präfixe | `faolan://assets/` |
| Secret-Referenz `${bifrost:secret/…}` | beide Präfixe | `${faolan:secret/…}` |
| Webhook eingehend: `X-Bifrost-Signature`, `X-Bifrost-Timestamp` | beide Paare; liegt das neue vor, zählt nur das neue | — |
| Benachrichtigung ausgehend: dieselben zwei Header | — | **beide Paare mit gleichem Wert**, bis die Brücke entfällt |
| `X-Bifrost-Setup-Token` eingehend | beide Namen | — |
| `X-Bifrost-Instance` | beide Namen bei der Schleifenerkennung | `X-Faolan-Instance` |
| `Wasi.HostExecutable` gespeicherter Upstreams: `bifrost-wasi-host`, `/usr/local/bin/bifrost-wasi-host` | der alte Name bezeichnet den Host `faolan-wasi-host` derselben Instanz, als Dateiname oder als Pfad im selben Verzeichnis; gestartet wird nur der Host des Betreibers (dritter Nachtrag 2026-11-22, Review-Befund H1). Groß- und Kleinschreibung wie im Dateisystem; Verbindungsdiagnose und Connector teilen die Regel (`WasiHostNames`): erst genau vergleichen, nur bei Bedarf übersetzen — ein eigenes Programm, das wirklich so heißt, bleibt gemeint. Die Übernahme meldet der Start und `doctor` als `BFR-UP-0002`, und nur dann, wenn die Brücke wirklich gebraucht wird (vierter Nachtrag) — gemessen am Host der Instanz; ohne eingerichteten `FAOLAN_WASI_HOST` startet nichts, das ist keine Brücke. Die Verbindungsdiagnose prüft das Programm, das der Connector startet: den Host der Instanz, wenn der Verweis ihn nennt, sonst meldet sie in Stufe 3, dass er es nicht tut (fünfter Nachtrag 2026-11-23) | was der Betreiber einträgt; die Doku nennt `faolan-wasi-host` |

Die ausgehende Benachrichtigung ist der eine Fall, in dem **geschrieben** wird, was ein fremdes
System liest: Ein Empfänger, der heute `X-Bifrost-Signature` prüft, bekäme sonst über Nacht
unsignierte Nachrichten. Bei Webhooks eingehend gilt: Liefert ein Absender beide Paare mit
verschiedenen Werten, wird ausschließlich das neue geprüft — nie „eines von beiden passt".

### 2.4 Bleibt

| Bezeichner | Fundstelle | Warum |
|---|---|---|
| `MCPMCP`, `McpMcp.UpstreamConfig.v1`, `McpMcp.UpstreamOAuthToken.v1`, `McpMcp.Webhook.Secret.v1` | `CryptographicNames.cs` | gehen in die Schlüsselableitung ein |
| `Bifrost.Server.ApprovalElicitation.RequestState.v1` | `ApprovalElicitation.cs:57` | ebenso; wandert als Konstante nach `CryptographicNames` und unter dessen Test |
| Archiveinträge `database/bifrost.db`, `database/bifrost.dump` | `BackupLayout.cs:28,40` | Formatvertrag der Archivfassung 1; ein neuer Name wäre eine neue Formatfassung ohne Nutzen |
| Tabelle `__BifrostMigrationJournal` | `MigrationJournal.cs:65` | steht in jeder bestehenden Datenbank |
| Diagnosecodes `BFR-*` | `DiagnosticCodes.cs` | undurchsichtige Kennungen in Doku, Support-Bundles und Runbooks |
| WIT-Paket `mcpmcp` | Connector-ABI, vier `.wasm`-Fixtures | in ausgelieferte Komponenten einkompiliert |
| Subjekt `CN=bifrost-keyring` in **bestehenden** Zertifikaten | Datenträger des Betreibers | wird nirgends abgeglichen; neu erzeugte bekommen `CN=faolan-keyring` |
| Container-Etiketten `de.bifrost.owner` = `bifrost-gateway`, `de.bifrost.instance`, `de.bifrost.upstream`, Namenspräfix `bifrost-` | `ContainerIdentity.cs` | danach räumt der Gateway verwaiste Upstream-Container auf; mit neuem Namen fände er die aus 0.12.0 nicht mehr (Nachtrag 2026-11-22) |
| Kennung `BIFROSTC` am Anfang jeder Cache-Datei | `spikes/wasi-component-runtime/src/disk_cache.rs` (`MAGIC`) | Dateiformat des Kompilat-Caches; ein neuer Wert machte jeden vorhandenen Cache unlesbar (Nachtrag 2026-11-22, zweiter Teil) |
| Schlüssel `0x424946524F535401` der PostgreSQL-Migrationssperre (ASCII „BIFROST“ + 1) | `MigrationLock.cs` | zwei Fassungen während eines Upgrades müssen dieselbe Sperre nehmen; ein neuer Schlüssel liesse beide gleichzeitig migrieren (Nachtrag 2026-11-22, zweiter Teil) |
| Marke `_bifrost_truncated` | `Invocation.cs:75` | bleibt in Teil R unangetastet und entfällt erst mit dem [Projektionsvertrag](rueckkanal-projektion.md) — ein Bruch statt zwei |
| CHANGELOG bis 0.12.0, ADR-0001 bis 0025, `docs/acceptance/*`, `docs/produktreife/BIFROST_*`, ältere Pläne, `gitleaks-baseline.json` | — | Geschichte; der Name war damals richtig |

### 2.5 Betreiber-Artefakte — Umbenennen, mit Bruch

| Alt | Neu | Bruch |
|---|---|---|
| CLI `bifrost`, Paket `Bifrost.Cli` | `faolan`, `Faolan.Cli` | Skripte des Betreibers |
| `bifrost-connector`, Paket `Bifrost.ConnectorKit` | `faolan-connector`, `Faolan.ConnectorKit` | Build-Skripte von Paketautoren |
| `bifrost-wasi-host`, `/usr/local/bin/bifrost-wasi-host` | `faolan-wasi-host` | nur bei selbst gesetztem `…_WASI_HOST`; gespeicherte Upstreams mit dem alten Namen laufen weiter (§2.3) |
| Einstieg `Bifrost.Server.dll` | `Faolan.Server.dll` | eigene Healthchecks und Startbefehle |
| Image `ghcr.io/lupusmalusdeviant/bifrost` | `ghcr.io/lupusmalusdeviant/faolan` | Image-Referenz; alte Tags bleiben abrufbar |
| Compose-Dienst `bifrost`, Volume `bifrost-data` | Dienst `faolan`, Volume über `FAOLAN_DATA_VOLUME` | **siehe Abschnitt 3** |
| Meter und ActivitySource `Bifrost.Gateway`; Instrumente `bifrost.tool_calls`, `bifrost.tool_call_duration`; Spans und Attribute `bifrost.*` | `Faolan.Gateway`, `faolan.*` | Dashboards und Alarme, **keine Doppelausgabe** |
| `serverInfo.name = "bifrost"`, Server-Instructions | `faolan`, „Faolan gateway: …" | Clients, die den Servernamen anzeigen oder auswerten |
| Vorgabename in `ClientConfigSnippets` | `faolan` | nur neu erzeugte Snippets |
| Dateinamen `bifrost-audit.*`, `bifrost-support-*.zip`, temporäre Präfixe | `faolan-*` | keiner |
| UI-Cookies `bifrost-ui`, `bifrost-ui-mode`, `bifrost-setup`, Claim `bifrost-ui-role` | `faolan-*` | einmal neu anmelden; ein laufendes Setup verliert seinen Griff (Nachtrag 2026-11-22) |

### 2.6 Die Betriebsvariablen im Einzelnen

Aus `src/`, `Dockerfile`, den Compose-Dateien und `.env.example` erhoben; jede bekommt denselben
Namen mit dem Präfix `FAOLAN_`:

`ALLOW_HOST_EXECUTION`, `APPROVAL_WEBHOOK_ALLOW_PRIVATE`, `APPROVAL_WEBHOOK_SECRET`,
`APPROVAL_WEBHOOK_URL`, `AUDIT_DEBUG_PAYLOADS`, `AUDIT_MODE`, `AUDIT_RETENTION_DAYS`, `BACKUP_DIR`,
`BACKUP_PASSPHRASE`, `BOOTSTRAP_TTL_MINUTES`, `CONFIG`, `DATA_DIR`, `DB_CONNECTION`, `DB_PROVIDER`,
`ENDPOINT`, `GUARD_ALLOW_CUSTOM_PATTERNS`, `GUARD_ENABLED`, `GUARD_MAX_SCAN_CHARS`, `HTTP_PORT`,
`IDENTITY`, `IMAGE`, `KEYRING_CERT_PASSWORD`, `KEYRING_CERT_PASSWORD_PREVIOUS`, `KEYRING_CERT_PATH`,
`KEYRING_CERT_PATH_PREVIOUS`, `KEYRING_PROTECTION`, `MAX_RESULT_CHARS`, `MCP_LIST_TTL_SECONDS`,
`MCP_STATELESS`, `OAUTH_AUDIENCE`, `OAUTH_ISSUER`, `POSTGRES_BIN`, `PUBLIC_BASE_URL`,
`SQLITE_JOURNAL_MODE`, `TOKEN`, `TRUSTED_PROXIES`, `VERSION`.

Die Kette arbeitet auf dem Präfix, nicht auf dieser Liste — eine Variable, die hier fehlt, wird
trotzdem übernommen. Die Liste ist der Maßstab für die Dokumentation.

## 3. Das Volume

Das ist die eine Stelle, an der der Prozess den Fehler nicht selbst erkennen kann: Hängt Compose
statt `bifrost-data` ein frisches `faolan-data` ein, sieht der Gateway ein leeres Verzeichnis und
ist im Recht, eine neue Instanz anzulegen.

- Das mitgelieferte Compose benennt das Volume über `FAOLAN_DATA_VOLUME`, Vorgabe `faolan-data`.
- Die Upgrade-Anleitung nennt den Wert, den eine Bestandsinstallation setzt. Der wirksame Name
  eines bisherigen Volumes trägt den Compose-Projektnamen als Präfix (`<projekt>_bifrost-data`);
  die Anleitung zeigt den Befehl, mit dem man ihn abliest.
- **Nachzumessen in R4, bevor es in der Anleitung steht:** wie Compose reagiert, wenn ein mit `name:`
  angegebenes Volume von einem anderen Projekt angelegt wurde (Warnung oder Abbruch, und ob
  `external: true` nötig ist). `[braucht Nutzer]` für den Lauf gegen die echte Instanz.
- **Gemessen am 2026-11-22** (Docker 29.9.3, Compose v5.7.0, Wegwerf-Volumes):
  - Gleicher Projektname, `FAOLAN_DATA_VOLUME=<projekt>_bifrost-data`: Das alte Volume wird ohne
    Warnung eingehängt, die Daten sind da.
  - Anderer Projektname: Das Volume wird ebenfalls eingehängt, die Daten sind da. Compose warnt nur
    (`already exists but was created for project …`) und bricht nicht ab.
  - `external: true` ist **nicht** nötig. Es unterdrückt die Warnung, verlangt aber bei jeder
    Neuinstallation ein vorher angelegtes Volume und taugt deshalb nicht als Vorgabe.
  - Ohne die Variable entsteht still ein leeres `faolan-data`. Genau diesen Fall fängt nur `doctor`
    (`BFR-DB-0008`: kein Upstream, keine Rolle, kein API-Schlüssel). Die Rolle `ui-internal-admin`,
    die der Gateway bei jedem Start selbst anlegt, zählt dabei nicht mit (fünfter Nachtrag
    2026-11-23) — sonst wäre keine gestartete Instanz je leer.
- **Postgres-Datei** (Nachtrag 2026-11-22): `docker-compose.postgres.yml` benennt ihr Volume ebenso
  über `FAOLAN_PGDATA_VOLUME`, Vorgabe `faolan-pgdata`. Ein Bestand setzt
  `<projekt>_bifrost-pgdata`. Die Vorgaben für `POSTGRES_DB` und `POSTGRES_USER` bleiben `bifrost`:
  Sie stehen in jeder bestehenden Datenbank, und mit gesetztem Volume passt der Bestand sofort.
- `doctor` meldet eine Instanz ohne Upstreams, ohne Rollen und ohne Schlüssel als das, was sie ist —
  mit dem Hinweis auf diese Stelle. Das ersetzt die Anleitung nicht, fängt aber den Fall, dass sie
  niemand gelesen hat.

## 4. Was sich für Betreiber ändert

Diese Liste geht wörtlich in den CHANGELOG und in `docs/upgrade-faolan.md`.

**Läuft von selbst weiter:** Variablen `BIFROST_*` und `MCPMCP_*`, Datenbankdatei, Schlüsselring und
alle verschlüsselten Zugangsdaten, Sicherungen und Exporte aus 0.12.0, installierte Connector-Pakete,
eingehende Webhooks, Empfänger von Freigabe-Benachrichtigungen.

**Einmal anzufassen:**
1. Image-Referenz auf `ghcr.io/lupusmalusdeviant/faolan`.
2. `FAOLAN_DATA_VOLUME` auf das bestehende Volume, mit PostgreSQL auch `FAOLAN_PGDATA_VOLUME`.
3. Dashboards und Alarme von `bifrost.*` auf `faolan.*`.
4. Eigene Skripte, die `bifrost`, `bifrost-connector` oder `Bifrost.Server.dll` aufrufen.
5. Log-Filter auf `Bifrost.Server.*`.
6. In der Weboberfläche einmal neu anmelden.

**Empfohlen, nicht nötig:** Variablen auf `FAOLAN_*` umstellen. Die Übernahme ist eine
Übergangshilfe und keine Zusage auf Dauer.

## 5. Ein Befund am Rand

Mehrere Meldungen verweisen auf einen Befehl `bifrost-server` (`OperationsCli.cs:166`,
`KeyRingStartup.cs:173`). In `Dockerfile`, im Server-Projekt und in `docs/operations.md` findet sich
kein solches Programm; der Einstieg ist `dotnet Bifrost.Server.dll`. R4 klärt, ob ein Wrapper
ausgeliefert oder die Meldung berichtigt wird — umbenannt wird nichts, was es nicht gibt.

## 6. Reihenfolge und Abgrenzung der Commits

1. **Mechanisch** — nur Klasse „Umbenennen" aus 2.1. Kein Verhalten ändert sich. Nachweis: dieselbe
   Zahl bestandener Tests wie vorher, je Testprojekt.
2. **Brücken** — 2.2 und 2.3, jede mit ihrem Test.
3. **Betreiber-Artefakte** — 2.5, Compose, Workflows, `.env.example`.
4. **Dokumentation** — Upgrade-Anleitung, README, Betriebsdoku, CHANGELOG.

Vor jedem Push: Codex-Review. Nach jedem Push: den CI-Lauf bis zum Ende ansehen.

Die Versionsnummer und der Zeitpunkt eines Releases sind nicht Teil dieses Vertrags; beides legt der
Nutzer fest.

## 7. Nachweise

Ein Paket gilt als fertig, wenn ein Lauf über **alle** Testprojekte der Solution vorliegt, geprüft
wie in der CI.

| Was | Test |
|---|---|
| Kette über drei Generationen, neuester Name gewinnt, `_FILE` inbegriffen | `LegacyEnvironmentTests` (erweitert) |
| dieselbe Kette im CLI-Prozess | `Faolan.Cli.Tests` (neu) |
| Dateiwahl `faolan.db` / `bifrost.db` / `mcpmcp.db` | `LegacyEnvironmentTests` |
| Kein kryptografischer Name geändert, vierter Zweck inbegriffen | `CryptographicNamesTests` |
| Instanz mit `BIFROST_*`, `bifrost.db` und 0.12.0-Schlüsselring startet und entschlüsselt | `Faolan.Upgrade.Tests` (neu) |
| dasselbe mit `MCPMCP_*` und `mcpmcp.db` | `Faolan.Upgrade.Tests` (neu) |
| Archiv aus 0.12.0 lässt sich zurückspielen | `BackupRestoreUpgradeTests` (erweitert) |
| Connector-Paket mit `bifrost.connector.v1` installiert sich; neu gebautes trägt die neue Id | Packaging-Tests (erweitert) |
| Konfigurationsexport mit `${bifrost:secret/…}` importiert | Export-Tests (erweitert) |
| Webhook mit altem, mit neuem und mit widersprüchlichem Headerpaar | Webhook-Tests (erweitert) |
| Benachrichtigung trägt beide Headerpaare mit gleichem Wert | Dispatcher-Tests (erweitert) |
| Jede Betriebsvariable ist dokumentiert | `Every_environment_option_is_documented…` (Muster auf `FAOLAN_`) |
| Im Quelltext steht `Bifrost` nur noch an den Stellen aus 2.4 | Governance-Test (neu), mit ausdrücklicher Erlaubnisliste |

Der letzte Test ist der, der die nächste Umbenennung billiger macht: Er hält fest, wo ein alter Name
stehen **darf**, und wird rot, sobald er woanders auftaucht.

## 8. Außerhalb des Repos `[braucht Nutzer]`

Markenregister (DPMA, EUIPO) prüfen · Repos auf GitHub und GitLab umbenennen · GHCR-Paket ·
Instanz auf Badwolf nach Abschnitt 4 · Connector-Namen in den MCP-Clients. Befehle und Reihenfolge
kommen mit der Upgrade-Anleitung.
