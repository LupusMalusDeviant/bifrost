# Betrieb — B.I.F.R.O.S.T Gateway

Praxisleitfaden zum Deployment und Betrieb. Zielgruppe: Self-hosted Single-Operator (ADR-0001).

### Die Web-UI braucht HTTPS — sonst hält die Anmeldung nicht

**Hinter einem TLS-Proxy zusätzlich `BIFROST_TRUSTED_PROXIES` setzen.** Sonst sieht der Gateway nur
HTTP und baut seine Umleitungen daraus: Wer eine geschützte Seite abgemeldet aufruft, wird von einer
`https`-Seite auf eine `http`-Adresse geschickt und bekommt vom Proxy ein
`400 The plain HTTP request was sent to HTTPS port`.

| Wert | Bedeutung |
|---|---|
| *(nicht gesetzt)* | Forwarded-Header werden **ignoriert** — richtig, wenn der Gateway direkt erreichbar ist |
| `any` | jedem Absender glauben — nur, wenn der Gateway ausschließlich über den Proxy erreichbar ist |
| `172.17.0.1` / `10.0.0.0/8` | Kommaliste aus Adressen und CIDR-Bereichen |

Opt-in ist Absicht: Steht der Gateway direkt im Netz, könnte jeder Client `X-Forwarded-Proto: https`
behaupten — und damit sowohl die Adressbildung als auch die Warnung oben aushebeln. Ein Tippfehler
im Wert bricht den Start ab, statt still auf „aus" zu fallen.

Der Proxy muss `X-Forwarded-Proto` **und** den Port im `Host` mitgeben (`proxy_set_header Host
$http_host` — `$host` verwirft den Port). Für die Web-UI braucht er außerdem WebSockets
(Blazor Interactive Server) und ungepufferte Antworten für `/mcp` (Server-Sent Events).


Außerhalb von `Development` trägt das Sitzungs-Cookie immer `Secure` (NFR-04). **Ein Browser
verwirft ein solches Cookie über Klartext-HTTP stillschweigend:** Die Anmeldung geht durch, der
Server antwortet mit `302`, und der nächste Seitenaufruf ist wieder die Login-Maske. Es gibt weder
im Browser noch im Server eine Fehlermeldung — das Symptom zeigt nicht auf die Ursache.

Deshalb sagt der Gateway es jetzt selbst: eine Zeile beim Start, wenn er nur auf HTTP lauscht, und
eine **eindeutige** Zeile bei jeder Anmeldung, die über HTTP hereinkommt, ohne dass ein Proxy
`X-Forwarded-Proto: https` gesetzt hat. Hinter einem TLS-Proxy erscheint sie nicht — eine Warnung,
die beim korrekten Aufbau mitläuft, wird ignoriert.

| Aufbau | Anmeldung hält? |
|---|---|
| TLS-Proxy davor, setzt `X-Forwarded-Proto: https` | ja |
| direkt über HTTPS | ja |
| `http://localhost:8080` (auch per SSH-Tunnel) | ja — Browser behandeln `localhost` als sicheren Ursprung |
| `http://<ip-oder-name>:8080` | **nein** |

Der Zugang über `/mcp` und `/api` ist davon **nicht** betroffen: Agenten authentifizieren sich mit
einem API-Key im Header, nicht mit einem Cookie.

## Installation (Docker)

Drei Befehle. Es wird **nichts** gebaut — der Standardweg zieht ein veröffentlichtes Image:

```bash
cp .env.example .env           # darin BIFROST_VERSION auf ein veröffentlichtes Release setzen
docker compose up -d           # SQLite-Default, ein Volume
curl -fsS http://localhost:8080/healthz
```

Danach den Erstzugang einrichten — das Setup-Token liegt in einer Datei, **nicht** im Log:

```bash
docker compose exec bifrost cat /data/config/bootstrap-token.txt
```

