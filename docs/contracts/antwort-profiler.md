# Vertrag: Antwort-Profiler

**Stand:** 2026-11-16 · **Status:** Abgenommen vom Nutzer am 2026-11-16 · **Gilt für:** Plan 0004, A2 – A5
**Grundlage:** [ADR-0027](../adr/0027-rueckkanal-projektion-im-invoker.md),
[Projektionsvertrag](rueckkanal-projektion.md),
[Plan 0004](../plans/0004-umbenennung-und-rueckkanal-projektion.md) Abschnitt 5

Der Profiler sieht echten Antworten zu, rechnet je Werkzeug eine **Formskizze** und leitet daraus
einen **Regelentwurf** für die Rückkanal-Projektion ab. Dieser Vertrag legt fest, was er sieht, was
er ablegt, wonach er entscheidet und was er nie tut. Weicht die Umsetzung ab, wird zuerst dieser
Vertrag geändert und erneut abgenommen.

## 1. Die Grenze zuerst

**Der Profiler sieht, was aufgebläht ist — nicht, was der Agent braucht.** Ein Feld, das groß und
langweilig aussieht, kann genau das sein, worauf ein Arbeitsschritt wartet. Daraus folgen vier
Zusagen, jede mit einem Test in Abschnitt 9:

- **Z1 — Er setzt nichts in Kraft.** Der Profiler hat keinen Schreibweg zur Regelablage. Ein
  Entwurf wird zur Regel, wenn ein Mensch ihn übernimmt.
- **Z2 — Entwürfe stehen auf `observe`.** Ein Entwurf im Modus `enforce` ist ein Fehler im
  Generator.
- **Z3 — Er streicht nur, was eindeutig Ballast ist.** Alles andere steht als Kandidat im Bericht
  und bleibt in der Regel.
- **Z4 — Er legt keine Werte ab.** Nicht in der Datenbank, nicht im Log, nicht in einer Antwort der
  Verwaltungs-API.

Weitere Grenzen, die in Bericht und Dokumentation stehen:

- Er sieht nur Aufrufe aus der Lernzeit. Ein Feld, das nur bei seltenen Argumenten gefüllt ist,
  erscheint als „fast immer leer".
- Ein Werkzeug, das je nach Argument verschiedene Formen liefert, bekommt **eine** Skizze über
  alle.
- Die Ersparnis im Bericht ist eine Schätzung über die Stichprobe. Die belastbare Zahl liefert der
  Probelauf der Regel.

## 2. Ein- und Ausschalten

Der Profiler ist **ab Werk aus**. Er ist ein Lernmodus, kein Dauerbetrieb.

| Weg | Wirkung |
|---|---|
| `FAOLAN_PROFILER=on` | läuft ab Start für alle Upstreams |
| `faolan profile start [--server <slug>] [--for <dauer>]` | zur Laufzeit, ohne Neustart; Vorgabe für `--for` sind 7 Tage. **Nicht dauerhaft:** Nach einem Neustart gilt wieder die Variable |
| `faolan profile stop [--server <slug>]` | beendet das Lernen; die Skizzen bleiben |
| `FAOLAN_PROFILER_SAMPLE_RATE` | Anteil der Aufrufe nach der Anlaufphase, 0 – 1, Vorgabe `0.1` |

Start, Stopp, Zurücksetzen und jeder erzeugte Entwurf erzeugen einen Verwaltungs-Eintrag im Audit.

## 3. Abgriff

In `Faolan.Abstractions`:

```csharp
public readonly ref struct ObservedResponse
{
    public ServerId Server { get; init; }
    public string Tool { get; init; }
    /// Das Original NACH Maskierung, UTF-8. Nur während des Aufrufs gültig.
    public ReadOnlySpan<byte> MaskedOriginalUtf8 { get; init; }
}

public interface IResponseObserver
{
    /// Stichprobenentscheidung. Ohne Allokation, ohne Sperre, O(1).
    bool WantsSample(ServerId server, string tool);

    /// Kopiert, was er behält, und kehrt zurück. Wartet nie, wirft nie.
    void Observe(in ObservedResponse sample);
}
```

- Der Shaper ruft `WantsSample` vor der Formung. Nur bei `true` erzeugt er das maskierte Original
  und ruft `Observe` — der Aufwand entsteht ausschließlich für gezogene Stichproben.
- **Nicht gezogen wird:** bei `isError: true`, bei einem zurückgehaltenen Ergebnis
  (`GuardBlocked`), oberhalb von 8 MiB. Diese Fälle werden nur gezählt.
- `Observe` kopiert die Bytes in einen gepoolten Puffer und legt ihn in einen **begrenzten Kanal**
  (höchstens 32 Stichproben und 64 MiB). Ist der Kanal voll, wird die Stichprobe verworfen und
  gezählt. Ein Aufruf wartet nie auf den Profiler.
