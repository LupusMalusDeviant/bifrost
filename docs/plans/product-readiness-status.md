# Produktreife — Fortschrittsprotokoll

Arbeitsstand des Programms aus Lastenheft 0002, Pflichtenheft 0004 und dem Opus-Runbook. Die drei
Planungsdokumente selbst liegen unter `docs/produktreife/` und sind **bewusst nicht versioniert**:
Sie kommen von außen und werden dort gepflegt.

**Regel für diese Datei:** Ein Paket steht erst auf `done`, wenn sein Ergebnis implementiert,
getestet, dokumentiert, integriert und durch einen **reproduzierbaren Nachweis** belegt ist. Eine
Agentenmeldung, ein grüner Unit-Test oder ein kompilierender Branch ist keine Abnahme.

## Aktueller Meilenstein: M0 — Baseline

| WP | Status | Nachweis | Blocker | Nächster Schritt |
|---|---|---|---|---|
| WP0.1 CI grün | `done` | `./build.sh verify-rust` grün (fmt, clippy `-D warnings`, `cargo test`) | – | – |
| WP0.2 Release-/Doku-Konsistenz | `done` | README, SECURITY.md, `CHANGELOG.md` gegen Ist-Stand abgeglichen | – | Release-Kanal-Frage (siehe unten) |
| WP0.3 Lokaler Verifier | `done` | `build.ps1` / `build.sh` mit `verify-fast`, `verify-dotnet`, `verify-rust`, `verify-container`; `verify-fast` und `verify-rust` lokal ausgeführt | – | CI ruft die Skripte noch nicht auf |
| WP0.4 Prozesslebenszyklus | `done` | `ProcessLifecycleTests` (2 Tests) plus Handprobe; Lücke in `HttpConnectorIntegrationTests` geschlossen | – | – |

## Befunde aus M0

### WP0.1 — der Formatfehler war Folgeschaden der Umbenennung

`cargo fmt --check` war rot, weil die Kiste von `mcpmcp_wasi_component_spike` auf
`bifrost_wasi_component_spike` umbenannt wurde: Der Import sortiert seitdem vor `ed25519_dalek`.
Kein inhaltlicher Fehler, aber ein Beleg dafür, dass eine repo-weite Textersetzung Gates verschiebt,
die niemand dabei im Blick hat.

### WP0.2 — drei Aussagen widersprachen dem Ist-Stand

| Aussage | Vorher | Tatsächlich |
|---|---|---|
| Image-Größe im README | `< 300 MB` | **315 MB** gemessen (v0.11.0), CI-Gate steht auf `< 350 MB` |
| Release-Status im README | „Pre-release v0.11.0" | v0.11.0 ist als **Release** markiert (siehe offene Frage) |
| `SECURITY.md` | kennt `v0.11.0` nicht | Tabelle ergänzt |

`CHANGELOG.md` gab es nicht; sie ist jetzt angelegt und beginnt bewusst bei `0.11.0` statt
rückwirkend erfunden zu werden.

### WP0.4 — genau ein Startpfad lag außerhalb der Prozess-Hygiene

Alle Testserver werden über den **Produktpfad** gestartet (`StdioTransportOptions` →
`StdioUpstreamConnector`), der unter Windows ein Job-Objekt mit `KILL_ON_JOB_CLOSE` herstellt —
stirbt der Testhost, räumt das Betriebssystem die Kinder ab.

Eine Ausnahme: `HttpConnectorIntegrationTests.StartHttpServer` rief `Process.Start` direkt auf, ohne
`ProcessHygiene.EnsureInitialized()`. Ein hart abgebrochener Lauf ließ diesen Prozess stehen; er
hielt danach seinen Port **und seine eigene Programmdatei**, sodass der nächste Build mit „wird von
einem anderen Prozess verwendet" scheiterte — mit einer Ursache zwei Läufe früher. Behoben.

