# Vertrag: Rückkanal-Projektion

**Stand:** 2026-11-16 · **Status:** Abgenommen vom Nutzer am 2026-11-16 · **Gilt für:** Plan 0004, P1 – P11
**Grundlage:** [ADR-0027](../adr/0027-rueckkanal-projektion-im-invoker.md),
[ADR-0028](../adr/0028-maskieren-als-dritter-guard-modus.md),
[Plan 0004](../plans/0004-umbenennung-und-rueckkanal-projektion.md) Abschnitte 1.3 und 4

Dieser Vertrag legt fest, wie die Antwort eines `tools/call` zwischen Upstream und Aufrufer
geformt wird: Reihenfolge, Regelformat, Schnittstellen, Audit und Nachweise. Weicht die Umsetzung
ab, wird zuerst dieser Vertrag geändert und erneut abgenommen.

Alle Bezeichner stehen in der Form **nach** der Umbenennung (`Faolan.*`, `FAOLAN_*`); der
[Umbenennungsvertrag](umbenennung-faolan.md) geht voraus. Wo dieser Vertrag an zwei Stellen vom
Plan 0004 abweicht (Einheiten in 1, Prüfung je Wert in 6.2), gilt der Vertrag.

## 1. Begriffe und Einheiten

| Begriff | Bedeutung |
|---|---|
| **Hülle** | das `CallToolResult` des Upstreams als JSON: `content`, `structuredContent`, `isError`, `_meta` |
| **Nutzlast** | ein JSON-Dokument innerhalb der Hülle: `structuredContent`, oder der Inhalt eines `text`-Blocks, der vollständig als JSON lesbar ist |
| **Textwert** | ein Zeichenkettenwert in einer Nutzlast, oder der Inhalt eines `text`-Blocks, der kein JSON ist |
| **Original** | die Hülle, wie der Gateway sie hält, nachdem das SDK die Antwort gelesen hat (`SdkUpstreamConnection.cs:127`) — nicht die Bytes auf dem Draht |
| **Bytes** | UTF-8-Bytes der serialisierten Hülle. Maß für **Größen** (Audit, Nachweis, Messung) |
| **Zeichen** | Unicode-Codepunkte. Maß für **Budgets** und für die globale Obergrenze |

Bytes für Größen, weil die Pipeline auf UTF-8 arbeitet und die Länge dort nichts kostet. Codepunkte
für Budgets, weil ein Schnitt an einer Codepunktgrenze nie ein Zeichen zerteilt. `FAOLAN_MAX_RESULT_CHARS`
zählt damit künftig Codepunkte statt UTF-16-Einheiten; der Unterschied betrifft nur Zeichen
außerhalb der Basisebene (Emoji) und steht im CHANGELOG.

## 2. Die Pipeline

Sie ersetzt `ResultCompressor.Compress` in `ToolInvoker.CallUpstreamAsync` und läuft für jede
Antwort mit Status `Success` — auch für `isError: true`.

```
Original
  0  Maß nehmen           Bytes; SHA-256, sobald etwas verändert wird (9.1)
  ·  Abgriff Profiler     Stichprobe, wartet nie (Profiler-Vertrag)
  1  Projektion           select · shape                        Regel nötig
  2  Cutter
       a  entfernen       strip                                 Regel nötig
       b  prüfen          alle eingehenden Guard-Regeln je Textwert:
                          Mask ersetzt · Block hält zurück · Observe zählt     immer
       c  falten          fold                                  Regel nötig
       d  Budget          budget                                Regel nötig
  3  Obergrenze           hüllenerhaltend (FR-16)               wenn gesetzt
  4  Guard eingehend      über der fertigen Hülle, wie heute    immer
```

| Stufe | wirkt, wenn | im Modus `observe` | bei `isError: true` |
|---|---|---|---|
| 1, 2a, 2c | eine Regel mit dem Abschnitt gilt | gerechnet, nicht ausgeliefert | **nie** |
| 2b | der Guard eingeschaltet ist | ausgeliefert | ja |
| 2d | eine Regel mit `budget` gilt | gerechnet, nicht ausgeliefert | ja |
| 3 | `FAOLAN_MAX_RESULT_CHARS` > 0 oder `budget.total` | global: ausgeliefert · `budget.total`: gerechnet | ja |
| 4 | der Guard eingeschaltet ist | ausgeliefert | ja |

Maskierung, Obergrenze und Guard hängen nicht am Modus einer Projektionsregel: Sie sind Schutz,
kein Probelauf.

## 3. Invarianten

Jede hat einen Test in Abschnitt 14.

- **I1 — Ohne Eingriff bytegleich.** Gilt für ein Werkzeug keine Regel, trifft keine Guard-Regel
  und greift die Obergrenze nicht, ist die ausgelieferte Hülle bytegleich mit dem Original.
- **I2 — Die Hülle bleibt.** Was ausgeliefert wird, ist nach jeder Stufe ein gültiges
  `CallToolResult`. `isError` wird nie verändert, `_meta` des Upstreams nie entfernt. Die Marke
  `_bifrost_truncated` und die Ersatzobjekte des `ResultCompressor` entfallen.
- **I3 — Fehler werden nicht projiziert.** Bei `isError: true` laufen nur 2b, 2d, 3 und 4.
- **I4 — Maskiert wird, bevor irgendetwas abgelegt wird.** Kein Audit-Mitschnitt, keine
  Profiler-Stichprobe und kein Log sieht einen Textwert vor 2b. Projektion, Faltung und Budget
  wirken nur auf das, was ausgeliefert wird.
