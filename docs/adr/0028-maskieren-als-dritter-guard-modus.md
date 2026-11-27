# ADR-0028: Maskieren als dritter Modus der Guard-Regeln

- **Status:** Akzeptiert (vom Nutzer abgenommen am 2026-11-16)
- **Datum:** 2026-11-16
- **Autor:** Lead/Integrator, ausgearbeitet mit Claude
- **Betrifft:** ergänzt [ADR-0011](./0011-secret-erkennung-als-guardrail.md) (ersetzt es nicht);
  [Plan 0004](../plans/0004-umbenennung-und-rueckkanal-projektion.md) P4; bindende Einzelheiten im
  [Projektionsvertrag](../contracts/rueckkanal-projektion.md) Abschnitt 6; NFR-04 (Secrets)

## Kontext und Problemstellung

ADR-0011 hat entschieden: Ein Treffer der Secret-Erkennung **blockiert** (E1). Eine Regel kennt
zwei Modi — `Observe` zählt, `Block` hält das Ergebnis zurück. Für ein Zugangsdatum, das als
solches eindeutig ist (`ghp_…`, `AKIA…`, ein PEM-Block), ist das richtig.

In der Ergebnis-Richtung hat Blockieren aber einen Preis, den ADR-0011 selbst benennt: Der Aufruf
ist bereits gelaufen, der Seiteneffekt eingetreten, und der Agent bekommt statt des Ergebnisses
eine Fehlermeldung. Für eine ganze Klasse von Funden ist das unverhältnismäßig:

- eine Verbindungszeichenkette in einer Konfigurationsdatei (`…;Password=…;`),
- ein `Authorization: Bearer …` in einem mitgeschnittenen Request,
- Zugangsdaten in einer URL (`https://nutzer:geheim@host/…`).

Hier ist nicht die ganze Antwort das Problem, sondern eine Stelle darin. Die übrigen 40 Zeilen
Konfiguration oder Log sind genau das, was der Agent angefordert hat. Mit `Block` verliert er
alles, mit `Observe` bekommt er das Secret.

Die vorhandene Redaction (`RedactionService`) hilft nicht: Sie maskiert nach **Schlüsselnamen**
und nur für das Audit-Log, nicht auf dem Weg zum Agenten.

Die Rückkanal-Pipeline aus [ADR-0027](./0027-rueckkanal-projektion-im-invoker.md) verlangt
außerdem eine Stelle, an der maskiert wird, **bevor** Text gefaltet und gekürzt wird: Ein Budget,
das ein Token in der Mitte durchschneidet, macht es für jede Regel unkenntlich und liefert die
erste Hälfte trotzdem aus.

**Kernfrage:** Wie verlässt eine Antwort den Gateway ohne das Zugangsdatum, aber mit ihrem übrigen
Inhalt — ohne dass ein zweiter Regelsatz neben der Secret-Erkennung entsteht?

## Anforderungen

### Funktional

- Ein erkannter Wert lässt sich in der Antwort ersetzen; der Rest der Antwort bleibt.
- Der Agent sieht, **dass** und **durch welche Regel** ersetzt wurde.
- Die Ersetzung erzeugt nie ungültiges JSON und nie eine ungültige Hülle.
- Je Regel bleibt einstellbar, ob sie beobachtet, maskiert oder blockiert.
- Bestehende Regeln und ihr Verhalten ändern sich durch das Upgrade nicht.

### Nicht-Funktional

- **Ein Regelsatz.** Muster, Vorfilter, Zeitgrenze, Pflege in der Oberfläche und Export bleiben
  dieselben wie in ADR-0011.
- **Keine Aufweichung von E1.** Was heute blockiert, blockiert weiter. Maskieren ist kein neuer
  Vorgabewert für eindeutige Zugangsdaten.
- **Kein Klartext in Befunden.** Wie in ADR-0011: Fingerabdruck, Regel-Id, Länge — nie der Wert.
- **Im Latenzbudget.** Der Normalfall — kein Treffer des Vorfilters — kostet nicht mehr als heute.

## Betrachtete Optionen

### Option 1: Eigener Regelsatz zum Maskieren

Neben den Guard-Regeln entsteht ein zweiter Satz „Maskier-Regeln" mit eigener Tabelle, eigener
Pflege und eigenem Export.

