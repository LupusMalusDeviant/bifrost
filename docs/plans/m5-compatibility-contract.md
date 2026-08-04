# M5 — Eingefrorener Vertrag für Kompatibilität und Betriebsnachweise

**Stand:** 2026-08-02 · **Eingefroren durch:** Lead/Integrator · **Gilt für:** WP5.1 – WP5.6
**Grundlage:** Pflichtenheft 0004 Kapitel 11, Lastenheft 0002 Gruppen F und G,
[docs/acceptance/performance.md](../acceptance/performance.md),
[ADR-0023](../adr/0023-stateless-kern-und-mrtr.md)

M5 ist der erste Meilenstein, der fast keinen Produktcode baut, sondern **Nachweise**. Deshalb regelt
dieser Vertrag nicht in erster Linie Schnittstellen, sondern Artefakte: wie ein Ergebnis heißt, in
welchem Format es liegt, wer es schreiben darf und **wann es als Beleg zählt**. Wer abweichen will,
meldet das an den Lead, statt den Vertrag zu erweitern.

Genau ein Paket fasst planmäßig Produktionscode an: **WP5.6**. Alle anderen Pakete melden Funde als
Finding an den Lead und bekommen dafür ein eigenes Fix-WP. Ein Testpaket, das nebenbei repariert,
verändert das Verhalten, das es gerade belegen soll.

## 1. Die Beweisregeln dieser Welle

Sie stehen vor allem anderen, weil M5 sonst grün meldet, ohne etwas zu belegen. Alle vier stammen
aus Fehlern, die dieses Repository schon gemacht hat.

1. **Ein Harness ohne Negativkontrolle ist kein Nachweis.** Jedes Harness weist an einem Fixture
   nach, dass es einen eingebauten Fehler (Leak, Regression, unerlaubtes Feature, Canary-Secret)
   auch **findet**. Ein Prüfstand, der nie rot wird, misst nichts.
2. **Eine Gate-Variable, die niemand setzt, ist ein übersprungener Test mit grüner Meldung.**
   `BIFROST_RUN_BENCHMARK` war wochenlang nirgends gesetzt, und damit waren NFR-01/02 faktisch
   ungeprüft ([performance.md](../acceptance/performance.md), Korrektur 2026-07-18). Deshalb:
   **Jede neue Gate-Variable wird im selben PR von genau einem existierenden CI-Job gesetzt.**
   Ohne diesen Job wird das Paket nicht abgenommen.
3. **Ein einzelner Lauf ist eine Anekdote.** Messwerte werden als Median aus mindestens drei Läufen
   nach Aufwärmphase berichtet, mit Spanne. Der erste Lauf ist der Kaltstart und wird als solcher
   ausgewiesen.
4. **Manuell heißt manuell.** Ein handgeführtes Clientprotokoll ist ein zulässiger Nachweis — aber
   es trägt Datum, Client-Version, OS und Prüfer und wird nie als Lauf ausgegeben.

## 2. Vertrag in `Bifrost.Abstractions` (nur WP5.6)

Neue Datei `src/Bifrost.Abstractions/Support.cs`, Namespace `Bifrost.Abstractions.Operations` —
dieselbe Nachbarschaft wie Backup, Restore und Diagnose aus M2.

```csharp
public interface ISupportBundleService
{
    Task<SupportBundlePreview> PreviewAsync(SupportBundleRequest request, CancellationToken ct);
    Task<SupportBundleResult> CreateAsync(SupportBundleRequest request, CancellationToken ct);
}

public sealed record SupportBundleRequest(
    IReadOnlySet<SupportBundleCategory> Categories,   // leer = Standardsatz
    int LogTailLines,
    string? OutputPath);

public sealed record SupportBundlePreview(
    IReadOnlyList<SupportBundleEntry> Entries,        // was hineinkäme, mit Größe und Kategorie
    IReadOnlyList<string> SkippedCategories,          // mit Begründung, nicht still weggelassen
    long EstimatedBytes);

public enum SupportBundleCategory
{
    Version, Configuration, Diagnostics, Health, Logs, Migrations, Runtime
}
```

