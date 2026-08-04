# Rückverfolgung — Anforderung, Paket, Test, Artefakt

> **Generiert aus `traceability.json`.** Änderungen gehören in die Quelle, nicht hierher —
> `TraceabilityTests` färbt den Lauf rot, sobald beides auseinanderläuft.

Eine Zeile zählt nur, wenn der genannte Test wirklich existiert; ein Test in dieser Suite
prüft genau das. **`offen` ist ein zulässiger Status — eine weggelassene Anforderung nicht.**

**Anforderungen:** 72 · **Must:** 50 · **Must ohne "erfuellt":** 12

**12 Must-Anforderungen sind noch nicht erfüllt.** Solange das so steht, ist der Release Candidate keiner — die Zeilen stehen unten mit Grund.

- erfüllt 53
- teilweise 12
- **offen** 7

## Must

| Anforderung | Paket | Status | Test | Artefakt |
|---|---|---|---|---|
| **FR-P001** Jede veröffentlichte Gateway-Version wird als versioniertes OCI-Image für `linux/amd64`… | WP1.1 | teilweise | — | `.github/workflows/release.yml` |
| **FR-P002** Das Image wird mit unveränderlichem Digest, SBOM, Build-Provenance und überprüfbarer Si… | WP1.2 | teilweise | — | `.github/workflows/release.yml` |
| **FR-P003** Die Gateway-CLI wird als eigenständiges Artefakt für Windows, Linux und macOS auf den r… | WP1.3 | erfüllt | `GatewayCliTests` | `src/Bifrost.Cli` |
| **FR-P004** Checksums und Signaturen aller Binärartefakte sind Teil des Releases. | WP1.2 | teilweise | — | `.github/workflows/release.yml` |
| **FR-P005** Das Standard-Compose-Beispiel verwendet ein veröffentlichtes Image und löst keinen Sour… | WP1.4 | erfüllt | — | `docker-compose.yml` |
| **FR-P006** Source-Build und lokales Image-Build bleiben als expliziter Entwicklerpfad verfügbar. | WP1.4 | erfüllt | — | `README.md` |
| **FR-P007** Release-Kanal und Semantik sind eindeutig: Pre-Releases werden als solche markiert; `la… | WP1.5 | teilweise | — | `.github/workflows/release.yml` |
| **FR-P010** Der erste Start führt durch einen sicheren, einmaligen Bootstrap. | WP3.3 | erfüllt | `BootstrapAccessTests` | `src/Bifrost.Server/Bootstrap` |
| **FR-P011** Bootstrap-Credentials erscheinen nicht im normalen, dauerhaft gespeicherten Anwendungslog. | WP3.3 | erfüllt | `BootstrapAccessTests` | `src/Bifrost.Server/Bootstrap/BootstrapHandoverFile.cs` |
| **FR-P012** Ein geführter Setup-Prozess erstellt Admin, Betriebsmodus, ersten Upstream, erste Agent… | WP4.4 | erfüllt | `SetupWizardJourneyTests` | `src/Bifrost.Web/Components/Pages/SetupWizard.razor` |
| **FR-P013** Bestehende Konfigurationen aus Claude, Cursor, VS Code, Codex und generischem `mcp.json… | WP4.1 | erfüllt | `ProviderRecognitionTests` | `src/Bifrost.Core/Importing` |
| **FR-P014** Der Import zeigt Namenskollisionen, Secrets, Host-Ausführung und nicht unterstützte Fel… | WP4.2 | erfüllt | `ImportCliTests` | `src/Bifrost.Core/Importing` |
| **FR-P015** Importierte Secrets werden nie ungefragt im Klartext persistiert oder angezeigt. | WP4.2 | erfüllt | `SetupWizardLeakTests` | `src/Bifrost.Core/Importing` |
| **FR-P020** Der Betreiber kann ein vollständiges, versioniertes Backup erzeugen. | WP2.1 | erfüllt | `BackupCreationTests` | `src/Bifrost.Core/Backup` |
| **FR-P021** Das Backup umfasst Datenbank, Key-Ring, aktive Paketartefakte und die für eine Wiederhe… | WP2.1 | erfüllt | `BackupInspectionTests` | `src/Bifrost.Core/Backup` |
| **FR-P022** Backups mit Secrets können verschlüsselt und auf Integrität geprüft werden. | WP2.1 | erfüllt | `BackupInspectionTests` | `src/Bifrost.Core/Backup` |
| **FR-P023** Restore ist auf einer leeren Installation und auf einer bestehenden Instanz mit explizi… | WP2.2 | erfüllt | `RestoreTests` | `src/Bifrost.Persistence/Backup/RestoreService.cs` |
| **FR-P024** Vor schemaändernden Upgrades wird ein recoverbares Backup erzeugt oder dessen bewusste … | WP2.3 | erfüllt | `PostgresPreMigrationBackupTests` | `src/Bifrost.Persistence/DatabaseInitializer.cs` |
| **FR-P025** Parallele Instanzen dürfen nicht gleichzeitig dieselbe Migration ausführen. | WP2.3 | erfüllt | `MigrationSafetyTests` | `src/Bifrost.Persistence/DatabaseInitializer.cs` |
| **FR-P026** Unvollständige Migrationen werden beim Start erkannt und mit einem sicheren Recovery-Hi… | WP2.3 | erfüllt | `MigrationSafetyTests` | `src/Bifrost.Persistence/DatabaseInitializer.cs` |
| **FR-P027** Upgradepfade von mindestens drei Vorgängerversionen werden automatisiert geprüft. | WP2.6 | teilweise | `SqliteUpgradeMatrixTests` | `tests/Bifrost.Upgrade.Tests` |
| **FR-P030** Ein `doctor`-Befehl prüft Datenverzeichnis, Berechtigungen, Datenbank, Migrationen, Key… | WP2.4 | erfüllt | `DiagnosticServiceTests` | `src/Bifrost.Core/Diagnostics` |
| **FR-P031** Upstream-Diagnosen prüfen Prozessstart bzw. DNS/Netzwerk, SSRF-Entscheidung, Authentifi… | WP2.4 | erfüllt | `UpstreamTimelineTests` | `src/Bifrost.Core/Diagnostics` |
| **FR-P032** Diagnoseausgaben enthalten keine Secrets. | WP2.4 | erfüllt | `DiagnosticRedactionTests` | `src/Bifrost.Core/Diagnostics` |
| **FR-P033** Jeder Fehler enthält einen stabilen maschinenlesbaren Code, eine verständliche Erklärun… | WP2.4 | erfüllt | `DiagnosticCodeTests` | `src/Bifrost.Core/Diagnostics/DiagnosticCodes.cs` |
| **FR-P040** Native stdio-/CLI-Ausführung im Hostmodus kann global deaktiviert werden. | WP3.1 | erfüllt | `HostExecutionArchitectureTests` | `src/Bifrost.Core/Execution` |
| **FR-P041** Neu angelegte native Upstreams verwenden standardmäßig Container- oder WASI-Isolation, … | WP3.1 | erfüllt | `HostExecutionCreationPathTests` | `src/Bifrost.Core/Execution` |
| **FR-P042** Hostmodus verlangt eine explizite Risikobestätigung und wird in UI, API und Audit klar … | WP3.1 | erfüllt | `HostExecutionAdoptionTests` | `src/Bifrost.Core/Execution` |
| **FR-P043** Container-Upstreams erhalten standardmäßig Limits für CPU, RAM, Prozesse, Output, Zeit … | WP3.2 | erfüllt | `ContainerIsolationE2ETests` | `src/Bifrost.Upstream` |
| **FR-P044** Netzwerkzugriff von isolierten Upstreams ist default-deny und wird pro Upstream freigeg… | WP3.2 | erfüllt | `ContainerIsolationE2ETests` | `src/Bifrost.Upstream` |
| **FR-P045** Der DataProtection-Key-Ring kann beim Setup ohne manuelle Kryptografiearbeit geschützt … | WP3.4 | erfüllt | `KeyRingCommandTests` | `src/Bifrost.Server/KeyRing` |
| **FR-P046** Ein ungeschützter Key-Ring ist dauerhaft sichtbar und über Diagnose/APIs maschinenlesba… | WP3.4 | erfüllt | `KeyRingCheckTests` | `src/Bifrost.Core/Diagnostics/Checks` |
| **FR-P047** Security-Scans für Quellcode, Container, NuGet und Cargo sind Release-Gates. | WP3.5 | erfüllt | — | `.github/workflows/security.yml` |
| **FR-P050** Abgebrochene Test- oder Gateway-Prozesse hinterlassen keine verwalteten Upstream-Kindpr… | WP5.4 | erfüllt | `OrphanDetectionTests` | `tests/soak/Bifrost.Soak.Tests` |
| **FR-P051** Ein automatisierter Soak-Test betreibt den Gateway mindestens 72 Stunden mit wechselnde… | WP5.4 | **offen** | `SoakRunTests` | `artifacts/soak` |
| **FR-P052** Chaos-Szenarien umfassen Prozesskill, Upstream-Crash, langsame Antworten, Verbindungsab… | WP5.4 | erfüllt | `SoakRunTests` | `tests/soak/Bifrost.Soak.Tests/Harness/ChaosEngine.cs` |
| **FR-P053** Der Gateway kommt nach hartem Prozessabbruch mit konsistenter Datenbank und erklärbarem… | WP5.4 | erfüllt | `SoakRunTests` | `tests/soak/Bifrost.Soak.Tests` |
| **FR-P054** UI-Kernpfade werden mit Browser-E2E-Tests geprüft. | WP5.5 | erfüllt | `ApprovalJourneyTests` | `tests/Bifrost.Ui.E2E` |
| **FR-P055** Kritische Security-Invarianten werden als Property-/Architekturtests geschützt. | WP5.5 | erfüllt | `HostExecutionArchitectureTests` | `tests/Bifrost.Security.Tests` |
| **FR-P060** Eine veröffentlichte Matrix nennt getestete Clientversionen und unterstützte Funktionen. | WP5.1 | erfüllt | `MatrixSchemaTests` | `tests/compatibility/matrix.json` |
| **FR-P061** Mindestens Claude, Cursor, VS Code, Codex und MCP Inspector werden regelmäßig gegen ein… | WP5.1 | **offen** | `SdkClientCompatibilityTests` | `docs/acceptance/compatibility.md` |
| **FR-P062** OAuth wird gegen mindestens einen selbst betriebenen Referenzanbieter und einen realen … | WP5.2 | teilweise | `UpstreamOAuthAgainstKeycloakTests` | `tests/Bifrost.Integration.Tests/OAuth` |
| **FR-P063** Tests umfassen Tokenablauf, Refresh, Widerruf, Issuer-/Audience-Fehler und Resource Ind… | WP5.2 | teilweise | `UpstreamOAuthAgainstKeycloakTests` | `tests/Bifrost.Integration.Tests/OAuth` |
| **FR-P064** Netzwerkrealistische Tests laufen über Kestrel, TCP, TLS und einen Reverse Proxy. | WP5.3 | erfüllt | `ReverseProxyHopTests` | `docs/acceptance/performance.md` |
| **FR-P070** Health-, Metrik- und Trace-Namen sind versioniert und dokumentiert. | WP3.6 | teilweise | — | `docs/operations.md` |
| **FR-P071** Argumente, Ergebnisse und Secrets gelangen nie in Traces. | WP3.6 | erfüllt | `DiagnosticRedactionTests` | `src/Bifrost.Core/Audit` |
| **FR-P072** Auditdaten können in JSONL und CSV gestreamt exportiert werden, ohne den Arbeitsspeiche… | WP6.2 | erfüllt | `AuditExportScaleTests` | `docs/operations.md` |
| **FR-P090** Quickstart, Betrieb, Security und Troubleshooting stehen mindestens auf Deutsch und Eng… | WP0.2 | erfüllt | — | `docs/operations.md` |
| **FR-P091** Jede sicherheitsrelevante Option dokumentiert Default, Risiko und Empfehlung. | WP0.2 | teilweise | — | `docs/operations.md` |
| **FR-P092** CONTRIBUTING, Code of Conduct, Issue-/PR-Templates und lokale Verifikationsbefehle sind… | WP0.2 | **offen** | — | — |

