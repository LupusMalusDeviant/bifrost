# M2 — Eingefrorener Vertrag für Backup, Restore, Migration und Diagnose

**Stand:** 2026-07-31 · **Eingefroren durch:** Lead/Integrator · **Gilt für:** WP2.1 – WP2.7
**Grundlage:** [ADR-0024](../adr/0024-backup-restore-und-migrationssicherheit.md), Pflichtenheft 0004 Kapitel 8

Namen, Formate und Fehlercodes stehen hier **fest**. Wer abweichen will, meldet das an den Lead.
Die Fachlogik liegt in Diensten; CLI, API und UI sind **Adapter** darauf und implementieren keine
zweite Variante derselben Regeln (Pflichtenheft §3.1).

## 1. Verträge in `Bifrost.Abstractions`

Neuer Namespace `Bifrost.Abstractions.Operations`:

```csharp
public interface IBackupService
{
    Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken ct);
    Task<BackupInspection> InspectAsync(string archivePath, string? passphrase, CancellationToken ct);
}

public interface IRestoreService
{
    Task<RestorePlan> PlanAsync(RestoreRequest request, CancellationToken ct);
    Task<RestoreResult> ApplyAsync(RestorePlan plan, CancellationToken ct);
}

public interface IDiagnosticService
{
    Task<DiagnosticReport> RunAsync(DiagnosticScope scope, CancellationToken ct);
}
```

`PlanAsync` vor `ApplyAsync` ist Pflicht und kein Komfort: Ein Restore, der erst beim Schreiben
merkt, dass er nicht passt, hat bereits geschrieben.

### Nachtrag nach der Welle: der Plan trägt ein Handle

**Der Fehler.** In der ersten Fassung trug weder `RestorePlan` noch `ConfigurationImportPlan` einen
Verweis auf das, woraus er entstand. Beide betroffenen Pakete haben sich unabhängig voneinander mit
einer `ConditionalWeakTable` beholfen und einen fremden Plan abgewiesen, statt zu raten — die
richtige Notlösung, aber sie bindet Planung und Anwendung an dieselbe **Objektidentität**. Für die
CLI trägt das. Für die REST-API nicht: Dort geht der Plan als JSON hinaus und kommt als neues
Objekt zurück, ein Restore über die API wäre grundsätzlich nicht anwendbar gewesen.

Dass zwei Pakete ohne Kenntnis voneinander dieselbe Stelle melden, war die Aussage.

**Die Korrektur.** Beide Pläne tragen ein `Token` — ein zufälliges, undurchsichtiges Handle. Der
Zustand bleibt beim Dienst; nur das Handle reist.

- Archivpfad und **Passphrase** stehen weiterhin *nicht* im Plan. Eine Passphrase, die durch eine
  API-Antwort läuft, steht danach in jedem Log.
- Ein Handle gilt 30 Minuten. Ein Plan beschreibt einen Zustand, den er nur zum Zeitpunkt der
  Prüfung kannte — je länger er gilt, desto eher trifft er eine Instanz, die inzwischen eine andere
  ist. Abgelaufene Vormerkungen werden weggeräumt, denn sie halten Passphrasen und entschlüsselte
  Zugangsdaten im Arbeitsspeicher.
- Ein Handle ist **einmalig**. Nach der Anwendung ist es verbraucht.
- Unbekannt oder abgelaufen heißt Absage mit Begründung, nie ein Versuch auf geratenen Daten.

`ConfigurationImportPlan` hat zugleich eine vierte Liste bekommen: `Unchanged`. Objekte, die auf der
Zielinstanz inhaltsgleich schon vorliegen — etwa der mitgelieferte Guard-Regelsatz — sind weder
Zugang noch Konflikt. Ohne diese Unterscheidung wäre auf einer vorbelegten Instanz **kein einziger
Export je anwendbar** gewesen.

## 2. Backup-Manifest v1 (`manifest.json`)

```jsonc
{
  "formatVersion": 1,                    // Format des Archivs, nicht des Produkts
  "productVersion": "0.12.0",            // erzeugende B.I.F.R.O.S.T-Version
  "minimumRestoreVersion": "0.11.0",     // darunter verweigert der Restore (ADR-0024 E6)
  "createdAt": "2026-07-31T12:00:00Z",
  "instanceId": "…",                     // aus config/instance.json
  "database": { "provider": "sqlite" | "postgres", "migration": "20260731_…" },
  "sections": ["database", "keyring", "packages", "config"],
  "encryption": { "algorithm": "none" | "aes-256-gcm", "kdf": "pbkdf2-sha256", "iterations": 600000 },
  "checksumAlgorithm": "sha-256"
}
```