- **I5 — Geprüft wird vor dem Kürzen.** Kein Textwert wird gefaltet oder budgetiert, bevor er 2b
  durchlaufen hat. Ein Budget kann kein Token halbieren, das danach keine Regel mehr erkennt.
- **I6 — Blockieren schlägt Maskieren.** Trifft auf einen Textwert eine `Block`-Regel, wird das
  Ergebnis zurückgehalten (`GuardBlocked`), gleich welche `Mask`-Regel ihn ebenfalls trifft.
- **I7 — Was nicht hinausgeht, blockiert nicht.** Ein Feld, das Stufe 1 verwirft, wird nicht
  geprüft.
- **I8 — Deterministisch.** Gleiches Original, gleiche Regel, gleicher Guard-Regelsatz: bytegleiche
  Ausgabe. Keine Uhrzeit, keine Kultur, kein Zufall in der Ausgabe.
- **I9 — Formung fällt offen aus.** Wirft Stufe 1, 2a, 2c oder 2d, geht das Original weiter — durch
  2b, 3 und 4 wie jede Antwort. Der Bericht nennt die Stufe; der Fehler wird je Regel-Hash einmal
  protokolliert, ohne Inhalt.
- **I10 — Schutz fällt geschlossen aus.** Läuft in 2b ein Muster in die Zeitgrenze oder wirft die
  Stufe, wird das Ergebnis zurückgehalten — ungeprüft heißt blockiert (ADR-0011, E4).
- **I11 — Eine Projektion, die nichts trifft, gilt nicht.** Wählt `select` aus einer nicht leeren
  Nutzlast nichts aus, wird diese Nutzlast unprojiziert weitergegeben und der Fall gezählt. Eine
  Regel, die nach einer Formatänderung des Upstreams ins Leere greift, liefert so keine leeren
  Antworten.
- **I12 — Größengrenze.** Oberhalb von `FAOLAN_SHAPING_MAX_BYTES` (Vorgabe 16 MiB) entfallen die
  Stufen 1, 2a, 2c und 2d; der Bericht sagt es. 2b, 3 und 4 laufen unverändert.
- **I13 — Unverändertes wird nicht neu geschrieben.** Ein Wert, den keine Stufe anfasst, erscheint
  in der Ausgabe mit denselben Bytes wie im Original. Veränderte Werte werden kompakt geschrieben;
  Nicht-ASCII-Zeichen bleiben Zeichen und werden nicht zu `\uXXXX`-Folgen.
- **I14 — Der Agent erfährt es.** Jede verlustbehaftete Veränderung ist in der Antwort
  gekennzeichnet (Abschnitt 8), sofern die Regel das nicht ausdrücklich abschaltet.

## 4. Regelschema `faolan.projection/v1`

Eine Regel je Upstream-Server und Werkzeug. JSON, kein YAML.

```json
{
  "schema": "faolan.projection/v1",
  "server": "github",
  "tool": "search_issues",
  "mode": "enforce",
  "select": ["total_count", "items[].number", "items[].title", "items[].state", "items[].user.login"],
  "shape": { "items": { "format": "table", "maxItems": 50 } },
  "text": {
    "strip": ["base64", "tracking-params", "ansi"],
    "fold": { "stacktrace": { "head": 5, "tail": 3 }, "repeatedLines": true },
    "budget": { "default": 2000, "fields": { "items[].body": 400 }, "total": 12000 }
  },
  "nonText": "keep",
  "notice": "summary"
}
```

### 4.1 Felder

| Feld | Pflicht | Werte | Vorgabe |
|---|---|---|---|
| `schema` | ja | `faolan.projection/v1` | — |
| `server` | ja | Slug des Upstreams | — |
| `tool` | ja | Name des Werkzeugs beim Upstream, oder `*` für die Server-Vorgabe | — |
| `mode` | ja | `off` · `observe` · `enforce` | — |
| `select` | nein | 1 – 256 Pfade (4.2). Fehlt es, entfällt Stufe 1 | — |
| `shape` | nein | Arraypfad → `{ format, maxItems }` (4.3) | — |
| `text` | nein | `strip`, `fold`, `budget` (Abschnitt 5) | nichts davon |
| `nonText` | nein | `keep` · `drop` · `placeholder` | `keep` |
| `notice` | nein | `summary` · `none` | `summary` |

Eine Regel mit `tool: "*"` darf `select` und `shape` **nicht** tragen — Pfade gelten für ein
Antwortformat, nicht für einen Server.

**Unbekannte Schlüssel sind ein Fehler**, kein stilles Übergehen: Ein Tippfehler in `selct` wäre
sonst eine Regel ohne Projektion, die niemand bemerkt. Ein leeres `select` ist ebenfalls ein Fehler.

**`mode`:** `off` — abgelegt, wirkungslos. `observe` — die Stufen werden gerechnet, der Bericht
nennt die Größe, die ausgeliefert **würde**; ausgeliefert wird das Original. `enforce` — das
Ergebnis wird ausgeliefert. Neue Regeln legt die Oberfläche in `observe` an.

### 4.2 Pfadsprache

Bewusst kein JSONPath: keine Bibliothek, keine Filterausdrücke, in einem Durchlauf entscheidbar.

```
pfad     = "[]" *( "." schritt )  /  schritt *( "." schritt )
schritt  = ( name / "*" ) *"[]"
name     = 1*( ALPHA / DIGIT / "_" / "-" / "$" / "@" )  /  zitat
zitat    = DQUOTE 1*zeichen DQUOTE          ; für Schlüssel mit Punkt, Leerzeichen, Klammer;
                                            ; \" und \\ als Fluchtfolgen
```