## Should

| Anforderung | Paket | Status | Test | Artefakt |
|---|---|---|---|---|
| **FR-P008** Release Notes werden aus einem kuratierten Changelog erzeugt und nennen Breaking Change… | WP1.5 | teilweise | — | `CHANGELOG.md` |
| **FR-P016** Die UI bietet einen reduzierten Basis-Modus und einen vollständigen Experten-Modus. | WP4.5 | erfüllt | `UiNavigationModelTests` | `src/Bifrost.Web/UiNavigation.cs` |
| **FR-P017** Für verbreitete Einsatzzwecke existieren validierte Startvorlagen. | WP4.5 | **offen** | — | — |
| **FR-P028** Ein Konfigurationsexport ohne Secrets und ein verschlüsselter Vollständiger Export steh… | WP2.5 | erfüllt | `ConfigurationRoundtripTests` | `src/Bifrost.Core/Configuration` |
| **FR-P034** Ein Support-Bundle kann redigierte Konfiguration, Versionsinformationen, Healthstatus u… | WP5.6 | erfüllt | `SupportBundleTests` | `src/Bifrost.Core/Operations/SupportBundle` |
| **FR-P035** Die UI zeigt dieselben Diagnoseergebnisse wie die CLI und verwendet denselben Backenddi… | WP4.6 | erfüllt | `SupportBundleJourneyTests` | `src/Bifrost.Web/Components/Pages/Operations.razor` |
| **FR-P036** Readiness unterscheidet zwischen lokalem Prozesszustand und tatsächlich nutzbaren Kerna… | WP2.4 | teilweise | `DiagnosticServiceTests` | `src/Bifrost.Server/Program.cs` |
| **FR-P048** Secrets können über Datei-/Container-Secrets referenziert werden, ohne sie als Environm… | WP3.5 | erfüllt | `ConfigurationSecretExportTests` | `src/Bifrost.Core/Configuration` |
| **FR-P056** Testläufe liefern bei Hängern automatisch Sequenz, Dump und aktive Kindprozessliste. | WP5.4 | **offen** | — | — |
| **FR-P065** Kompatibilitätsabweichungen werden als bekannte Einschränkung mit Workaround dokumentiert. | WP5.1 | erfüllt | `MatrixValidatorNegativeTests` | `tests/compatibility/matrix.json` |
| **FR-P073** Audit-Einträge können optional durch eine Hash-Kette gegen unbemerkte lokale Manipulati… | WP6.2 | erfüllt | `AuditChainTests` | `src/Bifrost.Abstractions/AuditChain.cs` |
| **FR-P074** Signierte Webhooks können Approval- und Security-Ereignisse an externe Systeme melden. | WP6.3 | erfüllt | `ApprovalNotificationTests` | `src/Bifrost.Core/Approvals` |
| **FR-P075** Ein redigiertes Support-Bundle ist klar vom vollständigen Auditexport getrennt. | WP5.6 | erfüllt | `SupportBundleTests` | `docs/operations.md` |
| **FR-P080** Versionierte Policy Packs bündeln RBAC, Profile, Approval-Regeln, Rate-Limits und Guard… | WP6.1 | teilweise | `PolicyPackTests` | `src/Bifrost.Core/Policy` |
| **FR-P081** Vor Aktivierung eines Policy Packs wird ein Diff der Auswirkungen gezeigt. | WP6.1 | erfüllt | `PolicyPackApiTests` | `src/Bifrost.Core/Policy/RightsProjection.cs` |
| **FR-P082** Das Token-Cockpit zeigt Schema-Kosten pro Server und Profil sowie tatsächlich genutzte … | WP6.4 | erfüllt | `UsageCockpitTests` | `src/Bifrost.Core/Usage` |
| **FR-P083** Das System schlägt ungenutzte Pinnings oder überbreite Profile vor, nimmt aber keine au… | WP6.4 | erfüllt | `UsageCockpitApiTests` | `src/Bifrost.Core/Usage` |
| **FR-P084** Approval-Anfragen unterstützen Ablaufzeit, signierte Benachrichtigungen und Deep-Links. | WP6.3 | erfüllt | `ApprovalNotificationTests` | `src/Bifrost.Core/Approvals` |
| **FR-P086** Ein Connector-Entwickler kann Pakete initialisieren, bauen, signieren, verifizieren und… | WP6.5 | erfüllt | `ConnectorKitTests` | `docs/connectors/developer-kit.md` |
| **FR-P093** Architektur- und Connector-Beispiele sind klein, lauffähig und CI-geprüft. | WP4.7 | erfüllt | `ConnectorKitTests` | `samples/counter-connector` |
| **FR-P094** Das öffentliche Roadmap-Dokument unterscheidet zugesagt, geplant, experimentell und bew… | WP4.7 | **offen** | — | — |

