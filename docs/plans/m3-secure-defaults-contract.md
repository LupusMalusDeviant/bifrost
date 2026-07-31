# M3 — Eingefrorener Vertrag für sichere Vorgaben

**Stand:** 2026-07-31 · **Eingefroren durch:** Lead/Integrator · **Gilt für:** WP3.1 – WP3.6
**Grundlage:** [ADR-0025](../adr/0025-host-ausfuehrung-verbieten-und-bestehende-instanzen-migrieren.md),
[ADR-0018](../adr/0018-native-prozess-und-container-isolation.md), Pflichtenheft 0004 Kapitel 9

Namen, Codes und Zonen stehen hier **fest**. Wer abweichen will, meldet das an den Lead, statt den
Vertrag zu erweitern — an ihm hängen mehrere gleichzeitig arbeitende Pakete.

## 1. Der Vertrag in `Bifrost.Abstractions`

```csharp
public interface IHostExecutionPolicy
{
    HostExecutionDecision Evaluate(UpstreamServerConfig config);
}

public sealed record HostExecutionDecision(
    bool Allowed,
    string ReasonCode,   // stabil, maschinenlesbar: "BFR-POL-0001"
    string Summary,      // ein Satz, für Menschen
    string? Remediation);
```

**Unbekannt heißt nein.** Eine Policy, die im Zweifel erlaubt, ist eine Dokumentation (ADR-0025 E1).

## 2. Reason-Codes

| Präfix | Bereich |
|---|---|
| `BFR-POL-*` | Ausführungs-Policy: erlaubt, verboten, übernommen, Ausnahme |

Reserviert: `BFR-POL-0001…0099`. Die Diagnosecodes aus M2 (`BFR-CFG/DB/KEY/NET/RT/UP`) bleiben
unberührt; die Policyentscheidung erscheint zusätzlich im Diagnosebericht (Pflichtenheft WP3.1).

## 3. Die Umstellung bestehender Instanzen (ADR-0025 E3)

Genau ein Weg, und er gehört **WP3.1 allein**:

1. Host-Upstreams vorhanden **und** keine ausdrückliche Einstellung → bisheriger Zustand wird
   übernommen, die Instanz läuft weiter.
2. Die Übernahme wird **geschrieben**, nicht angenommen.
3. Audit-Eintrag **und** Diagnosewarnung, die jeden betroffenen Upstream namentlich nennt.

Kein Paket baut eine zweite Variante davon. Ein zweiter Umstellungsweg wäre schlimmer als keiner:
Zwei Stellen, die dieselbe Bestandsentscheidung treffen, treffen sie irgendwann verschieden.

## 4. Namensfrage, hier entschieden

`CliIsolationMode` und `CliIsolationOptions` heißen künftig `IsolationMode` und `IsolationOptions`
und gelten für stdio **und** CLI (ADR-0025 E5). Die Umbenennung macht **WP3.2 allein**, in einem
Zug, mit Beibehaltung des JSON-Namens in der gespeicherten Konfiguration.

**Der gespeicherte Name ändert sich nicht.** Eine bestehende Konfiguration, die nach einem Upgrade
nicht mehr gelesen wird, ist Datenverlust — dieselbe Klasse Fehler wie eine umbenannte
DataProtection-Purpose (siehe `CryptographicNames`).

## 5. Dateizonen und Besitz

| Zone | Owner | Regel |
|---|---|---|
| `src/Bifrost.Abstractions/Execution.cs` | **Lead** | Vertrag wird zentral gelegt, dann eingefroren |
| Policy, Bestandsübernahme, Validator | WP3.1 | einziger Schreiber der Umstellungslogik |
| `src/Bifrost.Upstream/Cli/**`, `StdioUpstreamConnector.cs`, Isolationsmodell | WP3.2 | einziger Schreiber der Umbenennung |
| DataProtection-Komposition, Key-Ring-Setup | WP3.3 | `Program.cs`-Integration sequenziell mit WP3.4 |
| Bootstrap/Setup-Token, Login-UI | WP3.4 | – |
| `.github/**`, Dependabot/Renovate | WP3.5 | genau ein Schreiber je Workflow-Datei |
| Security-/Architekturtests | WP3.6 | **nur Testcode**; Produktionsänderung nur bei Fund, dann melden |

`Program.cs` hat in dieser Welle **einen** Schreiber. WP3.3 und WP3.4 fassen es nacheinander an,
nicht gleichzeitig.

## 6. Unverletzbare Invarianten (unverändert aus M2)

1. Keine Toolausführung außerhalb `IToolInvoker`.
2. Secrets werden vor Persistenz, Log, Export und Diagnose redigiert.
3. Neue Konfiguration ist fail-closed; unbekannte Werte werden nicht still ignoriert.
4. Bereits veröffentlichte EF-Migrationen werden **nicht** nachträglich editiert.
5. SQLite bleibt der Zero-Setup-Default.
6. Die Bezeichner in `CryptographicNames` werden nicht geändert — nur mit Migrationslauf.

## 7. Grenze der lokalen Prüfbarkeit

Echte Linux-Container in CI verlangt das Pflichtenheft für WP3.2. Lokal steht Docker Desktop unter
Windows zur Verfügung; ein Linux-Runner-Verhalten ist damit **nicht** nachgestellt. Was ein Paket
nicht prüfen konnte, benennt es in seiner Abgabe — eine übersprungene Prüfung wird als übersprungen
gemeldet, nicht als bestanden.