- Ein Pfad gilt **relativ zur Wurzel der Nutzlast**, nicht zur Hülle. Dieselbe Regel wird auf
  `structuredContent` und auf jeden JSON-`text`-Block einzeln angewandt.
- Ein Pfad wählt den Wert an seinem Ende **mit allem darunter**.
- `[]` steigt in jedes Element eines Arrays. Auf einem Wert, der kein Array ist, trifft es nichts;
  ein Namensschritt auf einem Array ohne `[]` trifft ebenfalls nichts. Der Pfad `[]` am Anfang
  steht für eine Nutzlast, deren Wurzel ein Array ist.
- `*` trifft jeden Schlüssel eines Objekts auf dieser Ebene.
- Namen werden ordinal und mit Groß-/Kleinschreibung verglichen, auf dem entschlüsselten Schlüssel.
- Die Vorfahren eines gewählten Werts bleiben als Behälter erhalten. Ein Objekt, in dem nichts
  gewählt wurde, entfällt — außer als Array-Element: Dort bleibt es als `{}` stehen, damit Anzahl
  und Reihenfolge der Elemente stimmen.
- Die Reihenfolge der Ausgabe ist die des Originals, nicht die der Regel.
- Grenzen: höchstens 256 Pfade, 16 Schritte je Pfad, 512 Zeichen je Pfad, 64 KiB je Regel.

### 4.3 `shape`

Schlüssel ist ein Arraypfad **ohne** abschließendes `[]`.

- **`maxItems`** (≥ 1): Die ersten *n* Elemente bleiben, die übrigen entfallen und werden gezählt.
  Das Array behält seine Form; die Auslassung steht in der Notiz und im Bericht.
- **`format: "table"`**: Ein Array von Objekten wird spaltenweise ausgegeben —

  ```json
  { "cols": ["number", "title", "state", "user.login"], "rows": [[1, "…", "open", "octocat"]] }
  ```

  Die Spalten sind die `select`-Pfade unterhalb des Arrays, relativ dazu, **in der Reihenfolge der
  Regel** — also je Regel konstant, unabhängig von den Daten. Eine Zelle enthält den Wert am Pfad,
  wie er ist; fehlt er, steht `null`. Spaltenpfade dürfen weder `*` noch `[]` enthalten; die Regel
  muss mindestens einen `select`-Pfad unter dem Array haben. Verstöße sind Fehler beim Speichern.
- **`format: "objects"`** (Vorgabe): unverändert als Array von Objekten.

Es bleibt in jedem Fall gültiges JSON und funktioniert damit auch in `structuredContent`.

### 4.4 Vorrang

1. die Regel für genau dieses Werkzeug,
2. die Server-Vorgabe (`tool: "*"`),
3. sonst nichts — es gibt keine eingebauten Vorgaben für `strip`, `fold` oder `budget`.

Die Abschnitte `text`, `nonText` und `notice` einer Werkzeug-Regel **ersetzen** die der
Server-Vorgabe als Ganzes; es wird nicht in die Tiefe gemischt. Fehlt der Abschnitt in der
Werkzeug-Regel, gilt der des Servers. Der `mode` der Werkzeug-Regel gilt für alles, was sie
anwendet, auch für das Geerbte.

### 4.5 Prüfung beim Speichern, kanonische Form, Hash

Eine Regel wird beim Speichern vollständig geprüft (Schema, Pfade, Grenzen, bekannte Namen in
`strip` und `fold`). Was hier durchgeht, kann zur Laufzeit nicht an seiner Form scheitern.

Der **Regel-Hash** ist SHA-256 über die kanonische Form: Schlüssel ordinal sortiert, keine
Leerzeichen, Zahlen ohne Nachkommaanteil. Er steht im Audit und im Bericht und ändert sich mit
jeder inhaltlichen Änderung, nicht mit der Formatierung.

## 5. Cutter

Die Reihenfolge ist fest: entfernen → prüfen → falten → Budget. Geprüft wird nach dem Entfernen,
weil ein Base64-Block von zwei Megabyte kein Muster mehr durchlaufen muss; gefaltet und gekürzt
wird nach dem Prüfen (I5).

Die Stufen wirken auf jeden Textwert, der nach Stufe 1 übrig ist. Ein JSON-`text`-Block wird dafür
gelesen und kompakt neu geschrieben — auch ohne `select`.

### 5.1 Entfernen (`strip`)

| Name | Wirkung | Marke |
|---|---|---|
| `base64` | zusammenhängender Lauf von ≥ 512 Zeichen aus `A–Z a–z 0–9 + / - _ =` | `[base64: N Zeichen entfernt]` |
| `data-uri` | `data:<typ>;base64,…` jeder Länge | `[data:<typ>: N Zeichen entfernt]` |
| `tracking-params` | in `http(s)`-URLs die Parameter `utm_source`, `utm_medium`, `utm_campaign`, `utm_term`, `utm_content`, `utm_id`, `gclid`, `dclid`, `fbclid`, `msclkid`, `mc_cid`, `mc_eid`, `igshid`, `yclid`, `_ga`, `_gl` | keine; gezählt |
| `ansi` | ANSI-Steuersequenzen (CSI, OSC) | keine; gezählt |

Zeilenumbrochenes Base64 (76 Zeichen je Zeile) erkennt diese Fassung nicht.

### 5.2 Falten (`fold`)

Nur auf Textwerten mit Zeilenumbruch.

