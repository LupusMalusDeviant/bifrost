# Externer Security-Review — Auftragsunterlage (M7/WP7.2)

> **Dieses Dokument ist die Einladung, nicht der Review.** Es beschreibt, was geprüft werden soll,
> woran, womit — und was der Prüfer von uns bekommt. Der Review selbst braucht einen beauftragten
> externen Prüfer; solange es den nicht gibt, steht WP7.2 auf `nicht startbar`.

## 1. Bezugscommit

Der Review läuft gegen **einen** Stand, nicht gegen einen Branch. Der Stand wird bei der
Beauftragung eingetragen und danach nicht mehr bewegt:

| | |
|---|---|
| Commit | *(bei Beauftragung eintragen)* |
| Datum | *(bei Beauftragung eintragen)* |
| Bauanleitung | `dotnet build Bifrost.slnx`, .NET 10 SDK; Rust stable für `spikes/wasi-component-runtime` |
| Testlauf | `dotnet test Bifrost.slnx --settings .runsettings` |

**Warum ein Commit und kein Branch:** Ein Review, dessen Gegenstand sich während der Prüfung
ändert, prüft am Ende etwas, das niemand mehr hat — und jeder Befund braucht danach die Rückfrage,
ob er noch gilt.

## 2. Scope — wörtlich aus dem Pflichtenheft

| Bereich | Wo es liegt | Was dort die Zusage ist |
|---|---|---|
| **Auth** | `src/Bifrost.Server/AuthEndpoints.cs`, `src/Bifrost.Core/Rbac/**` | API-Keys, UI-Sitzungen, OAuth als Resource-Server; Rollen und Grants entscheiden jeden Aufruf |
| **Invoker-Invarianten** | `src/Bifrost.Core/Invocation/**` | Keine Toolausführung außerhalb `IToolInvoker`; Guardrails, Freigaben und Rate-Limits sitzen auf genau diesem Weg |
| **SSRF** | `src/Bifrost.Upstream/**`, `src/Bifrost.Server/Operations/SsrfGuardingHandler.cs` | Jedes ausgehende Ziel geht durch dieselbe Prüfung; private Netze nur mit ausdrücklichem Schalter |
| **Bootstrap** | `src/Bifrost.Server/Bootstrap/**` | Einmaliges Setup-Token, in einer abgeschirmten Datei statt im Log, verfällt und verschwindet |
| **Backupcrypto** | `src/Bifrost.Core/Backup/**`, `src/Bifrost.Persistence/Backup/**` | Verschlüsselung und Integritätsprüfung des Archivs; der Key-Ring ist Teil davon |
| **Package/WASI** | `src/Bifrost.Core/Packaging/**`, `spikes/wasi-component-runtime` | Ed25519-Signatur über Manifest und Nutzlast, Herausgeber-Trust-Store, Sandbox |
| **Containerlaunch** | `src/Bifrost.Upstream/**` (Isolation) | Vorgabe ist Container; Host-Ausführung nur mit ausdrücklicher Erlaubnis |
| **Releaseworkflow** | `.github/workflows/release.yml` | Signatur, SBOM, Provenance, Checksummen — **ungeprüft am Ergebnis**, siehe §4 |

## 3. Was der Prüfer bekommt

- **Die Verträge**: `docs/plans/m1`…`m7-*.md`. Sie sagen nicht nur, was gebaut wurde, sondern was
  ausdrücklich **nicht** passiert ist und warum.
- **Die Invarianten**: jeweils §„Unverletzbare Invarianten" der Verträge, fortgeschrieben von M2 bis M7.
- **Die Finding-Liste**: `docs/acceptance/findings.md` — **inklusive der offenen Punkte.** Ein
  Review, dem man die bekannten Schwächen verschweigt, prüft die falsche Hälfte.
- **Die Rückverfolgung**: `docs/acceptance/traceability.md`, mit den Anforderungen, die *nicht*
  erfüllt sind.
- **Das Fortschrittsprotokoll**: `docs/plans/product-readiness-status.md`. Dort steht auch, was
  schiefging.
- **Die ADRs**: `docs/adr/**` — jede Entscheidung mit ihren Konsequenzen.

## 4. Was wir selbst für die schwächste Stelle halten

Diese Liste gehört in die Auftragsunterlage, weil ein Prüfer sonst Zeit damit verbringt, sie zu
finden:

1. **Der Releaseworkflow ist nie gegen ein veröffentlichtes Artefakt geprüft worden.** M1 gilt seit
   drei Meilensteinen als „implementiert, nicht abgenommen". Zweimal hat jemand genau hingesehen und
   jedes Mal etwas gefunden (BFR-F-0036, BFR-F-0042). Die Wahrscheinlichkeit, dass dort noch etwas
   liegt, ist die höchste im ganzen Bestand.
2. **Der Maskierungslauf ist ein Netz, kein Inhaltsscanner** (BFR-F-0005). Ein Secret unter einem
   harmlosen Feldnamen und ohne erkennbare Form bleibt stehen. Das ist die gewollte Bauweise — die
   Frage an den Review ist, ob sie an allen Ausgängen gleich gut hält.
3. **Die Integritätskette des Audits schützt nicht gegen einen übernommenen Wirt** und nicht gegen
   das Entfernen der letzten Einträge eines Abschnitts. Beides steht in ihrer eigenen Ausgabe; die
   Frage ist, ob die Grenze dort richtig gezogen ist.
4. **`BIFROST_TRUSTED_PROXIES=any`** ist ein zulässiger Betriebsmodus. Steht der Gateway zusätzlich
   direkt im Netz, kann jeder Client `X-Forwarded-Proto: https` behaupten.
5. **Host-Ausführung** (`BIFROST_ALLOW_HOST_EXECUTION`) gibt einem Upstream die Rechte des
   Dienstbenutzers — einschließlich der API-Keys aller Agenten. Der Schalter ist bewusst da; die
   Frage ist, ob der Weg dorthin ausreichend im Weg steht.

## 5. Was der Prüfer nicht bekommt

**Keine Zugangsdaten.** Auch nicht für eine Testinstanz. Wer eine Instanz zum Anfassen braucht,
bekommt die Anleitung, sie selbst aufzusetzen — der Weg dorthin ist Teil dessen, was geprüft wird.

## 6. Wie Befunde zurückkommen

- **In dieselbe Liste**: `docs/acceptance/findings.json`, mit Schweregrad nach demselben Schema.
  Critical und High blockieren den Release Candidate; das ist keine Verhandlungssache, sondern ein
  Test (`FindingsTests.No_open_critical_or_high_finding_blocks_the_release_candidate`).
- **Mit Regressionstest**: Jeder behobene Befund bekommt einen Test, der ohne die Korrektur rot
  wird. Ein Fix ohne Gegenprobe ist eine Behauptung.
- **Re-Review** der Critical-/High-Korrekturen, gegen einen neuen Bezugscommit.
- **Veröffentlichung**: Befunde werden öffentlich oder verantwortungsvoll zusammengefasst — die
  Entscheidung trifft der Produktverantwortliche, nicht der Prüfer und nicht wir.