Das Manifest liegt **unverschlüsselt** im Archiv, auch wenn die Nutzlast verschlüsselt ist — sonst
kann ein Werkzeug nicht prüfen, was es vor sich hat.

## 3. Diagnosemodell

```csharp
public sealed record DiagnosticCheck(
    string Code,          // stabil, maschinenlesbar, z. B. "BFR-DB-0003"
    CheckStatus Status,   // Pass | Warning | Fail | Skipped
    string Summary,       // ein Satz, für Menschen
    string? Remediation,  // die naechste Handlung, wenn es eine gibt
    IReadOnlyDictionary<string, string> SafeDetails); // NIE Credentials
```

**Codebereiche** (vierstellig je Bereich, damit ein Code nie zweimal vergeben wird):

| Präfix | Bereich |
|---|---|
| `BFR-CFG-*` | Konfiguration, Umgebungsvariablen, Datenverzeichnis |
| `BFR-DB-*` | Datenbank, Migrationen, Provider |
| `BFR-KEY-*` | DataProtection-Key-Ring |
| `BFR-NET-*` | Ports, öffentliche Adresse, Proxy-Vertrauen |
| `BFR-RT-*` | Container-Runtime, WASI-Host |
| `BFR-UP-*` | Upstreams (Prozess/DNS, SSRF, Auth, Handshake, Discovery) |

## 4. Exit-Codes (CLI, für alle Operations-Befehle gleich)

| Code | Bedeutung |
|---|---:|
| `0` | Erfolg, keine Warnung |
| `1` | unerwarteter Fehler |
| `2` | Bedienfehler (Argumente, fehlende Datei) |
| `3` | Diagnose mit **Warnung** |
| `4` | Diagnose mit **Fehler** |
| `5` | Archiv ungültig, beschädigt oder inkompatibel |
| `6` | Zielinstanz nicht leer und kein `--replace` |

## 5. Dateizonen und Besitz

| Zone | Owner | Regel |
|---|---|---|
| `src/Bifrost.Abstractions/Operations.cs` | **Lead** | Verträge werden zentral gelegt, dann eingefroren |
| `src/Bifrost.Persistence/Backup/**` | WP2.1/2.2 | ein Owner für Format **und** Restore |
| `DatabaseInitializer.cs`, `BifrostDbContext.cs`, beide Migrations-Snapshots | **WP2.3 allein** | niemand sonst, in keiner Welle |
| `src/Bifrost.Core/Diagnostics/**` | WP2.4 | – |
| `src/Bifrost.Core/Configuration/**` (Export) | WP2.5 | – |
| `src/Bifrost.Cli/**`, `ApiEndpoints.cs`, `Program.cs` | **Lead** | Adapter und Verdrahtung erst nach stabilen Diensten (WP2.7) |
| `tests/Bifrost.Upgrade.Tests/**` | WP2.6 | – |

**Keine zwei Agenten erzeugen EF-Migrationen.** Das ist die härteste Regel dieser Welle.

## 6. Unverletzbare Invarianten (Pflichtenheft §2)

1. Keine Toolausführung außerhalb `IToolInvoker` — Operations-Dienste rufen keine Tools auf.
2. Secrets werden vor Persistenz, Log, Export und Diagnose redigiert.
3. Neue Konfiguration ist fail-closed; unbekannte Werte werden nicht still ignoriert.
4. Bereits veröffentlichte EF-Migrationen werden **nicht** nachträglich editiert.
5. SQLite bleibt der Zero-Setup-Default.

## 7. Grenze der lokalen Prüfbarkeit

PostgreSQL läuft hier über Testcontainers (vorhanden, siehe `BIFROST_REQUIRE_POSTGRES`). Was **nicht**
geht: ein echter Mehrknotenbetrieb, ein realer Plattenvoll-Fall und ein Upgrade über drei
veröffentlichte Releases — Letzteres braucht Artefakte, die es noch nicht gibt (M1 ist nicht
abgenommen). WP2.6 baut den Harness, kann ihn aber nur gegen selbst erzeugte Fixtures fahren.

Jedes Paket benennt in seiner Abgabe, **was geprüft wurde und was offen bleibt**.