- **`stacktrace: { head, tail }`** — erkannt werden Frame-Zeilen von .NET (`   at X.Y(…)`),
  Java (`\tat pkg.Klasse.methode(Datei.java:12)`), Python (`  File "…", line 12, in f`) und
  JavaScript (`    at f (datei:12:3)`). Ein Lauf von mehr als `head + tail + 1` Frames wird auf die
  ersten `head` und die letzten `tail` gekürzt; dazwischen steht eine Zeile
  `… [N Frames gefaltet]`. Die Zeile mit Ausnahmetyp und Meldung ist kein Frame und bleibt immer.
- **`repeatedLines: true`** — drei oder mehr aufeinanderfolgende Zeilen, die nach Abzug eines
  führenden Zeitstempels (ISO 8601 oder `HH:MM:SS[.fff]`) gleich sind, werden zur ersten Zeile und
  einer Zeile `… [N× wiederholt]`.

### 5.3 Budget (`budget`)

- **`default`** — Obergrenze je Textwert in Zeichen.
- **`fields`** — Pfad → Obergrenze für die Textwerte an diesem Pfad; geht `default` vor. `0` heißt
  unbegrenzt. Pfade beziehen sich auf die Nutzlast **vor** der Tabellenform.
- **`total`** — Obergrenze für die ganze Hülle; nutzt den Algorithmus aus Abschnitt 7. Es gilt die
  engere von `total` und der globalen Grenze.

Ein Textwert über seiner Grenze wird an einer Codepunktgrenze abgeschnitten und bekommt die Marke
`…[gekürzt: ALT → NEU Zeichen]`. Die Marke zählt nicht gegen die Grenze des Felds. Zahlen in Marken
stehen ohne Tausendertrennung.

### 5.4 Nicht-Text-Blöcke (`nonText`)

`keep` lässt Bild-, Audio- und Ressourcenblöcke unangetastet. `drop` entfernt sie und zählt.
`placeholder` ersetzt jeden durch einen Textblock `[<typ>: <mimeType>, N Bytes ausgelassen]`.

## 6. Maskieren (ADR-0028)

### 6.1 Der Modus

```csharp
public enum GuardMode { Observe = 0, Block = 1, Mask = 2 }   // Wert am Ende: liegt als Zahl in der DB
```

- `Mask` wirkt **nur eingehend**. Eine Regel mit `Direction = Outbound` und `Mode = Mask` wird beim
  Speichern abgelehnt. Bei `Direction = Both` wird eingehend maskiert und ausgehend blockiert; die
  Oberfläche sagt das an der Regel.
- Ersetzt wird der Treffer durch `[maskiert:<regel-id>]`. Die Marke darf selbst von keiner
  eingebauten Regel getroffen werden.
- Treffen mehrere `Mask`-Regeln überlappend, gilt der am weitesten links beginnende Treffer, bei
  Gleichstand der längere, dann die ordinal kleinere Regel-Id.
- Maskiert werden nur Textwerte — keine Schlüsselnamen, keine Zahlen.

### 6.2 Zwei Prüfstellen

Der Plan 0004 hatte den Guard nur am Ende der Pipeline. Das genügt nicht: Ein Budget könnte ein
Token halbieren, bevor eine Regel es sieht. Deshalb:

- **2b, je Textwert, vor dem Kürzen:** alle eingehenden Regeln. `Mask` ersetzt, `Block` hält das
  ganze Ergebnis zurück, `Observe` zählt.
- **4, über der fertigen Hülle:** `IContentGuard.Inspect` wie heute, samt Prüfgrenze
  (`MaxScanChars`). Diese Stufe sieht auch Schlüsselnamen und Treffer über Wertgrenzen hinweg; sie
  kann blockieren und beobachten, nicht maskieren.

Ein beobachteter Treffer, den beide Stellen sehen, wird über Regel-Id und Fingerabdruck
zusammengeführt und einmal protokolliert.

Der Normalfall darf nicht teurer werden als heute: 2b beginnt mit demselben Keyword-Vorfilter wie
der `SecretGuard`. Trifft kein Keyword und gibt es keine Regel ohne Keyword, wird kein Textwert
einzeln angefasst.

### 6.3 Schnittstelle

`SecretGuard` implementiert sie mit; es bleibt **ein** kompilierter Regelsatz, ein Tausch bei
`Changed`.

```csharp
public enum ValueVerdict { Clean = 0, Masked = 1, Blocked = 2 }

public interface IContentMasker
{
    /// Schneller Ausschluss über die ganze Nutzlast (Keyword-Vorfilter).
    bool MayMatchInbound(ReadOnlySpan<char> payload);

    /// Prüft einen Textwert gegen alle eingehenden Regeln. Bei Masked steht die ersetzte
    /// Fassung in <paramref name="masked"/>. Wirft nicht; eine Zeitüberschreitung ist Blocked.
    ValueVerdict InspectValue(
        ReadOnlySpan<char> value,
        IBufferWriter<char> masked,
        ICollection<GuardFinding> findings);
}
```

`GuardFinding` bleibt, wie es ist: Regel-Id, Modus, Fingerabdruck, Offset und Länge **im Textwert**
— nie der Fund.

### 6.4 Neue eingebaute Regeln

| Id | trifft | Modus ab Werk |
|---|---|---|
| `http-bearer-header` | `Authorization: Bearer <token>` in Text | `Mask` |
| `connection-string-password` | `Password=` / `Pwd=` in Verbindungszeichenketten | `Mask` |
| `url-userinfo` | Zugangsdaten in URLs (`schema://nutzer:geheim@host`) | `Mask` |

