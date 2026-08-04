# M6 — Eingefrorener Vertrag für Differenzierung nach der Stabilisierung

**Stand:** 2026-08-02 · **Eingefroren durch:** Lead/Integrator · **Gilt für:** WP6.1 – WP6.5
**Grundlage:** Pflichtenheft 0004 Kapitel 12, Lastenheft 0002 Gruppe I,
[ADR-0013](../adr/0013-webhook-trigger.md), [ADR-0016](../adr/0016-versionierter-connector-plugin-vertrag.md),
[ADR-0024](../adr/0024-backup-restore-und-migrationssicherheit.md) E8

M6 ist **kein 1.0-Blocker** (Pflichtenheft §12). Das ist keine Einladung zur Nachlässigkeit,
sondern eine Priorisierungsregel: Wenn ein Paket hier mit einem M5-Rest kollidiert, gewinnt der
M5-Rest.

Diese Welle hat eine Eigenart, die die vorigen nicht hatten: **Vier von fünf Paketen bauen auf
etwas auf, das es schon gibt** — Konfigurationsimport, Audit, Webhooks, Paketierung. Die größte
Gefahr ist deshalb nicht ein Fehler im Neuen, sondern eine **zweite Fassung des Alten**. Jeder
Abschnitt unten sagt darum zuerst, woran angeschlossen wird.

## 1. Die Beweisregeln aus M5 gelten unverändert

Sie stehen im [M5-Vertrag §1](m5-compatibility-contract.md) und werden hier nicht wiederholt, mit
einer Ergänzung, die das M5-Audit erzwungen hat:

**Eine Fertigmeldung zählt die Suite, nicht die eigenen Tests.** Wer ein Paket abgibt, nennt das
Ergebnis eines Laufs über **alle** Testprojekte — nicht über die selbst geschriebenen. Der schwerste
Befund des M5-Audits war ein bestehender Wächter, der rot wurde, weil ein Paket seine eigene Suite
für den Nachweis hielt.

## 2. WP6.1 — Policy Packs: ein Importweg, nicht zwei

**Woran angeschlossen wird:** `IConfigurationExportService` mit `PlanImportAsync`/`ApplyImportAsync`
(M2/WP2.5). Der Weg kann bereits, was ein Policy Pack braucht: prüfen, Konflikte benennen,
Unverändertes erkennen, über ein Handle anwenden.

**Die Entscheidung: ein Policy Pack ist eine Konfigurationsnutzlast mit Signatur und Namen, kein
zweiter Importmechanismus.** Was hinzukommt, ist genau dreierlei:

1. **Signatur und Herkunft** — dieselbe Ed25519-Prüfung und derselbe Publisher-Trust-Store wie bei
   Connector-Paketen (ADR-0016). Kein zweites Vertrauensmodell.
2. **Der Wirkungsdiff auf Rechte** — nicht „welche Objekte ändern sich", sondern **wer darf danach
   was mehr**. Ein Diff auf Objektebene ist kein Rechte-Diff; genau diese Verwechslung macht
   Rechteausweitung unsichtbar.
3. **Atomare Aktivierung mit Rückweg** — halb aktivierte Rechte sind schlimmer als keine.

**Unverletzbar:** *Niemals stille Rechteausweitung.* Ein Pack, das mehr erlaubt als der aktuelle
Stand, wird nur angewendet, wenn der Zuwachs vorher benannt wurde — namentlich je Identität und
Werkzeug, nicht als Zahl.

**Dateizone:** `src/Bifrost.Core/Policy/**`, Erweiterung von `ConfigurationImport*`; CLI/API/UI als
Adapter. **Nicht** angefasst: das RBAC-Modell selbst.

## 3. WP6.2 — Auditexport und Integritätskette

**Woran angeschlossen wird:** `IAuditQuery` (Seitenabfrage) und `ChannelAuditSink`/`AuditBatchWriter`.
Heute gibt es **keinen** Export — die Oberfläche zeigt Seiten, die API liefert Seiten.

**Vertrag für den Export:**

- **Streaming, nicht Materialisierung.** JSONL und CSV, Zeile für Zeile aus dem Datenbankleser in
  die Antwort. Der Nachweis ist eine Fixture mit einer Million Einträgen, deren Export **nicht**
  linear Arbeitsspeicher zieht (FR-P072). Ohne diese Messung ist „streamt" eine Behauptung.