**Positiv:**
- Klare Trennung: Die Guard-Regeln bleiben, was sie sind.
- Maskier-Regeln könnten eigene Eigenschaften bekommen (Ersetzungstext, Teilmaskierung).

**Negativ:**
- Dieselben Muster stünden an zwei Stellen. Wer `ghp_…` blockiert und anderswo maskiert, pflegt
  zwei Wahrheiten über dasselbe Token.
- Zweite Kompilierung, zweiter Vorfilter, zweite Zeitgrenze im Hot Path.
- Die Frage „was gilt, wenn beide treffen?" entsteht erst durch die Trennung.

### Option 2: Dritter Modus `Mask` an den bestehenden Regeln

`GuardMode` bekommt neben `Observe` und `Block` den Wert `Mask`. Eine Regel in diesem Modus
ersetzt ihren Treffer durch eine sichtbare Marke.

**Positiv:**
- Ein Regelsatz, eine Pflege, ein Export; der Modus ist eine Spalte, die es schon gibt.
- Die Eskalation je Regel wird zu einer Leiter: beobachten → maskieren → blockieren.
- Der Vorrang ist einfach festzulegen, weil alle Regeln denselben Treffer sehen.

**Negativ:**
- Der Guard, bisher ein reiner Prüfer, **verändert** jetzt Inhalt. Die Schnittstelle
  `IContentGuard.Inspect(rawJson)` reicht dafür nicht; es braucht eine zweite, die je Textwert
  arbeitet.
- Ein älterer Gateway kennt den Wert nicht und behandelt eine `Mask`-Regel wie `Observe` — bei
  einem Rollback ginge das Secret durch.
- Eine falsch-positive Maske verändert eine Antwort still an einer Stelle, statt laut zu scheitern.

### Option 3: Die Schlüsselnamen-Redaction auf den Rückweg legen

`RedactionService` maskiert schon heute Werte unter Schlüsseln wie `password` oder `token` — für
das Audit. Dieselbe Logik würde auf die ausgelieferte Antwort angewandt.

**Positiv:**
- Vorhandener, getesteter Code; kein Regex im Spiel.
- Trifft strukturierte Antworten zuverlässig.

**Negativ:**
- Greift nur, wo das Secret unter einem sprechenden Schlüssel steht. Die drei Fälle oben sind
  Freitext: eine Zeile in einem Log, ein Wert in einer Datei.
- Maskiert umgekehrt alles unter `token` — auch Seitentoken und CSRF-Felder, die ein Agent zum
  Weiterblättern braucht.
- Zwei verschiedene Begriffe von „Secret" auf demselben Weg.

### Option 4: Bei Beobachten und Blockieren bleiben

Kein Maskieren. Die neuen Muster kommen als `Block`- oder `Observe`-Regeln.

**Positiv:**
- Nichts Neues; E1 bleibt in seiner reinen Form.
- Kein Inhalt wird verändert — eine Antwort ist entweder ganz da oder ganz weg.

**Negativ:**
- Die drei Fälle oben bleiben ungelöst: `Block` kostet den Arbeitsschritt, `Observe` das Secret.
- Eine Regel, die zu oft blockiert, wird abgeschaltet — ADR-0011 hält diese Gefahr für JWT selbst
  fest.
- Die Rückkanal-Pipeline hätte keinen Schutz davor, ein Token beim Kürzen zu halbieren.

## Vorschlag des Autors

Option 2. Maskieren ist kein zweites Thema neben der Secret-Erkennung, sondern eine dritte Antwort
auf denselben Treffer. Die Einwände lassen sich eingrenzen:

- **Nur in der Ergebnis-Richtung.** Argumente auf dem Hinweg werden nie maskiert. Ein Werkzeug mit
  einem still veränderten Argument aufzurufen, täte etwas anderes als der Agent verlangt hat — ein
  Issue mit dem Text `[maskiert:…]` ist schlechter als ein abgelehnter Aufruf. Eine Regel für beide
  Richtungen maskiert eingehend und blockiert ausgehend.
- **Je Textwert, nicht auf dem rohen JSON.** Ersetzt wird innerhalb einer Zeichenkette; Struktur,
  Schlüssel und Zahlen bleiben unberührt. So kann eine Ersetzung das Dokument nicht zerbrechen.
- **Blockieren gewinnt.** Trifft auf denselben Wert auch eine `Block`-Regel, wird blockiert.
- **Vor dem Kürzen.** Jeder Textwert wird gegen alle eingehenden Regeln geprüft, bevor er gefaltet
  oder budgetiert wird. Der Guard über der fertigen Antwort bleibt als zweites Netz.
