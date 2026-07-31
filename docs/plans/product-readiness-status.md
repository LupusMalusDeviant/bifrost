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
