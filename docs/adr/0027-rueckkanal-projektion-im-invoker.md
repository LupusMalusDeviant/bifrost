# ADR-0027: Antworten deterministisch formen — je Werkzeug, an einer Stelle im Invoker

- **Status:** Akzeptiert (vom Nutzer abgenommen am 2026-11-16)
- **Datum:** 2026-11-16
- **Autor:** Lead/Integrator, ausgearbeitet mit Claude
- **Betrifft:** [Plan 0004](../plans/0004-umbenennung-und-rueckkanal-projektion.md) Teil P und A;
  bindende Einzelheiten im [Projektionsvertrag](../contracts/rueckkanal-projektion.md) und im
  [Profiler-Vertrag](../contracts/antwort-profiler.md); FR-16 (Ergebnis-Kürzung), NFR-01 (Latenz),
  NFR-04 (Secrets); ADR-0008 (Invocation-Kern), ADR-0011 (Guardrail)

## Kontext und Problemstellung

Der Gateway spart heute Kontext auf dem **Hinweg**: Lazy Discovery und Profile (ADR-0003) halten
die Werkzeugliste klein. Auf dem **Rückweg** reicht er durch, was der Upstream liefert. Eine
Issue-Suche bringt je Treffer Avatar-Adressen, URL-Vorlagen und Knoten-Ids mit, ein Log-Werkzeug
dreihundert gleiche Zeilen, ein Build-Werkzeug einen Stacktrace mit achtzig Frames. Das alles landet
im Kontextfenster des Agenten, wird bezahlt und verdrängt, was er eigentlich braucht.

Das einzige Mittel dagegen ist heute FR-16: eine globale Obergrenze in Zeichen
(`BIFROST_MAX_RESULT_CHARS`, ab Werk aus). Die Bestandsaufnahme (Plan 0004, 1.3) hat daran drei
Mängel gefunden:

- Sie schneidet **am Ende ab**, nicht dort, wo der Ballast sitzt.
- Sie **zerstört die Hülle**: Aus `{content:[…], isError}` wird `{_bifrost_truncated, partialJson}`;
  `isError` und die Blockstruktur sind danach weg, der Agent bekommt abgeschnittenes JSON in einer
  Zeichenkette.
- Das Audit hält die **gekürzte** Größe fest, in Zeichen, unter dem Namen `ResponseBytes`. Was
  ankam, weiß danach niemand mehr.

Alle fünf Fassaden (MCP, Meta-Tools, REST, Oberfläche, Webhook) laufen durch `IToolInvoker`
(ADR-0008). Eine Formung an dieser einen Stelle wirkt überall gleich.

Gemessen ist bisher nur die Machbarkeit, nicht der Nutzen im Betrieb: Eine Projektion im Durchlauf
(`Utf8JsonReader` → `Utf8JsonWriter`) bringt eine synthetische Trefferliste von 124 079 auf
9 791 Bytes, mit 136 Bytes Allokation und rund 300 µs je Durchlauf (Release, Entwicklerrechner,
keine Referenzhardware). Wie viel echte Upstreams hergeben, zeigt erst der Probelauf.

**Kernfrage:** Wie wird aus einer Upstream-Antwort das, was der Agent braucht — vorhersagbar, im
Mikrosekundenbereich, ohne dass ein Betreiber blind etwas wegnimmt und ohne dass hinterher unklar
ist, was ankam und was hinausging?

## Anforderungen

### Funktional

- Je Upstream-Server und Werkzeug lässt sich festlegen, welche Felder einer Antwort den Gateway
  verlassen. Was nicht genannt ist, geht nicht hinaus.
- Objekt-Arrays lassen sich kompakter ausgeben, ohne dass die Antwort aufhört, gültiges JSON und
  eine gültige `CallToolResult`-Hülle zu sein.
- Text lässt sich bereinigen: Base64-Blöcke, Tracking-Parameter, Stacktraces, wiederholte Zeilen,
  Budgets je Feld.
- Eine Regel lässt sich **im Probelauf** einführen: Sie rechnet und protokolliert die Ersparnis,
  liefert aber das Original aus.