Die Muster entstehen in P4 mit Positiv-, Negativ- und Grenzfällen und müssen unter
`NonBacktracking` laufen (ADR-0011, E2). Die 15 vorhandenen Regeln behalten ihren Modus.

### 6.5 Nachsäen auf bestehenden Instanzen

`GuardRuleStore.LoadAsync` sät nur in eine leere Tabelle; eine Instanz aus 0.12.0 bekäme die drei
Regeln nie. Sie kommen deshalb **einmalig mit der Migration**: eingefügt wird nur, wenn die Tabelle
schon Zeilen hat (eine leere füllt der Start wie bisher mit allen 18) und die Id noch fehlt — und
dann im Modus **`Observe`**. Ein Upgrade soll nicht ungefragt verändern, was Agenten bekommen; die
Oberfläche zeigt die drei als neu und bietet das Umschalten an. Der Rückweg der Migration entfernt
sie wieder. Ein Test hält die Zeilen der Migration und `BuiltInGuardRules` gleich.

## 7. Globale Obergrenze, hüllenerhaltend (Stufe 3)

Gemessen wird die serialisierte Hülle in Zeichen. Liegt sie unter der Grenze, geschieht nichts.
Sonst wird der Überhang in fester Reihenfolge abgebaut, bis die Grenze hält:

1. **Nicht-Text-Blöcke**, größter zuerst, werden durch den Platzhalter aus 5.4 ersetzt. Sie werden
   nie angeschnitten.
2. **Arrays:** In der größten Nutzlast entfallen vom Ende des größten Arrays her Elemente. „Größtes"
   heißt: meiste Bytes; bei Gleichstand das erste im Dokument. Das Array behält seine Form.
3. **Textwerte:** Der längste Textwert wird vom Ende her gekürzt, mit der Marke aus 5.3.
4. **Letzter Ausweg:** Reicht das nicht, wird der größte `text`-Block als Text behandelt und am
   Ende abgeschnitten. Sein Inhalt ist dann kein gültiges JSON mehr — die Hülle bleibt es.

Jede Auslassung wird gezählt und steht in der Notiz. `isError` bleibt in jedem Fall erhalten.
Schritt 2 – 4 sind durch goldene Dateien festgehalten (Abschnitt 14); sie sind Teil des Vertrags.

## 8. Kennzeichnung

Eine Formung, die der Agent nicht sieht, ist gefährlicher als keine: Er hält ein fehlendes Feld für
nicht vorhanden.

- **Marken an der Stelle** — in jedem gekürzten, gefalteten, maskierten oder entfernten Textwert
  (Abschnitte 5 und 6). Nicht abschaltbar.
- **Notiz** — bei `notice: "summary"` (Vorgabe) bekommt `content` einen letzten `text`-Block, eine
  Zeile:

  `[faolan: Antwort geformt — 14 Felder ausgelassen, 150 von 200 Einträgen in items, 3 Werte gekürzt, 1 maskiert]`

  Er erscheint nur, wenn Felder oder Elemente entfallen sind oder die Obergrenze gegriffen hat —
  also dort, wo an der Stelle selbst nichts zu sehen ist. Im Modus `observe` erscheint er nie.
  `notice: "none"` schaltet ihn je Regel ab; die Marken bleiben.
- **`_meta`** — unter dem Schlüssel `faolan/shaping` steht der Bericht maschinenlesbar:

  ```json
  { "originalBytes": 124079, "deliveredBytes": 9791, "mode": "enforce", "rule": "9f2c41d07a3e5b18",
    "counts": { "fieldsDropped": 14, "itemsDropped": 150, "valuesTruncated": 3 } }
  ```

  Nur gesetzt, wenn die Pipeline etwas verändert oder im Probelauf gerechnet hat. `rule` sind die
  ersten 16 Hex-Zeichen des Regel-Hashs. Der Schlüsselname wird in P5 gegen die geltende
  Spec-Revision geprüft.
- **REST** — `/tools/{name}/invoke` und `/capabilities/{id}/invoke` bekommen ein Feld `shaping` mit
  demselben Inhalt. Das vorhandene Feld `truncation { originalChars, truncatedChars }` bleibt und
  ist gesetzt, wenn Stufe 3 gegriffen hat.

## 9. Schnittstellen

In `Faolan.Abstractions`:

```csharp
public enum ProjectionMode { Off = 0, Observe = 1, Enforce = 2 }

public sealed record ProjectionRule(
    ServerId Server,
    string Tool,                 // Werkzeugname beim Upstream oder "*"
    ProjectionMode Mode,
    string CanonicalJson,        // die Regel in kanonischer Form (4.5)
    string Hash,                 // SHA-256 darüber, hex, klein
    DateTimeOffset UpdatedAt);

public interface IProjectionRuleStore            // Muster: IGuardRuleStore
{
    IReadOnlyList<ProjectionRule> All { get; }
    ProjectionRule? Find(ServerId server, string tool);     // genau dieses Werkzeug, sonst "*"
    Task UpsertAsync(ProjectionRule rule, CancellationToken ct);
    Task RemoveAsync(ServerId server, string tool, CancellationToken ct);
    event EventHandler? Changed;
}

public readonly record struct ShapingContext(
    ServerId Server,
    string Tool,
    bool CaptureForAudit);       // Debug-Mitschnitt an: das maskierte Original wird mitgegeben

public interface IResponseShaper                 // synchron, ohne E/A, im Hot Path; wirft nicht
{
    ShapedResponse Shape(in ShapingContext context, JsonElement upstreamResult);
}

public sealed record ShapedResponse(
    JsonElement? Delivered,              // null genau dann, wenn Blocked
    string? BlockedMessage,              // Text wie GuardMessages.Inbound
    ShapingReport Report,
    JsonElement? MaskedOriginal);        // nur bei CaptureForAudit

public sealed record ShapingReport(
    int OriginalBytes,
    int DeliveredBytes,
    int? ObservedBytes,                  // nur im Modus Observe: was ausgeliefert würde
    string? OriginalSha256,              // siehe 9.1
    string? RuleHash,
    ProjectionMode Mode,
    ShapingCounts Counts,
    string? Skipped);                    // null · "too-large" · "failed:<stufe>" · "empty-projection"

public sealed record ShapingCounts(
    int FieldsDropped, int ItemsDropped, int BlocksDropped,
    int SpansStripped, int ValuesMasked, int BlocksFolded, int ValuesTruncated,
    bool CapApplied);

public interface IResponseObserver               // Abgriff des Profilers; blockiert nie, wie IAuditSink
{
    bool WantsSample(ServerId server, string tool);
    void Observe(in ObservedResponse sample);
}
```