Details unter [Erstzugang](#erstzugang).

### Es gibt kein `latest`

Das Image liegt unter `ghcr.io/lupusmalusdeviant/bifrost`, getaggt mit `<version>` (`0.12.0`),
`<major>.<minor>` (`0.12`) und `sha-<kurz-sha>`. Ein `latest` wird bewusst **nicht** gesetzt: Ein
beweglicher Zeiger macht aus einem Neustart — einem Stromausfall, einem `restart: unless-stopped`
— unbemerkt ein Upgrade. Welche Version läuft, gehört in eine Datei, die man liest, bevor man sie
ändert. Das ist `.env`:

```bash
BIFROST_VERSION=0.12.0
```

**Für den Produktivbetrieb den Digest festnageln.** Ein Tag lässt sich in der Registry neu setzen,
ein Digest nicht — erst damit läuft nach jedem Neustart nachweislich dasselbe Image. `BIFROST_IMAGE`
ersetzt die Referenz vollständig und nimmt beide Formen:

```bash
BIFROST_IMAGE=ghcr.io/lupusmalusdeviant/bifrost@sha256:<64-hex>
```

Der Digest steht in der Release-Ausgabe; von einem bereits gezogenen Image holt ihn
`docker image inspect --format '{{index .RepoDigests 0}}' ghcr.io/lupusmalusdeviant/bifrost:0.12.0`.

### Upgrade

```bash
docker compose down                      # Container stoppen, Volumes bleiben
# BIFROST_VERSION (oder BIFROST_IMAGE) in .env auf die neue Fassung setzen
docker compose pull && docker compose up -d
docker compose logs -f bifrost           # Migrationsmeldung bestätigen (siehe Schema & Upgrades)
```

Vorher das Datenverzeichnis sichern (siehe [Backup](#backup)) — das Schema wird beim Start
automatisch migriert, und eine Migration ist nicht rückwärts fahrbar. Ein Rollback ist der Weg
zurück auf die vorherige Zeile in `.env`, nicht ein zweites `up`.

`docker compose pull` ist auch bei einem Digest sinnvoll: Es holt genau dieses Manifest und
scheitert, wenn es nicht mehr existiert, statt still ein lokal vorhandenes älteres zu nehmen.

### Aus dem Quelltext bauen (Entwicklerpfad)

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
```

Das ist ausdrücklich **kein** Installationsweg: Was dabei entsteht, trägt weder Provenance noch
Signatur. Es heißt deshalb `bifrost-local:dev` und nie so wie das Release-Image — sonst ließe sich
später nicht mehr sagen, welches von beiden gerade läuft.

### Konfiguration liegt in `.env`, nicht in der Compose-Datei

`.env` wirkt an zwei Stellen: Compose ersetzt damit die `${…}` in den Compose-Dateien (Image, Port,
Datenbankpasswort), und der Container bekommt den Inhalt als Umgebung (`env_file`). Betriebs-
geheimnisse gehören deshalb dorthin und nicht in eine versionierte Datei. `.env` steht in
`.gitignore`; `chmod 600 .env`.

Weil der Inhalt vollständig in die Container-Umgebung geht, gehören dort **nur** `BIFROST_*`,
`POSTGRES_*`, `COMPOSE_*` und `OTEL_*` hinein — eine Zeile `PATH=` überschriebe die Umgebung des
Containers.

### Der Volume-Name — die teuerste Zeile dieser Seite

Compose stellt jedem Volume den **Projektnamen** voran. Ohne Angabe ist der Projektname der
kleingeschriebene Name des Verzeichnisses, in dem die Compose-Datei liegt. Aus `bifrost-data` wird
so `<projekt>_bifrost-data`.

```bash
docker compose config --volumes        # die Schlüssel
docker compose config | tail -6        # die vollständigen Namen, inklusive Präfix
```

Wer den Schlüssel umbenennt, das Verzeichnis umbenennt oder die Compose-Datei woanders hinlegt,
zeigt damit auf ein **anderes** Volume. Docker legt es stillschweigend neu und leer an, der Gateway
findet eine leere Datenbank vor, richtet sie ein und meldet sich fehlerfrei als bereit — ohne
Server, ohne Rollen, ohne Key-Ring. **Der Ausfall sieht aus wie ein gelungener Start.** Erst wenn
jemand ein Tool aufruft oder sich anmelden will, fällt es auf.

Wer davor sicher sein will, setzt den Projektnamen einmal fest, statt ihn vom Verzeichnis abhängen
zu lassen:

```bash
COMPOSE_PROJECT_NAME=bifrost           # in .env
```

Das ändert den Volume-Namen **ebenfalls** — also nur bei einer Neuinstallation setzen, oder
zusammen mit dem Umzug unten.

### Umstieg einer MCP-MCP-Installation

Eine Installation aus der Zeit vor der Umbenennung (2026-07-31) hat Service und Volume unter dem
alten Namen: Service `mcpmcp`, Volume `<projekt>_mcpmcp-data`. Die heutige Compose-Datei nennt
beides `bifrost`. Ein `docker compose up -d` auf der alten Installation startet damit **auf einem
neuen, leeren Volume** — mit genau dem Fehlerbild von oben.

Erst nachsehen, dann handeln:

```bash
docker volume ls | grep -E 'mcpmcp|bifrost'
docker compose config | tail -6        # welchen Namen die neue Fassung erwartet
```

Stimmen die beiden nicht überein, ist der Inhalt umzuziehen. Docker kann Volumes nicht umbenennen;
kopiert wird über einen Wegwerf-Container, bei **gestopptem** Gateway:

```bash
docker compose down                    # oder: docker stop <alter-container>

docker volume create mcpmcp_bifrost-data          # Zielname aus 'docker compose config'

docker run --rm \
  -v mcpmcp_mcpmcp-data:/from \
  -v mcpmcp_bifrost-data:/to \
  alpine sh -c 'cp -a /from/. /to/'

docker run --rm -v mcpmcp_bifrost-data:/d alpine ls -la /d      # bifrost.db bzw. mcpmcp.db + keys/
```

`cp -a` erhält Rechte und Eigentümer — der Container läuft als non-root `app`, ein Kopieren ohne
`-a` liefert ihm ein Verzeichnis, das er nicht beschreiben kann.

Danach `docker compose up -d` und im Log prüfen, dass die Daten da sind (`Migrated` bzw.
`BaselinedLegacySchema`, siehe [Schema & Upgrades](#schema--upgrades)), **bevor** das alte Volume
gelöscht wird. Es kostet ein paar hundert Megabyte, es noch eine Weile stehen zu lassen.

Zwei Dinge nimmt der Gateway einem dabei ab, und zwar ohne Zutun:

- Eine Datenbankdatei, die noch `mcpmcp.db` heißt, wird weiterverwendet — es entsteht keine leere
  `bifrost.db` daneben.
- Alt benannte Umgebungsvariablen (`MCPMCP_*`) werden beim Start als `BIFROST_*` übernommen und im
  Log genannt. Der neue Name gewinnt, wenn beide gesetzt sind. Trotzdem umbenennen: Die Übernahme
  ist eine Übergangshilfe, keine Zusage.

Der DataProtection-Anwendungsname bleibt aus demselben Grund `MCPMCP` — er geht in die
Schlüsselableitung ein, und ihn zu ändern machte jeden gespeicherten Geheimtext unlesbar.

<a id="erstzugang"></a>

### Erstzugang

Beim **Erststart** legt der Gateway **keinen** Zugang an. Er stellt ein einmaliges, kurzlebiges
**Setup-Token** aus und schreibt es in eine Datei mit restriktiven Rechten:

```
<datadir>/config/bootstrap-token.txt      # Unix 0600, Windows ACL ohne Vererbung
```

Im Container:

```bash
docker compose exec bifrost cat /data/config/bootstrap-token.txt
```

Damit dann `http://localhost:8080/setup` aufrufen und **Benutzername und Passwort selbst wählen**.
Nach dem Einlösen ist das Token tot und die Datei gelöscht; die Anmeldung ist sofort aktiv.

Einen **API-Key** für Agenten (Claude Code, MCP Inspector, REST-Fassade) erzeugt der angemeldete
Administrator in der Oberfläche unter **RBAC → Keys**. Er wird dort einmal angezeigt, mitsamt
fertiger Client-Konfiguration.

> **Warum nicht mehr über das Log.** Bis v0.11 standen Adminpasswort und API-Key als Klartext im
> Anwendungslog. Das ist ein Geheimnis an genau dem Ort, den man weitergibt, wenn etwas nicht
> funktioniert: Supportanfrage, Ticketanhang, Logaggregation, Sicherung des Logverzeichnisses. Und
> es ist der Ort, den niemand rotiert. `docker compose logs bifrost` nennt jetzt nur noch den
> **Pfad** der Übergabedatei und die Frist — nicht das Token.

Das Token gilt **eine Stunde** (`BIFROST_BOOTSTRAP_TTL_MINUTES`) und genau **einmal**. Verstreicht
die Frist, ohne dass jemand eingerichtet ist, stellt der nächste Start ein neues aus — eine
Installation, in die niemand hineinkommt, ist kein Sicherheitsgewinn. Ist der Zugang dagegen
einmal eingerichtet, gibt es **kein zweites Token über das Netz**: siehe
[Zugang zurücksetzen](#zugang-zurücksetzen).

**Bestehende Installationen** sind davon nicht betroffen. Wer bereits einen UI-Zugang hat, bekommt
beim ersten Start nach dem Upgrade nur einen Vermerk in `config/bootstrap.json` und meldet sich
unverändert an. Es wird kein Passwort zurückgesetzt und kein Token ausgestellt.

**Restrisiko, ausdrücklich:** Die Übergabedatei liegt im Datenverzeichnis. Eine Sicherung, die
*innerhalb* der Frist entsteht, trägt das Token im Klartext mit. Danach nicht mehr — die Datei wird
beim Einlösen und beim Ablauf gelöscht. Das ist der bewusste Tausch gegen den Logeintrag: ein
Fenster von einer Stunde in einer Datei mit `0600` statt eines dauerhaften Eintrags in einem
Archiv, das ohnehin herumgereicht wird.

`config/bootstrap.json` gehört zum Datenverzeichnis und wird mitgesichert. Darin steht **nur der
Hash** des Tokens, nie das Token selbst. Ist die Datei vorhanden, aber unlesbar, **bricht der Start
ab** statt sie als „frische Installation" zu deuten — sonst würde ausgerechnet ein Lesefehler ein
neues Setup-Token auf einer produktiven Installation erzeugen. Die Datei dann prüfen oder aus der
Sicherung zurückholen.

## Konfiguration (Env-Vars)

| Variable | Default | Zweck |
|---|---|---|
| `BIFROST_DATA_DIR` | `data` (bzw. `/data` im Container) | Verzeichnis für SQLite-DB **und** DataProtection-Key-Ring |
| `BIFROST_DB_PROVIDER` | `sqlite` | `sqlite` oder `postgres` |
| `BIFROST_DB_CONNECTION` | `Data Source=<datadir>/bifrost.db` | Connection-String (bei Postgres Pflicht) |
| `BIFROST_AUDIT_MODE` | `best-effort` | `best-effort` verwirft bei Überlast gezählt; `compliance` meldet Überlast explizit und retryt DB-Fehler mit Backpressure |
| `BIFROST_BOOTSTRAP_TTL_MINUTES` | `60` | Gültigkeitsdauer des Setup-Tokens beim [Erstzugang](#erstzugang). Ungültige Angabe fällt auf den Default zurück |
| `ASPNETCORE_URLS` | `http://+:8080` (Container) | Bind-Adresse/Port |
| `BIFROST_KEYRING_PROTECTION` | *(nicht gesetzt)* | Ausdrückliche Betriebsart: `certificate`, `file-secret` oder `none` (siehe [Key-Ring schützen](#key-ring-schützen)). Nicht gesetzt = **keine Wahl getroffen**, der Start warnt |
| `BIFROST_KEYRING_CERT_PATH` | *(nicht gesetzt)* | PFX-Zertifikat zum Verschlüsseln des Key-Rings |
| `BIFROST_KEYRING_CERT_PASSWORD` | *(nicht gesetzt)* | Passwort des PFX. **Steht damit in der Prozessumgebung** und ist per `docker inspect` lesbar |
| `BIFROST_KEYRING_CERT_PASSWORD_FILE` | *(nicht gesetzt)* | Dasselbe Passwort als **Datei-Secret** (FR-P048). Sind beide Formen gesetzt, bricht der Start ab — es gibt bewusst keine Rangfolge |
| `BIFROST_KEYRING_CERT_PATH_PREVIOUS` | *(nicht gesetzt)* | Das **vorherige** Zertifikat beim Wechsel. Es verschlüsselt nichts mehr, entschlüsselt aber weiterhin |
| `BIFROST_KEYRING_CERT_PASSWORD_PREVIOUS` | *(nicht gesetzt)* | Passwort dazu; ebenfalls mit `_FILE`-Suffix möglich |
| `BIFROST_OAUTH_ISSUER` | *(nicht gesetzt)* | Authorization Server, dem für **eingehende** Agenten-Token vertraut wird. Gesetzt = der Gateway ist zusätzlich OAuth-Resource-Server (siehe [Agenten über OAuth](#agenten-über-oauth)) |
| `BIFROST_OAUTH_AUDIENCE` | `BIFROST_PUBLIC_BASE_URL` | Kanonische Adresse dieses Gateways; ein Token muss darauf lauten |
| `BIFROST_WASI_HOST` | *(nicht gesetzt)* | Pfad zum WASI-Host-Binary. **Pflicht für die Installation von Connector-Paketen** — ohne ihn lässt sich ein Paket nicht proben, und ungeprobt wird nichts aktiv |
| `BIFROST_PUBLIC_BASE_URL` | *(nicht gesetzt)* | Öffentliche Adresse des Gateways; nötig für die Redirect-URI der Upstream-Autorisierung (siehe [OAuth gegen Upstreams](#oauth-gegen-upstreams)) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(nicht gesetzt)* | Ziel für Metriken **und** Traces (siehe [Metriken und Traces](#metriken-und-traces)) |
| `BIFROST_AUDIT_DEBUG_PAYLOADS` | *(aus)* | `1`/`true` schaltet den Debug-Modus des Audits ein (siehe [Audit-Debug-Modus](#audit-debug-modus)) |
| `BIFROST_AUDIT_RETENTION_DAYS` | `30` | Aufbewahrung der Audit-Ereignisse in Tagen; ältere werden täglich gelöscht (FR-25) |
| `BIFROST_MAX_RESULT_CHARS` | *(aus)* | Kürzt Tool-Ergebnisse oberhalb dieser Zeichenzahl (FR-16, siehe [Ergebnis-Kompression](#ergebnis-kompression)) |
| `BIFROST_GUARD_ENABLED` | `1` | `0`/`false` schaltet die Secret-Guardrail global ab (Not-Aus) |
| `BIFROST_GUARD_MAX_SCAN_CHARS` | `262144` | Nutzlasten darüber werden nicht geprüft und **abgewiesen** |
| `BIFROST_GUARD_ALLOW_CUSTOM_PATTERNS` | *(aus)* | Erlaubt Admins eigene Regex in der UI (siehe [Guardrails](#guardrails)) |
| `BIFROST_MCP_STATELESS` | `1` | `0`/`false` schaltet auf den Sitzungsbetrieb der alten Protokollrevision zurück (siehe [Protokollstand](#protokollstand-sessionlos-oder-mit-sitzung)) |
| `BIFROST_MCP_LIST_TTL_SECONDS` | `60` | Wie lange ein Client die Werkzeug-, Resource- und Prompt-Listen für frisch halten darf. `0` = kein Hinweis |
| `BIFROST_BACKUP_PASSPHRASE` | *(nicht gesetzt)* | Verschlüsselt die **automatischen** Sicherungen vor einer Migration. Ohne sie entstehen sie unverschlüsselt — mit ihr sind sie ohne die Passphrase wertlos (siehe [Backup](#backup)) |

## Protokollstand: sessionlos oder mit Sitzung

Der Gateway spricht die MCP-Spec-Revision **`2026-07-28`**. Sie hat den `initialize`-Handshake und
`Mcp-Session-Id` gestrichen — jede Anfrage steht für sich. Das ist die Vorgabe, und für die meisten
Installationen die richtige Einstellung ([ADR-0023](adr/0023-stateless-kern-und-mrtr.md)).

**Ältere Clients laufen unverändert weiter.** Wer noch `2025-11-25` spricht, wird vom SDK bedient
wie bisher; ein Umstieg auf der Client-Seite ist nicht nötig.

Zwei Dinge setzen allerdings eine stehende Sitzung voraus und fehlen einem solchen Client deshalb
im sessionlosen Betrieb:

- **Die Freigabe-Rückfrage im laufenden Aufruf.** Ein Client auf `2026-07-28` bekommt sie über MRTR
  (der Aufruf endet mit einer Frage, der Client wiederholt ihn mit der Antwort). Ein älterer Client
  bekommt sie nicht — für ihn bleibt die Freigabe-Warteschlange in der Oberfläche.
- **`tools/list_changed`.** An seine Stelle tritt die Cache-Frist auf den Listen
  (`BIFROST_MCP_LIST_TTL_SECONDS`): Der Client holt sich den Stand nach Ablauf selbst. Eine Änderung
  am Katalog ist also nach höchstens einer Frist sichtbar, nicht sofort.

**Wann `BIFROST_MCP_STATELESS=0` sinnvoll ist:** wenn *alle* angeschlossenen Clients auf dem alten
Stand sind und die Rückfrage im laufenden Aufruf gebraucht wird.

> **Der Schalter gilt für den ganzen Gateway, nicht je Client.** Im Sitzungsbetrieb wird eine
> Anfrage auf `2026-07-28` mit `-32022 UnsupportedProtocolVersion` abgewiesen; der Client handelt
> daraufhin selbst den alten Stand aus. Er läuft also weiter — aber die ganze Installation spricht
> dann die alte Revision, auch gegenüber Clients, die längst weiter sind.

Was die Oberfläche zeigt, hängt davon ab: Im sessionlosen Betrieb gibt es keine offenen Sitzungen
zu zählen, das Dashboard meldet dort **„Aktive Agenten"** (Identitäten mit Anfragen in den letzten
fünf Minuten) statt **„Aktive Sessions"**.

### Upstreams auf dem neuen Stand

Dieselbe Änderung gilt in der Gegenrichtung: Ein **Upstream** auf `2026-07-28` meldet
Katalogänderungen nicht mehr von sich aus. Der Gateway fragt solche Server deshalb turnusmäßig neu
ab (Vorgabe: jede Minute) und meldet nur echte Änderungen weiter. Upstreams auf dem alten Stand
melden sich unverändert selbst und werden nicht zusätzlich gefragt.

Zwei Fehlerbilder, die dabei neu auftreten können:

- *„Der Upstream lieferte eine Werkzeugliste, die sich nicht lesen lässt."* — Seit `2026-07-28` ist
  `inputSchema` an jedem Werkzeug Pflicht. Ein Server, der es weglässt, kommt nicht mehr durch; ein
  leeres `{}` genügt ihm.
- *„Das Werkzeug verlangt eine Rückfrage beim Menschen (MRTR)."* — Rückfragen eines Upstreams werden
  nicht durchgereicht ([ADR-0010](adr/0010-sampling-elicitation-nicht-durchreichen.md)); der Gateway
  könnte nicht sagen, an welchen seiner vielen Aufrufer sie gehen sollen.

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

- **Befunde enthalten nie den gefundenen Wert.** Protokolliert werden Regel-Id, Richtung, Modus und
  Fingerabdruck (Hash). Position und Länge stehen im Befund selbst, gehen aber nicht ins Log —
  beides zusammen grenzt eine Zeichenkette weiter ein, als für das Wiedererkennen nötig ist. Eine Secret-Erkennung, die ihre Funde im Klartext loggt, kopiert
  Secrets in ein zweites und meist schwächer geschütztes System.
- **Über der Prüfgrenze wird abgewiesen**, nicht durchgelassen — sonst wäre die Grenze genau der
  blinde Fleck, den man ansteuert. Wer große Ergebnisse erwartet, kombiniert das mit
  `BIFROST_MAX_RESULT_CHARS`: Die Kürzung greift vorher, und das gekürzte Ergebnis läuft durch.

### Eigene Regeln

Der **geführte Editor** ist der Normalfall: Präfix, Zeichenart und Längenbereich als Felder,
daraus wird das Muster erzeugt. Das deckt praktisch alle Token-Formate ab.

Freitext-Regex ist standardmäßig **aus** und über `BIFROST_GUARD_ALLOW_CUSTOM_PATTERNS=1`
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
**Freigabe per Rückfrage.** Kann der anfragende Client gefragt werden, fragt der Gateway im Moment
des Aufrufs nach, statt die Anfrage nur in die Warteschlange zu legen: ein Dialog beim Menschen,
Zustimmung lässt genau diesen einen Aufruf durch. Kann er es nicht, bleibt alles wie bisher — die
Warteschlange ist die Rückfallebene, kein Aufruf geht verloren.

Welcher Weg das ist, hängt vom Protokollstand des Clients ab
([ADR-0023](adr/0023-stateless-kern-und-mrtr.md)):

- **`2026-07-28` und neuer:** über **MRTR**. Der Aufruf endet mit `input_required`, der Client zeigt
  das Formular und wiederholt den Aufruf mit der Antwort. Das funktioniert auch ohne Sitzung und ist
  im Normalbetrieb der einzige Weg.
- **`2025-11-25` und älter:** über die klassische **Elicitation** — nur im Sitzungsbetrieb
  (`BIFROST_MCP_STATELESS=0`), weil der Gateway den Client dafür während des Aufrufs erreichen muss.

> **Ein Client auf dem neuen Stand, der kein Formular anzeigen kann,** bekommt beim Aufruf eines
> freigabepflichtigen Werkzeugs einen Fehler seines eigenen SDK (*„no ElicitationHandler is
> registered"*) statt der Warteschlangen-Meldung. Der Grund: Seit `2026-07-28` meldet kein Client
> mehr eine Elicitation-Fähigkeit, an der sich das vorher unterscheiden ließ. **Der Vorgang geht
> dabei nicht verloren** — er steht in der Warteschlange und lässt sich unter *Freigaben*
> entscheiden.

Der Grund ist nicht Bequemlichkeit allein: Verlangt jede Freigabe einen Wechsel in Oberfläche oder
CLI, schaltet jemand bei einem oft gebrauchten Werkzeug irgendwann die Freigabepflicht ab — und dann
schützt sie gar nicht mehr.

**Es bleibt eine menschliche Freigabe.** Die Frage geht an den Client, die Antwort kommt von dem
Menschen davor; der Agent hält keinen Freigabe-Schlüssel und kann die Antwort nicht erfinden. Im
Dialog stehen dieselben **maskierten** Argumente wie in der Warteschlange — das Popup zeigt nie
mehr als die Oberfläche.

Welchen Stand ein verbundener Client spricht und ob er gefragt werden kann, steht beim ersten
Aufruf im Log:

```
MCP-Client: <Name> <Version>, Protokoll 2026-07-28. Rueckfrage moeglich — MRTR: True, Elicitation: False.
```

Bleibt eine Rückfrage aus, steht der Grund ebenfalls im Log
(`Keine Rueckfrage fuer <Tool> — <Grund>. Der Aufruf bleibt in der Warteschlange.`). Das ist
Absicht: Ein Aufruf in der Warteschlange sieht von außen gleich aus, egal ob niemand gefragt wurde,
die Frage scheiterte oder ein Mensch abgelehnt hat.

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

### Unterbau seit 2026-07-26: Freigaben sind Vorgänge

Am Verhalten ändert sich nichts — an der Speicherung schon. Freigaben liegen seit
[ADR-0019](adr/0019-langlaufende-tasks-und-events.md) in der **Vorgangs-Tabelle** (`Tasks`): Eine
wartende Anfrage ist ein Vorgang im Zustand `input-required`, eine erteilte Freigabe einer im
Zustand `working`, eine abgelehnte ein gescheiterter mit dem Code `approval-denied`. Statt zweier
Warteschlangen gibt es eine.

Beim **ersten Start nach dem Update** werden vorhandene Freigaben einmalig übernommen, bevor
irgendwem eine Liste angezeigt wird — sonst sähe ein Operator eine leere Queue und hielte offene
Anfragen für erledigt. Die Übernahme ist idempotent (die Freigabe-Id wird die Vorgangs-Id), ein
zweiter Start kopiert also nichts doppelt. Eine bereits **verbrauchte** Freigabe kommt als
eingelöst herüber und ist nicht erneut einlösbar.

Die alte Tabelle `ApprovalRequests` bleibt mit ihren Zeilen stehen. Sie zu leeren wäre unumkehrbar,
und der Gewinn wären ein paar Kilobyte.

### Vorgänge ansehen und abbrechen

Die Web-UI zeigt sie unter **Vorgänge** (Operator/Admin). Über REST:

```bash
curl -H "Authorization: Bearer $API_KEY" http://localhost:8080/api/v1/tasks
```

- `GET /api/v1/tasks` — Liste mit Offset-Paginierung (`page`, `pageSize`, Filter `state` und `tool`).
  **Sichtbarkeit folgt der Eigentümerschaft:** Wer keinen Global-Grant hat, sieht ausschließlich
  seine eigenen Vorgänge. Ein fremder Vorgang ist `404`, nicht `403` — sonst liesse sich über den
  Statuscode abfragen, welche Ids existieren.
- `GET /api/v1/tasks/{id}` — einzelner Vorgang.
- `POST /api/v1/tasks/{id}/cancel` — Abbruch. Solange **nichts läuft** (der Vorgang wartet oder ist
  freigegeben, aber noch nicht eingelöst), ist er **endgültig**: Antwort `200`, Zustand `Cancelled`,
  `Cancellation: Confirmed`. Es gibt hier keinen Ausführenden, der noch etwas bestätigen müsste.
  **Eine so abgebrochene Freigabe ist nicht mehr einlösbar** — das ist der eigentliche Zweck.
  Ein bereits **eingelöster** Vorgang antwortet `409`: Der Aufruf ist gelaufen, da ist nichts mehr zu
  stoppen. Ein abgeschlossener Vorgang ebenfalls `409`.

  Für künftige, wirklich laufende Vorgänge bleibt die Unterscheidung aus ADR-0019 bestehen —
  `Requested` bis der Ausführende bestätigt. Einen solchen Ausführenden gibt es heute noch nicht.

Der Zustand wird **geholt, nicht zugestellt** (ADR-0019). Es gibt kein Abo und keine Zusage, dass
eine Benachrichtigung ankommt; wer den Stand braucht, fragt danach.

**Verfall:** Ein Hintergrunddienst setzt überfällige Vorgänge alle fünf Minuten auf `expired`. Das
ist Sichtbarkeit, keine Durchsetzung — eine verstrichene Frist wirkt schon vorher, weil der
Einlöse-Pfad sie selbst prüft. Abgelaufene Vorgänge werden nicht gelöscht: Sie bleiben als
Terminalzustand auditierbar stehen.

## OpenRPC-Dienste als Upstream

Ein JSON-RPC-Dienst mit OpenRPC-Beschreibung wird ein normaler Upstream. Die Beschreibung kommt
entweder aus einem Dokument oder über den Discovery-Aufruf des Dienstes:

```json
"OpenRpc": {
  "Endpoint": "https://dienst.example/rpc",
  "SpecLocation": "https://dienst.example/openrpc.json",
  "AuthKind": "Bearer",
  "Credential": "…"
}
```

Ohne `SpecLocation` versucht der Gateway `rpc.discover` am Endpunkt. Beide Wege laufen durch
dieselbe Prüfung von Ziel, Größe und Schema — Discovery bekommt keinen Vertrauensvorschuss.

**Was beim Import abgewiesen wird**, statt es zu laden oder zu raten:

- **Externe `$ref`.** Ein Verweis nach außen wäre ein zweiter, ungeprüfter Ladevorgang mitten in der
  Schemaverarbeitung. Lokale Verweise werden aufgelöst, mit Tiefen- und Zyklenprüfung.
- **Doppelte Methodennamen.** Beim Aufruf wäre nicht bestimmbar, welche Signatur gilt.
- **Dokumente über 10 MB.**
- **Ziele in privaten, Loopback- oder Link-Local-Netzen** — einschließlich der Adressen hinter einer
  Weiterleitung, die einzeln geprüft werden. Sonst wäre der Gateway ein Werkzeug, um interne Dienste
  zu erreichen (etwa den Cloud-Metadatendienst). Für einen Dienst im eigenen Netz setzt man
  `AllowPrivateTargets: true` — ausdrücklich und pro Upstream.

**Nicht unterstützt in v1:** Batch-Requests und Notifications. Ein Batch bündelt mehrere Aufrufe in
einer Nachricht; jeder müsste einzeln durch RBAC, Guardrail, Approval und Audit, sonst entstünde ein
Weg, an der Governance vorbei mehrere Dinge zu tun. Eine Notification hat definitionsgemäß keine
Antwort und passt nicht auf einen Tool-Call.

Zu den Parametern: `paramStructure: by-name` schickt ein Objekt, `by-position` ein **geordnetes
Array** in der Reihenfolge aus dem Dokument. Der Aufrufer nennt in beiden Fällen die Namen; die
Reihenfolge kommt aus der Beschreibung, nicht aus der Reihenfolge im Aufruf.

## Skills: was der Mensch sieht und was der Agent selbst holt

Zentrale Skills (Assets) gehen auf **zwei** Wegen an die Agenten, und der Unterschied ist wichtig:

| Weg | Wer löst aus | Wo er auftaucht |
|---|---|---|
| MCP-**Prompt** `assets__<name>` und Resource `bifrost://assets/<name>` | **der Mensch** | Slash-Menü bzw. Anhang-Menü des Clients |
| Meta-Tools `list_skills` / `read_skill` | **das Modell** | im Tool-Katalog, wie jedes andere Tool |

Der Prompt-Weg allein reicht nicht, wenn ein Agent *selbst* merken soll, dass es für seine Aufgabe
eine hinterlegte Anleitung gibt: Prompts sind in den meisten Clients nutzerinitiiert — die Liste
sieht der Mensch, nicht das Modell. Tools ruft das Modell dagegen von sich aus auf.

**Und die Token-Rechnung, weil sie die naheliegende Sorge ist:** `list_skills` liefert **nur** Namen
und Kurzbeschreibungen, `read_skill` holt den Text. Dasselbe Muster wie `search_tools`/
`describe_tool`, aus demselben Grund. Die beiden Schemas heben die Schätzung des Lazy-Pfads von 700
auf 950 Tokens — einmal je Sitzung, gegenüber den Tausenden, die das Anpinnen aller Tools kostete.
Ein Blick in den Skill-Bestand kostet danach so viel wie eine kurze Liste, nicht wie ein Dokument.

### Struktur, Referenzen und Versionen

Ein Skill ist Text plus ein paar **deklarierte** Angaben:

| Feld | Wofür |
|---|---|
| Beschreibung | eine Zeile; sie entscheidet, ob ein Agent zugreift |
| Wann anwenden | geht mit in `list_skills` — der eigentliche Auslöser |
| Referenzierte Skills | andere Skills, die dieser voraussetzt oder ergänzt |
| Benötigte Tools | namespaced Tool-Namen, die der Skill voraussetzt |

**Warum überhaupt Struktur, wo ein Skill doch Text ist:** Nur was deklariert ist, lässt sich
prüfen. Ein Verweis in der Prosa („Details siehe `codebase-mapper/references/x`") hängt still ins
Leere, sobald jemand umbenennt. Deklariert man ihn, sagt der Gateway, dass er nicht aufgeht — und
bei den vorausgesetzten Tools kann er es gegen den **Katalog** prüfen. Das kann kein Datei-Editor,
weil nur der Gateway weiß, welche Tools angeschlossen sind.

Die Befunde sind **Warnungen, keine Fehler**. Wer Skill A schreibt, der B referenziert, legt B
vielleicht erst danach an; ein hartes Nein erzwänge eine Reihenfolge, die niemand einhält — und die
naheliegende Reaktion wäre, das Feld leer zu lassen. Ein leeres Feld prüft nichts.

**Mehrteilige Skills** bildest du über den Namen ab: `codebase-mapper/SKILL`,
`codebase-mapper/references/format`. Der Einstieg deklariert die Teile als Referenzen, ein Agent
liest zuerst den Einstieg und zieht mit `read_skill` nur das nach, was er braucht. Das ist dieselbe
schrittweise Offenlegung wie bei `search_tools`/`describe_tool`. Für den Slash-Command-Weg sind
flache Namen die sicherere Wahl — wie ein Client mit einem Schrägstrich im Prompt-Namen umgeht, ist
nicht garantiert.

**Versionen** sind append-only. Die Historie zeigt jede Fassung samt ihrer Angaben; Zurückschalten
hängt die alte Fassung als **neue** Version an, statt Geschichte zu überschreiben — dieselbe Regel
wie bei der Server-Konfiguration.

### Skills über die API pflegen

Skills hatten lange nur die Weboberfläche. Für einen einzelnen Text reicht ein Formular; für die
Skill-Sammlung eines Agenten — Dutzende Dateien mit Verweisen untereinander — ist Abtippen keine
Bedienung. Deshalb gibt es die REST-Fläche, admin-only wie die übrigen Management-Endpunkte:

| Methode | Pfad | Zweck |
|---|---|---|
| `GET` | `/api/v1/skills` | Bestand mit Angaben, Version und Herkunft |
| `GET` | `/api/v1/skills/{id}?version=N` | ein Skill samt Text |
| `POST` | `/api/v1/skills` | anlegen (`name`, `content`, optional `description`, `whenToUse`, `references`, `requiredTools`) |
| `POST` | `/api/v1/skills/{id}/versions` | neue Version anhängen |

Die **Befunde der Prüfung kommen mit der Antwort zurück** — ein Skript sieht die Oberfläche nicht,
und ein Verweis ins Leere soll auffallen, auch wenn er nicht blockiert. Ein doppelter Name oder ein
zu großer Text sind `400` mit Begründung, kein Serverfehler.

Damit lässt sich der Bestand aus einem Repository befüllen, versionieren und sichern.

### Skills aus einem Paket

Ein Connector-Paket kann die Skills mitbringen, die erklären, wie man seinen Konnektor benutzt
([ADR-0021](adr/0021-skills-in-paketen.md)). Ein Pakettyp für Skill-Bündel **ohne** Konnektor ist
entschieden, aber noch nicht gebaut. Der Grund ist
nicht Bequemlichkeit: *Benötigte Tools* konnte der Gateway bisher nur **prüfen** und melden, wenn
etwas fehlt. Ein Paket **stellt die Zusage her** — die Tools kommen mit.

Beim Installieren zeigt die Paket-Seite jeden mitgelieferten Text an, und er ist einzeln zu
bestätigen — **auch bei einem offiziellen Herausgeber**. Für einen Zugriff nach außen gibt es eine
Laufzeitgrenze, die ihn durchsetzt; für einen Skill nicht. Er ist Text, der ungefiltert in die
Denkschleife eines Agenten geht, der Tools aufrufen darf. Es gibt keine Sandbox für einen Satz.

Die Zustimmung lautet `skill:<name>@<hash>` und bindet an den **Inhalt**. Ändert ein Update den
Text, verfällt sie und ist neu zu geben — sonst wäre sie eine Zustimmung zu einem Namen, und unter
demselben Namen stünde beim nächsten Mal etwas anderes.

| Was | Verhalten |
|---|---|
| Name | `<paket-id>/<skill>` — ein Paket kann keinen handgeschriebenen Skill überschatten |
| Herkunft | steht an der Version; die Skill-Liste zeigt sie an |
| Update | hängt eine Version an; die vorherige bleibt |
| Eigene Änderung | Wer den Text bearbeitet, verliert die Herkunft. Das nächste Paket-Update **meldet**, dass es eine angepasste Fassung ablöst — sie bleibt in der Historie und lässt sich zurückschalten |
| Paket entfernt | Die mitgelieferten Skills gehen mit — **samt Historie**, und das ist die einzige Stelle, an der Historie verloren geht ([ADR-0021](adr/0021-skills-in-paketen.md), F5). Vorher wird angekündigt, was mitgeht; eine von dir angepasste Fassung wird dabei besonders genannt. Eine einzelne alte Paketversion zu entfernen lässt die Skills stehen — sie gehören zum Paket, nicht zu einer Version |
| Probe gescheitert | Kein Skill wird eingespielt — eine Anweisung aus einem Konnektor, der nie gelaufen ist, wäre trotzdem in Umlauf |

Skill-Namen sind seitdem **eindeutig**. Vorher waren zwei gleichen Namens möglich; ausgeliefert
wurde dann der erstbeste. Beim Aktualisieren einer bestehenden Datenbank scheitert die Migration,
wenn dort schon Dopplungen stehen — dann vorher umbenennen.

**Größe:** Ein Skill darf höchstens 256 KB groß sein — dieselbe Grenze für einen von Hand
angelegten wie für einen aus einem Paket. Der Grund ist die Auslieferung: `read_skill` schickt den
Text vollständig in den Kontext eines Agenten, und ein unbegrenzter Skill hebelt genau das Argument
aus, für das die Meta-Tools existieren. Was länger ist, gehört auf mehrere Skills verteilt, die
sich gegenseitig referenzieren. Die Grenze wirkt beim **Speichern**; ein bereits gespeicherter,
größerer Skill wird weiter vollständig geliefert — ihn stillschweigend abzuschneiden hieße, einem
Agenten eine halbe Anweisung zu geben.

**Frontmatter-Import:** Eine bestehende `SKILL.md` mit YAML-Kopf lässt sich einfügen; der Knopf
*Frontmatter aus Inhalt übernehmen* liest `name`, `description`, `when-to-use`, `references` und
`required-tools` in die Felder und lässt den Rest als Inhalt stehen. Das ist bewusst **kein
YAML-Parser**, sondern ein Leser für genau diese flache Form.

Jeder angeschlossene Agent erfährt beim Verbinden über die **Server-Instruktion**, dass es diesen
Bestand gibt — drei Sätze, ebenfalls einmal je Sitzung. Länger macht sie nicht wirksamer.

> Skills sind für **jede** authentifizierte Identität lesbar (FR-40) — auch über `list_skills`.
> Keine Zugangsdaten in Skills ablegen.

## Agenten über OAuth

Neben API-Keys kann sich ein Agent mit einem Zugriffstoken eines vorhandenen Identitätsanbieters
anmelden. Der Gateway ist dann **Resource Server** im Sinne der MCP-Autorisierung — er stellt selbst
keine Token aus, das bleibt Sache des Authorization Servers.

```bash
BIFROST_OAUTH_ISSUER=https://login.example.com/realms/bifrost
BIFROST_PUBLIC_BASE_URL=https://gateway.example.com
```

Damit passieren drei Dinge:

- `/.well-known/oauth-protected-resource` liefert die Protected Resource Metadata (RFC 9728) —
  **anonym**, denn sie ist der Weg, auf dem ein Client überhaupt erst erfährt, wo er ein Token
  bekommt.
- Eine `401`-Antwort trägt `WWW-Authenticate: Bearer resource_metadata="…"` und verweist dorthin.
- Ein mitgeschicktes Token wird gegen den JWKS des Issuers geprüft: Signatur, Issuer, Ablauf — und
  **die Audience**. Ein Token, das für einen anderen Dienst ausgestellt wurde, bewirkt hier nichts.
  Ohne diese Prüfung wäre der Gateway die Stelle, an der fremde Token eingelöst werden.

**API-Keys bleiben bestehen.** Sie werden zuerst geprüft, das Token danach; ein Agent, der heute
läuft, läuft ohne Umstellung weiter. Ohne `BIFROST_OAUTH_ISSUER` ändert sich gar nichts — der
Standard nennt Autorisierung ausdrücklich optional.

> **Wie eine neue Identität entsteht:** Beim ersten gültigen Token eines unbekannten Subjects legt
> der Gateway eine Identität an — **ohne jede Rolle**. Sie kann damit nichts, bis ein Administrator
> ihr eine gibt (Default-Deny). Das ist Absicht: Die Alternative wäre, unbekannte Subjects
> abzuweisen, und dann sähe nie jemand, wer angeklopft hat. Der Name trägt den Issuer mit
> (`oauth:<issuer>#<sub>`), damit zwei Authorization Server mit gleichem `sub` nicht auf dieselbe
> Identität fallen.

## OAuth gegen Upstreams

Ein HTTP-Upstream, der OAuth verlangt, war bisher nicht anbindbar — statische Header reichen dafür
nicht. Mit `Http.OAuth` holt sich der Gateway ein Token beim Authorization Server des Upstreams:

```json
"Http": {
  "Endpoint": "https://upstream.example.com/mcp",
  "OAuth": { "ClientId": "bifrost-gateway", "ClientSecret": "…" }
}
```

**Voraussetzung:** Der Gateway ist als Client beim Authorization Server registriert, mit der
Redirect-URI `<BIFROST_PUBLIC_BASE_URL>/oauth/upstream/callback`. Dynamic Client Registration ist im
Standard abgelöst, und Client-ID-Metadata-Documents verlangen ein öffentlich abrufbares Dokument —
ein selbst gehosteter Gateway steht oft nicht im Netz. Vorregistrierung ist deshalb der Weg, der
ohne öffentliche Erreichbarkeit funktioniert.

**Verbinden** ist ein einmaliger Schritt eines Administrators: In der Server-Verwaltung *Verbinden*
klicken, beim Authorization Server zustimmen, fertig. Danach erneuert der Gateway das Token selbst,
mit zwei Minuten Sicherheitsabstand vor dem Ablauf.

Was dabei passiert, in der Reihenfolge des Standards: unautorisiert anfragen → `WWW-Authenticate`
lesen → Protected Resource Metadata (RFC 9728) holen → Authorization Server bestimmen → dessen
Metadaten holen → Authorization Code mit **PKCE S256** und dem `resource`-Parameter (RFC 8707), der
das Token an genau diesen Upstream bindet.

**Wo abgebrochen wird, statt es irgendwie hinzubiegen:**

- Der Authorization Server weist **S256 nicht aus** → keine Autorisierung. Ohne PKCE ist der Code
  abfangbar, und ein stiller Verzicht fiele niemandem auf.
- Die Metadaten nennen einen **anderen Issuer** als den angefragten → Abbruch. Sonst liefe die
  spätere Prüfung gegen einen Wert, den die Gegenseite vorgegeben hat.
- Der `iss`-Parameter der Antwort **passt nicht** zum notierten Issuer (RFC 9207) → Abbruch, auch
  bei Fehlerantworten. Das ist der Schutz gegen untergeschobene Antworten eines fremden Servers.
- Discovery- oder Token-Endpunkt zeigen **ins interne Netz** → Abbruch. Diese Adressen kommen vom
  Upstream; es ist derselbe SSRF-Weg wie bei importierten Schemabeschreibungen, und es greift
  dieselbe Prüfung. `AllowPrivateTargets` ist die ausdrückliche Ausnahme.
- **Kein HTTPS** → Abbruch. Ein Token über Klartext ist keins.
- Die **Erneuerung scheitert** → der Upstream kommt nicht hoch, statt es mit dem alten Token zu
  versuchen. Ein 401 im Betrieb wäre schwerer zu deuten als eine klare Meldung beim Start.

Token liegen DataProtection-verschlüsselt in einer eigenen Tabelle, getrennt von der
Konfigurationshistorie — ein Token erneuert sich laufend, und jede Erneuerung als
Konfigurationsversion zu führen wäre Unsinn.

> **Was das nicht ist:** Alle Agenten teilen sich weiterhin die Identität, unter der der Gateway
> beim Upstream angemeldet ist. Ein Token **je Nutzer** würde jedem Agenten einen zugeordneten
> Menschen abverlangen, der selbst zustimmt — das ändert das Modell und ist bewusst nicht Teil
> dieses Schritts.

## Geänderte Tool-Definitionen (Rug-Pull-Schutz)

Ein Upstream, dem einmal vertraut wurde, kann später still die **Beschreibung** eines Tools ändern.
Die Beschreibung landet unverändert im Kontext des Modells — sie ist damit der bequemste Weg,
Anweisungen einzuschleusen, ohne die Konfiguration anzufassen. Kein MCP-Standard verlangt Integrität
von Tool-Definitionen; das OWASP-Cheat-Sheet fordert sie ausdrücklich, und CVE-2025-54136 zeigt den
Fall in freier Wildbahn.

Der Gateway hält deshalb bei jeder Discovery einen Fingerabdruck über **Name, Beschreibung und
Eingabeschema** fest und vergleicht ihn:

| Fall | Verhalten |
|---|---|
| Erstsichtung | Wird übernommen (Trust-on-first-use) und ist ab dann der Bezugspunkt. |
| Unverändert | Nichts passiert. |
| **Abweichend** | Das Tool wird **zurückgehalten** — nicht sichtbar, nicht aufrufbar — und die Abweichung landet im Audit. |
| Zurück zum angenommenen Stand | Die Abweichung gilt als erledigt. |

**Zurückgehalten wird nur das geänderte Tool, nicht der ganze Server.** Ein Rug Pull zielt auf ein
Tool; den Upstream komplett abzuschalten wäre bei jedem normalen Update Kollateralschaden — und ein
Schutz, der bei jedem Update den Betrieb anhält, wird abgeschaltet.

Die neue Fassung nimmt ein Administrator an, in der UI unter *Server* (das zurückgehaltene Tool
steht dort mit Knopf) oder über die API:

```bash
curl -X POST http://localhost:8080/api/v1/tool-definitions/<serverId>/<tool>/accept \
  -H "Authorization: Bearer $KEY"
```

Danach wird der Katalog des Upstreams neu abgefragt, und das Tool ist mit der neuen Fassung wieder
da — ohne Neustart.

> **Was das nicht leistet:** Trust-on-first-use schützt gegen Änderungen **nach** der Aufnahme, nicht
> gegen einen von Anfang an bösartigen Upstream. Dafür sind Herausgeber-Signaturen zuständig (siehe
> Connector-Pakete unten) und die Entscheidung, wen man überhaupt anschließt.

## Connector-Pakete installieren

Ein Connector kann als signiertes Paket kommen statt als Pfad in der Konfiguration
([ADR-0016](adr/0016-versionierter-connector-plugin-vertrag.md)). Ein `.mcpkg` ist ein ZIP mit
`manifest.json`, dessen Ed25519-Signatur `manifest.sig` und den Nutzdateien; das Manifest nennt den
SHA-256 jeder Datei.

**Voraussetzung ist ein gepinnter Herausgeber.** Ohne ihn wird nichts installiert — ein leerer
Trust-Store heißt fail-closed, nicht „keine Einschränkung":

```bash
curl -X POST http://localhost:8080/api/v1/publishers -H "Authorization: Bearer $KEY" \
  -H 'Content-Type: application/json' \
  -d '{"publicKey":"<base64-32-byte>","label":"Beispiel GmbH"}'
```

Die **Vertrauensstufe** entscheidet, wie viel ein Paket dieses Herausgebers ohne Rückfrage bekommt.
Vorgabe ist `ThirdParty`; ein gepinnter Schlüssel heißt „dieser Herausgeber ist echt", nicht
„dieser Herausgeber darf ins Dateisystem":

| Stufe | Was ohne Rückfrage gilt |
|---|---|
| `Core` | Mit dem Produkt ausgeliefert. **Nicht installierbar** — ein Paket kann das nicht sein. |
| `Official` | Die Zugriffe aus dem signierten Manifest werden erteilt. |
| `ThirdParty` | Jeder Zugriff nach außen ist beim Installieren einzeln zu bestätigen. |
| `Community` | Wie `ThirdParty`, zusätzlich muss das Paket selbst freigegeben werden. |

```bash
curl -X PUT http://localhost:8080/api/v1/publishers/<keyId>/trust-level \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' -d '{"level":"Official"}'
```

Installiert wird über die UI (*Connector-Pakete*) oder die API:

```bash
curl -X POST http://localhost:8080/api/v1/packages?grant=env:TOKEN \
  -H "Authorization: Bearer $KEY" --data-binary @connector.mcpkg
```

**Was beim Installieren passiert**, in dieser Reihenfolge: Signatur gegen die gepinnten Herausgeber
prüfen → Manifest lesen → Hash jeder Datei vergleichen → in **Quarantäne** auspacken → den Connector
dort **wirklich starten** und seinen Katalog abfragen → erst dann atomar aktivieren. Ein Paket, das
die Probe nicht besteht, hat nie in Betrieb gestanden; der Fehlschlag bleibt mit Begründung sichtbar.

Die vorherige Version bleibt liegen. Ein Rollback ist deshalb ein Schalter und kein neuer Download:

```bash
curl -X POST http://localhost:8080/api/v1/packages/com.example.connector/rollback \
  -H "Authorization: Bearer $KEY"
```

Ein Upstream verweist über `Wasi.PackageId` auf das Paket statt auf Dateipfade — ein Update
wechselt damit die Dateien, ohne dass jemand die Konfiguration anfasst. Gibt es keine aktive
Version, kommt der Upstream **nicht** hoch; ein Rückfall auf alte Pfade wäre eine stille Abweichung
von dem, was konfiguriert ist.

> **Grenzen heute:** Nur WASI-Connectoren sind paketierbar, und Pakete werden hochgeladen, nicht
> aus einem Verzeichnisdienst geholt.

## Ziele im internen Netz (OpenAPI und OpenRPC)

Beide Konnektoren rufen Adressen ab, die ein Administrator konfiguriert hat. Ohne Prüfung wäre der
Gateway damit ein Werkzeug, um **interne** Dienste zu erreichen — den Cloud-Metadatendienst auf
`169.254.169.254`, einen Admin-Port auf `127.0.0.1`, einen Nachbarn im Firmennetz. Geprüft wird
deshalb beides: die **Quelle der Beschreibung** und die **Ziel-API**. Der Hostname wird aufgelöst
und *alle* seine Adressen geprüft; Weiterleitungen beim Laden werden einzeln erneut geprüft.

Im **Aufrufpfad** folgt kein Konnektor einer Weiterleitung. Ein `302` der Gegenstelle zeigte sonst
auf eine Adresse, die nie geprüft wurde; stattdessen kommt der Aufruf mit einem Fehler zurück, der
das Ziel nennt. Ist die Weiterleitung beabsichtigt, gehört die Zieladresse in die Konfiguration.

Für einen Dienst im eigenen Netz — in Entwicklungsaufbauten der Normalfall — setzt man den Schalter
ausdrücklich und pro Upstream:

```json
"OpenApi": {
  "SpecLocation": "http://localhost:8080/openapi.json",
  "AllowPrivateTargets": true
}
```

In der UI ist es das Häkchen „Ziele im internen Netz erlauben" im OpenAPI-Formular.

> **Umstellung:** Vorgabe ist `false`. Bestehende OpenAPI-Upstreams, die auf `localhost` oder ein
> privates Netz zeigen, kommen nach dem Update nicht mehr hoch, bis der Schalter gesetzt ist. Die
> Fehlermeldung nennt die Adresse und den Schalter beim Namen — der Upstream steht auf `Failed`,
> nichts läuft still weiter.

## CLI-Programme im Container ausführen

Ein CLI-Upstream läuft standardmäßig als Host-Prozess: gehärtet (absolute Pfade, Root-Allowlist,
minimale Umgebung, Prozessbaum-Kill), aber **keine Sandbox**. Für Programme, denen man nicht
vertraut, gibt es seit [ADR-0018](adr/0018-native-prozess-und-container-isolation.md) den
Container-Modus:

```json
"Cli": {
  "Executable": "/usr/bin/werkzeug",
  "Isolation": { "Mode": "Container", "Image": "meine-registry/werkzeug@sha256:…" },
  "AllowedReadRoots": ["/daten/ein"],
  "AllowedWriteRoots": ["/daten/aus"]
}
```

Was der Container mitbringt, ohne dass man es einstellen muss: read-only Wurzeldateisystem, fester
Nicht-root-Benutzer, alle Linux-Capabilities entfernt, `no-new-privileges`, CPU-/RAM-/PID-Grenzen,
ein beschreibbares `/tmp` als tmpfs, **kein Netzwerk**, und ein Container je Aufruf, der danach
verschwindet.

Wichtig für die Konfiguration:

- **`Executable` liegt im Image**, nicht auf dem Host. Ein `ExecutableSha256` wird abgelehnt — er
  prüfte eine Datei auf diesem Rechner. Der passende Pin ist ein **Image-Digest** im Image-Namen
  (`@sha256:…`), so wie oben.
- **Mounts kommen aus `AllowedReadRoots`/`AllowedWriteRoots`** — denselben Listen, die der
  Host-Modus schon durchsetzt. Was nicht darin steht, sieht das Programm nicht.
- **Secrets** aus `EnvironmentVariables` erreichen das Programm über die Umgebung. Sie stehen nie in
  der Kommandozeile des Container-Prozesses, wo jeder sie über die Prozessliste läse.
- **Netzwerk ist aus und bleibt es vorerst.** Eine `NetworkAllow`-Liste wird abgelehnt statt als
  offenes Bridge-Netz durchgereicht — ein offenes Netz mit dem Etikett „Allowlist" wäre schlimmer
  als eine ehrliche Absage.

**Kein stiller Rückfall.** Verlangt eine Konfiguration Container und ist keine passende Runtime da,
kommt der Upstream **nicht** hoch, mit genau dieser Meldung. Ein Ausweichen auf den Host würde die
Isolation abschalten, ohne dass es jemand merkt.

Geprüft wird dabei nicht nur, *ob* die Runtime antwortet, sondern ob sie die Policy durchsetzen
kann: **Docker im Windows-Container-Modus** antwortet bereitwillig und lehnt dann `--read-only`,
`--cap-drop` und `--user` ab. Auf einem Windows-Host braucht der Container-Modus deshalb Docker im
**Linux-Modus** (WSL2-Backend); sonst wird er abgelehnt statt ungeschützt ausgeführt.

Nicht abgedeckt: **stdio-Upstreams**. Deren Vertrag ist eine langlebige Verbindung, kein Job je
Aufruf — dafür braucht es einen eigenen Entwurf.

## WASI-Components als Upstream

Ein signiertes WebAssembly-Component läuft in einem eigenen Rust-Host-Prozess
([ADR-0020](adr/0020-wasi-runtime-out-of-process-rust-host.md)). Der Host **liegt im Image** unter
`/usr/local/bin/bifrost-wasi-host` — genau dieser Pfad gehört in `Wasi.HostExecutable` eines
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

### Resources: `Wasi.PersistentInstance`

Manche Components geben `resource`-Handles aus — ein Handle ist ein Index in die Guest-Instanz, die
es ausgegeben hat, und überlebt den Aufruf. Damit das funktioniert, muss die Instanz leben bleiben:
`Wasi.PersistentInstance: true`. Ohne das Flag bekommt jeder Aufruf eine frische Instanz, und der
Host weist Resource-Aufrufe mit genau dieser Begründung ab.

Über die Leitung ist ein Handle ein undurchsichtiges Objekt (`{"handle": "res-1"}`); der Wert
bleibt im Host. Jedes Handle gehört dem Aufrufer, für den es entstanden ist — ein anderer bekommt
„Handle ist unbekannt", ob es den Namen nun gibt oder nicht.

**Die Auflage dazu:** Eine persistente Instanz teilt ihren **internen** Zustand (Globals, linearer
Speicher) zwischen allen Aufrufern desselben Upstreams. Die Handle-Trennung ändert daran nichts —
sie verhindert nur, dass ein Aufrufer ein fremdes Handle benennt. Das Flag gehört deshalb nur an
Upstreams, die Resources wirklich brauchen, und nur an Components, denen man das Zusammenlegen
zutraut. Wer strikte Trennung braucht, legt pro Mandant einen eigenen Upstream an.

Betriebliches: `health` meldet die offenen Handles der Instanz; über 256 lehnt der Host neue ab.
Ein Trap verwirft die Instanz samt Handles, der nächste Aufruf startet frisch. Ein Reload (neues
Component, Hot-Swap) beendet die Instanz ebenfalls — Handles von davor sind danach ungültig.

### Kapazität: bis zu 16 Aufrufe gleichzeitig

Seit Vertrag v4 trägt jede Anfrage eine Korrelations-Id, und Antworten dürfen in anderer
Reihenfolge zurückkommen. Ein langsames Component blockiert damit **nicht mehr** die übrigen
Aufrufe desselben Upstreams; jeder bekommt seine eigene Instanz mit eigenem Store.

Die Grenze liegt bei **16 gleichzeitigen Aufrufen je Host-Prozess**. Darüber antwortet der Host mit
`too-many-calls`, statt weiter Speicher zu binden: `MaxMemoryBytes` gilt pro Aufruf, ohne Grenze
wäre der Speicherbedarf das Produkt aus Limit und Anzahl der Anfragen. Wer dauerhaft mehr braucht,
legt den Upstream mehrfach an — jeder bekommt seinen eigenen Host-Prozess.

**Eine Ausnahme:** Mit `Wasi.PersistentInstance` gibt es genau eine Guest-Instanz, und die kann
nicht zweimal gleichzeitig rechnen. Aufrufe darauf laufen weiterhin nacheinander. Sie sind
trotzdem abbrechbar (siehe unten) — sie blockieren nur einander, nicht den Host.

### Abbruch: ein Aufruf lässt sich stoppen

Bricht der Aufrufer ab (Per-Call-Timeout FR-09, abgebrochener Request), schickt das Gateway ein
`cancel` an den Host. Der trappt den laufenden Guest über die Epoche und antwortet erst, wenn der
Aufruf **wirklich** beendet ist — `confirmed: true` heißt beendet, nicht „Abbruch abgeschickt".
Bleibt die Bestätigung binnen fünf Sekunden aus, meldet der Host `confirmed: false`; dann läuft der
Guest noch, und es greift weiterhin nur sein Fuel- oder Zeitlimit.

Der Unterschied ist im Ergebnis sichtbar: Ein abgebrochener Aufruf endet mit dem Code `cancelled`,
eine abgelaufene Frist mit dem gewohnten Timeout. Wer im Audit nachsieht, muss nicht raten, welches
von beidem passiert ist.

### Bereitschaft, Phasen und geordnetes Beenden

`health` unterscheidet **Leben** von **Bereitschaft**. `status: "ok"` heißt nur, dass der Host
antwortet; `ready: true` heißt, dass er Aufrufe annimmt. Dazwischen liegen echte Zustände, die das
Feld `phase` nennt:

| Phase | Bedeutung |
|---|---|
| `handshake` | Prozess läuft, Version noch nicht verhandelt |
| `negotiated` | verhandelt, aber kein Component geladen — Aufrufe gingen ins Leere |
| `ready` | geladen und annahmebereit |
| `draining` | nimmt keine neuen Aufrufe mehr an; die laufenden dürfen zu Ende kommen |

Dazu meldet `health` die Zahl der gerade laufenden Aufrufe (`inFlight`) und die offenen
Resource-Handles.

**Beim Beenden erst drainieren.** Das Gateway schickt `drain` (Vorgabe 5 s Frist) und danach
`shutdown`. Ein `shutdown` ohne Drain bricht laufende Aufrufe ab — seit Vertrag v4 können mehrere
gleichzeitig unterwegs sein, und der Host beendet sie, statt beliebig lange auf sie zu warten. Die
Antwort auf `drain` sagt, ob es sauber war: `idle: true` heißt, es lief nichts mehr. Ein Aufruf, der
nach dem Drain noch eintrifft, bekommt den Code `draining` — nicht einen allgemeinen Fehler.

**Capability-Flags beim Handshake.** Der Host nennt in der `hello`-Antwort, was er kann
(`cancellation`, `concurrency`, `drain`, `readiness`, `persistentInstances`, `resources`, `secrets`,
`diskCache`) und was nicht (`streams: false`). Fehlt dem Gateway ein Pflichtfeature, kommt der
Upstream gar nicht hoch — mit Nennung des fehlenden Namens, statt beim ersten Aufruf zu scheitern.

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
  `X-Bifrost-Signature: sha256=<hmac>` und `X-Bifrost-Timestamp: <unix-sekunden>`.
- Anfragen älter als **5 Minuten** werden abgewiesen (Replay-Schutz).
- Fehlende, falsche oder abgelaufene Signatur → **401**. Eine unbekannte Webhook-Id liefert
  ebenfalls 401, damit sich keine gültigen Ids durchprobieren lassen.

Der ausgelöste Aufruf durchläuft die **volle Pipeline** — RBAC der gebundenen Identität, Guardrail,
Rate-Limit — und erscheint im Audit mit Herkunft **`Webhook`**. Ein Webhook kann damit nie mehr,
als seine Identität ohnehin darf.

**Grenze:** Ein Webhook löst genau ein Tool aus, keine Kette. Mehrstufige Abläufe sind v2.

## Ergebnis-Kompression

Ein einzelnes umfangreiches Tool-Ergebnis kann die Token-Ersparnis der Profile wieder auffressen.
`BIFROST_MAX_RESULT_CHARS` begrenzt das:

```
BIFROST_MAX_RESULT_CHARS=20000
```

Standardmäßig **aus** — Kürzen ist verlustbehaftet, das soll niemand unbemerkt bekommen. Wenn es
greift, bleibt das Ergebnis gültiges JSON und trägt das Feld `_bifrost_truncated: true` samt Hinweis,
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
BIFROST_AUDIT_DEBUG_PAYLOADS=1
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
claude mcp add --transport http bifrost http://localhost:8080/mcp \
  --header "Authorization: Bearer <API-KEY>"
```

Der Agent sieht dann die Meta-Tools `search_tools` / `describe_tool` / `invoke_tool` (Lazy-Default) plus die im Profil gepinnten Tools. Upstream-Server, Rollen und Profile werden über die Web-UI oder die REST-API verwaltet.

## PostgreSQL statt SQLite

Für größere Setups (viel Audit-Volumen, mehrere Instanzen an einer DB) die Override-Datei
dazunehmen. Sie setzt Provider und Connection-String selbst; einzutragen ist nur das Passwort:

```bash
echo 'POSTGRES_PASSWORD=…' >> .env
docker compose -f docker-compose.yml -f docker-compose.postgres.yml up -d
```

Das Passwort steht bewusst **nicht** mehr in der Compose-Datei. Ein Vorgabewert wie das frühere
`CHANGE_ME` wird übernommen und nie geändert — fehlt es dagegen, kommt die Datenbank mit einer
eindeutigen Meldung gar nicht erst hoch (*„Database is uninitialized and superuser password is not
specified"*). Alternativ liest das Postgres-Image es aus einer Datei (`POSTGRES_PASSWORD_FILE`); der
Block dafür steht auskommentiert in `docker-compose.postgres.yml`. Der Gateway selbst kann das
nicht — sein Connection-String braucht den Wert in der Umgebung.

Die Override-Datei lässt sich mit dem Entwicklerpfad kombinieren; die Reihenfolge der `-f` ist
beliebig, solange `docker-compose.yml` zuerst kommt:

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml \
               -f docker-compose.postgres.yml up -d --build
```

> **Das Datenverzeichnis bleibt auch mit PostgreSQL nötig.** Der DataProtection-Key-Ring unter
> `/data/keys` liegt nicht in der Datenbank; ohne ihn sind die verschlüsselten
> Upstream-Zugangsdaten unbrauchbar. Beides gehört zusammen ins Backup.

Das Schema wird beim Start automatisch über EF-Migrationen angelegt (siehe [Schema & Upgrades](#schema--upgrades)).

## Schema & Upgrades

Der Gateway verwaltet sein Datenbankschema über EF-Core-Migrationen. Beim Start passiert automatisch genau eine von drei Sachen — das Ergebnis steht im Log (`Datenbank initialisiert (…)`):

| Vorgefunden | Aktion | Log-Ausgabe |
|---|---|---|
| Leere/neue DB | Schema aus Migrationen anlegen | `CreatedFromMigrations` |
| **Alt-DB** aus einem Build vor der Migrationsverwaltung (per `EnsureCreated` erzeugt, ohne Migrationshistorie) | Initial-Migration als Baseline stempeln (**kein DDL, keine Datenänderung**), dann migrieren | `BaselinedLegacySchema` |
| Bereits migrationsverwaltet | ausstehende Migrationen anwenden | `Migrated` |

### Upgrade einer Alt-Datenbank

Es ist **kein manueller Schritt nötig** — der Gateway erkennt das Alt-Schema selbst und stempelt die Baseline. Trotzdem gilt die übliche Sorgfalt:

1. Dienst stoppen.
2. **Datenverzeichnis sichern** (`bifrost.db` **und** `keys/`, siehe [Backup](#backup)).
3. Neue Version starten und im Log `BaselinedLegacySchema` bestätigen.

Bei einem Rollback auf einen solchen Alt-Build ist die zusätzliche Tabelle `__EFMigrationsHistory` unschädlich — er ignoriert sie.

Jeder Provider hat eine eigene Migrations-Assembly (`Bifrost.Persistence.Migrations.Sqlite` bzw. `.Postgres`), weil SQLite und PostgreSQL unterschiedliches DDL brauchen. Beide sind im Image enthalten; die Auswahl erfolgt automatisch über `BIFROST_DB_PROVIDER`.

## TLS / Reverse-Proxy

Der Gateway terminiert selbst kein TLS. **Immer hinter einen Reverse-Proxy** (Caddy, nginx, Traefik) mit TLS setzen — der Gateway hält Upstream-Credentials und API-Keys, ein Klartext-Transport ist inakzeptabel (NFR-04). Beispiel Caddy:

```
gateway.example.com {
    reverse_proxy localhost:8080
}
```

Der Proxy sollte `X-Forwarded-*`-Header setzen; das UI-Cookie ist `SameSite=Strict` + `HttpOnly`.

## Backup

Alles Persistente liegt im Datenverzeichnis (`BIFROST_DATA_DIR`):

- `bifrost.db` — Konfiguration, RBAC, API-Key-Hashes, Audit-Log (bei SQLite).
- `keys/` — **DataProtection-Key-Ring**. Ohne ihn sind die verschlüsselten Upstream-Credentials unbrauchbar.
- `packages/` — installierte Connector-Pakete.
- `config/instance.json` — die stabile Kennung dieser Installation. Sie entsteht beim ersten Start
  und steht im Manifest jeder Sicherung; daran erkennt man später, ob ein Archiv hierher gehört.

Seit M2 gibt es dafür einen Produktpfad statt „Dateien raten" (ADR-0024). Alles darunter läuft über
`bifrost` gegen einen **laufenden** Gateway.

> **Ein Vollbackup ist ein Geheimnis.** Es enthält den Key-Ring — also den Schlüssel zu allen
> gespeicherten Upstream-Zugangsdaten, OAuth-Token und Webhook-Secrets. Wer es hat, hat die Instanz
> (ADR-0024 E3). Es gehört auf ein Ziel, das genauso geschützt ist wie das Datenverzeichnis selbst,
> oder es wird mit einer Passphrase verschlüsselt.

### Sichern, prüfen, zurückspielen

```bash
# Vollsicherung. Der Pfad gilt auf dem Rechner, auf dem der GATEWAY laeuft.
bifrost backup create --out /data/backups/bifrost-2026-07-31.zip

# Nur einzelne Bereiche:
bifrost backup create --out /data/backups/nur-db.zip --sections database,config

# Verschluesselt. Die Passphrase kommt aus einer Umgebungsvariablen oder aus einer
# Eingabe ohne Echo — NIE als Argument (sie stuende in ps und in der Shell-Historie):
BIFROST_BACKUP_PASSPHRASE=… bifrost backup create --out /data/backups/a.zip \
    --passphrase-env BIFROST_BACKUP_PASSPHRASE
bifrost backup create --out /data/backups/a.zip --passphrase-prompt

# Pruefen, ohne zurueckzuspielen (Manifest, Pruefsummen, Vollstaendigkeit):
bifrost backup verify /data/backups/bifrost-2026-07-31.zip

# Zurueckspielen. Zeigt ZUERST den Plan und wendet erst danach an:
bifrost restore /data/backups/bifrost-2026-07-31.zip

# Auf eine Instanz, die nicht leer ist — verlangt zusaetzlich eine Bestaetigung:
bifrost restore /data/backups/bifrost-2026-07-31.zip --replace
```

`--replace` fragt zurück; die Antwort ist das Wort `replace`. In Skripten übernimmt `--yes` diese
Bestätigung. Vor dem Überschreiben entsteht automatisch eine Sicherung des Altzustands — ohne
Ausweg kein Überschreiben (ADR-0024 E5).

**Ein Restore braucht einen Wartungsmoment.** Er tauscht Datenbank und Key-Ring aus; das lässt sich
im laufenden Schreibbetrieb nicht atomar halten. Gateway stoppen, zurückspielen, starten.

### Exit-Codes

Alle Betriebsbefehle benutzen dieselbe Tabelle (M2-Vertrag §4):

| Code | Bedeutung |
|---:|---|
| `0` | Erfolg, keine Warnung |
| `1` | unerwarteter Fehler (auch: Gateway nicht erreichbar) |
| `2` | Bedienfehler — fehlendes Argument, fehlende Berechtigung, auf dieser Instanz nicht anwendbar |
| `3` | Diagnose mit **Warnung** |
| `4` | Diagnose mit **Fehler** |
| `5` | Archiv ungültig, beschädigt oder inkompatibel (auch: Import mit Konflikten) |
| `6` | Zielinstanz nicht leer und kein `--replace`, oder `--replace` nicht bestätigt |

Ein übersprungener Diagnose-Check (`Skipped`) ist **neutral** und ergibt `0`: Er steht sichtbar mit
Begründung im Bericht, und das ist die Aussage.

### PostgreSQL

**Es gibt keine PostgreSQL-Sicherung über `bifrost backup`.** Vorgesehen ist `pg_dump`
(ADR-0024 E2); solange das nicht gebaut ist, lehnt der Befehl mit einer Meldung ab, statt still
Zeilen zu exportieren. Für PostgreSQL also weiterhin: Datenbank mit `pg_dump` sichern und `keys/`
aus dem Datenverzeichnis dazu — beides gehört zusammen.

Dieselbe Grenze gilt für die automatische Sicherung vor Migrationen (siehe unten).

### Sicherung vor jeder Migration

Bei **SQLite** entsteht vor einer schemaändernden Migration automatisch eine Vollsicherung unter
`<Datenverzeichnis>/backups/pre-migration-*.zip` — und ohne sie wird nicht migriert (ADR-0024 E7).
Der Pfad steht im Migrationsjournal und in der Meldung, falls die Migration scheitert.

Diese Sicherung ist standardmäßig **unverschlüsselt**; sie liegt im selben Schutzbereich wie die
Datenbank, aus der sie entsteht. Wer sie verschlüsselt haben will, setzt `BIFROST_BACKUP_PASSPHRASE`
— dann ist die Sicherung ohne diese Passphrase allerdings wertlos.

Bei **PostgreSQL** entsteht keine automatische Sicherung (siehe oben). Der Start warnt und migriert
weiter; die Sicherung ist dort Betriebspflicht.

## Diagnose

```bash
bifrost doctor                       # alles
bifrost doctor --scope database,network
bifrost --json doctor                # maschinenlesbar
```

Der Bericht liest nur. Einzige Ausnahme ist die Schreibprobe im Datenverzeichnis: Sie legt eine
Datei `.bifrost-doctor-*.tmp` an und löscht sie sofort wieder — rein lesend lässt sich
Beschreibbarkeit auf keinem der beiden Betriebssysteme verlässlich beantworten.

Jeder Befund trägt einen **stabilen Code** (`BFR-DB-0002`), auf den sich ein Runbook stützen kann;
der Text daneben darf sich ändern, der Code nicht.

| Präfix | Bereich |
|---|---|
| `BFR-CFG-*` | Konfiguration, Umgebungsvariablen, Datenverzeichnis |
| `BFR-DB-0001…0099` | Datenbank, Migrationen, Provider |
| `BFR-DB-0100…0199` | Startkoordination: Lock, Journal, Schemastand |
| `BFR-KEY-*` | DataProtection-Key-Ring |
| `BFR-NET-*` | Ports, öffentliche Adresse, Proxy-Vertrauen |
| `BFR-RT-*` | Container-Runtime, WASI-Host |
| `BFR-UP-*` | Upstreams |

Dieselbe Ansicht gibt es in der Oberfläche unter **Betrieb** (nur für Admins), zusammen mit dem
Anlegen einer Sicherung.

## Wenn der Start BFR-DB-0101 meldet

`BFR-DB-0101` heißt: Ein früherer Migrationslauf ist mittendrin abgebrochen, der Schemazustand ist
unbekannt — und der Gateway **verweigert deshalb den Schreibbetrieb**, indem er gar nicht erst
hochkommt. Er repariert von sich aus nichts; das ist Absicht (ADR-0024 E7).

Der Weg zurück:

1. **Datenbank beurteilen.** Steht im Journaleintrag ein Sicherungspfad (`backupPath`), ist das die
   Sicherung, die unmittelbar vor dem Lauf entstanden ist. Im Zweifel diese zurückspielen:
   `bifrost restore <archiv> --replace` bei laufendem Gateway einer *anderen* Installation, oder
   das Datenverzeichnis von Hand aus dem Archiv herstellen.
2. **Riegel lösen.** Erst wenn der Zustand geprüft ist:

```bash
# Der Gateway laeuft NICHT — deshalb im Serverprozess, nicht ueber die CLI:
docker compose run --rm bifrost dotnet Bifrost.Server.dll --db-unblock
# ohne Container:
dotnet run --project src/Bifrost.Server -- --db-unblock
```

3. Starten und im Log `Datenbank initialisiert` bestätigen.

> **Warum nicht `bifrost db unblock`?** Den Befehl gibt es, und er tut dasselbe — aber er spricht
> über HTTP mit einem laufenden Gateway. Im Normalfall von `BFR-DB-0101` läuft keiner. Nützlich ist
> er dort, wo eine zweite Instanz noch läuft (PostgreSQL, mehrere Knoten) oder wo der Eintrag
> vorsorglich weggeräumt wird, bevor jemand neu startet.

Beide Wege beurteilen den Schemazustand **nicht**. Sie lösen, was der Betreiber geprüft hat.

## Konfiguration exportieren und übernehmen

Ein Konfigurationsexport ist **kein Backup** (ADR-0024 E8): Er stellt nicht dieselbe Instanz wieder
her, sondern baut eine gleichartige auf — und er enthält deshalb **keine Secretwerte**, sondern
Referenzen und Masken.

```bash
# Ohne Zugangsdaten, in eine LOKALE Datei (die Nutzlast ist JSON und reist durch die Antwort):
bifrost config export --out konfiguration.json

# Mit Zugangsdaten — nur verschluesselt, das ist keine Option:
bifrost config export --include-secrets --passphrase-env EXPORT_PASSPHRASE --out voll.json

# Zielinstanz: erst zeigen, was entstuende …
bifrost config import konfiguration.json --dry-run
# … dann anwenden:
bifrost config import konfiguration.json
```

Der Import ist zweistufig und **ausschließlich additiv**: Er legt an, überschreibt nichts und löscht
nichts. Konflikte und fehlende Abhängigkeiten stehen im Plan und führen zu Exit `5`, bevor
irgendetwas geschrieben wird.

| | Backup | Konfigurationsexport |
|---|---|---|
| Zweck | dieselbe Instanz wiederherstellen | eine gleichartige Instanz aufbauen |
| Enthält | alles, inkl. Key-Ring | Server, Rollen, Profile, Regeln, Skills |
| Secrets | ja (deshalb schützenswert) | nein — Referenzen oder Masken |
| Format | ZIP mit Manifest | JSON, versioniert |

### Berechtigung

Alle Betriebs-Endpunkte (`/api/v1/operations/*`) verlangen eine Identität mit **Global-Grant** —
dieselbe Schwelle wie RBAC-Verwaltung und Paketinstallation. In der Oberfläche liegt die Seite
**Betrieb** hinter der Admin-Rolle. Jeder schreibende Vorgang steht im Audit-Log.

## Audit-Retention

Das Audit-Log wächst mit jedem Call. Default-Aufbewahrung: 30 Tage, stündlicher Bereinigungs-Job (FR-25). Bei SQLite ist Retention **Betriebspflicht** (ADR-0007) — sehr große Logs (> ~10 GB) sind ein Grund, auf PostgreSQL zu wechseln.

## Metriken und Traces

Der Gateway misst jeden Tool-Call (FR-26) unter dem Meter `Bifrost.Gateway`:

| Instrument | Bedeutung | Dimensionen |
|---|---|---|
| `bifrost.tool_calls` | Zähler aller Calls — daraus ergeben sich Calls/s und Fehlerquote | `server`, `tool`, `status`, `origin` |
| `bifrost.tool_call_duration` | Latenz-Histogramm (ms) — daraus Perzentile | `server`, `tool`, `status` |

Der Export ist **aus**, solange kein Ziel konfiguriert ist (sonst würde der Exporter dauerhaft ins Leere laufen):

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://collector:4317
```

Exportiert wird per **OTLP** — der OpenTelemetry-Standard. Für **Prometheus** einen OTel-Collector davorschalten, der OTLP annimmt und einen Scrape-Endpoint anbietet; ein direkter Prometheus-Exporter ist im .NET-Ökosystem noch nicht stabil veröffentlicht, deshalb bewusst dieser Weg.

### Traces

Derselbe Schalter aktiviert **Traces** aus der Quelle `Bifrost.Gateway`. Metriken beantworten „wie
viele und wie schnell im Mittel", Traces beantworten „wo ist die Zeit *dieses einen* Aufrufs
geblieben":

| Span | Bedeutung | Tags |
|---|---|---|
| `bifrost.tool_call` | Der gesamte Aufruf durch die Pipeline | `bifrost.tool`, `bifrost.server`, `bifrost.status`, `bifrost.origin`, `bifrost.caller` |
| `bifrost.upstream_call` | Nur der Fremdanteil, als Kind-Span | `bifrost.server`, `bifrost.upstream_tool` |

Die Differenz zwischen beiden ist der **Gateway-Overhead** — genau die Frage, die NFR-01 stellt.
Ohne die Trennung sieht man in einer langsamen Antwort nicht, wer sie verursacht hat.

Ein Aufruf, der nicht mit `Success` endet, wird als Fehler-Span markiert. Ein Deny oder ein
Guardrail-Treffer ist kein Serverfehler, aber auch kein gelungener Aufruf — in einer Fehlersuche
will man ihn sehen.

> **Spans tragen keine Argumente und keine Ergebnisse.** Das Audit-Log ist redigiert, ein
> Telemetrie-Backend ist es nicht — ein Payload im Span wäre der bequemste Weg, die Redaction zu
> umgehen, und zwar an eine Stelle, die oft weniger geschützt ist als die Datenbank. Ein Test hält
> das fest.

`/healthz` und `/readyz` sind vom Tracing ausgenommen; im Sekundentakt laufende Probes würden den
Trace-Strom fluten, ohne etwas über einen Tool-Aufruf zu sagen.

## Health / Readiness

- `GET /healthz` — Prozess lebt (anonym).
- `GET /readyz` — DB erreichbar + Upstream-Zustände (anonym).

Der Container-Healthcheck nutzt `dotnet Bifrost.Server.dll --healthcheck` (self-ping, da das schlanke Runtime-Image kein `curl` enthält). Der Container läuft als non-root `app`-User.

## Key-Ring schützen

Der DataProtection-Key-Ring unter `<datadir>/keys/` entschlüsselt **sämtliche** at-rest
verschlüsselten Upstream-Zugangsdaten, OAuth-Token und Webhook-Secrets dieser Instanz. Wie er
geschützt wird, ist eine von **drei** Betriebsarten — und keine davon ist ein Vorgabezustand.

| Betriebsart | Was sie verlangt | Was sie schützt | Was sie nicht schützt |
|---|---|---|---|
| `certificate` | `BIFROST_KEYRING_CERT_PATH` (PFX mit privatem Schlüssel), Passwort in `BIFROST_KEYRING_CERT_PASSWORD` | Die Schlüsseldateien sind verschlüsselt. Ein **Backup oder Volume-Abzug allein** reicht nicht mehr für die Zugangsdaten | Das Passwort steht in `.env` und in der Prozessumgebung — lesbar für jeden, der `docker inspect` darf |
| `file-secret` | dasselbe, aber das Passwort über `BIFROST_KEYRING_CERT_PASSWORD_FILE` aus einer Datei (Compose-/K8s-Secret) | zusätzlich: das Passwort verlässt nie die Secret-Ablage. Weder `.env` noch `docker inspect` noch `/proc/<pid>/environ` zeigen es | Wer Root auf der Maschine ist, kommt an beides. Das ist die Grenze jedes dateibasierten Verfahrens |
| `none` | ausdrücklich `BIFROST_KEYRING_PROTECTION=none` | **nichts** — die Schlüsseldateien liegen im Klartext. Vertretbar für eine Einzelinstanz mit restriktiven Verzeichnisrechten | Jede Sicherung des Datenverzeichnisses enthält damit die Upstream-Zugangsdaten (ADR-0024 E3) |

Ist **gar nichts** gesetzt, gilt keine dieser Betriebsarten: Der Ring liegt zwar wie bei `none` im
Klartext, aber niemand hat das entschieden. Der Start warnt, `bifrost doctor` meldet `BFR-KEY-0002`
als Warnung, und `--keyring-check` endet mit Exit-Code 3. Wer den ungeschützten Betrieb will, wählt
ihn — dann ist es eine Entscheidung und keine Lücke, und die Diagnose wird grün.

### Einrichten

Der Serverprozess bringt den Weg mit. Er erzeugt Zertifikat **und** Passwortdatei und setzt beiden
restriktive Rechte (Unix `0600`, Windows eine ACL ohne Vererbung) — genau der Schritt, den eine
`openssl`-Zeile aus einer Anleitung nicht tut:

```bash
docker compose run --rm bifrost dotnet Bifrost.Server.dll --keyring-setup --cert /secrets/keyring.pfx
```

Die Ausgabe nennt Fingerabdruck, Ablauf, die gesetzten Rechte und die drei Zeilen, die danach in die
Konfiguration gehören. Ein vorhandenes Zertifikat wird **nie** überschrieben: Ein zweiter
Setup-Lauf, der die Datei ersetzt, hätte den Ring der Instanz entwertet, bevor irgendjemand gefragt
wurde.

Das Zertifikat gehört **neben** das Datenverzeichnis, nicht hinein — sonst liegt es in jedem Backup
mit drin und schützt gegen nichts mehr.

### Unter Compose: PFX **und** Passwort als Secret

```bash
mkdir -p secrets
# Zertifikat und Passwortdatei erzeugen lassen (s. o.), beide landen in ./secrets
# in docker-compose.yml die 'secrets:'-Blöcke einkommentieren, dann in .env:
#   BIFROST_KEYRING_PROTECTION=file-secret
#   BIFROST_KEYRING_CERT_PATH=/run/secrets/bifrost-keyring-pfx
#   BIFROST_KEYRING_CERT_PASSWORD_FILE=/run/secrets/bifrost-keyring-password
docker compose up -d
```

Ein deklariertes, aber fehlendes Secret bricht schon `up` ab — die Dateien müssen **vor** dem
Einkommentieren liegen.

> **`_FILE` gilt je Einstellung und kennt keine Rangfolge.** Sind `BIFROST_KEYRING_CERT_PASSWORD`
> und `BIFROST_KEYRING_CERT_PASSWORD_FILE` beide gesetzt, bricht der Start mit Meldung ab. Welche
> gewänne, wäre eine Regel, die man nachlesen muss — und wer sie falsch erinnert, betreibt danach
> eine Instanz mit dem falschen Geheimnis. Aus der Secret-Datei fällt genau **ein** abschließender
> Zeilenumbruch weg (`echo geheim > datei` schreibt einen); weiter wird nicht getrimmt.

**Was das schützt und was nicht:** Der Gewinn ist real und liegt beim Backup — ein Volume-Abzug
allein reicht dann nicht mehr, um an die gespeicherten Upstream-Credentials zu kommen. Wer mehr
will, legt das PFX auf ein Medium, das nicht mitgesichert wird.

### Prüfen

```bash
docker compose exec bifrost dotnet Bifrost.Server.dll --keyring-check
```

Meldet Betriebsart, Zertifikatslage, Dateirechte, Zeugeneintrag — und **öffnet den vorhandenen Ring
probehalber auf einer Kopie**. Exit-Codes wie bei `bifrost doctor`: `0` ok, `3` Warnung, `4` Befund.

### Zertifikat wechseln

**Erst durchspielen, dann umstellen.** Ein Wechsel, der erst im Betrieb auffällt, hat die Instanz
bereits unlesbar gemacht:

```bash
docker compose exec bifrost dotnet Bifrost.Server.dll --keyring-rotate --new-cert /secrets/keyring-neu.pfx --new-password-file /secrets/keyring-neu.password
```

Der Befehl kopiert den Ring, versucht ihn mit **neuem und altem** Zertifikat zu öffnen und sagt
entweder „gefahrlos" samt der zu setzenden Zeilen oder „NICHT UMSTELLEN" (Exit-Code 4).

Danach:

```
BIFROST_KEYRING_CERT_PATH=/secrets/keyring-neu.pfx
BIFROST_KEYRING_CERT_PATH_PREVIOUS=/secrets/keyring.pfx
```

Das vorherige Zertifikat bleibt nötig, solange auch nur ein Schlüssel damit verschlüsselt ist.
DataProtection verschlüsselt bestehende Schlüssel **nicht** nach — sie werden erst mit der Zeit
durch neue abgelöst.

### Wenn der Key-Ring fehlt, startet der Gateway nicht mehr

Das ist die wichtigste Änderung. Früher legte DataProtection bei leerem Verzeichnis einfach einen
neuen Ring an: Der Dienst kam hoch, meldete „bereit" — und konnte keine einzige gespeicherte
Zugangsdatei mehr entschlüsseln. Beim v0.11.0-Umstieg hat genau das zugeschlagen (umbenanntes
Volume, leere Ablage, fehlerfreier Start).

Der Start prüft jetzt zwei unabhängige Zeugen:

- **`<datadir>/config/keyring.json`** hält fest, wie viele Schlüssel diese Instanz zuletzt hatte und
  welche. Liegt die Datei vor und ist das Schlüsselverzeichnis leer, ist der Ring verloren.
- **Geheimtext in der Datenbank.** Der Zeugeneintrag liegt im selben Volume wie der Ring und
  verschwindet mit ihm. Steht die Datenbank noch (PostgreSQL, oder eine zurückgespielte
  SQLite-Datei) und enthält verschlüsselte Datensätze, kann es keine frische Installation sein.

Trifft eines von beidem zu, **bricht der Start mit Exit-Code 78 ab** und legt keinen Ersatzring an.
Dasselbe gilt, wenn der Ring da ist, sich mit der konfigurierten Zertifikatslage aber nicht öffnen
lässt — auch dann würde DataProtection sonst daneben einen frischen Schlüssel anlegen, und ab da
wäre auch mit dem richtigen Zertifikat nichts mehr zu retten.

Was dann zu tun ist:

1. Zeigt `BIFROST_DATA_DIR` auf das richtige Volume? Ein umbenanntes Volume sieht genau so aus.
2. Sonst den Key-Ring aus der Sicherung zurückspielen — er liegt im Vollbackup unter `keyring/`
   (ADR-0024 E3: Datenbank und Key-Ring gehören in dieselbe Sicherung, weil sie sich nur gemeinsam
   benutzen lassen).

Die Recovery-Kommandos (`--bootstrap-init`, `--reset-ui-admin`, `--issue-bootstrap-key`,
`--db-unblock`) laufen **vor**
dieser Prüfung und bleiben deshalb erreichbar, wenn der Key-Ring gerade das zweite Problem ist.

Ein **vollständig ausgetauschter** Ring (kein einziger der zuletzt gesehenen Schlüssel ist noch da)
bricht den Start dagegen **nicht** ab: So sieht auch eine legitime Wiederherstellung aus. Er wird
aber einmal deutlich protokolliert und als Audit-Ereignis festgehalten.

### Was der Diagnosebericht sagt — und was nicht

`bifrost doctor` und die Betriebsseite der Oberfläche sind **Admin-only** (`RequireAdmin`
beziehungsweise `UiPolicies.Admin`). Der Bericht nennt trotzdem nie den **Ort** von
Schlüsselmaterial: Vom PFX und von der Passwortdatei steht nur der Dateiname da (`…/keyring.pfx`).
Das Datenverzeichnis und das Schlüsselverzeichnis stehen vollständig darin — sie sind die Angabe,
wegen der ein Betreiber die Diagnose aufruft, und er hat sie selbst gesetzt. Das Passwort erscheint
nirgends, in keiner Form.

| Code | Aussage |
|---|---|
| `BFR-KEY-0001` | Ist Schlüsselmaterial da? |
| `BFR-KEY-0002` | Welche Betriebsart? `none` ausdrücklich gewählt besteht; gar nichts erklärt warnt |
| `BFR-KEY-0003` | Liegen die konfigurierten Zertifikate an ihrem Platz? |
| `BFR-KEY-0004` | **Fehlt Schlüsselmaterial, das laut Zeugeneintrag da sein müsste?** |
| `BFR-KEY-0005` | Kommt das Zertifikatspasswort aus einer Datei oder aus der Umgebung? |

## Zugang zurücksetzen

Ein Setup-Token entsteht von selbst **nur**, solange die Installation noch keinen Zugang hat (siehe
[Erstzugang](#erstzugang)). Danach gibt es über das Netz keinen Weg mehr zu einem zweiten — das ist
der Punkt. Für verlorene Zugänge gibt es Kommandos, die gegen die konfigurierte Datenbank laufen,
den Zugang **einmalig auf der Konsole** ausgeben und sich beenden, ohne den Gateway zu starten:

```bash
# Neues Setup-Token ausstellen (auch auf einer Installation mit bestehenden Zugängen):
docker compose run --rm bifrost dotnet Bifrost.Server.dll --bootstrap-init

# UI-Passwort zurücksetzen (Default-Benutzer "admin"; Rolle bleibt unverändert,
# ein fehlender Nutzer wird als Admin angelegt):
docker compose run --rm bifrost dotnet Bifrost.Server.dll --reset-ui-admin
docker compose run --rm bifrost dotnet Bifrost.Server.dll --reset-ui-admin betreiber

# Notfall-API-Key: legt eine NEUE Agenten-Identität mit Global-Grant an
# (bestehende bleiben unangetastet):
docker compose run --rm bifrost dotnet Bifrost.Server.dll --issue-bootstrap-key

# Riegel aus BFR-DB-0101 lösen — erst nach Prüfung der Datenbank, siehe
# "Wenn der Start BFR-DB-0101 meldet":
docker compose run --rm bifrost dotnet Bifrost.Server.dll --db-unblock
```

Ohne Container analog mit `dotnet run --project src/Bifrost.Server -- --reset-ui-admin`. Den Notfall-Zugang nach Gebrauch wieder entfernen, falls er nur der Wiederherstellung diente.

- **UI-Passwort vergessen, aber anderer Admin existiert** → einfacher über die UI (Seite „UI-Nutzer") neu setzen.

### Der lokale Recovery-Nachweis

`--bootstrap-init` stellt auf einer Installation mit bestehenden Zugängen nur dann ein Token aus,
wenn es **Schreibzugriff auf das Datenverzeichnis** nachweisen kann. Geprüft wird das durch Tun: Es
wird eine Probedatei angelegt, zurückgelesen und wieder entfernt.

Das ist keine erfundene Hürde, sondern die Benennung der richtigen. Wer in das Datenverzeichnis
schreiben kann, kann die Datenbank austauschen und den Dienst mit leerem Volume neu starten — er
bekäme ohnehin einen frischen Erstzugang. Was der Nachweis zuverlässig ausschließt, ist der Weg,
um den es geht: **über das Netz**. Am HTTP-Endpunkt gibt es keinen Weg, ein Token *anzufordern*;
dort lässt sich nur eines *einlösen*, das bereits ausgestellt ist.

Alle drei Wege — Ausstellen, Zurücksetzen, Notfall-Key — schreiben einen Audit-Eintrag
(`Kind=Authentication`, `Origin=System`, `Tool=recovery`). Er steht in der Datenbank, nicht nur im
Log des beendeten Prozesses.

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