**`PreviewAsync` vor `CreateAsync`** ist der vorgesehene Weg, aus demselben Grund wie `PlanAsync`
vor `ApplyAsync` in M2: Ein Bundle, dessen Inhalt man erst nach dem Schreiben sieht, ist bereits
geschrieben — und ein Support-Bundle verlässt danach das Haus.

### Nachtrag nach dem Audit (2026-08-02): „Pflicht" war zu viel gesagt

**Der Befund.** Die erste Fassung schrieb „Pflicht", der Dienst erzwingt aber nichts: Er hält
keinen Zustand zwischen Vorschau und Erzeugung, und CLI wie REST können unmittelbar erzeugen. Nur
die Oberfläche sperrt den Knopf ohne Vorschau. Vertrag und Code sagten also Verschiedenes.

**Die Entscheidung: der Code hat recht, der Text wird korrigiert.** Ein Handle wie bei
`RestorePlan` ist hier nicht die Antwort — es löst ein anderes Problem. Ein Restore **verändert die
Instanz**, und das Handle stellt sicher, dass genau der geprüfte Plan angewendet wird. Ein Bundle
schreibt eine neue Datei und verändert nichts; das Risiko liegt danach, beim Weitergeben. Dagegen
hilft kein Handle: Man kann eine Vorschau anfordern und ihr Ergebnis ignorieren.

Was stattdessen gilt und geprüft wird: **Jede Erzeugung sagt hinterher, was drin ist** — die
CLI-Ausgabe nennt die übersprungenen Kategorien und die Zahl der maskierten Felder, die Oberfläche
zeigt die Liste, und das Manifest liegt oben im Archiv. Die Oberfläche behält ihre strengere
Reihenfolge, weil sie sie ohne Kosten anbieten kann.

**Auditpayload und Credentialmaterial sind keine Kategorie**, auch nicht per Opt-in-Flag. Ein Flag,
das den Auditexport in dasselbe Archiv legt, macht aus dem weitergabefähigen Bundle über kurz oder
lang das, was jemand aus Bequemlichkeit mitschickt (FR-P075: redigiertes Bundle **klar getrennt**
vom vollständigen Auditexport). Wer Auditdaten braucht, nimmt den Auditexport.

Bundle-Erstellung ist **admin-only und auditiert**; die Adapter (CLI, API, UI) implementieren keine
zweite Fassung der Regeln, sondern rufen den Dienst (Pflichtenheft §3.1).

## 3. Bundle-Manifest v1 (`manifest.json`)

```jsonc
{
  "formatVersion": 1,                     // Format des Bundles, nicht des Produkts
  "productVersion": "0.16.0",
  "createdAt": "2026-08-02T12:00:00Z",
  "instanceId": "…",                      // aus config/instance.json
  "categories": ["version", "configuration", "diagnostics", "health", "logs", "migrations", "runtime"],
  "files": [
    { "path": "diagnostics/report.json", "category": "diagnostics", "bytes": 4211, "sha256": "…" }
  ],
  "redaction": { "pass": "central-v1", "redactedFields": 7 },  // wie oft, nie was
  "checksumAlgorithm": "sha-256"
}
```

Das Manifest liegt unverschlüsselt im Archiv, wie beim Backup: Sonst kann ein Empfänger nicht
prüfen, was er vor sich hat, bevor er es öffnet.

**Reason-Codes:** reserviert `BFR-SUP-0001…0099`. Die M2-Bereiche (`BFR-CFG/DB/KEY/NET/RT/UP`) und
`BFR-POL-*` aus M3 bleiben unberührt.

## 4. Ergebnisartefakte — Pfade und Erzeuger stehen fest

