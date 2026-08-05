# Pilot 1 von 3 — Protokoll

**Datum:** 2026-08-05 · **Paket:** M7/WP7.3 · **Betreiber:** Entwicklungsrechner (Windows 11,
Docker 29.6.2, WSL2-Backend) · **Version:** `0.12.0`, veröffentlicht am 2026-08-01

> **Was dieser Pilot ist und was nicht.** Er ist **eine** der drei vom Pflichtenheft verlangten
> Installationen, gefahren gegen ein **veröffentlichtes** Release nach der
> [Pilotcheckliste](pilot-checklist.md). Er ist **nicht** WP7.3: Es fehlen die zweite und dritte
> Installation, der PostgreSQL-Pilot, echte Clients, echte OAuth-Kombinationen und zwei Wochen
> Alltag. Und er hat einen Konstruktionsfehler, den nur ein fremder Betreiber nicht hat: Der Pilot
> kennt das Produkt. Was ihm selbstverständlich vorkam, ist deshalb kein Beleg dafür, dass es
> selbsterklärend war.

## Aufbau

| | |
|---|---|
| Weg | genau der dokumentierte: `cp .env.example .env` → `docker compose up -d` → `curl /healthz` |
| Quelle | `ghcr.io/lupusmalusdeviant/bifrost:0.12.0`, Digest `sha256:3ab92c41…f7e4b45` |
| Datenbank | SQLite (Vorgabe, nichts eingetragen) |
| Betriebsart | Container, kein Reverse Proxy, HTTP auf `localhost` |
| Verzeichnis | ein frisch angelegtes, **nicht** das Repository — sonst prüft man seinen eigenen Baum |

Zwei weitere Installationen dienten als Gegenstücke: **Pilot 2** (Port 8081, leeres Volume) für den
Restore, **Pilot 3** (Port 8082) für das Upgrade.

## Was durchgelaufen ist

| Schritt der Checkliste | Ergebnis |
|---|---|
| Erstaufbau über den Standardweg | **ok** — `healthz: 200` aus einem leeren Verzeichnis heraus |
| Setup-Token aus `<datadir>/config/bootstrap-token.txt` | **ok** — Datei mit Modus 600, Token nicht im Log |
| Zugang über `/setup` angelegt | **ok** — Tokendatei danach automatisch entfernt |
| API-Key über die Oberfläche erzeugt | **ok** — einmal angezeigt, mit fertigem Client-Snippet |
| `bifrost doctor` | **ok**, Exit 3 (Warnung) — beide Warnungen sind die erwarteten: kein Key-Ring-Modus gewählt, kein TLS davor |
| `bifrost backup create --passphrase-env` | **ok**, Exit 0, 256 449 Bytes, verschlüsselt |
| `bifrost backup verify` | **ok**, Exit 0; Manifest angezeigt |
| dasselbe mit **falscher** Passphrase | **ok** — Exit 5, „ließ sich nicht entschlüsseln". Ein Prüfschritt, der nur bestätigt, bestätigt auch Unsinn |
| `bifrost restore` ohne `--replace` | **ok**, Exit 6 mit Blocker-Begründung — und siehe BFR-F-0049 |
| `bifrost restore --replace --yes` | Exit 0, Vorab-Sicherung entstanden — **aber siehe BFR-F-0047** |
| Restore inhaltlich belegt | **ok, nach Neustart**: der API-Key aus Pilot 1 authentifiziert gegen Pilot 2, der eigene Schlüssel von Pilot 2 ist tot. `instance.json` beider Installationen identisch |
| **Upgrade** | **nicht durchführbar** — siehe BFR-F-0046 |
| Freigabe entscheiden, Auditexport, Kettenprüfung | **nicht durchführbar** in 0.12.0 — siehe BFR-F-0054 |

## Der Nachweis für den Restore

Die Checkliste verlangt: *„Der Restore ist erst bewiesen, wenn etwas damit läuft — nicht, wenn er
ohne Fehler endet."* Deshalb dieser Nachweis statt eines Blicks in die Oberfläche:

```
vor dem Neustart von Pilot 2:
  Schlüssel aus Pilot 1  an :8081  ->  401
  eigener Schlüssel      an :8081  ->  200      <-- der alte Zustand antwortet noch

nach dem Neustart:
  Schlüssel aus Pilot 1  an :8081  ->  200
  eigener Schlüssel      an :8081  ->  401      <-- der wiederhergestellte Zustand ist wirksam
```

Beides ist wichtig. Die zweite Zeile ist der Beleg, dass der Restore die Identitäten und den
Key-Ring wirklich übertragen hat. Die erste ist BFR-F-0047.

## Was der Pilot gefunden hat

Zehn Befunde, davon einer High und einer schon behoben. Sie stehen mit vollem Text in
[`findings.md`](findings.md) — hier nur die Zuordnung:

| Befund | Kurz |
|---|---|
| BFR-F-0046 | nur eine installierbare Version — Upgrade und Rollback haben kein Ziel |
| **BFR-F-0047** | **`restore` meldet Erfolg; bis zum Neustart bedient der Gateway den alten Stand** |
| BFR-F-0048 | die angekündigte Vorab-Sicherung heißt anders als die geschriebene |
| BFR-F-0049 | ein Restore auf eine leere Installation gibt es in der Praxis nicht |
| BFR-F-0050 | `.env.example` widerspricht dem Produkt beim Key-Ring-Passwort |
| BFR-F-0051 | `BIFROST_KEYRING_PROTECTION` fehlt in der Beispielkonfiguration |
| BFR-F-0052 | vier `Error`-Zeilen im Log eines fehlerfreien Erststarts |
| BFR-F-0053 | unbekannte UI-Pfade liefern 404 mit leerem Rumpf |
| BFR-F-0054 | die Pilotcheckliste verlangt Befehle, die es im Release nicht gibt (behoben) |
| BFR-F-0056 | der Erststart legt eine unverschlüsselte Vollsicherung an, obwohl es nichts zu sichern gab |

**Der eigene Fehler zuerst:** BFR-F-0054 ist meiner. Die Checkliste ist einen Tag alt und verlangt
`bifrost audit export` und `bifrost audit verify` — Befehle aus M6, die in der einzigen
veröffentlichten Fassung nicht existieren. Eine Checkliste gegen ein Repository geschrieben, das
weiter ist als das, was Betreiber bekommen können.

## Was der Pilot **nicht** gezeigt hat

- **Kein Upgrade.** Es gibt genau ein installierbares Image. Der Sprung `0.11.0 → 0.12.0` scheitert
  daran, dass `0.11.0` als Image nie existiert hat (BFR-F-0046).
- **Kein Rollback.** Aus demselben Grund. Der dokumentierte Weg („zurück auf die vorherige Zeile in
  `.env`") hat heute kein Ziel.
- **Keine Migration über eine Versionsgrenze.** Was lief, war die Erstmigration auf einer leeren
  Datenbank — nicht dasselbe.
- **Kein echter Upstream, kein echter Agent, kein Alltag.** Der Katalog blieb leer.
- **Kein PostgreSQL.**

## Was als Nächstes fehlt

Zwei weitere Installationen bei Betreibern, die das Produkt **nicht** gebaut haben, davon eine auf
PostgreSQL — und ein zweites veröffentlichtes Release, ohne das der Upgrade- und der Rollbackweg
unbelegt bleiben.