**Der Nachweis** ist der eigentliche DoD, nicht die Korrektur. Der normale Weg war längst geprüft
(`SupervisorIntegrationTests.Dispose_leaves_no_zombie_processes`); der interessante Fall ist der
Prozess, der gar nicht mehr zum Aufräumen kommt.

Dafür gibt es jetzt `ProcessLifecycleTests` mit einem eigenen Wirt (`tests/Bifrost.TestServers/OrphanHost`):
Er startet über den **Produktpfad** einen echten stdio-Upstream, meldet dessen Prozess-Id und wartet.
Der Test schießt **nur den Wirt** hart ab — bewusst ohne `entireProcessTree`, sonst prüfte er seine
eigene Aufräumarbeit statt der Hygiene, die das Produkt herstellt — und erwartet, dass das Kind
stirbt. Ein Test kann seinen eigenen Testhost nicht töten; deshalb der zweite Prozess.

Von Hand gegengeprüft: Wirt `13592` hart beendet, Kind `11376` war danach weg. Der Negativfall ist
ebenfalls belegt, allerdings unfreiwillig — ein `BulkServer` überlebte heute seinen Elternprozess
und blockierte den nächsten Build, weil er außerhalb des Job-Objekts gestartet worden war.

## Offene Entscheidung für den Product Owner

**Release-Kanal.** Das Lastenheft verlangt in FR-P007: *Pre-Releases werden als solche markiert;
`latest` wird erst für stabile Releases verwendet.* Am 2026-07-31 wurde `v0.11.0` auf Anweisung
ausdrücklich zum normalen Release befördert, weil GitHub sonst weiterhin `v0.5.0` als „Latest"
bewarb — die beiden Schalter sind bei GitHub nicht unabhängig (`Latest release cannot be draft or
prerelease`).

Damit steht die Anforderung gegen den Ist-Zustand. Zwei Wege:

1. **FR-P007 anpassen** — die `0.x`-Linie trägt die Aussage „nicht stabil" bereits in der
   Versionsnummer, und die Startseite soll den aktuellen Stand zeigen.
2. **Ist-Zustand anpassen** — `v0.11.0` wieder als Pre-Release markieren und in Kauf nehmen, dass
   GitHub bis zum ersten stabilen Release einen veralteten Stand als „Latest" führt.

Bis zur Entscheidung beschreibt die Dokumentation den **Ist-Zustand**, nicht die Zielregel.

## Laufender Meilenstein: M1 — Distribution

Vertrag eingefroren in [`m1-distribution-contract.md`](m1-distribution-contract.md). Vier Pakete
laufen parallel mit disjunkten Dateizonen; `release.yml` hat genau einen Schreiber.

| WP | Status | Nachweis |
|---|---|---|
| WP1.1 Multi-Arch-Image | `done` | `actionlint` sauber, amd64-Build und Smoke lokal grün, kein `latest`, alle Actions SHA-gepinnt |
| WP1.2 SBOM/Provenance/Signatur | `done` | Syft/Trivy/Cosign containerisiert ausgeführt, Action-SHAs stichprobenartig gegen die API geprüft |
| WP1.3 Native CLI-Artefakte | `done` | 12 CLI-Tests grün, `--version` trägt den echten HEAD-Commit |
| WP1.4 Compose-/Installationspfade | `done` | drei `docker compose config`-Läufe grün, Volume-Name nachgeprüft |
| WP1.5 Release-Automation | `done` | sechs Jobs zusammengeführt, `actionlint` auf `release.yml` ohne Befund, 783 Tests grün |

**Die Abnahme steht aus.** Alle fünf Pakete sind implementiert und lokal geprüft, aber **kein
einziger Releaselauf hat stattgefunden**. Was erst der erste echte Tag zeigt: arm64-Build unter
QEMU, GHCR-Login und Push, die Form von `steps.push.outputs.digest`, Attestation und Signatur mit
den gesetzten Berechtigungen, und ob die fünf CLI-Runner-Labels für dieses Repository verfügbar
sind. Nach der Abschlussregel des Pflichtenhefts (Kapitel 20, Punkt 6) muss der Nachweis **auf dem
gebauten Releaseartefakt** erfolgen — M1 ist damit implementiert, aber nicht abgenommen.

