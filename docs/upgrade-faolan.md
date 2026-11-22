# Upgrade von Bifrost 0.12.0 auf Faolan

Bis 0.12.0 hieß das Produkt Bifrost (davor mcp-mcp). Mit der ersten Faolan-Version ändern sich
Name, Image, Programme und Compose-Dateien. Die Daten bleiben, wie sie sind: Datenbank,
Schlüsselring, verschlüsselte Zugangsdaten, Sicherungen und installierte Connector-Pakete laufen
ohne Umwandlung weiter.

Grundlage ist der [Vertrag zur Umbenennung](contracts/umbenennung-faolan.md) und
[ADR-0026](adr/0026-umbenennung-auf-faolan.md).

> **Das Wichtigste zuerst: das Volume.** Wer nur eine Stelle liest, liest Schritt 2. Ohne ihn
> startet Faolan auf einem neuen, leeren Volume und meldet sich trotzdem fehlerfrei als bereit.

## Was sich für Betreiber ändert

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

## Schritt für Schritt mit Docker Compose

### 1. Sichern

Wie vor jedem Upgrade (siehe [Betrieb, Backup](operations.md)), noch mit der alten CLI:

```bash
bifrost backup create --out /data/backups/vor-faolan.zip
bifrost backup verify /data/backups/vor-faolan.zip
```

Den Schlüsselring bzw. das PFX gesondert sichern, wie bisher.

### 2. Den Namen des bestehenden Volumes ablesen

Compose stellt dem Volume den Projektnamen voran. Der Projektname ist der Ordnername der
Compose-Datei, sofern `COMPOSE_PROJECT_NAME` nichts anderes sagt.

```bash
docker volume ls --filter name=bifrost-data
# DRIVER    VOLUME NAME
# local     meinprojekt_bifrost-data
```

Mit PostgreSQL zusätzlich:

```bash
docker volume ls --filter name=bifrost-pgdata
```

### 3. Den Namen in `.env` eintragen

```dotenv
FAOLAN_DATA_VOLUME=meinprojekt_bifrost-data
# nur mit docker-compose.postgres.yml:
FAOLAN_PGDATA_VOLUME=meinprojekt_bifrost-pgdata
```

Die Vorgaben für `POSTGRES_DB` und `POSTGRES_USER` bleiben `bifrost`. Sie stehen in jeder
bestehenden Datenbank; mit gesetztem Volume passt der Bestand sofort.

**Gemessenes Verhalten** (Docker 29.9.3, Compose v5.7.0):

| Lage | Ergebnis |
|---|---|
| Gleicher Projektname, Variable gesetzt | Das alte Volume wird ohne Warnung eingehängt, die Daten sind da. |
| Anderer Projektname, Variable gesetzt | Das alte Volume wird eingehängt, die Daten sind da. Compose warnt nur (`already exists but was created for project …`) und bricht nicht ab. |
| Variable **nicht** gesetzt | Compose legt still ein neues, leeres `faolan-data` an. |

`external: true` ist nicht nötig. Die Warnung im zweiten Fall ist harmlos.

### 4. Neue Compose-Dateien und Image

Die mitgelieferten Compose-Dateien aus dem neuen Release übernehmen. Der Dienst heißt jetzt
`faolan`, das Image `ghcr.io/lupusmalusdeviant/faolan`. In `.env` werden `BIFROST_VERSION`,
`BIFROST_IMAGE` und `BIFROST_HTTP_PORT` zu `FAOLAN_VERSION`, `FAOLAN_IMAGE` und `FAOLAN_HTTP_PORT`.
Diese drei liest Compose, nicht der Gateway; für sie gibt es keine Übernahme.

Die Compose-Secrets heißen jetzt `faolan-keyring-pfx` und `faolan-keyring-password`. Wer sie
benutzt, benennt die Einträge in einer eigenen Override-Datei entsprechend um.

```bash
docker compose down
docker compose pull
docker compose up -d
docker compose logs -f faolan
```

### 5. Prüfen

Mit der neuen CLI (Archiv `faolan-cli-<version>-<plattform>` aus dem Release, siehe
[CLI-Installation](cli-installation.md); das Programm heißt jetzt `faolan`):

