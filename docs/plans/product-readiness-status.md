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

## Nicht begonnen

M1 bis M8 sind nicht angefasst. Nach Pflichtenheft darf M1 erst starten, wenn M0 vollständig grün
ist — es fehlt der Nachweis aus WP0.4.
