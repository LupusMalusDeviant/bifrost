# Pilotbetrieb — Checkliste (M7/WP7.3)

> **Diese Liste ist die Vorbereitung, nicht der Pilot.** Sie sagt, was ein Pilot installiert, was er
> ausführt und was er zurückmeldet. Der Pilot selbst braucht **drei getrennte Installationen und
> Menschen, die sie betreiben**; solange es die nicht gibt, steht WP7.3 auf `nicht startbar`.

## Was das Pflichtenheft verlangt

- mindestens **drei getrennte Installationen**
- mindestens **ein SQLite- und ein PostgreSQL-Pilot**
- **Upgrade, Backup und Restore** im Pilot wirklich ausgeführt
- **reale Client-/OAuth-Kombinationen** dokumentiert
- Betriebsmetriken opt-in **ohne Payloads** — oder ein manueller Wochenreport

## Vor dem Start

| | |
|---|---|
| Version | ein **veröffentlichtes** Release; kein Stand aus dem Repository |
| Datenbank | je Pilot festhalten: SQLite oder PostgreSQL |
| Betriebsart | Container-Isolation (Vorgabe) oder Host-Ausführung — **mit Begründung**, wenn Host |
| Key-Ring | `BIFROST_KEYRING_PROTECTION` ausdrücklich gesetzt; „keine Wahl getroffen" ist keine Wahl |
| Erreichbarkeit | direkt oder hinter einem Reverse Proxy; bei Proxy `BIFROST_TRUSTED_PROXIES` setzen |

**Der Pilot beginnt mit `bifrost doctor`.** Was dort rot oder gelb ist, gehört ins Protokoll —
auch wenn es der Pilot selbst gleich behebt. Ein Startzustand, den niemand aufgeschrieben hat, ist
später von einem Betriebsproblem nicht zu unterscheiden.

## Was jeder Pilot ausführt

### 1. Erstaufbau

- [ ] Setup-Token aus `<datadir>/config/bootstrap-token.txt` eingelöst, Zugang angelegt
- [ ] mindestens ein echter Upstream angeschlossen (kein Testserver)
- [ ] mindestens ein Agent angebunden, ein echter Toolaufruf gelaufen

### 2. Backup und Restore — **beides, nicht nur das Erste**

- [ ] `bifrost backup create --out <pfad>` mit Passphrase
- [ ] Archiv **geöffnet** und `manifest.json` angesehen
- [ ] `bifrost restore <archiv>` auf einer **zweiten, leeren** Installation
- [ ] danach: sind Upstreams, Rollen und Schlüssel dort? (Der Restore ist erst bewiesen, wenn etwas
      damit läuft — nicht, wenn er ohne Fehler endet.)

### 3. Upgrade

- [ ] von der installierten auf die nächste veröffentlichte Version
- [ ] Migrationsmeldung im Log gelesen (`docker compose logs -f bifrost`)
- [ ] die automatische Vorab-Sicherung gefunden und ihren Pfad notiert
- [ ] `bifrost doctor` danach erneut

### 4. Der Alltag, zwei Wochen

- [ ] eine Freigabe (Approval) über die Oberfläche entschieden
- [ ] ein Auditexport gezogen (`bifrost audit export`) — **erst ab der Fassung, die den Befehl
      mitbringt**; `0.12.0` kennt ihn nicht
- [ ] die Integritätskette geprüft (`bifrost audit verify`) — dieselbe Einschränkung
- [ ] mindestens einmal eine geänderte Tool-Definition angenommen oder abgelehnt

> **Was hier schiefging** (BFR-F-0054): Diese drei Zeilen standen zuerst ohne Einschränkung da.
> Geschrieben waren sie gegen das Repository — und das ist weiter als alles, was ein Pilot
> installieren kann. Eine Checkliste gegen den eigenen Arbeitsstand ist keine.

## Was zurückgemeldet wird

**Ein Wochenreport, von Hand.** Telemetrie gibt es nicht — der opt-in-Metrikpfad aus dem
Pflichtenheft ist eine Produktentscheidung und kein Nebenprodukt dieses Pakets. Solange sie nicht
getroffen ist, gilt der manuelle Bericht.

| Feld | Warum |
|---|---|
| Version, Datenbank, Betriebsart | ohne das ist jeder Befund unzuordenbar |
| Clients und ihre Versionen | die Kompatibilitätsmatrix führt sie sonst weiter als `untested` |
| OAuth-Anbieter, falls verwendet | FR-P062 verlangt einen **externen** Anbieter; ein Pilot kann ihn liefern |
| Was nicht funktioniert hat | **das ist der Punkt.** Ein Pilotbericht ohne Probleme ist ein Bericht, den niemand gelesen hat |
| Was unklar war | Dokumentationslücken sind Befunde. Sie kommen in dieselbe Liste |

**Keine Payloads, keine Auditinhalte, keine Zugangsdaten** im Bericht. Wer etwas zeigen muss, nimmt
das Support-Bundle: Es ist redigiert und ausdrücklich vom Auditexport getrennt.

## Wie es zurückfließt

Jeder Befund aus dem Pilot geht in `docs/acceptance/findings.json` — gleiche Schweregrade, gleiche
Regeln. Ein Pilotbefund ist kein Sonderfall: Critical und High blockieren den Release Candidate
genauso wie ein eigener.

Jede bestätigte Client-/OAuth-Kombination geht in `tests/compatibility/matrix.json`, mit
`evidence.kind = "manual"` und dem Namen des Betreibers. `untested` bleibt stehen, solange niemand
es wirklich gefahren hat.