- Das Audit belegt je Aufruf, was ankam, welche Regel es verändert hat und was hinausging.
- Der Agent erkennt, dass eine Antwort geformt wurde.
- Ein Werkzeug ohne Regel verhält sich wie heute.

### Nicht-Funktional

- **Deterministisch.** Gleiche Antwort und gleiche Regel ergeben bytegleich dasselbe Ergebnis. Kein
  Modellaufruf, keine Heuristik zur Laufzeit.
- **Im Latenzbudget** (NFR-01, 7 ms): unter einer Millisekunde für 100 KB, ohne Kopie der Nutzlast.
- **Fällt sicher aus.** Eine fehlerhafte Regel darf keinen Aufruf scheitern lassen und kein Secret
  durchlassen.
- **Keine neue Abhängigkeit** in der Laufzeit (NFR-10, Lieferketten-Gates).
- **Zur Laufzeit tauschbar**, wie Guard-Regeln und Upstreams.

## Betrachtete Optionen

### Option 0: Bei der globalen Obergrenze bleiben

FR-16 bleibt das einzige Mittel; allenfalls wird der Hüllenfehler behoben.

**Positiv:**
- Kein neues Konzept, keine Regelpflege.
- Schützt zuverlässig vor dem einen Ausreißer mit zwei Megabyte.

**Negativ:**
- Trifft nicht den Ballast: Eine Antwort knapp unter der Grenze bleibt, wie sie ist.
- Schneidet ohne Ansehen des Inhalts ab — das letzte Feld kann das wichtige sein.
- Die Frage „was kam an?" bleibt im Audit offen.

### Option 1: Deterministische Formung je Werkzeug im Invoker

Eine Pipeline an der Stelle, an der heute gekürzt wird: Positivliste der Felder, Bereinigung von
Text, Budgets, zuletzt die globale Obergrenze. Regeln je Server und Werkzeug, in der Datenbank,
zur Laufzeit tauschbar.

**Positiv:**
- Wirkt in allen fünf Fassaden gleich und liegt vor der Inhaltsprüfung, die damit nur noch sieht,
  was wirklich hinausgeht.
- Vorhersagbar und prüfbar: Eine Regel lässt sich offline gegen eine Beispielantwort rechnen.
- Schließt an Vorhandenes an — Regelablage, Probelauf-Modus und Importweg gibt es für die
  Guard-Regeln bereits.
- Die Positivliste fällt in die sichere Richtung: Ein Feld, das ein Upstream neu einführt, geht
  nicht von selbst hinaus.

**Negativ:**
- Regeln müssen je Werkzeug geschrieben und gepflegt werden; ändert ein Upstream sein Format,
  veraltet die Regel.
- Eine zu strenge Regel nimmt dem Agenten etwas weg, ohne dass er es merkt, wenn die Kennzeichnung
  fehlt.
- Neue Fläche im Kern: Regelschema, Ablage, Oberfläche, CLI, Audit-Felder, eine neue Fassung der
  Audit-Kette.

### Option 2: Große Antworten von einem Modell zusammenfassen lassen

Der Gateway ruft für Antworten oberhalb einer Schwelle ein Sprachmodell und liefert die
Zusammenfassung.

**Positiv:**
- Keine Regelpflege; kommt mit jedem Format zurecht.
- Kann inhaltlich gewichten, was eine Feldliste nicht kann.

**Negativ:**
- Nicht deterministisch und nicht nachrechenbar — dieselbe Antwort ergibt zweimal Verschiedenes.
- Hundert- bis tausendfache Latenz, eigene Kosten, eigener Ausfallpunkt.
- Die ungefilterte Antwort geht an einen weiteren Modellanbieter — genau der Abfluss, den ADR-0011
  verhindern soll.
- ADR-0011 hält fest, dass der Gateway kein LLM-Proxy ist und keine modellgestützte Prüfung im
  Hot Path fährt.

### Option 3: Formung nur in der MCP-Fassade