## Could

| Anforderung | Paket | Status | Test | Artefakt |
|---|---|---|---|---|
| **FR-P085** Vier-Augen-Freigabe kann für ausgewählte Toolklassen verlangt werden. | WP6.3 | **offen** | — | — |

## Wo es nicht rund ist — und warum

- **FR-P001** (teilweise): Multi-Arch-Image gebaut und lokal geraucht; der Nachweis am veroeffentlichten Image steht aus (M1 ist nicht abgenommen).
- **FR-P002** (teilweise): Syft/Trivy/Cosign laufen containerisiert im Releaselauf. Ein Nachweis gegen ein wirklich veroeffentlichtes Image fehlt.
- **FR-P004** (teilweise): Signatur- und Checksummenschritte sind Teil des Releaselaufs; geprueft ist der Lauf, nicht das Ergebnis am Artefakt.
- **FR-P005** (erfuellt): Drei docker-compose-config-Laeufe gruen, Volume-Name nachgeprueft (Protokoll WP1.4).
- **FR-P007** (teilweise): Pre-Release-Semantik ist im Workflow abgebildet; ohne veroeffentlichte Kanaele nicht am Ergebnis geprueft.
- **FR-P008** (teilweise): Der Changelog wird kuratiert gefuehrt; die Erzeugung der Release Notes daraus ist nicht automatisiert.
- **FR-P011** (erfuellt): Das Token liegt in einer abgeschirmten Datei und nicht im Log; die Datei verschwindet nach dem Einloesen.
- **FR-P012** (erfuellt): Browserweg gegen eine Instanz ohne jeden UI-Zugang, Token aus der Uebergabedatei.
- **FR-P017** (offen): Validierte Startvorlagen fuer verbreitete Einsatzzwecke gibt es nicht. Nicht gebaut, nicht geplant.
- **FR-P026** (erfuellt): BFR-DB-0101: Der Start verweigert den Schreibbetrieb bei offenem Migrationseintrag.
- **FR-P027** (teilweise): Die Matrix faehrt gegen selbst erzeugte Fixtures. Drei wirklich veroeffentlichte Vorgaengerversionen gibt es nicht, weil M1 nicht abgenommen ist.
- **FR-P036** (teilweise): Health und Readiness sind getrennt; die Unterscheidung "lokal bereit" gegen "Upstreams erreichbar" ist nicht eigens getestet.
- **FR-P051** (offen): Der Harness traegt einen 72-Stunden-Lauf; gefahren sind sieben Minuten. Der Vertrag verweist den Langlauf auf M7/M8.
- **FR-P054** (erfuellt): 18 Browserwege gegen eine veroeffentlichte Instanz ueber TLS.
- **FR-P056** (offen): Bei einem Haenger liefert der Lauf keine Sequenz, keinen Dump und keine aktiven Vorgaenge. Nicht gebaut.
- **FR-P061** (offen): Geprueft ist der SDK-Client. Claude, Cursor, VS Code, Codex und MCP Inspector stehen in der Matrix als untested — sichtbar, aber ungeprueft.
- **FR-P062** (teilweise): Der selbst betriebene Referenzanbieter (Keycloak) ist belegt. Der externe Anbieter fehlt; er braucht fremde Zugangsdaten.
- **FR-P063** (teilweise): Ablauf und Refresh samt Rotation sind belegt. Widerruf, Issuer-/Audience-Fehler und Consent sind nicht eigens gefahren.
- **FR-P064** (erfuellt): Kestrel, TCP, TLS und ein echter nginx davor: rund 1,6 ms Anteil an der p95.
- **FR-P070** (teilweise): Metrik- und Tracenamen sind dokumentiert; eine Versionierung der Namen als eigener Vertrag fehlt.
- **FR-P072** (erfuellt): 1 MB gehaltener Speicher bei 100 000 Zeilen, 0 MB bei 1 000 000.
- **FR-P080** (teilweise): Packs buendeln Rollen und Profile. Approval-Regeln und Rate-Limits reisen als Teil derselben Nutzlast mit, sind aber nicht eigens belegt.
- **FR-P081** (erfuellt): Der Diff nennt jede Identitaet, die gewinnt, samt Wirkungsebene — nicht nur die geaenderte Rolle.
- **FR-P083** (erfuellt): Das Cockpit schlaegt vor und aendert nichts; das ist als Test festgehalten.
- **FR-P085** (offen): Vier-Augen-Freigabe gibt es nicht. Einzige Could-Anforderung; nicht gebaut, nicht geplant.
- **FR-P086** (erfuellt): Die dokumentierte Befehlsfolge ist der erste Test.
- **FR-P090** (erfuellt): Deutsch vollstaendig, englischer Spiegel fuer Betrieb vorhanden.
- **FR-P091** (teilweise): Die sicherheitsrelevanten Schalter sind mit Risiko und Empfehlung dokumentiert; eine vollstaendige Gegenpruefung Option fuer Option steht aus.
- **FR-P092** (offen): CONTRIBUTING, Code of Conduct und Issue-/PR-Templates fehlen im Repository.
- **FR-P093** (erfuellt): Das Beispiel ist klein, lauffaehig und laeuft im Testlauf mit.
- **FR-P094** (offen): Ein oeffentliches Roadmap-Dokument mit der Unterscheidung zugesagt/geplant/experimentell gibt es nicht.

