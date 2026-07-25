# Betrieb — MCP-MCP Gateway

Praxisleitfaden zum Deployment und Betrieb. Zielgruppe: Self-hosted Single-Operator (ADR-0001).

## Schnellstart (Docker)

```bash
docker compose up -d          # SQLite-Default, ein Volume
docker compose logs mcpmcp    # Bootstrap-Zugangsdaten NUR beim Erststart ablesen
```

Beim **Erststart** legt der Gateway zwei Zugänge an und loggt sie **genau einmal** (Henne-Ei — danach nie wieder):

```
ERSTSTART: Bootstrap-Admin (Agent) angelegt. API-Key (wird NIE wieder angezeigt): mcpk_...
ERSTSTART: UI-Admin 'admin' angelegt. Passwort (wird NIE wieder angezeigt): ...
```

- **API-Key** → für Agenten (Claude Code, MCP Inspector) und die REST-Fassade.
- **UI-Passwort** → Login der Web-UI (`http://localhost:8080`, Benutzer `admin`).

Beide Werte sofort sichern. Verloren? Siehe [Zugang zurücksetzen](#zugang-zurücksetzen).

## Konfiguration (Env-Vars)

| Variable | Default | Zweck |
|---|---|---|
| `MCPMCP_DATA_DIR` | `data` (bzw. `/data` im Container) | Verzeichnis für SQLite-DB **und** DataProtection-Key-Ring |
| `MCPMCP_DB_PROVIDER` | `sqlite` | `sqlite` oder `postgres` |
| `MCPMCP_DB_CONNECTION` | `Data Source=<datadir>/mcpmcp.db` | Connection-String (bei Postgres Pflicht) |
| `MCPMCP_AUDIT_MODE` | `best-effort` | `best-effort` verwirft bei Überlast gezählt; `compliance` meldet Überlast explizit und retryt DB-Fehler mit Backpressure |
| `ASPNETCORE_URLS` | `http://+:8080` (Container) | Bind-Adresse/Port |
| `MCPMCP_KEYRING_CERT_PATH` | *(nicht gesetzt)* | PFX-Zertifikat zum Verschlüsseln des Key-Rings (siehe [Key-Ring schützen](#key-ring-schützen)) |
| `MCPMCP_KEYRING_CERT_PASSWORD` | *(nicht gesetzt)* | Passwort des PFX |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(nicht gesetzt)* | Ziel für den Metriken-Export (siehe [Metriken](#metriken)) |
| `MCPMCP_AUDIT_DEBUG_PAYLOADS` | *(aus)* | `1`/`true` schaltet den Debug-Modus des Audits ein (siehe [Audit-Debug-Modus](#audit-debug-modus)) |
| `MCPMCP_AUDIT_RETENTION_DAYS` | `30` | Aufbewahrung der Audit-Ereignisse in Tagen; ältere werden täglich gelöscht (FR-25) |
| `MCPMCP_MAX_RESULT_CHARS` | *(aus)* | Kürzt Tool-Ergebnisse oberhalb dieser Zeichenzahl (FR-16, siehe [Ergebnis-Kompression](#ergebnis-kompression)) |
| `MCPMCP_GUARD_ENABLED` | `1` | `0`/`false` schaltet die Secret-Guardrail global ab (Not-Aus) |
| `MCPMCP_GUARD_MAX_SCAN_CHARS` | `262144` | Nutzlasten darüber werden nicht geprüft und **abgewiesen** |
| `MCPMCP_GUARD_ALLOW_CUSTOM_PATTERNS` | *(aus)* | Erlaubt Admins eigene Regex in der UI (siehe [Guardrails](#guardrails)) |

## Guardrails

Der Gateway prüft Tool-**Argumente** und Tool-**Ergebnisse** auf Zugangsdaten
([ADR-0011](adr/0011-secret-erkennung-als-guardrail.md)). Verwaltung unter **Guardrails** in der
Web-UI: Regeln lassen sich pro Stück ein- und ausschalten, zwischen *Blockieren* und *Beobachten*
umstellen und ergänzen — alles zur Laufzeit, ohne Neustart.

Die wichtigere Richtung ist **Ergebnis → Agent**: Ein Tool, das eine `.env`, ein Kubernetes-Secret
oder eine Datenbankzeile liefert, schiebt den Wert sonst ins Kontextfenster des Modells — und von
dort in dessen Logs und Folgeantworten.

### Was beim Blockieren passiert

| Richtung | Verhalten |
|---|---|
| Argumente | Der Aufruf wird **vor** dem Upstream abgebrochen. Kein Seiteneffekt. |
| Ergebnis | Der Aufruf **ist bereits gelaufen**; nur das Ergebnis wird zurückgehalten. |

Der zweite Fall ist der wichtige: Bei einem schreibenden Tool ist die Aktion eingetreten. Die
Fehlermeldung sagt das ausdrücklich und weist darauf hin, den Aufruf **nicht** zu wiederholen —
sonst legt ein Agent dasselbe Issue ein zweites Mal an. Im Audit trägt der Vorgang den eigenen
Status `GuardBlocked` und ist damit von einem RBAC-`Denied` unterscheidbar.

### Grenzen — bitte lesen, bevor man sich darauf verlässt

Erkannt wird, was ein **Muster** hat: `AKIA…`, `ghp_…`, `sk-ant-…`, PEM-Blöcke, Slack-Webhooks.
**Nicht** erkannt wird, was keins hat — ein 32-stelliges Zufallspasswort ist von einer Datei-Id
nicht zu unterscheiden. Entropie-Heuristik ist bewusst nicht eingebaut: Sie schlägt auf
Git-Commit-SHAs und UUIDs praktisch zu 100 % an, und unter „blockieren" wäre jeder Fehlalarm ein
abgebrochener Arbeitsschritt statt einer Logzeile.

Die Guardrail ist damit eine **zusätzliche Schicht**, kein Ersatz dafür, Zugangsdaten aus
Tool-Ergebnissen herauszuhalten.

Zwei weitere Punkte:

- **Befunde enthalten nie den gefundenen Wert.** Protokolliert werden Regel-Id, Fingerabdruck
  (Hash), Position und Länge. Eine Secret-Erkennung, die ihre Funde im Klartext loggt, kopiert
  Secrets in ein zweites und meist schwächer geschütztes System.
- **Über der Prüfgrenze wird abgewiesen**, nicht durchgelassen — sonst wäre die Grenze genau der
  blinde Fleck, den man ansteuert. Wer große Ergebnisse erwartet, kombiniert das mit
  `MCPMCP_MAX_RESULT_CHARS`: Die Kürzung greift vorher, und das gekürzte Ergebnis läuft durch.

### Eigene Regeln

Der **geführte Editor** ist der Normalfall: Präfix, Zeichenart und Längenbereich als Felder,
daraus wird das Muster erzeugt. Das deckt praktisch alle Token-Formate ab.

Freitext-Regex ist standardmäßig **aus** und über `MCPMCP_GUARD_ALLOW_CUSTOM_PATTERNS=1`
einschaltbar. Das ist eine bewusste Vertrauensentscheidung: .NET bietet laut Microsoft keine
Sicherheitsgrenze gegen bösartige Muster — auch die hier verwendete backtracking-freie Engine
schützt gegen teure *Eingaben*, nicht gegen bösartige *Muster*. Wer den Schalter setzt, erlaubt
Admins, Rechenzeit im Gateway-Prozess zu verbrauchen.

Neue eigene Regeln starten immer im Modus **Beobachten**. Erst nach Sichtung der Treffer auf
*Blockieren* stellen — eine Regel scharfzuschalten, die man nie hat feuern sehen, bricht im
Zweifel produktive Arbeit ab.

## Freigabe-Flows (Approval)

Einzelne Tools lassen sich freigabepflichtig machen (FR-32,
[ADR-0012](adr/0012-approval-flows-asynchron.md)): Ein solcher Aufruf wird **nicht** ausgeführt,
sondern **sofort abgewiesen** (`ApprovalRequired`), und eine Anfrage landet in der Queue unter
**Freigaben** in der Web-UI. Ein Mensch (Operator/Admin) sieht dort die konkreten — maskierten —
Argumente und entscheidet.

Nach der Freigabe setzt der Agent **denselben** Aufruf erneut ab; er läuft dann **einmalig** durch.
Die Freigabe bindet an `(Identität, Tool, Argument-Fingerprint)` und verfällt nach einer Stunde:

- Kein hängender Agent — der Timeout aus FR-09 bleibt unberührt, es wird nichts blockierend gewartet.
- Eine Freigabe für `delete_file{path:/tmp/x}` deckt **nicht** `delete_file{path:/etc/passwd}` ab.
- Einmalig: Eine Wiederholung erfordert erneute Freigabe. So wird eine erteilte Zustimmung nicht
  zum Dauerfreifahrtschein.

Welche Tools freigabepflichtig sind, ist unter **Freigaben** (Admin-Bereich) zur Laufzeit
schaltbar, ohne Neustart.

## WASI-Components als Upstream

Ein signiertes WebAssembly-Component läuft in einem eigenen Rust-Host-Prozess
([ADR-0020](adr/0020-wasi-runtime-out-of-process-rust-host.md)). Der Host **liegt im Image** unter
`/usr/local/bin/mcpmcp-wasi-host` — genau dieser Pfad gehört in `Wasi.HostExecutable` eines
WASI-Upstreams.

Vertrauen kommt **ausschließlich** aus dem Publisher-Trust-Store, nicht aus der Upstream-Config:

```bash
curl -X POST http://localhost:8080/api/v1/publishers \
  -H "Authorization: Bearer $ADMIN_KEY" -H 'Content-Type: application/json' \
  -d '{"publicKey":"<Ed25519-Public-Key, Base64, 32 Byte>","label":"acme"}'
```

- Ohne passenden gepinnten Schlüssel lädt der Host nichts — ein Upstream kommt dann gar nicht erst
  hoch (fail-closed). Beim ersten Start nach dem Update werden Schlüssel aus vorhandenen
  `Wasi.PinnedPublishers` einmalig übernommen und danach ignoriert.
- `POST /api/v1/publishers/{keyId}/revoke` wirkt **sofort**: Laufende Upstreams mit Components
  dieses Publishers werden gestoppt und der Vorgang auditiert.
- Grants sind default-deny und werden pro WASI-Interface durchgesetzt. Dateisystem-Preopens sind
  absolute Pfade und werden **nur lesend** eingehängt, Netzwerkziele sind `host:port`.
- Secrets: `Grants.Secrets` nennt die Namen, `Wasi.Secrets` liefert die Werte (verschlüsselt wie
  alle Upstream-Credentials, in Ausgaben maskiert). Der Host injiziert sie als
  Environment-Einträge — wer Secrets gewährt, gewährt damit die Environment-Schnittstelle, das
  Component kann also alle gesetzten Variablen lesen.
- Jeder Load steht im Audit: Modulhash, Publisher, Runtime und die tatsächlich erteilten Grants.

### Kapazität: ein Aufruf pro Upstream gleichzeitig

Der IPC-Vertrag ist strikt request/response, die Verbindung zum Host serialisiert. Ein langsames
Component blockiert damit weitere Aufrufe **desselben** WASI-Upstreams; andere Upstreams sind nicht
betroffen. Der Per-Call-Timeout (FR-09) und Fuel/Epoch-Limits begrenzen, wie lange das dauern kann.

Wer mehrere gleichzeitige Aufrufe eines Components braucht, legt ihn heute als mehrere Upstreams
an — jeder bekommt seinen eigenen Host-Prozess. Echte Nebenläufigkeit in einer Verbindung bräuchte
Korrelations-Ids im Vertrag und wäre damit eine Versionsänderung.

### Platten-Cache für Kompilate

Ohne `Wasi.ModuleCacheDirectory` kompiliert **jeder Host-Start** neu — gemessen rund 2,3 ms je KiB,
bei einem Component von 1–3 MB also 3–7 Sekunden pro Gateway-Neustart oder Hot-Swap. Mit
Verzeichnis (im Image z. B. `/data/wasi-cache`) fällt das auf unter 3 ms:

| | Ladezeit | Kompilierung |
|---|---|---|
| erster Host-Start | 107 ms | 90 ms |
| jeder weitere Start | 0,9–2,6 ms | keine |

Ein Kompilat ist **ausführbarer Maschinencode**, den die Publisher-Signatur nicht abdeckt. Der Host
legt deshalb im Cache-Verzeichnis einen eigenen Schlüssel (`mac.key`, unter Unix `0600`) an und
versieht jeden Eintrag mit einem HMAC darüber; ein Eintrag ohne gültigen MAC wird gelöscht statt
geladen. Daraus folgen zwei Betriebsauflagen:

- Das Verzeichnis **muss** dem Host-Benutzer gehören und darf für andere nicht schreibbar sein.
  Kein geteiltes Volume, kein weltschreibbares Temp-Verzeichnis.
- Der Schlüssel schützt gegen fremden Schreibzugriff und Bitfehler, **nicht** gegen jemanden, der
  als derselbe Benutzer läuft — der liest den Schlüssel und könnte ohnehin das Host-Binary
  austauschen.

**Obergrenzen** halten beides endlich: Im Speicher hält der Host höchstens 8 Kompilate und
verdrängt das am längsten nicht genutzte; auf Platte gilt ein Budget von 256 MiB, über
`Wasi.ModuleCacheMaxBytes` einstellbar (`0` = ausdrücklich unbegrenzt). Verdrängt wird nach
Nutzung, nicht nach Schreibalter — ein Treffer stempelt seinen Eintrag frisch. Ein verdrängtes
Kompilat kostet nur eine erneute Kompilierung. `mac.key` wird beim Aufräumen nie angefasst.

`mac.key` löschen macht alle Einträge ungültig (sie werden verworfen und neu erzeugt) — der Weg,
einen Cache-Verdacht auszuräumen. Im `health`-Signal des Hosts stehen `diskHits` und `diskErrors`;
bleibt `diskHits` bei 0 und `diskErrors` steigt, sind meist die Verzeichnisrechte falsch.

## Webhook-Trigger

Ein eingehender Webhook löst genau **einen** Tool-Aufruf im Namen einer festen Identität aus
(FR-20, [ADR-0013](adr/0013-webhook-trigger.md)). Anlegen unter **Webhooks** (Admin): Name,
Identität und Tool wählen — das Signatur-Secret wird **einmalig** angezeigt.

Der Trigger-Endpunkt ist `POST /webhooks/{id}/trigger` und ist der **einzige unauthentifizierte
Pfad**. Absicherung über HMAC-SHA256:

- Der Absender signiert `{timestamp}.{body}` mit dem Secret und schickt zwei Header:
  `X-McpMcp-Signature: sha256=<hmac>` und `X-McpMcp-Timestamp: <unix-sekunden>`.
- Anfragen älter als **5 Minuten** werden abgewiesen (Replay-Schutz).
- Fehlende, falsche oder abgelaufene Signatur → **401**. Eine unbekannte Webhook-Id liefert
  ebenfalls 401, damit sich keine gültigen Ids durchprobieren lassen.

Der ausgelöste Aufruf durchläuft die **volle Pipeline** — RBAC der gebundenen Identität, Guardrail,
Rate-Limit — und erscheint im Audit mit Herkunft **`Webhook`**. Ein Webhook kann damit nie mehr,
als seine Identität ohnehin darf.

**Grenze:** Ein Webhook löst genau ein Tool aus, keine Kette. Mehrstufige Abläufe sind v2.

## Ergebnis-Kompression

Ein einzelnes umfangreiches Tool-Ergebnis kann die Token-Ersparnis der Profile wieder auffressen.
`MCPMCP_MAX_RESULT_CHARS` begrenzt das:

```
MCPMCP_MAX_RESULT_CHARS=20000
```

Standardmäßig **aus** — Kürzen ist verlustbehaftet, das soll niemand unbemerkt bekommen. Wenn es
greift, bleibt das Ergebnis gültiges JSON und trägt das Feld `_mcpmcp_truncated: true` samt Hinweis,
wie viel fehlt. Bei Listen bleiben die vorderen Einträge erhalten und `totalItems` nennt die
Gesamtzahl; bei einzelnen großen Objekten ist der Ausschnitt ausdrücklich als nicht parsbar
gekennzeichnet. Das Audit hält weiterhin die **ungekürzte** Größe fest, damit man die Kürzung
nachträglich einordnen kann.

## Agenten anbinden (Config-Snippets)

Beim Ausstellen eines API-Keys zeigt die Web-UI unter **RBAC → Keys** fertige
Konfigurations-Snippets für Claude Code und für JSON-basierte MCP-Clients (FR-41) — inklusive
Endpunkt und Authorization-Header. Sie enthalten den Key im Klartext und erscheinen nur einmal,
zusammen mit dem Key selbst.

Läuft die UI hinter einem Reverse-Proxy, prüfe die Adresse im Snippet: sie stammt aus dem
Browser-Aufruf und ist nicht zwingend die, unter der Agenten den Gateway erreichen.

Für Upstream-Server, die noch kein Streamable HTTP sprechen, fällt der Gateway automatisch auf
den abgelösten HTTP+SSE-Transport zurück; abschaltbar je Server über den Schalter im Anlege-Formular
([ADR-0009](adr/0009-sse-legacy-transport.md)). Als **Server** spricht der Gateway ausschließlich
Streamable HTTP — Agenten, die nur SSE können, lassen sich nicht anbinden.

Logs werden außerhalb von `Development` als **JSON** auf stdout geschrieben (NFR-07), damit
Container-Logs ohne Zusatzkonfiguration von einem Aggregator geparst werden können. Lokal bleibt
es beim lesbaren Textformat.

## Audit-Debug-Modus

Standardmäßig schreibt das Audit **keine** Ergebnis-Payloads mit — nur deren Größe in Bytes.
Zur Fehlersuche lässt sich das umschalten:

```
MCPMCP_AUDIT_DEBUG_PAYLOADS=1
```

Dann landet der vollständige Antwort-Payload im Audit-Log, **maskiert** durch dieselbe Redaction
wie die Argumente. Zwei Dinge dazu:

- Der Schalter ist als Debug-Hilfe gedacht, nicht für Dauerbetrieb: Antworten können groß sein und
  die Audit-Tabelle schnell aufblähen. Die Retention greift zwar, aber der Plattenbedarf steigt spürbar.
- Redaction maskiert bekannte Secret-Feldnamen. Trägt ein Upstream Geheimnisse in *unbenannten*
  Strukturen (Freitext, Base64-Blobs), hilft das nicht — in dem Fall den Schalter aus lassen.

Zusätzliche Muster pro Tool lassen sich in der Web-UI unter **Tools → \[Tool wählen\]** als
Admin pflegen; sie gelten zusätzlich zu den globalen Mustern (`password`, `token`, `secret`, `key` …).

## Agent anbinden

```bash
claude mcp add --transport http mcpmcp http://localhost:8080/mcp \
  --header "Authorization: Bearer <API-KEY>"
```

Der Agent sieht dann die Meta-Tools `search_tools` / `describe_tool` / `invoke_tool` (Lazy-Default) plus die im Profil gepinnten Tools. Upstream-Server, Rollen und Profile werden über die Web-UI oder die REST-API verwaltet.

## PostgreSQL statt SQLite

Für größere Setups (viel Audit-Volumen, mehrere Instanzen an einer DB):

```bash
docker compose --profile postgres up -d
# in docker-compose.yml MCPMCP_DB_PROVIDER + MCPMCP_DB_CONNECTION einkommentieren,
# und das Passwort (CHANGE_ME) ersetzen.
```

Das Schema wird beim Start automatisch über EF-Migrationen angelegt (siehe [Schema & Upgrades](#schema--upgrades)).

## Schema & Upgrades

Ab **v1.1** verwaltet der Gateway sein Datenbankschema über EF-Core-Migrationen. Beim Start passiert automatisch genau eine von drei Sachen — das Ergebnis steht im Log (`Datenbank initialisiert (…)`):

| Vorgefunden | Aktion | Log-Ausgabe |
|---|---|---|
| Leere/neue DB | Schema aus Migrationen anlegen | `CreatedFromMigrations` |
| **v1.0-DB** (per `EnsureCreated` erzeugt, ohne Migrationshistorie) | Initial-Migration als Baseline stempeln (**kein DDL, keine Datenänderung**), dann migrieren | `BaselinedLegacySchema` |
| Bereits migrationsverwaltet | ausstehende Migrationen anwenden | `Migrated` |

### Upgrade von v1.0 auf v1.1

Es ist **kein manueller Schritt nötig** — der Gateway erkennt das Alt-Schema selbst und stempelt die Baseline. Trotzdem gilt die übliche Sorgfalt:

1. Dienst stoppen.
2. **Datenverzeichnis sichern** (`mcpmcp.db` **und** `keys/`, siehe [Backup](#backup)).
3. Neue Version starten und im Log `BaselinedLegacySchema` bestätigen.

Beim Rollback auf v1.0 ist die zusätzliche Tabelle `__EFMigrationsHistory` unschädlich — v1.0 ignoriert sie.

Jeder Provider hat eine eigene Migrations-Assembly (`McpMcp.Persistence.Migrations.Sqlite` bzw. `.Postgres`), weil SQLite und PostgreSQL unterschiedliches DDL brauchen. Beide sind im Image enthalten; die Auswahl erfolgt automatisch über `MCPMCP_DB_PROVIDER`.

## TLS / Reverse-Proxy

Der Gateway terminiert selbst kein TLS. **Immer hinter einen Reverse-Proxy** (Caddy, nginx, Traefik) mit TLS setzen — der Gateway hält Upstream-Credentials und API-Keys, ein Klartext-Transport ist inakzeptabel (NFR-04). Beispiel Caddy:

```
gateway.example.com {
    reverse_proxy localhost:8080
}
```

Der Proxy sollte `X-Forwarded-*`-Header setzen; das UI-Cookie ist `SameSite=Strict` + `HttpOnly`.

## Backup

Alles Persistente liegt im Datenverzeichnis (`MCPMCP_DATA_DIR`):

- `mcpmcp.db` — Konfiguration, RBAC, API-Key-Hashes, Audit-Log (bei SQLite).
- `keys/` — **DataProtection-Key-Ring**. Ohne ihn sind die verschlüsselten Upstream-Credentials unbrauchbar.

Beide **zusammen** sichern (Volume-Snapshot bei gestopptem Container oder DB-Dump + `keys/`-Kopie). Bei PostgreSQL: DB separat dumpen, `keys/` weiterhin aus dem Datenvolume sichern.

## Audit-Retention

Das Audit-Log wächst mit jedem Call. Default-Aufbewahrung: 30 Tage, stündlicher Bereinigungs-Job (FR-25). Bei SQLite ist Retention **Betriebspflicht** (ADR-0007) — sehr große Logs (> ~10 GB) sind ein Grund, auf PostgreSQL zu wechseln.

## Metriken

Der Gateway misst jeden Tool-Call (FR-26) unter dem Meter `McpMcp.Gateway`:

| Instrument | Bedeutung | Dimensionen |
|---|---|---|
| `mcpmcp.tool_calls` | Zähler aller Calls — daraus ergeben sich Calls/s und Fehlerquote | `server`, `tool`, `status`, `origin` |
| `mcpmcp.tool_call_duration` | Latenz-Histogramm (ms) — daraus Perzentile | `server`, `tool`, `status` |

Der Export ist **aus**, solange kein Ziel konfiguriert ist (sonst würde der Exporter dauerhaft ins Leere laufen):

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://collector:4317
```

Exportiert wird per **OTLP** — der OpenTelemetry-Standard. Für **Prometheus** einen OTel-Collector davorschalten, der OTLP annimmt und einen Scrape-Endpoint anbietet; ein direkter Prometheus-Exporter ist im .NET-Ökosystem noch nicht stabil veröffentlicht, deshalb bewusst dieser Weg.

## Health / Readiness

- `GET /healthz` — Prozess lebt (anonym).
- `GET /readyz` — DB erreichbar + Upstream-Zustände (anonym).

Der Container-Healthcheck nutzt `dotnet McpMcp.Server.dll --healthcheck` (self-ping, da das schlanke Runtime-Image kein `curl` enthält). Der Container läuft als non-root `app`-User.

## Key-Ring schützen

Der DataProtection-Key-Ring unter `<datadir>/keys/` entschlüsselt die at-rest verschlüsselten Upstream-Credentials. Ohne Zusatzschutz liegt er im Klartext neben der Datenbank — der Gateway warnt beim Start entsprechend.

Ab v1.1 lässt er sich mit einem X509-Zertifikat verschlüsseln (bewusst zertifikatsbasiert statt Cloud-KMS, damit es self-hosted funktioniert):

```bash
# Zertifikat einmalig erzeugen (Beispiel, OpenSSL):
openssl req -x509 -newkey rsa:2048 -keyout k.pem -out c.pem -days 3650 -nodes -subj "/CN=mcpmcp-keyring"
openssl pkcs12 -export -out keyring.pfx -inkey k.pem -in c.pem -password pass:GEHEIM

# Gateway damit starten:
MCPMCP_KEYRING_CERT_PATH=/secrets/keyring.pfx
MCPMCP_KEYRING_CERT_PASSWORD=GEHEIM
```

Danach enthalten die XML-Dateien im Key-Ring nur noch verschlüsseltes Material. **Das Zertifikat wird zum Entschlüsseln gebraucht** — geht es verloren, sind die gespeicherten Upstream-Credentials unbrauchbar (die Server müssen dann neu konfiguriert werden). Zertifikat also getrennt vom Datenverzeichnis sichern. Beim Zertifikatswechsel bleibt Altmaterial lesbar, solange das alte Zertifikat weiterhin angegeben wird.

## Zugang zurücksetzen

Bootstrap-Zugänge werden nur bei **leerer** DB erzeugt. Für verlorene Zugänge gibt es ab v1.1 zwei Kommandos, die gegen die konfigurierte Datenbank laufen, den Zugang **einmalig** ausgeben und sich beenden, ohne den Gateway zu starten:

```bash
# UI-Passwort zurücksetzen (Default-Benutzer "admin"; Rolle bleibt unverändert,
# ein fehlender Nutzer wird als Admin angelegt):
docker compose run --rm mcpmcp dotnet McpMcp.Server.dll --reset-ui-admin
docker compose run --rm mcpmcp dotnet McpMcp.Server.dll --reset-ui-admin betreiber

# Notfall-API-Key: legt eine NEUE Agenten-Identität mit Global-Grant an
# (bestehende bleiben unangetastet):
docker compose run --rm mcpmcp dotnet McpMcp.Server.dll --issue-bootstrap-key
```

Ohne Container analog mit `dotnet run --project src/McpMcp.Server -- --reset-ui-admin`. Den Notfall-Zugang nach Gebrauch wieder entfernen, falls er nur der Wiederherstellung diente.

- **UI-Passwort vergessen, aber anderer Admin existiert** → einfacher über die UI (Seite „UI-Nutzer") neu setzen.

## Audit-Betriebsmodi

`best-effort` hält Tool-Calls bei Audit-Überlast nicht auf. Drops werden gezählt, in `/readyz`
ausgegeben und beim Shutdown geloggt. Fehlgeschlagene DB-Batches werden als Fehler geloggt und
verworfen.

`compliance` wirft bei vollem Channel einen expliziten `AuditUnavailableException`, markiert
Readiness als nicht bereit und verwirft fehlgeschlagene DB-Batches nicht; der Writer retryt sie.
Das kann Shutdown oder Verarbeitung bei längerem DB-Ausfall bewusst blockieren. Für HA ist vor
Produktionsfreigabe zusätzlich ein durables externes Spool/Queue-Backend erforderlich.

`/readyz` liefert `auditMode`, `auditHealthy` und `auditDropped`. Alarmieren auf
`auditHealthy=false`, `auditDropped>0` und HTTP 503.

## Sicherheit

Vor dem Produktivbetrieb unbedingt [SECURITY.md](../SECURITY.md) und das [Threat-Model](security/threat-model.md) lesen — insbesondere: **nur vertrauenswürdige stdio-Server anschließen** (v1 ohne Sandbox, ADR-0005), Gateway als non-root betreiben (das Container-Image tut das bereits), Netzexposition minimieren.