Die Regeln greifen in `GatewayMcpHandlers`, dort, wo aus dem Ergebnis ein `CallToolResult` wird.

**Positiv:**
- Arbeitet auf dem typisierten SDK-Objekt statt auf rohem JSON.
- Der Kern bleibt unberührt.

**Negativ:**
- REST, Meta-Tools, Oberfläche und Webhook bekämen weiter die ungeformte Antwort — vier Wege am
  Filter vorbei.
- Liegt **nach** Inhaltsprüfung und Audit: Der Guard blockiert wegen Feldern, die nie
  ausgeliefert würden, und das Audit kennt die Formung nicht.
- Widerspricht ADR-0008 („ein Kern, mehrere Fassaden").

### Option 4: Dem Upstream oder dem Client überlassen

Der Gateway formt nicht. Upstream-Server bieten eigene Feldauswahl an, oder der Client kürzt selbst.

**Positiv:**
- Kein Aufwand im Gateway.
- Wer das Werkzeug gebaut hat, weiß am besten, was wichtig ist.

**Negativ:**
- Fremde Upstream-Server lassen sich nicht ändern; die wenigsten bieten eine Feldauswahl.
- Clients kürzen, wenn überhaupt, erst nachdem die Antwort im Kontext angekommen ist.
- Der Gateway ist die einzige Stelle, die alle Upstreams sieht und unter der Kontrolle des
  Betreibers steht.

## Vorschlag des Autors

Option 1. Sie ist die einzige, die den Ballast dort trifft, wo er sitzt, und dabei nachrechenbar
bleibt. Der Preis — Regelpflege je Werkzeug — wird durch zwei Dinge tragbar, die deshalb zur
Entscheidung gehören und nicht bloß Komfort sind:

- **Der Probelauf.** Eine Regel wird beobachtend eingeführt, wie eine Guard-Regel nach ADR-0011.
  Scharf geschaltet wird, wenn die Ersparnis und die weggefallenen Felder gesichtet sind.
- **Der Profiler.** Er sieht echten Antworten zu und schlägt Regeln vor. Er sieht, was aufgebläht
  ist — nicht, was der Agent braucht; deshalb setzt er nie selbst eine Regel in Kraft.

Fünf Grundsätze tragen den Entwurf; ihre genaue Fassung steht im Vertrag:

1. **Positivliste, keine Negativliste.** Genannt wird, was hinausgeht.
2. **Die Hülle bleibt.** Geformt wird innerhalb von `content` und `structuredContent`; `isError`
   wird nie projiziert. Das gilt auch für die globale Obergrenze, die als letzte Stufe aufgeht.
3. **Vor der Inhaltsprüfung.** Ein Feld, das nicht ausgeliefert wird, blockiert keinen Aufruf.
4. **Fehler öffnen, Secrets nicht.** Scheitert die Formung, geht das Original hinaus — durch
   Maskierung und Guard wie jede andere Antwort. Scheitert die Maskierung, geht nichts hinaus.
5. **Das Audit belegt, es bewahrt nicht auf.** Größe und SHA-256 des Originals, Größe des
   Ausgelieferten, Hash der Regel, Zähler. Die Nutzlast selbst wird nicht abgelegt (NFR-04).

Das Regelformat ist JSON über den bestehenden Konfigurationsexport — kein zweiter Importweg, keine
YAML-Abhängigkeit.

## Entscheidung

**Gewählte Option:** "Option 1: Deterministische Formung je Werkzeug im Invoker"

Den Ausschlag geben Determinismus und die eine Stelle für alle Fassaden. Option 2 scheitert an
Latenz, Nachvollziehbarkeit und am zusätzlichen Datenabfluss, Option 3 an vier Wegen am Filter
vorbei. In Kauf genommen werden die Regelpflege und die neue Fläche im Kern.

## Konsequenzen

### Positiv

- Der Rückweg wird je Werkzeug steuerbar, nicht nur gedeckelt.
- FR-16 verliert seinen Hüllenfehler: Auch eine gekürzte Antwort bleibt ein gültiges
  `CallToolResult` mit `isError`.
- Das Audit beantwortet zum ersten Mal die Frage „was kam an?", und `ResponseBytes` bekommt eine
  festgeschriebene Bedeutung.
- Die Inhaltsprüfung arbeitet auf weniger Daten; ein großes Ergebnis, das auf wenige Kilobyte
  projiziert wird, scheitert nicht mehr an der Prüfgrenze.
- Der Kern liest die Nutzlast nicht mehr bis zu dreimal als Zeichenkette aus (Kürzung, Guard,
  Audit).

### Negativ

- **Eine Regel kann wegnehmen, was der Agent braucht.** Probelauf, Kennzeichnung in der Antwort und
  der Schutz vor einer Projektion, die nichts mehr trifft, mindern das; sie heben es nicht auf.
- **Regeln veralten**, wenn ein Upstream sein Antwortformat ändert. Die Positivliste lässt dann
  neue Felder weg, statt sie durchzulassen — sicher, aber stumm.
- **Die Marke `_bifrost_truncated` entfällt.** Wer sie auswertet, muss umstellen; mit der
  Umbenennung zusammen ist es ein Bruch statt zwei.
- **Die Audit-Kette bekommt eine zweite Fassung.** Der Prüfer rechnet beide; ein älterer Gateway
  kann Zeilen der neuen Fassung nicht prüfen.
- **Der Konfigurationsexport bekommt Formatfassung 2.** Ältere Gateways lehnen ihn ab.
- Mehr Fläche: eine Tabelle, zwei Migrationen je Anbieter, REST-Endpunkte, CLI-Befehle, eine Seite
  in der Oberfläche.

### Folge-Entscheidungen

- Ob Originale zusätzlich verschlüsselt abgelegt und über ein Meta-Tool nachholbar werden
  (Plan 0004, P7). Vorschlag: zunächst nicht — nur Hash und Größe.
- Ob `tools/list` künftig `outputSchema` durchreicht. Heute nicht; mit Projektion müsste das Schema
  mitprojiziert werden.
- Ob `resources/read` und `prompts/get` dieselbe Pipeline bekommen. Sie laufen nicht durch den
  Invoker und sind hier nicht erfasst.
- Eine kompaktere Textform für Tabellen hinter demselben Schalter.

### Review

**Reality-Check geplant für:** 2026-12-28

## Weitere Informationen

### Scope

Gilt für die Antwort von `tools/call` auf allen fünf Fassaden. Nicht erfasst: `resources/read`,
`prompts/get`, die Argumente auf dem Hinweg (dort wirkt nur der Guard) und der Inhalt von
`tools/list`.

Das Maskieren von Zugangsdaten ist eine eigene Entscheidung und steht in
[ADR-0028](./0028-maskieren-als-dritter-guard-modus.md); die Pipeline hier gibt ihm nur seinen Platz.

### Tooling-Empfehlung

Zeitmessung in der bestehenden Vorrichtung (`tests/Bifrost.Performance.Tests`, hinter dem
Benchmark-Schalter) und Allokationstests über `GC.GetAllocatedBytesForCurrentThread()` in der
gewöhnlichen Testsuite. Kein BenchmarkDotNet — es wäre eine neue Entwicklungsabhängigkeit ohne
Aussage, die die vorhandene Vorrichtung nicht liefert.

### Referenzen

- [Projektionsvertrag](../contracts/rueckkanal-projektion.md) — Pipeline, Regelschema, Schnittstellen, Audit, Nachweise
- [Profiler-Vertrag](../contracts/antwort-profiler.md) — Lernmodus und Regelentwürfe
- [Plan 0004](../plans/0004-umbenennung-und-rueckkanal-projektion.md) Abschnitt 1.3 — der Rückweg heute und die vier Befunde
- [ADR-0008](./0008-api-mcp-bridge-als-erstklassige-fassaden.md), [ADR-0011](./0011-secret-erkennung-als-guardrail.md)
- `src/Bifrost.Core/Invocation/ToolInvoker.cs:240–264`, `ResultCompressor.cs` — der heutige Rückweg