```bash
faolan doctor
```

- **`BFR-DB-0008`** warnt, wenn die Instanz keinen Upstream, keine Rolle und keinen API-Schlüssel
  hat. Nach einem Upgrade heißt das fast immer: das falsche Volume. Zurück zu Schritt 2.
- **`BFR-DB-0005`** nennt die benutzte SQLite-Datei. `bifrost.db` ist richtig und bleibt so; es gibt
  keinen Grund, sie umzubenennen.
- **`BFR-CFG-0002`** listet Variablen, die noch alt benannt sind. Das ist eine Empfehlung, kein
  Fehler.

Danach in der Weboberfläche neu anmelden: Die Cookies heißen jetzt `faolan-*`, die alten Sitzungen
gelten nicht mehr.

## Was der Gateway von selbst übernimmt

| Altbestand | Verhalten |
|---|---|
| Variablen `BIFROST_*`, `MCPMCP_*` (auch `_FILE`) | werden gelesen; der Start nennt sie. Ist dieselbe Variable mehrfach gesetzt, gilt die jüngste Generation (`FAOLAN_` vor `BIFROST_` vor `MCPMCP_`). |
| Datenbankdatei | gesucht wird `faolan.db`, dann `bifrost.db`, dann `mcpmcp.db`. Eine Neuinstallation bekommt `faolan.db`. |
| Sicherungen aus 0.12.0 | lassen sich zurückspielen; das Archivformat ist unverändert. |
| Connector-Pakete `bifrost.connector.v1` | werden installiert und laufen; neu gebaute tragen `faolan.connector.v1`. |
| Platzhalter `${bifrost:secret/…}` in Exporten | werden beim Import erkannt wie `${faolan:secret/…}`. |
| Asset-URIs `bifrost://assets/…` | bleiben lesbar; gelistet wird `faolan://assets/…`. |
| Eingehende Webhooks mit `X-Bifrost-Signature` | werden geprüft. Sobald ein Absender eine `X-Faolan-*`-Kopfzeile schickt, gilt nur noch das neue Paar. |
| Freigabe-Benachrichtigungen | tragen beide Kopfzeilenpaare mit gleichen Werten. |
| Upstream-Container aus 0.12.0 | werden weiter erkannt und aufgeräumt; ihre Etiketten `de.bifrost.*` bleiben. |
| WASI-Upstreams mit `bifrost-wasi-host` bzw. `/usr/local/bin/bifrost-wasi-host` in `Wasi.HostExecutable` | laufen weiter: Der alte Name bezeichnet den umbenannten Host `faolan-wasi-host`. Gestartet wird wie bisher nur der Host der Instanz. |

> **Ein Abbruch beim Start, der gewollt ist.** Das Image setzt `FAOLAN_DATA_DIR=/data` und
> `FAOLAN_WASI_HOST` selbst. Wer bisher `BIFROST_DATA_DIR` oder `BIFROST_WASI_HOST` auf einen
> **anderen** Wert gesetzt hatte, bekäme sonst still die Vorgabe des Images — beim Datenverzeichnis
> heißt das: eine leere Instanz. Stattdessen bricht der Start mit Exit-Code 2 ab und nennt beide
> Variablen. Abhilfe: den Wert unter dem neuen Namen setzen (`FAOLAN_DATA_DIR=/srv/gateway`).

## Ohne Docker

- Der Serverstart heißt `dotnet Faolan.Server.dll` statt `dotnet Bifrost.Server.dll`.
- Die CLI heißt `faolan`, das Connector-Werkzeug `faolan-connector`, der WASI-Host
  `faolan-wasi-host`. Ein selbst gesetzter `BIFROST_WASI_HOST` wird gelesen, muss aber auf ein
  vorhandenes Programm zeigen.
- Ein systemd-Unit oder Skript mit den alten Namen ist einmal anzupassen.

## Beobachtung

Metriken und Traces heißen jetzt `faolan.*` (Meter und ActivitySource `Faolan.Gateway`), der
OpenTelemetry-Dienst `faolan`, die Log-Kategorien `Faolan.*`. Dashboards, Alarme und Log-Filter
auf die alten Namen zeigen nach dem Upgrade ins Leere.