- **Stabile Cursor.** Ein Export, der über Seitenzahlen läuft, verliert oder verdoppelt Einträge,
  sobald nebenher geschrieben wird — und nebenher wird immer geschrieben.

**Vertrag für die Kette (optional, FR-P073):**

- Verkettet wird über eine **kanonische** Fassung des Eintrags. Die Kanonisierung ist Teil des
  Vertrags, nicht der Implementierung: Wer sie ändert, entwertet jede bestehende Kette.
- **Der Anspruch ist begrenzt und wird mitgeschrieben:** Die Kette erschwert *lokale Manipulation*.
  Gegen einen vollständig übernommenen Wirt schützt sie nicht — wer den Schlüssel hat, schreibt die
  Kette neu. Diese Grenze gehört in die Ausgabe des Verifizierers, nicht nur in die Dokumentation.
- Der Verifizierer benennt die **erste** gebrochene Stelle, nicht nur „ungültig".

**Dateizone:** `src/Bifrost.Persistence/Audit/**` (Leser), `src/Bifrost.Core/Audit/**` (Kanonisierung,
Verifizierer), Adapter. **Achtung:** Der Audit-Schreibpfad steht unter laufender Arbeit aus der
Persistenz-Welle (Verlustsichtbarkeit, `BFR-DB-0007`) — WP6.2 wartet, bis diese gelandet ist, statt
denselben Dateien parallel zu folgen.

## 4. WP6.3 — Approval-Benachrichtigungen

**Woran angeschlossen wird:** Der signierte Webhook aus ADR-0013 (`WebhookSignature`,
`WebhookEndpoint`). Es entsteht **kein zweiter Signaturmechanismus** und kein zweites
Wiederholungsverhalten.

**Die drei Sätze, die den Ausschlag geben:**

1. **Zustellung ist von Entscheidung getrennt.** Eine Freigabe gilt, wenn sie entschieden wurde —
   nicht, wenn eine Benachrichtigung ankam. Ein ausgefallenes Ziel darf weder den Gateway noch die
   Warteschlange blockieren (DoD).
2. **Die Nutzlast trägt keine Toolsecrets** — sie trägt einen Deep-Link. Wer entscheiden darf, holt
   den Rest hinter der Anmeldung.
3. **Ein totes Ziel wird sichtbar, nicht still.** Nach erschöpftem Backoff steht der Zustand als
   Dead-Letter da und erscheint in der Diagnose.

**Dateizone:** `src/Bifrost.Core/Approvals/**`, `src/Bifrost.Core/Webhooks/**`. SMTP ist optional und
kommt erst, wenn der Webhookpfad steht.

## 5. WP6.4 — Token-Cockpit

**Woran angeschlossen wird:** Auditdaten (Aufrufe) und der Katalog (Schemagrößen je Werkzeug),
Profile und Pins.

**Die Regel, die dieses Paket trägt:** **Es ändert nichts.** Es rechnet, zeigt und simuliert —
Rechte oder Profile werden von hier aus nie automatisch geändert (Pflichtenheft WP6.4). Ein
Vorschlag ist ein Vorschlag.

**Zwei Größen, die nicht verwechselt werden dürfen:** *Exposure* ist, was ein Profil einem Agenten
an Schema zumutet; *Invocation* ist, was er davon benutzt hat. Ein Werkzeug ohne Aufruf ist nicht
automatisch überflüssig — es kann das sein, was einmal im Quartal den Betrieb rettet. Die Ausgabe
sagt deshalb „ungenutzt im Zeitfenster", nie „unnötig".

**Grenzen:** Kardinalität und Aufbewahrung werden begrenzt, bevor gerechnet wird. Eine Statistik,
die je Werkzeug und Identität wächst, ist auf einem großen Katalog selbst das Problem.

**Dateizone:** `src/Bifrost.Core/Usage/**`, UI. **Nicht** angefasst: Profile, Pins, RBAC.

## 6. WP6.5 — Connector Developer Kit