- **Nichts wird umgestellt.** Die 15 kuratierten Regeln behalten ihren Modus. `Mask` ist der
  Vorgabewert nur für die drei neuen Muster — und auf bestehenden Instanzen kommen auch die
  zunächst beobachtend an.

## Entscheidung

**Gewählte Option:** "Option 2: Dritter Modus `Mask` an den bestehenden Regeln"

Den Ausschlag gibt der eine Regelsatz. Option 1 verdoppelt Muster und Kosten, Option 3 trifft die
Fälle nicht, um die es geht, und Option 4 lässt die Lücke zwischen „alles weg" und „alles da"
offen. In Kauf genommen wird, dass der Guard Inhalt verändert und dafür eine zweite Schnittstelle
bekommt.

## Konsequenzen

### Positiv

- Eine Antwort mit einer Verbindungszeichenkette kommt an — ohne das Passwort.
- Der Weg, eine Regel zu schärfen, hat eine Zwischenstufe; das senkt den Druck, eine zu laute
  `Block`-Regel ganz abzuschalten.
- Die Rückkanal-Pipeline prüft jeden Wert, bevor sie ihn kürzt; ein halbiertes Token kann nicht
  mehr an den Regeln vorbei hinausgehen.
- Der Debug-Mitschnitt des Audits legt künftig das Original **nach** Maskierung ab.

### Negativ

- **Der Guard verändert Inhalt.** Eine falsch-positive Maske ersetzt eine Stelle, die kein Secret
  war. Sie ist sichtbar (`[maskiert:<regel-id>]`) und wird gezählt, aber sie scheitert nicht laut.
- **Rollback-Risiko.** Ein Gateway vor dieser Änderung liest den Moduswert 2 als unbekannt und
  lässt den Treffer durch. Das steht in der Upgrade-Anleitung; der Konfigurationsexport bekommt
  deshalb eine neue Formatfassung, die ältere Gateways ablehnen.
- **Zwei Prüfstellen.** Eingehende Regeln laufen je Wert vor dem Kürzen und einmal über der
  fertigen Antwort. Beobachtete Treffer müssen zusammengeführt werden, damit sie nicht doppelt
  zählen.
- **Maskiert werden nur Zeichenkettenwerte.** Ein Secret in einem Schlüsselnamen oder über zwei
  Werte verteilt trifft nur die Prüfung über der ganzen Antwort — und die kann nur blockieren oder
  beobachten.
- Mustererkennung bleibt unvollständig, wie in ADR-0011 festgehalten. Maskieren ändert daran nichts.

### Folge-Entscheidungen

- Ob einzelne der 15 kuratierten Regeln ab Werk von `Block` auf `Mask` wechseln. Vorschlag: nein —
  das wäre eine Aufweichung von E1 und gehört einzeln entschieden.
- Ob die neuen eingebauten Regeln auf bestehenden Instanzen nach einer Frist von selbst von
  `Observe` auf `Mask` gehen. Vorschlag: nein, der Betreiber schaltet.
- Teilmaskierung (die letzten vier Zeichen stehen lassen). Vorschlag: nicht in dieser Fassung.

### Review

**Reality-Check geplant für:** 2026-12-28

## Weitere Informationen

### Scope

Gilt für Ergebnisse von `tools/call`. ADR-0011 bleibt in allen Punkten gültig; E1 wird um einen
Modus ergänzt, nicht ersetzt. Die Schlüsselnamen-Redaction für das Audit (FR-24) bleibt, wie sie ist.

### Referenzen

- [ADR-0011](./0011-secret-erkennung-als-guardrail.md) — E1 (blockieren), E2 (Freitext-Muster), E4 (ungeprüft heißt blockiert)
- [ADR-0027](./0027-rueckkanal-projektion-im-invoker.md) — die Pipeline, in der maskiert wird
- [Projektionsvertrag](../contracts/rueckkanal-projektion.md) Abschnitt 6 — Marke, Vorrang, Schnittstelle, neue Regeln, Nachsäen
- `src/Bifrost.Abstractions/Guardrails.cs`, `src/Bifrost.Core/Guardrails/SecretGuard.cs`, `src/Bifrost.Persistence/GuardRuleStore.cs`
