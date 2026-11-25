# ADR-0026: Umbenennung auf Faolan — Markennamen wechseln, abgelegte Bezeichner nicht

- **Status:** Akzeptiert (vom Nutzer abgenommen am 2026-11-16)
- **Datum:** 2026-11-16
- **Autor:** Lead/Integrator, ausgearbeitet mit Claude
- **Betrifft:** [Plan 0004](../plans/0004-umbenennung-und-rueckkanal-projektion.md) Teil R; bindende
  Zuordnung im [Umbenennungsvertrag](../contracts/umbenennung-faolan.md)

## Kontext und Problemstellung

Das Produkt heißt seit dem 2026-07-31 B.I.F.R.O.S.T (Commit `c7cb446`). Unter demselben Namen
betreibt Maxim AI ein quelloffenes AI- und MCP-Gateway (`maximhq/bifrost`), das in jeder Suche und
in jedem MCP-Verzeichnis vor uns steht. Wer nach „Bifrost MCP Gateway" sucht, findet das andere
Projekt; wer unseres empfiehlt, muss es jedes Mal abgrenzen.

Der Nutzer hat am 2026-11-16 entschieden, das Produkt in **Faolan** umzubenennen (irisch *faolán*,
„kleiner Wolf"). Der zuerst gewünschte Name Heimdall und rund 180 weitere Kandidaten wurden geprüft
und verworfen; die Tabellen stehen im Plan 0004, Abschnitt 1.1.

Es ist die **zweite** Umbenennung. Die erste hat gezeigt, wo so etwas weh tut: nicht im Code,
sondern dort, wo ein Name in abgelegten Daten steht. Eine Instanz, die nach dem Upgrade mit
unbekannten Variablennamen startet, fällt lautlos auf Vorgabewerte zurück, legt eine leere Datenbank
neben die volle und meldet „bereit". Deshalb gibt es `LegacyEnvironment`, `CryptographicNames` und
die Diagnose `BFR-CFG-0002`.

Der Name steht heute in 763 von 879 versionierten Dateien. Rund ein Dutzend der Bezeichner sind
keine Markennamen, sondern Teil eines Formats: Einträge in Sicherungsarchiven, die Schema-Id in
signierten Connector-Paketen, Zwecke der Schlüsselableitung, Header, mit denen fremde Systeme
signieren.

**Kernfrage:** Wie wird aus Bifrost Faolan, ohne dass eine bestehende Installation beim Upgrade
Daten, Zugangsdaten oder Anbindungen verliert — und ohne dass der alte Name an zwanzig Stellen
dauerhaft als Zweitname mitgeschleppt wird?

## Anforderungen

### Funktional

- Eine Instanz der Version 0.12.0 startet nach dem Upgrade unverändert: gleiche Datenbank, gleiche
  Zugangsdaten, gleiche Rollen, gleiche Upstreams — ohne dass der Betreiber vorher etwas umbenennt.
- Dasselbe gilt für eine Instanz, die noch `MCPMCP_*`-Variablen oder eine `mcpmcp.db` hat.
- Sicherungen, Konfigurationsexporte, Policy Packs und Connector-Pakete aus 0.12.0 bleiben lesbar.
- Fremde Systeme, die Webhooks an den Gateway signieren oder Benachrichtigungen von ihm prüfen,
  arbeiten ohne Änderung weiter.
- Alles, was ein Betreiber einmalig tun muss, steht in einer Anleitung und ist im Diagnosebericht
  sichtbar.

### Nicht-Funktional

- **Kein stiller Ausfall.** Jede Übernahme eines alten Namens erzeugt eine Warnung beim Start und
  einen Befund in `doctor`.
- **Prüfbar.** Jede Brücke hat einen Test, der rot wird, wenn sie fehlt. Die erste Umbenennung hat
  gezeigt, dass Kommentare dafür nicht reichen.
- **Endlich.** Nach der Umbenennung steht im Quelltext genau ein Produktname. Alte Namen kommen nur
  noch in benannten Brücken und in Formatbezeichnern vor, die bewusst bleiben.

## Betrachtete Optionen

### Option 0: Beim Namen Bifrost bleiben

Nichts ändern; die Verwechslung in Kauf nehmen.

**Positiv:**
- Kein Aufwand, kein Upgrade-Risiko.
- Bestehende Dokumentation, Images und Client-Konfigurationen bleiben gültig.

**Negativ:**
- Die Verwechslung mit `maximhq/bifrost` bleibt und wächst mit dessen Verbreitung.
- Der Name ist in Paketregistern und MCP-Verzeichnissen faktisch nicht mehr zu besetzen.

### Option 1: Nur die Marke wechseln, den Code bei `Bifrost.*` lassen

README, Oberfläche, Image und CLI heißen Faolan; Namensräume, Projekte und Variablen bleiben.

**Positiv:**
- Wenige Dutzend statt 763 Dateien.
- Kein Brückenbedarf für Variablen und Dateinamen.

**Negativ:**
- Dauerhaft zwei Namen: Ein Betreiber setzt `BIFROST_DATA_DIR` für ein Produkt namens Faolan.
- Jede Fehlermeldung, jeder Stacktrace und jedes NuGet-Paket trägt weiter den fremden Namen.
- Die Verwechslung verschiebt sich nur von der Suche in den Support.

### Option 2: Vollständig umbenennen, alte Namen über benannte Brücken annehmen

Alles, was Markenname ist, heißt Faolan. Alles, was ein Betreiber gesetzt oder abgelegt hat, wird
unter dem alten Namen weiter angenommen und sichtbar gemeldet. Bezeichner, die Teil eines Formats
oder einer Schlüsselableitung sind, bleiben unverändert.

**Positiv:**
- Ein Name im Quelltext, in den Paketen und in der Betriebsdokumentation.
- Bestehende Instanzen laufen ohne Vorarbeit weiter.
- Schließt an Mechanik an, die es seit der ersten Umbenennung gibt und die getestet ist.

**Negativ:**
- Größter Umfang: 763 Dateien, 25 Projekte, sieben Workflows.
- Die Brücken sind Code, der gepflegt und irgendwann entfernt werden muss.
- Einige Brüche lassen sich nicht überbrücken (Metriknamen, `serverInfo.name`, Image-Referenz).

### Option 3: Vollständig umbenennen ohne Brücken

Wie Option 2, aber alte Namen werden nicht mehr angenommen; der Betreiber stellt vor dem Upgrade um.

**Positiv:**
- Kein Brückencode, keine Übergangszeit.

**Negativ:**
- Genau der stille Ausfall, den `LegacyEnvironment` verhindern soll: Wer eine Variable übersieht,
  startet mit einer leeren Instanz.
- Sicherungen und Pakete aus 0.12.0 wären ohne Zusatzwerkzeug nicht mehr lesbar.

## Vorschlag des Autors

Option 2. Der Aufwand ist überwiegend mechanisch und in einem einzigen verhaltensneutralen Commit
abgrenzbar. Das eigentliche Risiko liegt in den zwölf Bezeichnern, die in abgelegten Daten stehen —
und dafür ist die Antwort je Bezeichner verschieden, weshalb sie nicht in dieses ADR, sondern in die
bindende Tabelle des Vertrags gehört.

Die Trennlinie ist eine einzige Frage: **Hat ein Betreiber oder ein fremdes System diesen Namen
schon einmal geschrieben?** Wenn nein, wird umbenannt. Wenn ja, wird der alte Name weiter gelesen.
Ist der Name Teil einer Schlüsselableitung oder eines versionierten Formats, bleibt er, wie er ist —
ein kryptografischer Bezeichner ist kein Markenname.

## Entscheidung

**Gewählte Option:** "Option 2: Vollständig umbenennen, alte Namen über benannte Brücken annehmen"

Den Ausschlag geben die Anforderung „kein stiller Ausfall" und die Tatsache, dass die Mechanik dafür
bereits existiert und nur um eine Generation erweitert wird. In Kauf genommen werden der Umfang und
drei Brüche, die sich nicht überbrücken lassen und deshalb im CHANGELOG und in der Upgrade-Anleitung
stehen.

## Konsequenzen

### Positiv

- Das Produkt ist unter einem Namen auffindbar, der auf npm, nuget.org, PyPI und crates.io frei ist
  und unter dem kein MCP- oder Gateway-Projekt existiert (Stand 2026-11-16).
- Die Übernahme alter Namen wird von einer Sonderregel für `MCPMCP_` zu einer allgemeinen Kette mit
  beliebig vielen Generationen.
- Der DataProtection-Zweck der Freigabe-Rückfrage kommt unter den Test, der die übrigen
  kryptografischen Namen schützt.

### Negativ

- **Dashboards brechen.** Meter, Instrumente und Span-Attribute heißen künftig `faolan.*`; eine
  Doppelausgabe gibt es nicht.
- **Clients sehen einen neuen Servernamen.** `serverInfo.name` wechselt von `bifrost` auf `faolan`.
- **Image und Compose ändern sich.** Wer das Volume nicht ausdrücklich benennt, startet mit einem
  leeren Datenverzeichnis — das ist die eine Stelle, an der der Prozess den Fehler nicht selbst
  erkennen kann.
- Im Quelltext bleiben drei Generationen von Namen sichtbar: `MCPMCP` in der Schlüsselableitung,
  `Bifrost` in Formatbezeichnern und Diagnosecodes, `Faolan` überall sonst.
- Die Geschichte (CHANGELOG, ADR-0001 bis 0025, Abnahmedokumente) nennt weiter den alten Namen.

### Folge-Entscheidungen

- Wann die Brücken für `MCPMCP_*` und `BIFROST_*` entfallen. Vorschlag: nicht vor 1.0, und nur mit
  einem Diagnosebefund, der eine Version vorher warnt.
- Ob die Diagnosecodes `BFR-*` bei einer künftigen Neuordnung ein neues Präfix bekommen.
- Die Versionsnummer des Releases, das die Umbenennung trägt — legt der Nutzer fest.

### Review

**Reality-Check geplant für:** 2026-12-28

## Weitere Informationen

### Scope

Gilt für dieses Repository, seine Images, CLI-Artefakte und NuGet-Pakete. Nicht Teil der
Entscheidung: die Umbenennung der Repositories auf GitHub und GitLab, des GHCR-Pakets und der
laufenden Instanz — das sind Handgriffe des Nutzers (Plan 0004, R7).

Nicht geprüft und vor der Umsetzung nachzuholen: Markenregister (DPMA, EUIPO).

### Referenzen

- [Umbenennungsvertrag](../contracts/umbenennung-faolan.md) — die bindende Zuordnung je Bezeichner
- [Plan 0004](../plans/0004-umbenennung-und-rueckkanal-projektion.md) — Bestandsaufnahme und Namensprüfung
- `src/Bifrost.Server/LegacyEnvironment.cs`, `src/Bifrost.Persistence/CryptographicNames.cs` — die Vorbilder
- Commits `c7cb446` und `f22384c` — die erste Umbenennung und ihre Tests