**Woran angeschlossen wird:** `ConnectorPackageBuilder`/`Reader`/`Installer` und der
Publisher-Trust-Store (ADR-0016). Das CDK ist eine **Kommandozeile auf vorhandene Bausteine**, kein
zweites Paketformat.

- `bifrost-connector init | build | sign | verify | test` — dieselben Exitcodes wie die
  Betriebsbefehle (M2-Vertrag §4).
- **Ein Referenzconnector**, der mit den dokumentierten Befehlen entsteht und die Probe besteht.
  Solange das Beispiel nicht aus der Dokumentation heraus reproduzierbar ist, ist das Paket nicht
  fertig (DoD).
- **Keine öffentliche Registry.** Der lokale Katalog ist ein Verzeichnis, kein Dienst.
- Der Schlüssel des Herausgebers verlässt den Rechner nicht und erscheint in keiner Ausgabe.

**Dateizone:** `src/Bifrost.ConnectorKit/**`, `docs/connectors/**`, Beispielpaket unter `samples/**`.

**Nachtrag nach dem ersten Bau (2026-08-02): ein eigenes Programm, nicht `bifrost`.** Die Zeile
nannte zuerst `src/Bifrost.Cli/**`. Das geht nicht: Die Gateway-CLI ist der öffentliche HTTP-Client
und hält **bewusst keine Projektreferenz** — sie dupliziert dafür sogar Exitcode-Konstanten (siehe
`OperationsCli`). Das Kit braucht dagegen `Bifrost.Core`, weil es genau dessen Paketbauer und -leser
benutzen soll, statt das Format ein zweites Mal zu kennen. Beides in einer Datei hieße, eine der
beiden Entscheidungen aufzugeben — und die falsche wäre in jedem Fall dabei. Also
`bifrost-connector` als eigenes Werkzeug.

## 7. Was in M6 ausdrücklich NICHT passiert

- **Kein zweiter Importweg, kein zweites Vertrauensmodell, kein zweiter Signaturmechanismus, kein
  zweites Wiederholungsverhalten.** Vier von fünf Paketen bauen auf Vorhandenem auf; wer etwas
  danebenstellt, hat zwei Wahrheiten erzeugt.
- Keine automatische Änderung von Rechten oder Profilen aus einer Statistik heraus.
- Kein Anspruch, dass die Auditkette gegen einen übernommenen Wirt schützt.
- Keine Arbeit an Dateien, die die Persistenz-Welle offen hat, bevor diese gelandet ist.

## 8. Unverletzbare Invarianten (fortgeschrieben aus M2/M3/M5)

1. Keine Toolausführung außerhalb `IToolInvoker`.
2. Secrets werden vor Persistenz, Log, Export, Diagnose und Support-Bundle redigiert — **und jeder
   neue Maskierungsdurchgang trägt seinen Korpustest** (`RedactionCorpusTests`).
3. Neue Konfiguration ist fail-closed; unbekannte Werte werden nicht still ignoriert.
4. Bereits veröffentlichte EF-Migrationen werden **nicht** nachträglich editiert.
5. SQLite bleibt der Zero-Setup-Default.
6. Die Bezeichner in `CryptographicNames` werden nicht geändert.
7. Testcode weicht die Produktionskonfiguration nicht auf.
8. **Neu, aus WP6.1:** Rechteausweitung wird benannt, bevor sie wirkt — je Identität und Werkzeug.

## 9. Reihenfolge

**WP6.5** (CDK) und **WP6.4** (Cockpit) sind unabhängig und sofort startbar. **WP6.3** hängt am
Approval-Modell, das steht. **WP6.1** braucht den Importweg aus M2 — der steht ebenfalls.
**WP6.2 wartete** auf die Persistenz-Welle; sie ist gelandet, das Paket ist umgesetzt. Eine
Abweichung von §3 ist im Fortschrittsprotokoll benannt: Die Kanonisierung liegt in
`Bifrost.Abstractions` statt in `Bifrost.Core/Audit/**` — der Verifizierer bleibt im Kern.

Wer M7 vorbereitet, schließt zuerst die benannten M5-Reste: externer OAuth-Anbieter, Reverse-Proxy-
Sprung, 72-Stunden-Soak, die offenen Browserwege und die realen Clients in der Matrix. M6 ersetzt
davon nichts.