| Artefakt | Pfad | Erzeuger | Regel |
|---|---|---|---|
| Kompatibilitätsmatrix (Quelle der Wahrheit) | `tests/compatibility/matrix.json` | WP5.1 | maschinenlesbar, schemageprüft |
| Kompatibilitätsbericht | `docs/acceptance/compatibility.md` | WP5.1 | **generiert**, nie von Hand gepflegt |
| Benchmarkresultat | `artifacts/perf/<run-id>.json` | WP5.3 | trendfähig, CI-Artefakt |
| Performanceabnahme | `docs/acceptance/performance.md` | WP5.3 | fortschreiben, nicht ersetzen |
| Soak-Rohmetriken | `artifacts/soak/<run-id>/metrics.jsonl` | WP5.4 | fortlaufend geschrieben |
| Soak-Zusammenfassung | `artifacts/soak/<run-id>/summary.json` | WP5.4 | auch bei CI-Abbruch verwertbar |
| E2E-Trace/Screenshot/Video | CI-Artefakt je Lauf | WP5.5 | nur bei Fehlschlag |

`artifacts/` ist Laufwerk, kein Repositoryinhalt: Es wird in `.gitignore` geführt und nur als
CI-Artefakt veröffentlicht. Was im Repository landet, sind Matrix, Bericht und Abnahmedokumente.

## 5. Schema der Kompatibilitätsmatrix

```jsonc
{
  "schemaVersion": 1,
  "generatedAt": "2026-08-02T12:00:00Z",
  "entries": [
    {
      "client": "mcp-inspector",
      "clientVersion": "0.16.1",
      "os": "linux",                       // linux | windows | macos
      "transport": "http",                 // stdio | http
      "protocolVersion": "2026-07-28",
      "mode": "stateless",                 // stateless | session
      "features": { "tools": "pass", "resources": "pass", "listChanged": "untested" },
      "evidence": {
        "kind": "automated",               // automated | manual
        "ref": "ci://compatibility/1234",  // Lauf-URL ODER Protokollpfad
        "date": "2026-08-02",
        "operator": null                   // bei manual Pflicht
      },
      "limitations": ["Elicitation nicht angeboten (FR-P065)"]
    }
  ]
}
```

**Der Feature-Wortschatz ist geschlossen:** `tools`, `resources`, `prompts`, `elicitation`,
`approval`, `listChanged`, `oauth`, `skills`. Zulässige Werte: `pass`, `partial`, `fail`,
`unsupported`, `untested`.

`untested` ist ein **zulässiger und sichtbarer** Wert — eine weggelassene Zeile ist es nicht. Genau
darin liegt der Unterschied zwischen einer Matrix und einer Werbeaussage: Was nicht geprüft wurde,
steht drin und sieht ungeprüft aus.

**Ein Eintrag, der etwas behauptet, trägt einen Beleg** — und der Schematest ist Teil der
PR-Pflicht. Damit kann keine öffentliche Kompatibilitätsbehauptung entstehen, hinter der kein Lauf
und kein Protokoll steht (DoD WP5.1).

Die Umkehrung gilt genauso: Eine Zeile, deren acht Features **alle** auf `untested` stehen, ist ein
Platzhalter und trägt **kein** `evidence`. Ein Beleg dafür, dass nichts geprüft wurde, wäre ein
Widerspruch — und genau die Sorte Eintrag, die später jemand für einen Nachweis hält. Nur so kann
die Matrix die Pflichtclients aus FR-P061 überhaupt nennen, bevor sie geprüft sind.

**Pflichtclients** aus FR-P061: Claude Desktop/Code, Cursor, VS Code, Codex, MCP Inspector. Fehlt
einer, steht er mit `untested` in der Matrix, statt zu fehlen.

## 6. CI-Jobs, Trigger und Umgebungsvariablen