`IResponseObserver` und `ObservedResponse` sind im [Profiler-Vertrag](antwort-profiler.md)
Abschnitt 3 festgelegt. Der Shaper fragt `WantsSample` und gibt, wenn ja, das **maskierte**
Original weiter (I4) — nie bei `isError: true` und nie bei einem zurückgehaltenen Ergebnis.

`ToolInvoker` bekommt `IResponseShaper?` als weiteren optionalen Konstruktorparameter und ruft ihn
dort, wo heute `ResultCompressor.Compress` steht. Fehlt er, verhält sich der Invoker wie ohne
Regeln. `ToolInvocationResult` trägt statt `ResultTruncation? Truncation` künftig
`ShapingReport? Shaping`; die Fassaden reichen ihn nach Abschnitt 8 weiter. `ResultCompressor` und
`ResultTruncation` entfallen.

In `Faolan.Core/Projection` — Namen bindend, Inneres frei: `ProjectionPath`, `CompiledProjection`
(einmal je Regeländerung gebaut, nie im Aufruf), `JsonProjector`, `TableWriter`, `TextCutter`,
`EnvelopeCap`, `ResponseShaper`. Abhängigkeiten: nur `Faolan.Abstractions` und die
Laufzeitbibliothek.

### 9.1 Der Fingerabdruck des Originals

`OriginalSha256` ist SHA-256 über die UTF-8-Bytes des Originals. Er wird gerechnet, wenn die
ausgelieferte Hülle vom Original abweicht oder im Probelauf abweichen würde — sonst ist das
Ausgelieferte das Original und der Wert bleibt leer. `FAOLAN_AUDIT_RESPONSE_HASH` stellt das um:
`changed` (Vorgabe) · `always` · `off`.

Zwei Grenzen, die dazugehören: Der Hash **beweist Gleichheit, er holt nichts zurück** — ohne eine
Ablage der Originale (Plan 0004, P7) ist er eine Referenz ohne Ziel. Und er wird über das
**unmaskierte** Original gebildet; wer das Audit lesen kann und den Rest einer kurzen Antwort
kennt, könnte ein schwaches Secret darin durch Probieren bestätigen. Dasselbe Restrisiko trägt der
Fingerabdruck in ADR-0011 schon heute.

## 10. Ablage, Verwaltung, Export

- **Tabelle `ProjectionRules`:** `ServerId`, `Tool`, `Mode`, `RuleJson`, `Hash`, `UpdatedAt`;
  Schlüssel (`ServerId`, `Tool`). Migration für SQLite und PostgreSQL, je mit Rückweg. Wird ein
  Upstream entfernt, gehen seine Regeln mit.
- **Tausch zur Laufzeit:** erst Datenbank, dann Zwischenspeicher, dann `Changed`; der Shaper baut
  die `CompiledProjection` neu und tauscht sie atomar — das Muster von `GuardRuleStore`.
- **REST**, nur Admin: `GET /api/v1/projection/rules`, `GET|PUT|DELETE
  /api/v1/projection/rules/{server}/{tool}`, `POST /api/v1/projection/test` (Regel und
  Beispielantwort hinein, geformte Antwort und Bericht heraus; legt nichts ab). Jede Änderung
  erzeugt einen Verwaltungs-Eintrag im Audit mit Server, Werkzeug und Regel-Hash.
- **CLI:** `faolan projection list | show <server> <tool> | set --file <datei> | remove <server>
  <tool> | test --rule <datei> --sample <datei>`. `test` ruft den Endpunkt oben und zeigt vorher
  und nachher.
- **Export:** Der Konfigurationsexport bekommt `ProjectionRules` und **`FormatVersion` 2**. Der
  Import liest 1 und 2; ein Gateway vor dieser Änderung lehnt 2 ab — gewollt, weil er `Mask` als
  `Observe` läse (ADR-0028). Policy Packs tragen Projektionsregeln über denselben Weg. Es gibt
  keinen zweiten Importpfad.

## 11. Audit

### 11.1 Felder

| Feld | Bedeutung |
|---|---|
| `ResponseBytes` | **ausgelieferte** Hülle in Bytes. Bisher: Zeichen der gekürzten Fassung (`ToolInvoker.cs:478`). Für Zeilen der Kettenfassung 1 bleibt die alte Bedeutung dokumentiert |
| `OriginalResponseBytes` | Original in Bytes — bei jedem erfolgreichen Aufruf |
| `OriginalResponseSha256` | nach 9.1 |
| `ObservedResponseBytes` | nur im Probelauf |
| `ProjectionRuleHash` | Hash der angewandten Regel, sonst leer |
| `ShapingMode` | `Off` / `Observe` / `Enforce`, leer ohne Regel |
| `ShapingCounts` | die Zähler als kanonisches JSON, leer ohne Eingriff |