- Ein einzelner Hintergrundarbeiter rechnet die Skizze und gibt den Puffer zurück. Fehler dort
  erreichen nie einen Aufruf.

**Stichprobe, deterministisch:** Die ersten 20 Antworten eines Werkzeugs werden immer gezogen
(Anlaufphase), danach jede *n*-te mit *n* = gerundet 1 / Rate — über einen Zähler, nicht über
Zufall. Höchstens 500 Stichproben je Werkzeug und Tag.

## 4. Formskizze `faolan.profile/v1`

Eine Skizze je Server und Werkzeug. Sie ist das **Einzige**, was der Profiler ablegt.

```json
{
  "schema": "faolan.profile/v1",
  "samples": 137,
  "notSampled": { "error": 4, "blocked": 0, "tooLarge": 1, "dropped": 0 },
  "bytes": { "sum": 16998823, "max": 412004 },
  "payloads": { "structured": 0, "jsonText": 137, "plainText": 0, "nonText": { "image/png": 3 } },
  "paths": [
    {
      "path": "items[].labels_url",
      "present": 27400, "parents": 27400, "empty": 0,
      "types": { "string": 27400 },
      "bytes": 2548200,
      "length": { "min": 66, "max": 84, "mean": 78, "buckets": { "64": 27400 } },
      "constant": false,
      "kinds": { "url": 27400, "urlTemplate": 27400 }
    }
  ],
  "text": { "stacktraces": 0, "repeatedLineRuns": 0, "base64Runs": 0, "dataUris": 0, "ansi": 0, "trackingParams": 0 }
}
```

| Feld je Pfad | Bedeutung |
|---|---|
| `path` | in der Pfadsprache des Projektionsvertrags (4.2); Arrays zu `[]` zusammengefasst |
| `present` / `parents` | wie oft der Pfad vorkam / wie oft sein Behälter — das Verhältnis ist die Belegung |
| `empty` | davon `null`, `""`, `[]` oder `{}` |
| `types` | Anzahl je JSON-Typ |
| `bytes` | Summe der Bytes samt Schlüssel; geteilt durch `bytes.sum` der Skizze der **Anteil** |
| `length` | für Zeichenketten in Zeichen, für Arrays in Elementen; `buckets` sind Zweierpotenzen (untere Grenze → Anzahl) und damit zusammenführbar |
| `constant` | alle bisher gesehenen Werte waren gleich |
| `kinds` | Anzahl der Werte je Art: `url`, `urlTemplate`, `base64`, `dataUri`, `timestamp`, `uuid`, `hexId`, `multiline`, `stacktrace` |

**Was nie in der Skizze steht:** kein Wert, kein Wertausschnitt, kein Beispiel, kein Hash eines
Werts. `constant` wird über einen Hash des ersten Werts gehalten, der **nur im Arbeitsspeicher**
liegt, mit einem je Prozess zufälligen Salz; nach einem Neustart wird am nächsten gesehenen Wert
neu angesetzt. Eine Änderung genau über einen Neustart hinweg kann so unbemerkt bleiben — das ist
der Preis dafür, dass kein Hash abgelegt wird.

**Schlüsselnamen werden abgelegt** — ohne sie lässt sich keine Regel schreiben. Zwei Sicherungen:
Ein Objekt mit mehr als 50 verschiedenen Schlüsseln auf einer Ebene gilt als Abbildung und wird zu
`*` zusammengefasst; ein Schlüsselname, auf den eine eingehende Guard-Regel anschlägt, ebenfalls.
Trägt ein Upstream Personenbezug in wenigen, unauffälligen Schlüsselnamen, steht er in der Skizze.
Das steht so in der Betriebsdokumentation.

**Grenzen:** höchstens 2000 Pfade je Werkzeug und 16 Schritte je Pfad; was darüber liegt, wird
gezählt (`overflow`) und nicht einzeln geführt.

**Determinismus:** Dieselbe Folge von Stichproben ergibt bytegleich dieselbe Skizze (kanonische
Schlüsselreihenfolge, Pfade ordinal sortiert).

**Ablage:** Tabelle `ResponseProfiles` — `ServerId`, `Tool`, `SketchJson`, `Samples`,
`FirstSampleAt`, `LastSampleAt`; Schlüssel (`ServerId`, `Tool`). Geschrieben wird höchstens alle
60 Sekunden und beim Herunterfahren; ein Absturz kostet höchstens diese Spanne. Migration für beide
Anbieter mit Rückweg. Wird ein Upstream entfernt, gehen seine Skizzen mit. Skizzen sind Teil einer
Sicherung, aber **nicht** des Konfigurationsexports.

## 5. Von der Skizze zum Entwurf

Der Generator ist eine reine Funktion: Skizze hinein, Regel und Bericht heraus. Keine Uhr, kein
Zufall, kein Zugriff auf Antworten.

