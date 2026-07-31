# ADR-0025: Host-Ausführung verbieten — und was mit bestehenden Instanzen geschieht

- **Status:** **Akzeptiert.** Entschieden am 2026-07-31 im Rahmen von M3 (Pflichtenheft 0004).
- **Datum:** 2026-07-31
- **Autor:** Lead/Integrator, ausgearbeitet mit Claude
- **Betrifft:** FR-P030 bis FR-P034; [ADR-0018](0018-native-prozess-und-container-isolation.md),
  [ADR-0014](0014-cli-programme-als-upstream-transport.md)
- **Vorbedingung aus dem Pflichtenheft:** WP3.1 verlangt diese Entscheidung **vor** dem Code.

## Kontext

B.I.F.R.O.S.T startet fremde Programme. Zwei Wege führen dorthin:

| Weg | Isolation heute |
|---|---|
| CLI-Upstreams (ADR-0014/0018) | Container möglich, **Vorgabe ist `Host`** |
| stdio-Upstreams | **gar keine** — gehärtet, aber kein Modell dafür |

„Gehärtet" heißt: absolute Pfade, Root-Allowlist, minimale Umgebung, Prozessbaum-Kill unter einem
Job-Objekt. Das ist ordentliche Hygiene und keine Sandbox. Ein stdio-Upstream läuft mit den Rechten
des Gateways — und das Gateway hält den Schlüsselring, mit dem sich sämtliche Zugangsdaten aller
anderen Upstreams entschlüsseln lassen.

Der Container-Modus existiert seit ADR-0018 und wird nicht benutzt, weil er nicht die Vorgabe ist.
Eine Sicherheitsfunktion, die man einschalten muss, ist für die meisten Installationen keine.

**Kernfrage:** Wie wird Isolation zur Vorgabe, ohne bestehende Installationen beim nächsten Upgrade
stillzulegen — und ohne die Umstellung so weich zu machen, dass sie nichts ändert?

## Entscheidungen

### E1 — Eine Stelle entscheidet, und sie entscheidet fail-closed

`IHostExecutionPolicy` beantwortet genau eine Frage: Darf dieser Upstream nativ auf dem Host
starten? Die Antwort trägt einen **stabilen Reason-Code**, damit ein Betreiber ein Runbook darauf
stützen kann und die Begründung Umformulierungen überlebt.

Unbekannt heißt **nein**. Eine Policy, die im Zweifel erlaubt, ist eine Dokumentation.

### E2 — Neuinstallationen verbieten Host-Ausführung

`BIFROST_ALLOW_HOST_EXECUTION` steht für eine frische Instanz auf `false`. Wer nativ ausführen will,
schaltet das ein — bewusst, sichtbar, und im Diagnosebericht nachlesbar.

### E3 — Bestehende Instanzen werden nicht stillgelegt, aber auch nicht stillgeschwiegen

Das ist die eigentliche Entscheidung dieses ADR, und sie ist ein Kompromiss.

Eine bestehende Instanz mit laufenden Host-Upstreams beim Upgrade zu blockieren, wäre sicher und
falsch: Der Betreiber hat nichts getan, sein System stünde, und er erführe den Grund mitten in einem
Vorfall. Sie stillschweigend weiterlaufen zu lassen, wäre bequem und ebenso falsch — dann ändert
sich nichts, und die Vorgabe ist eine Behauptung im Changelog.

**Gewählt: übernehmen, festschreiben, sichtbar machen.**

1. Findet der Start Host-Upstreams **und** keine ausdrückliche Einstellung, übernimmt er den
   bisherigen Zustand (`true`) — die Instanz läuft weiter.
2. Die Übernahme wird **geschrieben**, nicht angenommen. Aus einer unsichtbaren Vorgabe wird ein
   sichtbarer Wert, den jemand ändern kann.
3. Sie erzeugt einen **Audit-Eintrag** und eine **Diagnosewarnung**, die jeden betroffenen Upstream
   namentlich nennt.

Der Unterschied zwischen „läuft weiter" und „läuft weiter, und alle wissen warum" ist der ganze
Zweck dieser Entscheidung. Dieselbe Mechanik hat beim Umstieg auf v0.11.0 gefehlt: Ein umbenanntes
Volume genügte für eine leere Datenbank, fehlerfrei, mit der Meldung „bereit".

### E4 — Die Prüfung sitzt vor jedem Startweg, nicht in jedem Formular

Geprüft wird beim Validieren, beim Testen, beim Start und beim **Paketimport**. Der Importpfad ist
ausdrücklich genannt, weil er der naheliegende Weg vorbei an einer Formularprüfung ist: Ein Paket
bringt eine Konfiguration mit, die niemand eingetippt hat.

Belegt wird das durch einen **Architekturtest**, der alle nativen Startpfade aufzählt und verlangt,
dass jeder durch die Policy geht. Eine Prüfung, die man an einer neuen Stelle vergessen kann, ist
keine — und neue Stellen entstehen bei jedem Adapter.

### E5 — stdio bekommt dasselbe Modell wie CLI

Zwei Isolationsmodelle für dieselbe Frage wären zwei Wahrheiten, von denen eine veraltet. stdio
erbt `CliIsolationMode`/`CliIsolationOptions` beziehungsweise deren gemeinsame Verallgemeinerung.

Die Namen tragen heute `Cli` im Bezeichner; ob umbenannt oder verallgemeinert wird, entscheidet der
Contract-Freeze zu M3 — **nicht** ein Paket unterwegs.

### E6 — Fehlende Runtime ist eine Diagnose, kein Rückfall

Steht der Modus auf `Container` und es gibt keine Runtime, kommt der Upstream nicht hoch. Ein
Ausweichen auf den Host wäre eine stille Herabstufung genau der Eigenschaft, wegen der jemand den
Container gewählt hat. Das bekräftigt ADR-0018 und gilt jetzt für stdio mit.

## Konsequenzen

**Positiv:** Ein fremdes Programm kommt nicht mehr an den Schlüsselring, nur weil es als
Upstream eingetragen wurde. Die Entscheidung „nativ ausführen" existiert als sichtbarer Wert statt
als Abwesenheit einer Einstellung.

**Negativ und bewusst in Kauf genommen:**

- **Bestehende Instanzen werden durch dieses ADR nicht sicherer.** Sie werden nur ehrlich. Der
  Schritt von der Warnung zur Umstellung bleibt Handarbeit des Betreibers — automatisch umstellen
  hieße, ihm ohne Rückfrage die Ausführungsart seiner Werkzeuge zu ändern.
- **Container werden zur Voraussetzung für den bequemen Weg.** Wer keine Runtime hat, muss die
  Ausnahme bewusst einschalten. Das ist gewollt und trotzdem eine Hürde.
- **Ein zweiter Startweg mehr.** stdio im Container ist Code, den es heute nicht gibt, mit
  Lebenszyklus, Gesundheitsprüfung und Aufräumen.

**Offen und ausdrücklich nicht entschieden:** ob `CliIsolation*` umbenannt oder verallgemeinert
wird (Contract-Freeze M3), und ob es je einen erzwungenen Umstellungstermin für Bestandsinstanzen
gibt.
