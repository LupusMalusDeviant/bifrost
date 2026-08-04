# M7 — Eingefrorener Vertrag für den Release Candidate

**Stand:** 2026-08-04 · **Eingefroren durch:** Lead/Integrator · **Gilt für:** WP7.1 – WP7.3
**Grundlage:** Pflichtenheft 0004 Kapitel 13, Lastenheft 0002, INDEX §Milestones

## 0. Die Eintrittsbedingung ist nicht erfüllt — und das ist der Grund, hier anzufangen

Der INDEX sagt: M7 darf beginnen, wenn **alle 1.0-Musskriterien erfüllt** sind. Sie sind es nicht;
M5 hat drei benannte Reste. Trotzdem beginnt M7 jetzt, und zwar mit **WP7.1** — denn genau dieses
Paket beantwortet die Frage, ob sie erfüllt sind.

**Der Unterschied ist wichtig:** WP7.1 ist keine Fertigmeldung. Es ist die Bestandsaufnahme, die
sagt, was einer Fertigmeldung im Weg steht. Ein Feature Freeze, den niemand gegen die Anforderungen
gehalten hat, ist eine Behauptung über eine Liste, die es nicht gibt.

**WP7.2 und WP7.3 sind hier nicht startbar** und werden es auch nicht durch Fleiß:

| Paket | Was fehlt | Wer kann es |
|---|---|---|
| WP7.2 Externer Security-Review | fremde Augen mit Sicherheitsauftrag | ein externer Prüfer, beauftragt |
| WP7.3 Pilotrelease | drei getrennte Installationen, echte Clients, echte Betreiber | der Produktverantwortliche |

Was hier vorbereitet werden **kann**, steht in §4 und §5: der Bezugscommit und der Scope für den
Review, die Betriebsunterlagen für die Piloten. Vorbereiten ist nicht durchführen — die Zeilen im
Fortschrittsprotokoll heißen entsprechend `vorbereitet`, nicht `implementiert`.

## 1. Die Beweisregeln aus M5/M6 gelten unverändert

Sie stehen im [M5-Vertrag §1](m5-compatibility-contract.md) und im
[M6-Vertrag §1](m6-differentiation-contract.md). Für M7 kommt eine dazu, und sie ist die härteste
der ganzen Reihe:

**Eine Zeile in der Rückverfolgung zählt nur, wenn der genannte Test wirklich existiert und wirklich
läuft.** Nicht „es gibt Tests dazu", sondern: dieser Test, dieser Name, dieser Lauf. Eine
Rückverfolgungsmatrix, die auf einen gelöschten oder übersprungenen Test zeigt, ist schlimmer als
keine — sie sieht aus wie ein Nachweis.

## 2. WP7.1 — Feature Freeze und offene Finding-Liste

**Woran angeschlossen wird:** Pflichtenheft 0004 (die Anforderungsnummern FR-*, FR-P*, NFR-*), die
vorhandenen Verträge M1–M6, die vorhandene Testsuite. Es entsteht **kein neues Anforderungsdokument**
— die Anforderungen stehen im Pflichtenheft und werden dort nicht umgeschrieben.

### 2.1 Die Rückverfolgung ist ein Artefakt, kein Text

- **Quelle der Wahrheit ist maschinenlesbar**: `docs/acceptance/traceability.json`, Schema
  versioniert. Jede Zeile: Anforderung → Work Package → **Testname** → Artefakt/Nachweis → Status.
- **Der Bericht ist generiert**: `docs/acceptance/traceability.md` wird erzeugt, nie von Hand
  gepflegt. Dieselbe Regel wie bei der Kompatibilitätsmatrix (M5-Vertrag §4), und aus demselben
  Grund: Ein von Hand gepflegter Bericht driftet, und die Drift fällt niemandem auf.
- **Ein Test hält beides zusammen.** Er prüft: Schema gültig, jeder genannte Test existiert in einer
  Testassembly, kein Bericht ohne Quelle, keine Quelle ohne Bericht.

### 2.2 Zulässige Status — und `offen` ist einer davon

`erfüllt` · `teilweise` · `offen` · `entfällt`

**Eine weggelassene Anforderung ist kein Status.** Was nicht abgedeckt ist, steht als `offen` drin
und sieht offen aus. Der Unterschied zwischen einer Rückverfolgung und einer Werbeaussage ist genau
diese Zeile — dieselbe Regel wie bei `untested` in der Kompatibilitätsmatrix.

`entfällt` verlangt eine Begründung im selben Datensatz. Ohne Begründung ist es `offen`.

### 2.3 Die Finding-Liste

- `docs/acceptance/findings.json` + generierter Bericht.
- Schweregrade: `critical` · `high` · `medium` · `low`.
- **Critical und High blockieren den RC.** Kein Ausnahmefeld, kein „akzeptiert" — wer eines davon
  akzeptieren will, ändert den Schweregrad und begründet das im Datensatz.
- **Medium verlangt Owner, Termin und Mitigation.** Alle drei, sonst gilt der Datensatz als
  unvollständig und der Prüfer schlägt an.
- Die Befunde aus den bisherigen Wellen (Fortschrittsprotokoll, CHANGELOG) werden **übernommen**,
  nicht neu erfunden. Ein Fund, der einmal aufgeschrieben wurde, verschwindet nicht durch eine neue
  Liste.

### 2.4 Der Wächter gegen still abgeschaltete Tore

Das Pflichtenheft verlangt „keine temporär deaktivierten Gates". Das ist ohne Prüfer eine
Absichtserklärung. Also ein Prüfer:

- **Kein `[Fact(Skip=…)]` und kein `[Theory(Skip=…)]`** in der gesamten Suite. Ein Skip gehört an
  eine Bedingung (`Assert.SkipUnless`), nicht an ein Attribut — ein Attribut-Skip läuft nie wieder
  an, und niemand merkt es.
- **Jede Gate-Variable hat einen CI-Job**, der sie setzt. Die Regel steht seit M5 (§1.2); hier wird
  sie geprüft statt eingehalten: Der Prüfer liest die `BIFROST_RUN_*`- und `BIFROST_REQUIRE_*`-Namen
  aus dem Testcode und hält sie gegen die Workflows.
- **Kein `NoWarn` und kein `#pragma warning disable` ohne Begründung** in derselben oder der
  vorigen Zeile.

Ein Verstoß ist ein **Fehlschlag**, keine Warnung. Ein Wächter, der warnt, wird weggeklickt.

### 2.5 Was WP7.1 ausdrücklich nicht tut

- **Keine Anforderung wird umformuliert, damit sie erfüllt aussieht.** Wenn eine Anforderung nicht
  erfüllt ist, ist die Zeile `offen` — nicht die Anforderung kleiner.
- **Kein Test wird geschrieben, um eine Zeile zu füllen.** Fehlt ein Nachweis, ist das ein Befund
  mit Schweregrad, kein Anlass für einen Test, der die Lücke zudeckt.
- **Keine Produktänderung.** WP7.1 stellt fest; es repariert nicht. Was es findet, wird ein eigenes
  Paket — sonst vermischt sich die Bestandsaufnahme mit dem, was sie auslöst.

**Dateizone:** `docs/acceptance/traceability.*`, `docs/acceptance/findings.*`,
`tests/Bifrost.Governance.Tests/**` (neues Projekt, vom Lead eingehängt).

## 3. Reihenfolge innerhalb WP7.1

1. Der Wächter gegen still abgeschaltete Tore. **Zuerst**, weil er das Einzige ist, was sofort rot
   werden kann — und weil ein Befund dort jede spätere Zeile in der Rückverfolgung entwertet.
2. Die Finding-Liste aus dem, was bereits aufgeschrieben ist.
3. Die Rückverfolgung, Anforderung für Anforderung.
4. Der Bericht, generiert.

## 4. WP7.2 — vorbereitet, nicht durchgeführt

Was hier entsteht, ist die **Einladung**, nicht der Review:

- **Bezugscommit**, benannt und reproduzierbar bauend.
- **Scope**, wörtlich aus dem Pflichtenheft: Auth, Invoker-Invarianten, SSRF, Bootstrap,
  Backupcrypto, Package/WASI, Containerlaunch, Releaseworkflow.
- **Was der Prüfer mitbekommt:** die Verträge, die Invarianten, die Finding-Liste, das
  Fortschrittsprotokoll mit seinen Befunden. Ein Review, dem man die bekannten Schwächen
  verschweigt, prüft die falsche Hälfte.
- **Was der Prüfer nicht mitbekommt:** Zugangsdaten. Auch nicht für eine Testinstanz.

## 5. WP7.3 — vorbereitet, nicht durchgeführt

- Die Betriebsunterlagen sind da (`docs/operations.md`, englischer Spiegel, Upgrade- und
  Restore-Wege). Was fehlt, sind **Installationen und Menschen**.
- Vorbereitet wird eine **Pilotcheckliste**: was ein Pilot installiert, was er ausführt (Upgrade,
  Backup, Restore), was er zurückmeldet und in welcher Form.
- **Keine Telemetrie ohne ausdrückliche Zustimmung.** Der opt-in-Betriebsmetrikpfad aus dem
  Pflichtenheft ist eine Produktentscheidung und kein Nebenprodukt von M7; solange er nicht
  beschlossen ist, gilt der manuelle Wochenreport.

## 6. Was in M7 ausdrücklich NICHT passiert

- **Kein neues Feature.** Feature Freeze heißt Feature Freeze; M6 war die letzte Welle mit neuem
  Funktionsumfang.
- **Keine Refactors aus der Bestandsaufnahme heraus.** Fund → Finding → eigenes Paket.
- **Kein Aufweichen eines Tores, damit die Suite grün wird.** Wenn ein Tor stört, ist entweder das
  Tor falsch — dann wird es begründet geändert — oder der Code.
- **Keine Stable-Tags, kein `latest`.** Das ist M8 (WP8.1), und die Reihenfolge ist der Sinn.

## 7. Unverletzbare Invarianten (fortgeschrieben aus M2/M3/M5/M6)

1. Keine Toolausführung außerhalb `IToolInvoker`.
2. Secrets werden vor Persistenz, Log, Export, Diagnose und Support-Bundle redigiert.
3. Neue Konfiguration ist fail-closed; unbekannte Werte werden nicht still ignoriert.
4. Bereits veröffentlichte EF-Migrationen werden **nicht** nachträglich editiert.
5. SQLite bleibt der Zero-Setup-Default.
6. Die Bezeichner in `CryptographicNames` werden nicht geändert.
7. Testcode weicht die Produktionskonfiguration nicht auf.
8. **Die Kanonisierung der Auditkette (`audit-chain-v1`) wird nicht geändert** — wer sie ändert,
   entwertet jede bestehende Kette.

## 8. Reihenfolge der Pakete

WP7.1 zuerst und allein. WP7.2 und WP7.3 sind Beschaffungen; sie beginnen, wenn jemand sie beauftragt
— und ihre Ergebnisse fließen in dieselbe Finding-Liste zurück.