**Voraussetzung:** mindestens 20 Stichproben. Darunter verweigert `draft` mit einer Meldung, die die
Zahl nennt; `--force` erzeugt den Entwurf trotzdem und schreibt die Warnung in den Bericht.

### 5.1 Was gestrichen wird

Ein Pfad fehlt im `select` des Entwurfs **nur**, wenn eine dieser Regeln zutrifft:

| Regel | Bedingung |
|---|---|
| **S1 immer leer** | `empty / present` ≥ 0,99 |
| **S2 URL-Vorlage** | `kinds.urlTemplate / present` ≥ 0,99 — Werte der Form `…{/name}` oder `…{?a,b}` |

Mehr nicht. Beide Fälle tragen in keiner gesehenen Antwort eine Auskunft.

### 5.2 Was Kandidat bleibt

Diese Pfade **bleiben in der Regel** und stehen im Bericht, nach Anteil absteigend, mit Begründung:

| Kandidat | Bedingung | warum nicht gestrichen |
|---|---|---|
| **K1 konstant** | `constant`, Länge ≥ 64 Zeichen | gleiche Argumente ergeben gleiche Antworten; das Feld kann die eigentliche Auskunft sein |
| **K2 undurchsichtige Kennung** | `uuid` oder `hexId` ≥ 0,99 | Kennungen braucht ein Agent für den Folgeaufruf |
| **K3 Adresse** | `url` ≥ 0,99, keine Vorlage | Links gibt ein Agent an Menschen weiter |
| **K4 großer Anteil** | Anteil ≥ 10 % | groß heißt nicht unwichtig |
| **K5 lange Liste** | Array, 95 % der Längen > 50 | Elemente wegzulassen ist nie eindeutig; der Bericht schlägt `maxItems` vor, der Entwurf setzt es nicht |
| **K6 selten belegt** | Belegung < 5 % | selten kann heißen: nur im wichtigen Fall |

### 5.3 Was der Entwurf außerdem setzt

| Abschnitt | Bedingung | Wert |
|---|---|---|
| `shape.<array>.format = "table"` | Array von Objekten, mittlere Länge ≥ 5, alle gewählten Pfade darunter sind Skalare ohne weiteres Array | — |
| `text.strip` | die jeweilige Art kam in mindestens einer Stichprobe vor | `base64`, `data-uri`, `ansi`, `tracking-params` |
| `text.fold.stacktrace` | `stacktraces` > 0 | `{ "head": 5, "tail": 3 }` |
| `text.fold.repeatedLines` | `repeatedLineRuns` > 0 | `true` |
| `text.budget.fields.<pfad>` | Zeichenkette, 95 % der Längen ≥ 2000 Zeichen | diese Länge, aufgerundet auf das nächste Tausend — es trifft nur Ausreißer |
| `nonText`, `notice`, `mode` | immer | `keep`, `summary`, `observe` |

`text.budget.default`, `budget.total` und `maxItems` setzt der Generator nie.

Das `select` nennt **Blattpfade**, keine Teilbäume: Ein Teilbaum ließe neue Felder des Upstreams
von selbst durch. Übersteigt die Liste 256 Pfade, werden die tiefsten vollständig gewählten
Teilbäume zu ihrem Elternpfad zusammengefasst, bis sie passt; der Bericht nennt jede
Zusammenfassung. Zusammengefasste Schlüssel erscheinen als `*`.

Trifft nichts aus 5.1 und 5.3 zu, sagt der Bericht das — ein Entwurf, der nichts ändert, wird nicht
als Erfolg ausgegeben.

### 5.4 Der Bericht

Zum Entwurf gehört ein Bericht, maschinenlesbar und als Text: Zahl und Zeitraum der Stichproben;
je gestrichenem Pfad die Regel (S1, S2) und sein Anteil; je Kandidat die Regel (K1 – K6) und sein
Anteil; je gesetztem Abschnitt die Bedingung; die geschätzte Ersparnis in Bytes und Prozent; die
Grenzen aus Abschnitt 1 im Wortlaut. Der Bericht enthält Pfade und Zahlen — keine Werte.

## 6. Bedienung

- **CLI:** `faolan profile status | start | stop | draft <server> [<werkzeug>] [-o <datei>]
  [--force] | reset [<server> [<werkzeug>]]`. `draft` ohne Werkzeug erzeugt je Werkzeug des Servers
  einen Entwurf. Ohne `-o` geht die Regel auf die Standardausgabe, der Bericht auf die
  Fehlerausgabe.
- **REST**, nur Admin: `GET /api/v1/projection/profiles` (Übersicht), `GET
  /api/v1/projection/profiles/{server}/{tool}` (Skizze), `POST …/{server}/{tool}/draft` (Regel und
  Bericht; legt nichts ab), `DELETE …/{server}/{tool}`, `POST /api/v1/projection/profiler/start`
  und `/stop`.