Damit ist je Aufruf belegt, was ankam, welche Regel es verändert hat und was hinausging — ohne die
Nutzlast abzulegen (NFR-04).

Der **Debug-Mitschnitt** (`FAOLAN_AUDIT_DEBUG_PAYLOADS`) legt künftig das **Original nach
Maskierung** ab, zusätzlich durch die Schlüsselnamen-Redaction wie bisher — nicht mehr die gekürzte
Fassung. Wer Regeln entwickelt, braucht genau das.

### 11.2 Kettenfassung 2

- Neue Spalte `ChainVersion` an `AuditEvents`; leer heißt 1.
- `audit-chain-v2` hängt die sechs neuen Felder in der Reihenfolge der Tabelle oben an die
  Feldfolge der Fassung 1 an. Die Feldfolge der Fassung 1 wird nicht angefasst.
- Nach der Migration schreibt der Gateway alle Ereignisse in Fassung 2, auch solche ohne Antwort.
- `AuditChainVerifier` rechnet jede Zeile mit ihrer Fassung. Ein Kettenabschnitt darf von 1 auf 2
  wechseln; bestehende Abschnitte bleiben gültig.
- Der Export nennt `chainVersion` je Zeile, wie heute — künftig aus der Zeile statt aus der
  Konstanten (`AuditExportWriter.cs:100`).
- Ein Gateway vor dieser Änderung kann Zeilen der Fassung 2 nicht prüfen. Das steht in der
  Upgrade-Anleitung.

## 12. Schalter

| Variable | Vorgabe | Wirkung |
|---|---|---|
| `FAOLAN_SHAPING` | `on` | `on` · `observe` (alle Regeln rechnen, keine wirkt) · `off`. Der Not-Aus für die Stufen 1, 2a, 2c, 2d. Maskierung, Obergrenze und Guard hängen nicht daran |
| `FAOLAN_SHAPING_MAX_BYTES` | 16 MiB | I12 |
| `FAOLAN_MAX_RESULT_CHARS` | 0 (aus) | Stufe 3; vorhanden, jetzt in Codepunkten und hüllenerhaltend |
| `FAOLAN_AUDIT_RESPONSE_HASH` | `changed` | 9.1 |

Im Alltag wird je Regel geschaltet, zur Laufzeit und ohne Neustart.

**Metriken** am Meter `Faolan.Gateway`, je Server und Werkzeug, nie mit Inhalt:
`faolan.shaping.original_bytes`, `faolan.shaping.delivered_bytes` (Zähler),
`faolan.shaping.duration` (Histogramm), `faolan.shaping.failures` (Zähler, mit der Stufe als
Attribut). Das Token-Cockpit rechnet die Ersparnis aus den beiden Audit-Größen.

## 13. Leistung

**Gemessene Ausgangswerte** — Scratch-Projekt, Release, Entwicklerrechner, **keine**
Referenzhardware; sie belegen die Machbarkeit, nicht das Ziel:

| Messung | Ergebnis |
|---|---|
| `JsonMarshal.GetRawUtf8Value` auf 100 055 Bytes | 0 Bytes Allokation |
| `JsonElement.GetRawText()` auf derselben Nutzlast | 200 136 Bytes Allokation |
| Projektion `Utf8JsonReader` → `Utf8JsonWriter` mit `Skip()`, 200 Elemente, drei von acht Feldern | 124 079 → 9 791 Bytes, 136 Bytes Allokation, rund 300 µs |
| `[GeneratedRegex]` mit `RegexOptions.NonBacktracking` | liefert ein gewöhnliches `Regex`; der Quellgenerator erzeugt dafür keinen Code |
| SDK 2.0.0 | `CallToolResult` hat `Content`, `StructuredContent`, `IsError`, `Meta`; `Tool` hat `OutputSchema` |

Aus der vierten Zeile folgt: Der Quellgenerator bringt nur für Muster etwas, die **mit**
Rückverfolgung laufen dürfen. Das sind die festen Muster des Cutters (Frame-Zeilen, Zeitstempel,
ANSI, URL-Parameter) — sie sind im Quelltext, rückverfolgungsarm geschrieben und haben eine
Zeitgrenze. Guard-Regeln bleiben bei `NonBacktracking` zur Laufzeit, wie in ADR-0011 entschieden.

**Ziele** — gemessen, nicht versprochen; auf Referenzhardware nachzuweisen (P11):

| Fall | Ziel |
|---|---|
| Werkzeug ohne Regel, kein Keyword-Treffer | nicht mehr Allokation und nicht mehr Zeit als heute |
| 100 KB JSON, Projektion auf rund 5 % | unter 1 ms; Allokation ≤ 1,5 × Ausgabegröße |
| Cutter auf 50 KB Logtext | unter 1 ms |
| Regeländerung | Neubau außerhalb jedes Aufrufs |

Die Nutzlast wird im Shaper **nie** als Zeichenkette kopiert (`GetRawText()` ist dort verboten;
ein Test hält das über die Allokation fest).

## 14. Nachweise

Ein Paket gilt als fertig, wenn ein Lauf über **alle** Testprojekte der Solution vorliegt, geprüft
wie in der CI, mit Befehl und Zahlen.