### WP1.4 — Prüfergebnis des Lead

Nachgeprüft und bestätigt: alle Compose-Kombinationen gültig, Volume löst auf
`mcpmcp_bifrost-data` auf, kein `latest`-Tag, Image zeigt auf die Registry aus dem Vertrag.

**Der wichtigste Fund betrifft eigene frühere Arbeit.** Der Umbenennungs-Commit `c7cb446` hat in
`docker-compose.yml` auch das Volume mitgezogen (`mcpmcp-data` → `bifrost-data`). Beim Deploy auf
Badwolf war das aufgefallen und dort umgangen — die Datei im Repository blieb aber die Falle, und
niemand hätte es gemerkt: Eine Bestandsinstallation bekäme ein leeres Volume und meldete sich
fehlerfrei als „bereit". Der Umstiegsweg steht jetzt in `docs/operations.md`.

Zwei Punkte gehen als Entscheidung an den Lead bzw. weiter:

1. **Der Standardweg ist bis zum ersten Release-Lauf kaputt.** `docker compose up -d` zieht ein
   Image, das es noch nicht gibt — vorher wurde lokal gebaut. Das ist die vom Pflichtenheft
   verlangte Richtung (WP1.4), aber die Lücke muss WP1.5 schließen: Der Vorgabewert der Version in
   `docker-compose.yml` und `.env.example` gehört an den ersten veröffentlichten Tag gekoppelt.
2. **Das Key-Ring-Passwort lässt sich nicht als Datei-Secret zuführen** (`Program.cs` liest nur die
   Umgebungsvariable). Das PFX selbst geht als Compose-Secret, das Passwort nicht. FR-P048 ist damit
   nur halb erfüllt; der Fix wäre ein `…_FILE`-Suffix oder `AddKeyPerFile`. Bewusst **nicht**
   nebenbei gemacht — Produktionscode gehört nicht in ein Doku-/Compose-Paket.

## Laufender Meilenstein: M2 — Wiederherstellbarkeit

Entscheidung vorab in [ADR-0024](../adr/0024-backup-restore-und-migrationssicherheit.md), Vertrag
eingefroren in [`m2-recoverability-contract.md`](m2-recoverability-contract.md).