- **Oberfläche:** je Werkzeug Stichprobenzahl und größte Pfade; Entwurf neben der geltenden Regel;
  ein Knopf „Als Probelauf übernehmen", der die Regel über den gewöhnlichen Weg der Regelablage im
  Modus `observe` anlegt. Einen Knopf, der einen Entwurf scharf schaltet, gibt es nicht.

## 7. Kosten

| Fall | Zusage |
|---|---|
| Profiler aus | kein Aufwand im Aufruf außer einer Abfrage |
| an, Aufruf nicht gezogen | `WantsSample`: keine Allokation |
| an, Aufruf gezogen | eine Kopie des maskierten Originals in einen gepoolten Puffer; die Skizze rechnet der Hintergrundarbeiter |
| Arbeitsspeicher | Kanal höchstens 64 MiB; Skizzen höchstens 2000 Pfade je Werkzeug |

Gemessen wird in P11 zusammen mit der Projektion; Zahlen stehen erst dann hier.

## 8. Vom Nutzer bestätigt

Mit der Abnahme am 2026-11-16 bestätigt. Jede Änderung braucht eine neue Abnahme.

| Nr. | Frage | festgeschrieben | Abschnitt |
|---|---|---|---|
| 1 | Vorgabe | **aus**; Einschalten per Variable oder zur Laufzeit, dort befristet und nicht dauerhaft | 2 |
| 2 | Wie mutig streicht der Entwurf? | **nur S1 und S2**; alles andere ist Kandidat | 5.1, 5.2 |
| 3 | Schlüsselnamen in der Skizze | **werden abgelegt**, mit den zwei Sicherungen | 4 |
| 4 | `constant` ohne abgelegten Hash | **ja**, mit der Lücke über einen Neustart | 4 |

## 9. Nachweise

Ein Paket gilt als fertig, wenn ein Lauf über **alle** Testprojekte der Solution vorliegt, geprüft
wie in der CI, mit Befehl und Zahlen.

| Was | Test |
|---|---|
| Z1 kein Schreibweg: nach `draft` ist die Regelablage unverändert; `Faolan.Core/Profiling` verweist nicht auf `IProjectionRuleStore` | Core, Governance |
| Z2 jeder Entwurf steht auf `observe` | Core — über alle Skizzen-Fixtures |
| Z3 nur S1 und S2 streichen; jeder Kandidat K1 – K6 bleibt im `select` | Core — je Regel eine Skizze knapp über und knapp unter der Schwelle |
| Z4 kein Wert in Tabelle, Log, REST-Antwort, Bericht | `Faolan.Security.Tests` — jede Zeichenkette der Fixtures trägt einen Köder; gesucht wird in allen vier Senken |
| Der Abgriff bekommt nur das maskierte Original | Security — Köder-Secret, `Mask`-Regel, Prüfung am Beobachter |
| Kein Abgriff bei Fehler, zurückgehaltenem Ergebnis, Übergröße | Core |
| Voller Kanal verwirft und zählt; ein blockierter Arbeiter hält keinen Aufruf auf | Integration — Arbeiter angehalten, Aufrufe laufen |
| Stichprobe: Anlaufphase, jede *n*-te, Tagesgrenze | Core |
| Skizze deterministisch; Zusammenführen zweier Skizzen | Core — goldene Dateien |
| Abbildungen zu `*`; Schlüsselname mit Guard-Treffer zu `*`; Überlauf gezählt | Core, Security |
| Generator: je Zeile aus 5.3 Positiv- und Negativfall; Zusammenfassung über 256 Pfade | Core — goldene Dateien |
| Jeder Entwurf besteht die Prüfung der Regelablage | Core — Entwurf durch den Validator aus dem Projektionsvertrag 4.5 |
| `draft` unter 20 Stichproben verweigert; `--force` warnt | `Faolan.Cli.Tests` |
| Skizze übersteht einen Neustart; entfernter Upstream nimmt sie mit | Integration |
| Migrationen beider Anbieter mit Rückweg | Governance (vorhanden) |
| Jede neue Variable ist dokumentiert | Governance (vorhanden) |
| `WantsSample` ohne Allokation | Core — `GC.GetAllocatedBytesForCurrentThread()` |

Skizzen-Fixtures entstehen aus den Antwortformen des Projektionsvertrags (Abschnitt 14), nicht aus
echten Antworten.

## 10. Außerhalb des Repos `[braucht Nutzer]`

Der Lauf gegen die echten Upstreams der Badwolf-Instanz und das Urteil über die Entwürfe (Plan
0004, A5). Ob eine Regel einem Agenten etwas nimmt, das er braucht, kann nur beurteilen, wer die
Arbeit kennt, die über diese Werkzeuge läuft.