| Was | Test |
|---|---|
| I1 bytegleich ohne Eingriff | `Faolan.Core.Tests` — Hüllen-Fixtures hinein, Bytevergleich |
| I2 gültige Hülle nach jeder Stufe, `isError` erhalten | Core — jede Ausgabe wird als `CallToolResult` zurückgelesen |
| I3 Fehlerantworten nicht projiziert | Core |
| I4 kein unmaskierter Wert in Audit, Profiler, Log | `Faolan.Security.Tests` — Köder-Secret durch alle Senken |
| I5 / I6 Token an der Budgetgrenze; Block gegen Mask auf demselben Wert | Security |
| I7 verworfenes Feld blockiert nicht | Security |
| I8 Determinismus | Core — jede Fixture zweimal, Bytevergleich; Lauf unter `de-DE` und `en-US` |
| I9 / I10 Fehler in der Formung öffnet, in der Prüfung schließt | Core, mit fehlerwerfenden Stufen |
| I11 Leerprojektion | Core |
| I12 Größengrenze | Core |
| I13 unveränderte Werte bytegleich | Core — Fluchtfolgen, Nicht-ASCII, große Zahlen |
| I14 Marken, Notiz, `_meta`, REST-Feld; keine Notiz im Probelauf und bei `notice: "none"` | Core, Integration |
| Pfadsprache: Grammatik, Grenzen, jede Regel aus 4.2 | Core — Tabellentest, dazu ungültige Pfade |
| Tabellenform, `maxItems` | Core — goldene Dateien |
| Jede `strip`- und `fold`-Art: Positiv-, Negativ-, Grenzfall | Core — goldene Dateien |
| Stufe 3, alle vier Schritte | Core — goldene Dateien |
| Vorrang Werkzeug / Server | Core |
| Unbekannter Schlüssel, leeres `select`, `select` an `*` werden abgelehnt | Core |
| Regel-Hash ändert sich mit dem Inhalt, nicht mit der Formatierung | Core |
| `Mask` ausgehend abgelehnt; `Both` maskiert ein und blockiert aus | Core |
| Maske wird von keiner eingebauten Regel getroffen | Core |
| Neue Regeln: je Muster Positiv-, Negativ-, Grenzfall | Core |
| Nachsäen: leere Tabelle 18 Regeln, Bestand + 3 in `Observe`, gelöschte bleiben gelöscht | `Faolan.Upgrade.Tests` |
| Migration und `BuiltInGuardRules` stimmen überein | Governance |
| Alle fünf Fassaden liefern dieselbe geformte Antwort und den Bericht | `Faolan.Integration.Tests` |
| `tools/list` reicht weiterhin kein `outputSchema` durch | Integration |
| Regeltausch zur Laufzeit wirkt beim nächsten Aufruf | Integration |
| Kette mit Zeilen beider Fassungen prüft; Manipulation an einem neuen Feld fällt auf | Core, Upgrade |
| Migrationen beider Anbieter mit Rückweg | Governance (vorhanden) |
| Export Fassung 2 hin und zurück; Import von Fassung 1 | Core |
| Jede neue Variable ist dokumentiert | Governance (vorhanden) |
| Allokation: ohne Regel, 100 KB Projektion, 50 KB Cutter | Core — `GC.GetAllocatedBytesForCurrentThread()` |
| Zeit | `Faolan.Performance.Tests` hinter `FAOLAN_RUN_BENCHMARK`; Ergebnis nach `docs/acceptance/performance.md` |

Fixtures sind **aufgezeichnete Antwortformen** echter Upstream-Arten (Trefferliste, Dateiinhalt,
Log, Stacktrace, Bild, Fehler), von Hand von Werten befreit. Keine echten Antworten im Repository.

## 15. Vom Nutzer bestätigt

Diese Punkte waren als Vorschlag festgeschrieben und sind mit der Abnahme am 2026-11-16 bestätigt.
Jede Änderung ändert die genannten Abschnitte und braucht eine neue Abnahme.

| Nr. | Frage | festgeschrieben | Abschnitt |
|---|---|---|---|
| 1 | Originale im Audit | **nur Größe und Hash**; keine Ablage der Originale (P7 bleibt offen) | 9.1, 11 |
| 2 | Regelformat | **nur JSON**; kein YAML, keine neue Abhängigkeit | 4, 10 |
| 3 | Zeitmessung | **bestehende Vorrichtung plus Allokationstests**; kein BenchmarkDotNet | 13, 14 |
| 4 | Metriken | **harte Umbenennung** auf `faolan.*`, keine Doppelausgabe; neue Zähler `faolan.shaping.*` | Umbenennungsvertrag 2.5 |
| 5 | Notiz an den Agenten | **Sammelnotiz als Vorgabe**, je Regel abschaltbar | 8 |
| 6 | Neue Guard-Regeln auf Bestandsinstanzen | kommen in **`Observe`** an, auf neuen Instanzen in `Mask` | 6.5 |
| 7 | Hash über das unmaskierte Original | **ja**, mit dem benannten Restrisiko | 9.1 |

## 16. Nicht Teil dieses Vertrags

- Die Ablage der Originale und ein Werkzeug zum Nachholen weggefilterter Felder (P7).
- `resources/read` und `prompts/get` — sie laufen nicht durch den Invoker.
- Das Durchreichen von `outputSchema`.
- Eine Textform für Tabellen.
- Die Oberfläche im Einzelnen (P10); sie wird gesichtet, nicht vertraglich festgelegt.
- Der Profiler — [eigener Vertrag](antwort-profiler.md).