**Vermerk zur Reihenfolge.** Das Pflichtenheft macht M2 von einem **abgenommenen** M1 abhängig.
Abgenommen ist M1 nicht — dazu fehlt ein echter Releaselauf, den der Product Owner für diesen
Durchgang ausdrücklich ausgeschlossen hat („keinen Release taggen"). M2 läuft daher auf Anweisung
vor der Abnahme. Das ist keine stille Umgehung der Regel, sondern eine bewusst getroffene
Reihenfolgeentscheidung, die hier festgehalten wird, damit sie bei der Abnahme sichtbar bleibt.

| WP | Status | Nachweis |
|---|---|---|
| WP2.1 Backup-Format und -Erzeugung | `implementiert` | 26 Backup-Tests; SQLite vollständig, **PostgreSQL nicht** |
| WP2.2 Restore mit Vorprüfung | `implementiert` | Staging, Pre-Backup vor `Replace`, Zip-Slip/Symlink/Bombe abgewiesen |
| WP2.3 Migrationssicherheit | `implementiert` | 80/80 im Namensraum `Persistence`, SQLite **und** Postgres |
| WP2.4 Diagnosedienst (`doctor`) | `implementiert` | 17 Codes, 107 Tests; Sonden noch nicht verdrahtet |
| WP2.5 Konfigurationsexport/-import | `implementiert` | 20 Tests, Secret-Negativkorpus je 8-Zeichen-Bruchstück |
| WP2.6 Upgrade-Kompatibilitätsmatrix | `implementiert` | 43 Tests, 15 Migrationsstände × 2 Provider; **hat E6 widerlegt** |
| WP2.7 Adapter (CLI, API, UI) | `implementiert` | 7 Befehlsgruppen, API hinter Global-Grant, Sonden verdrahtet |

Gesamtlauf nach der Zusammenführung: `./build.sh verify-dotnet` → **948 Tests grün, 0 Fehler**
(M1-Stand: 783). Kein Paket hat eine fremde Zone angefasst, `Operations.cs` blieb während der
gesamten Welle unverändert.

### Der Fund, der keinem Paket gehörte

WP2.5 meldete ihn nebenbei, und er ist der schwerwiegendste Einzelbefund dieser Welle:
`UpstreamConfigRedactor` maskierte das Credential von `OpenRpcTransportOptions` **nicht**. Der
Redactor sitzt in `ApiEndpoints.cs` vor der Upstream-Liste — das Zugangsdatum eines OpenRPC-Servers
ging damit im Klartext an Oberfläche und API.

Beim Nachprüfen kam eine zweite Lücke derselben Familie dazu, in die Gegenrichtung:
`UpstreamConfigMerge.CarryOverSecrets` kannte weder WASI-Secrets noch das OpenRPC-Credential. Der
Redactor blendete sie aus, die Übernahme holte sie nicht zurück — wer einen solchen Upstream in der
Oberfläche bearbeitete und speicherte, schrieb die Maske `***` als echten Wert in die Datenbank.
Der Upstream lief bis zum nächsten Neustart weiter und scheiterte dann an einem Zugangsdatum, das
wörtlich aus drei Sternen bestand.

Beide behoben. Der eigentliche Fehler lag aber im Test: `Every_transport_secret_is_masked…` prüfte
genau **einen** Transport und trug die Zusicherung für alle im Namen. Der Ersatz führt einen
Negativkorpus über alle sechs Transporte und hat einen Wächter, der rot wird, sobald ein siebter
dazukommt — das Versäumnis war nicht, `OpenRpc` zu vergessen, sondern keine Stelle zu haben, an der
das Vergessen auffällt.

### Lead-Entscheidungen zu den Rückfragen

| Frage | Entscheidung |
|---|---|
| Journaltabelle per Roh-DDL statt EF-Migration (WP2.3) | **bestätigt** — genau das Verfahren, mit dem EF seine eigene Historientabelle anlegt; eine EF-Migration war ausdrücklich verboten, ein Sidecar würde bei PostgreSQL die falsche Antwort geben |
| Codebereiche `BFR-DB-0001…0099` (WP2.4) / `0100…0199` (WP2.3) | **bestätigt**, Überschneidungsfreiheit nachgeprüft: 26 Codes, jeder genau einmal vergeben |
| `Skipped` im Exit-Code (WP2.4) | **neutral, also `0`.** Ein übersprungener Check ist keine Warnung — er steht sichtbar mit Begründung im Bericht, und das ist die Aussage |
| Schreibprobe legt `.bifrost-doctor-*.tmp` an (WP2.4) | **zulässig.** Einen Ordner rein lesend auf Beschreibbarkeit zu prüfen, geht auf keinem der beiden Betriebssysteme verlässlich |
| Ids beim Import erhalten (WP2.5) | **bestätigt** — neu vergebene Ids ließen jeden mitgelieferten Grant ins Leere zeigen, und Default-Deny meldet das nicht, sondern erlaubt nur nichts |
| `.gitignore`-Negation für `Backup/` (WP2.1) | **bestätigt**, Wirkung nachgeprüft (`git check-ignore`, 41 Dateien sichtbar). Die VS-Vorlagenregel `Backup*/` hätte eine ganze Quellcodezone unsichtbar gemacht |

### Vertragslücke — gefunden, entschieden, behoben

Zwei Pakete sind unabhängig voneinander auf denselben Fehler in `Operations.cs` gestoßen: Weder
`RestorePlan` noch `ConfigurationImportPlan` trug einen Verweis auf seine Nutzlast. Beide behalfen
sich mit einer `ConditionalWeakTable` und wiesen einen fremden Plan ab, statt zu raten — die
richtige Notlösung, aber sie bindet Planung und Anwendung an **dieselbe Objektidentität**. Für die
CLI trägt das. Für die REST-API nicht: Dort geht der Plan als JSON hinaus und kommt als neues
Objekt zurück, ein Restore über die API wäre grundsätzlich nicht anwendbar gewesen.

Dass zwei Pakete ohne Kenntnis voneinander dieselbe Stelle melden, war die Aussage — ein Fehler,
keine Auslegungsfrage. Die Welle war beendet, die Einfrierung damit aufgehoben; beide Pläne tragen
jetzt ein Handle mit 30-Minuten-Geltung, einmaliger Verwendung und ohne Passphrase im Plan. Details
im [Vertrag](m2-recoverability-contract.md#nachtrag-nach-der-welle-der-plan-trägt-ein-handle).

Der Nachweis ist ein Test, der den Plan tatsächlich durch `JsonSerializer` schickt und danach
anwendet — vorher war das genau der Fall, der scheiterte.

### WP2.6 hat das Rückwärts-Tor aus E6 widerlegt

Der Upgrade-Harness sollte belegen, dass ein Archiv aus einer neueren Version abgelehnt wird. Er
belegt das Gegenteil, und der Test steht jetzt als Feld 8 der Matrix.

`RestoreService` vergleicht die **selbst behauptete** `minimumRestoreVersion` aus dem Manifest. Die
stammt aus `BackupOptions.MinimumRestoreVersion`, und das ist die Konstante
`BackupLayout.DefaultMinimumRestoreVersion = "0.11.0"` — sie wird von keiner Version angehoben.
Ein Archiv aus einer späteren Version trägt also dieselbe Angabe wie eines von heute und wird
**eingespielt**. Aufgehalten wird es erst danach vom `DatabaseInitializer` mit `BFR-DB-0102` — also
nachdem geschrieben wurde, was E6 gerade verhindern sollte.

Der Fehler ist nicht die Konstante, sondern das Kriterium: Eine Versionsangabe, die das Archiv über
sich selbst macht, kann nicht das Tor bewachen. Der Restore muss den **Migrationsstand** aus dem
Manifest gegen die Migrationen prüfen, die dieser Build kennt — kennt er sie nicht, ist das Archiv
neuer, ganz ohne Versionsbuchhaltung. Das ist vor WP2.6 niemandem aufgefallen, weil der Test dazu
fehlte; der zuständige Agent hat gegen die eigene Zusage getestet und sie widerlegt.

**Behoben.** Der Restore prüft jetzt den Migrationsstand aus dem Manifest gegen die Migrationen, die
dieser Build kennt (`KnownMigrations.For`, gelesen aus der Migrations-Assembly, **ohne**
Datenbankverbindung — der Regelfall des Restore ist eine leere Instanz, eine Prüfung mit
Verbindungsbedarf fiele genau dann aus, wenn sie gebraucht wird). Kennt der Build den Stand nicht,
stammt das Archiv aus einer neueren Version, ganz ohne Versionsbuchhaltung.

Fehlt die Menge der bekannten Migrationen, meldet das Tor eine **Warnung** statt zu schweigen: Ein
Schutz, der still ausfällt, ist schlimmer als keiner. Drei Tests belegen die drei Fälle —
abgelehnt, angewendet (dasselbe Archiv, nur bekannter Stand), und ungeprüft mit Ansage.

### Vier Bezeichner, an denen jeder gespeicherte Geheimtext hängt — ungetestet

Ebenfalls von WP2.6 gemeldet und nachgeprüft:

| Bezeichner | Ort |
|---|---|
| `McpMcp.UpstreamConfig.v1` | `EfUpstreamConfigStore.cs:18` |
| `McpMcp.UpstreamOAuthToken.v1` | `UpstreamOAuthTokenStore.cs:21` |
| `McpMcp.Webhook.Secret.v1` | `WebhookStore.cs:18` |
| `SetApplicationName("MCPMCP")` | `Program.cs:97` |

Alle vier gehen in die Schlüsselableitung ein; eine Umbenennung macht **jeden** gespeicherten
Geheimtext unlesbar, ohne Fehlermeldung beim Start. Gesichert sind sie heute ausschließlich durch
Kommentare — kein Test greift darauf zu.

Das ist genau die Regression, die der Umbenennungs-Commit `c7cb446` beinahe ausgelöst hätte, und
sie ist zugleich die einzige, die der Upgrade-Harness prinzipiell nicht fangen kann: Er benutzt
seinen eigenen Anwendungsnamen und würde eine repo-weite Umbenennung stillschweigend mitmachen.

**Behoben.** Alle vier stehen jetzt als Konstanten in `CryptographicNames`, und
`CryptographicNamesTests` prüft sie gegen ihre Werte. Ein Test, der eine Konstante gegen ihren
eigenen Wert prüft, sieht sinnlos aus — er ist hier die einzige Stelle im Repository, an der eine
Umbenennung *auffällt*. Wer ihn rot macht, hat zwei Möglichkeiten: die Änderung zurücknehmen oder
einen Migrationslauf schreiben, der alles entschlüsselt und neu verschlüsselt. Den Test anzupassen
ist keine dritte, und das steht so im Test.

### Offen aus dieser Welle

- **PostgreSQL-Backup fehlt.** Der Weg lehnt laut ab statt still Zeilen zu exportieren (ADR-0024 E2),
  aber FR-P020 ist für Postgres damit nicht erfüllt. **Das ist die größte offene Lücke aus M2**, und
  sie zieht eine zweite nach sich: Auf PostgreSQL läuft jedes Upgrade ohne Rückweg, weil E7 dort
  keine Sicherung erzeugen kann (`WhenAvailable` statt `Always` — `Always` wäre dort ein
  Startverbot, keine Zusage).
- **E7 ist für SQLite erfüllt**, für PostgreSQL und Datenbanken im Arbeitsspeicher nicht.
- **`IAssetStore` kann keinen einzelnen Skill entfernen** und `CreateAsync` erhält die exportierte
  `AssetId` nicht. `RemoveSkillAsync` wirft deshalb; der Rückstand landet sichtbar in der
  Rückstandsliste der Kompensation, statt als „vollständig zurückgenommen" gemeldet zu werden. Ein
  zur Hälfte angewendeter Skill-Import bleibt Handarbeit.
- **`GuardOptions` sind ein Start-Singleton aus der Umgebung.** Beim Import lässt sich nur der
  Freigabe-Vorgabeweg übernehmen; abweichende Guard-Schalter werden protokolliert, nicht gesetzt.
- **„Instanz-Id" heißt zwei verschiedene Dinge.** `GatewayIdentity.InstanceId` ist ein frischer GUID
  je Prozess (für den Federations-Loop-Header), die neue stabile Id steht in `config/instance.json`.
  Für sich genommen beides richtig, zusammen eine Falle.
- **Publisher-Trust fehlt im Konfigurationsexport.** Ohne ihn ist ein WASI-Upstream auf der
  Zielinstanz nicht ladbar — der inhaltlich dringendste Kandidat für Exportformat v2.
- **Alte Daten in alter Form sind nicht prüfbar.** Der Upgrade-Harness fährt echte alte Schemata,
  aber mit dem heutigen schreibenden Code. Eine Regression im *Serialisierungsformat* eines früheren
  Builds findet er prinzipiell nicht; das ist seine schwerwiegendste Lücke, und sie steht in
  `docs/upgrade-matrix.md`.