| Workflow-Datei | Job | Trigger | Gate | Owner |
|---|---|---|---|---|
| `.github/workflows/compatibility.yml` | `compatibility` | PR (SDK-/Inspectorpfad), scheduled (voll) | `BIFROST_RUN_COMPAT=1` | WP5.1 |
| `.github/workflows/oauth.yml` | `oauth-local` | PR | `BIFROST_REQUIRE_KEYCLOAK=1` | WP5.2 |
| `.github/workflows/oauth.yml` | `oauth-saas-smoke` | scheduled/dispatch | Secrets, **nicht** für Fork-PRs | WP5.2 |
| `.github/workflows/ci.yml` | `benchmark` (bestehend) | scheduled/dispatch | `BIFROST_RUN_BENCHMARK=1` | **Lead** |
| `.github/workflows/soak.yml` | `soak-smoke` | PR, ≤ 10 min | `BIFROST_RUN_SOAK=smoke` | WP5.4 |
| `.github/workflows/soak.yml` | `soak-nightly` | scheduled | `BIFROST_RUN_SOAK=long` | WP5.4 |
| `.github/workflows/e2e.yml` | `ui-e2e` | PR (Chromium), scheduled (Firefox/WebKit) | `BIFROST_RUN_E2E=1` | WP5.5 |

**Warum eigene Workflow-Dateien:** In M3 galt „genau ein Schreiber je Workflow-Datei". Fünf Pakete,
die gleichzeitig Jobs in `ci.yml` schreiben, verletzen das zwangsläufig. Also bekommt jedes Paket
seine Datei; `ci.yml` fasst in dieser Welle nur der Lead an.

**Namenskonvention, hier festgelegt:**

- `BIFROST_REQUIRE_*` — die Abhängigkeit **muss** verfügbar sein, sonst schlägt der Lauf fehl
  (bestehend: `BIFROST_REQUIRE_POSTGRES`).
- `BIFROST_RUN_*` — der Lauf ist opt-in und wird ohne die Variable übersprungen
  (bestehend: `BIFROST_RUN_BENCHMARK`).

Der Unterschied war bisher nur Gewohnheit. Er ist ab hier Vertrag, weil davon abhängt, ob ein
fehlendes Docker eine rote CI oder eine stille Lücke ergibt.

## 7. Dateizonen und Besitz

| Zone | Owner | Regel |
|---|---|---|
| `src/Bifrost.Abstractions/Support.cs` | **Lead** | Vertrag wird zentral gelegt, dann eingefroren |
| `tests/compatibility/**`, Generator, `docs/acceptance/compatibility.md` | WP5.1 | einziger Schreiber der Matrix |
| `tests/Bifrost.Integration.Tests/OAuth/**` (Keycloak über Testcontainers) | WP5.2 | ein OAuth-Owner für Produktionsfixes |
| `tests/Bifrost.Performance.Tests/**`, `docs/acceptance/performance.md` | WP5.3 | verschiebt `PerformanceBenchmarkTests` dorthin |
| `tests/soak/**`, Soak-Compose-Fixtures | WP5.4 | – |
| `tests/Bifrost.Ui.E2E/**` | WP5.5 | – |
| `src/Bifrost.Web/**` (nur `data-testid`) | WP5.5 | **additiv**, keine Logik, kein Auth-Bypass |
| `src/Bifrost.Core/Operations/SupportBundle/**`, CLI-/API-/UI-Adapter | WP5.6 | – |
| `.github/workflows/ci.yml`, `Bifrost.slnx` | **Lead** | neue Projekte werden vom Lead eingehängt |

**Nachtrag nach dem Audit:** Die Zeile nannte ursprünglich eine `docker-compose.oauth.yml`. Gebaut
wurde stattdessen ein Testcontainer — das Pflichtenheft verlangt für WP5.2 ausdrücklich „Keycloak
als reproduzierbarer Testcontainer", und eine Compose-Datei daneben wäre eine zweite Art, dieselbe
Gegenstelle zu starten. Die Zeile ist entsprechend berichtigt.

`Bifrost.slnx` bekommt in dieser Welle vier neue Projekte. Vier Pakete, die gleichzeitig dieselbe
Solutiondatei ändern, erzeugen Konflikte in einer Datei, die niemandem gehört — deshalb der Lead.

## 8. Was in M5 ausdrücklich NICHT passiert

- Keine neuen Protokollfamilien, Agent-Runtimes oder Control-Planes (Feature Freeze bis
  einschließlich M5, Pflichtenheft §Feature Freeze).
- **Kein Auth-Bypass „nur für Tests".** Ein E2E-Test, der sich an der Anmeldung vorbeimogelt, prüft
  eine Installation, die es nicht gibt.
- Keine abgeschaltete Redaction, keine Testrolle mit Sonderrechten, keine Testkonfiguration, die
  Produktionsvorgaben aufweicht.
- Keine Produktionsrefactors aus Soak- oder Perf-Funden heraus; Fund → Finding → eigenes WP.
- **Keine harten Schwellen auf geteilter Hardware ohne Toleranzmodell.** Regression heißt: Median
  über dem Toleranzband in **zwei aufeinanderfolgenden** Läufen. Ein einzelner Ausreißer auf einem
  Shared Runner ist keine Regression, sondern ein Nachbar.
- Keine Durchsatzangabe aus einem Stoß. Die alte „~7800 Calls/s" aus einem 0,1-Sekunden-Fenster ist
  der benannte Negativfall (DoD WP5.3).

## 9. Unverletzbare Invarianten (fortgeschrieben aus M2/M3)

1. Keine Toolausführung außerhalb `IToolInvoker`.
2. Secrets werden vor Persistenz, Log, Export und Diagnose redigiert — jetzt zusätzlich vor dem
   Support-Bundle.
3. Neue Konfiguration ist fail-closed; unbekannte Werte werden nicht still ignoriert.
4. Bereits veröffentlichte EF-Migrationen werden **nicht** nachträglich editiert.
5. SQLite bleibt der Zero-Setup-Default.
6. Die Bezeichner in `CryptographicNames` werden nicht geändert.
7. **Testcode weicht die Produktionskonfiguration nicht auf** (siehe §8).
8. **Canary-Regel:** Jede Quelle, aus der das Support-Bundle liest, trägt im Test ein eindeutiges
   Canary-Secret. Erscheint eines im Bundle, ist der Lauf rot — nicht gelb, nicht kommentiert.

## 10. Grenze der lokalen Prüfbarkeit

Was hier **nicht** geht, und was daraus folgt:

- **72-Stunden-Soak.** Läuft weder in einer Sitzung noch auf dem Entwicklungsrechner nebenbei.
  WP5.4 baut den Harness und belegt ihn mit Kurz- und Nachtlauf; der 72-h-Lauf ist ein
  Release-Candidate-Ereignis (M7) und steht bis dahin als **ausstehend**, nicht als bestanden.
- **GUI-Clients.** Claude Desktop und Cursor sind ohne Desktop-Automation nur manuell prüfbar. Das
  Protokoll nach §1.4 ist der Nachweis — mit Datum, Version und Prüfer.
- **macOS.** `ci.yml` hat keinen macOS-Runner. macOS-Zeilen der Matrix bleiben `untested`.
- **Externer SaaS-OAuth.** Braucht Secrets, die Fork-PRs nicht sehen dürfen; deshalb
  scheduled-only, und ein Ausfall dort blockiert keinen PR.
- **Echtes Netz.** Reverse Proxy und TLS lassen sich lokal über Docker Desktop stellen; Latenz,
  Paketverlust und eine tatsächlich kleine VM nicht. WP5.3 nennt die verwendete Umgebung im
  Ergebnis, statt sie zu behaupten.

Jedes Paket benennt in seiner Abgabe, **was geprüft wurde und was offen bleibt**. Eine übersprungene
Prüfung wird als übersprungen gemeldet, nicht als bestanden.

## 11. Startreihenfolge

Unabhängig und sofort startbar: **WP5.3** (Perfharness — die Defizite sind in
[performance.md](../acceptance/performance.md) bereits benannt), **WP5.4** (Soak) und **WP5.1**
(Matrix). **WP5.2** hängt an M3-Security, **WP5.5** an der M4-UI, **WP5.6** an §2 dieses Vertrags,
den der Lead zuerst legt.
