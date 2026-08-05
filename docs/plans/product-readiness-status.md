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

**M1 ist am 2026-08-01 abgenommen.** `v0.12.0` ist veröffentlicht: Image unter
`ghcr.io/lupusmalusdeviant/bifrost:0.12.0`, 13 Release-Anhänge (fünf CLI-Archive, sechs SBOMs,
Prüfsummen, Signatur-Bündel), keyless signiert, sechs Attestationen, Trivy-Gates auf Image und
CLI-Artefakten grün. Der Signatur-Selbsttest lief in **beide** Richtungen — der Negativtest verlangt
ausdrücklich, dass Falsches durchfällt; ein Verifikationsschritt, der nur bestätigt, bestätigt auch
Unsinn.

### Was der erste Lauf gekostet hat: neun Befunde

Drei Trockenläufe und drei Tag-Läufe. Keiner dieser Punkte wäre ohne echten Lauf sichtbar geworden.

| # | Wo | Art |
|---|---|---|
| 1 | Push-Protection | erfundener Slack-Token im Negativkorpus, nicht aufgeteilt — Push blockiert |
| 2 | Secret-Gate | Baseline kannte zwei Werte aus WP3.3 nicht |
| 3 | Versionstest | Literal `"0.11.0"`, brach beim Versionssprung |
| 4 | **Bootstrap** | **Produktfehler:** bei zwei gleichzeitigen Einlösungen verloren beide |
| 5 | **WP0.4-Nachweis** | funktionierte unter Linux **noch nie** — 15-Zeichen-Grenze von `/proc/*/comm` |
| 6 | WP0.4-Nachweis, zweiter Anlauf | Namenszählung auch danach unzuverlässig |
| 7 | `supply-chain` | im Trockenlauf **immer** rot — der Trockenlaufmodus war selbst nie gelaufen |
| 8 | `release` | `dist/*` erfasste Unterverzeichnisse; Release angelegt, Anhänge fehlten |
| 9 | Backup-Test | Nebenläufigkeitsnachweis hing am Planer, nicht am Produkt |

Der schärfste ist **Nummer 5**. Ausgerechnet das Paket, dessen ganzer Sinn „Nachweis statt
Behauptung" war, hat auf einer ganzen Plattform nichts geprüft und trotzdem grün gemeldet. Er galt
als belegt, weil er nur unter Windows gelaufen war — seit seiner Entstehung war nichts gepusht
worden.

**Nummer 8** ist die unangenehmste Sorte: Das Release wurde angelegt, die Anhänge fehlten — genau
der Zustand, den der Kommentar über diesem Job vermeiden will („eine Zusage ohne Deckung"). Beim
Beheben sind zwei Torposten dazugekommen, die es vorher nicht gab: leere Dateiliste bricht ab,
doppelte Basisnamen brechen ab (`gh` hängt unter dem Basisnamen an — zwei gleichnamige Dateien
überschrieben einander lautlos).

**Der Satz „implementiert, aber nicht abgenommen" stand seit drei Meilensteinen im Protokoll und
klang nach Formalie. Er war keine.**

### Der ursprüngliche Vermerk (Stand bis 2026-07-31)

Alle fünf Pakete sind implementiert und lokal geprüft, aber **kein
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
2. ~~**Das Key-Ring-Passwort lässt sich nicht als Datei-Secret zuführen**~~ — **erledigt in WP3.3.**
   Der Befund stand hier seit M1: `Program.cs` las nur die Umgebungsvariable, das PFX ging als
   Compose-Secret, das Passwort landete in `.env` und damit in `docker inspect`. FR-P048 war
   dadurch nur halb erfüllt.

   Gelöst über `…_FILE` als **allgemeine Regel**, nicht als Einzelfall. Gegen `AddKeyPerFile`
   sprach, dass es ein ganzes Verzeichnis als Konfigurationsquelle einhängt — eine zweite, anders
   geformte Oberfläche neben dem dokumentierten `BIFROST_*`-Vertrag, die alles liest, was jemand
   hineinlegt. Sind Wert und `…_FILE` beide gesetzt, bricht der Start ab: Eine Rangfolge zwischen
   zwei Quellen desselben Secrets wäre eine Regel, die man im Zweifel falsch erinnert.
   `docker-compose.yml` führt den zweiten Secret-Block jetzt.

### WP5.5 abgeschlossen: die fünf offenen Wege — und zwei Produktfehler, die dabei auffielen

Gefahren wird gegen eine **veröffentlichte** Instanz über TLS, mit dem ausgelieferten
Anmeldeformular. Kein Weg mogelt sich an der Anmeldung vorbei (M5-Vertrag §8).

| Weg | Was er belegt |
|---|---|
| **Freigabe** (2 Wege) | Ein scharfgestelltes Werkzeug wird abgewiesen, erscheint in der Warteschlange, läuft **erst nach dem Klick** — und eine Ablehnung lässt es abgewiesen. |
| **Rolle/Profil** | Rolle, Identität, Profil und Schlüssel über die Formulare; danach fragt ein echter Agent mit dem echten Schlüssel, und **das Profil bestimmt, was er sieht**. |
| **Backup/Restore** (2 Wege) | Die Sicherung entsteht, das Archiv wird geöffnet und sein Manifest gelesen. Und: Die Wiederherstellung wird dort **nicht angeboten** — geprüft über alle Schaltflächen der Seite, nicht über eine Vermutung. |
| **Tool-Pin** | Ein Upstream ändert die Beschreibung seines Werkzeugs; es ist danach **weder sichtbar noch aufrufbar**, bis jemand die neue Fassung annimmt. |
| **Erstsetup** (2 Wege) | Eine Instanz **ohne jeden UI-Zugang**, das Setup-Token aus der Übergabedatei, Zugang über das Formular — und ein falsches Token legt keinen an. |

**Der wichtigste Satz zur Bauweise:** Jeder dieser Wege prüft die **Wirkung**, nicht die Anzeige.
Beim Profil ist der Beleg die Werkzeugliste eines echten Agenten, beim Backup das geöffnete Archiv,
beim Tool-Pin die Abwesenheit des Werkzeugs, beim Erstsetup eine geschützte Seite ohne zweite
Anmeldung. Eine Seite, die „erledigt" meldet, ist kein Beleg dafür, dass etwas erledigt ist.

**Produktfehler 1 — Argumente verschwanden bei chunked-Übertragung.** Der Invoke-Endpunkt las den
Rumpf nur bei gesetztem `Content-Length`. Bei `Transfer-Encoding: chunked` ist der `null` — und
damit verlor **jeder** Client, der die Länge nicht vorab berechnet, seine Argumente **still**;
darunter `HttpClient.PostAsJsonAsync` von .NET selbst. Der Aufruf lief mit leeren Argumenten weiter
und scheiterte an der Schemaprüfung mit „Pflichtfeld fehlt" — ein Fehler, der auf die falsche Stelle
zeigt. Der bestehende Integrationstest hieß `Rest_invoke_roundtrip_works_like_curl` und tat genau
das: Er setzte die Länge, wie curl es tut. Behoben, mit einem eigenen Gegentest ohne
`Content-Length`.

**Produktfehler 2 — der Browser-Harness lief gegen eine alte Veröffentlichung.** Er übersprang das
Publish, wenn die Executable schon existierte. Gefunden über WP6.2, hier noch einmal bestätigt:
Der Harness vergleicht jetzt den Zeitstempel der Veröffentlichung gegen den jüngsten Quellstand.

**Drei Funde in den eigenen Wegen** (keine Produktfehler, aber Fallen für den nächsten):

1. **Der Wizard legt seine Sitzung erst nach dem ersten Rendern an** — er liest die Kennung aus
   einem Cookie und braucht dafür einen Browser. Ein Klick davor läuft ins Leere; der Wizard sagt
   dann „Bitte eine Betriebsart wählen", und der Weg scheitert drei Schritte später an einem
   Formular, das nie erschienen war. Der Weg wartet jetzt, bis die Wahl **angekommen** ist.
2. **Die Übergabedatei ist ein Zettel für Menschen**, kein nacktes Token: Frist, Anleitung, Hinweis
   auf das Löschen. Wer sie im Ganzen einreicht, bekommt eine Absage — und die sieht aus wie ein
   abgelehntes Token.
3. **Der EchoServer konnte seine Definition nicht ändern.** Ohne einen Upstream, der das wirklich
   tut, ließe sich der Rug-Pull-Schutz nur an Attrappen zeigen — und eine Attrappe belegt nicht,
   dass der Vergleich im Betrieb anschlägt. Er hat jetzt eine zweite Fassung desselben Werkzeugs,
   umschaltbar über die Umgebung.

**Was dafür an der Oberfläche dazukam:** Test-Hooks (`data-testid`) auf Rollen-, Profil-, Freigabe-
und Sicherungsformularen. Zwei Felder binden jetzt auf `oninput` statt auf das Vorgabe-Ereignis —
sonst gilt ein eingetippter Pfad erst beim Verlassen des Feldes, und der Knopf daneben bleibt
gesperrt, obwohl etwas dasteht.

### Der Reverse-Proxy-Sprung: 1,6 ms, gemessen statt geschätzt

Ein **echter nginx in einem Container** vor dem Gateway, mit genau der Konfiguration, die
`docs/operations.md` empfiehlt. Ein vorgeschalteter Handler im selben Prozess wäre kein Sprung: Es
ginge kein Paket über eine zweite Verbindung, und genau die ist die Frage.

| Weg | Gateway + Netz p50 | p95 |
|---|---|---|
| direkt | 1,12 ms | 1,62 ms |
| über nginx | 2,70 ms | 3,54 ms |

**Gemessen wird die Differenz, nicht der absolute Wert.** Derselbe Lastlauf geht einmal direkt und
einmal durch den Proxy; die Fremdlatenz des Upstreams (~30 ms) steckt in beiden Zahlen und ist um
eine Größenordnung größer als das, was gesucht wird. Aus demselben Grund prüft der Test **keine
feste Millisekundenschranke** — auf geteilter Hardware wäre das eine Wette. Geprüft wird die
Größenordnung und dass kein einziger Aufruf scheitert.

**Kein neues Tor.** Der Lauf hängt an `BIFROST_RUN_BENCHMARK`, das der CI-Job `benchmark` ohnehin
setzt; fehlt die Container-Runtime, wird mit Begründung übersprungen — außer
`BIFROST_REQUIRE_CONTAINER` ist gesetzt, dann ist ein Skip ein Ausfall des Nachweises.

**Ein Fund am Messstand, kein Produktfehler:** Die Zuordnung einer Datei in einen Container zielt
auf ein **Verzeichnis**, nicht auf einen Dateinamen. Die erste Fassung legte die Konfiguration unter
einem Zufallsnamen ab, und nginx suchte an der richtigen Stelle vergeblich.

### Eine falsche Zeile in der eigenen Rückverfolgung

**FR-P092 stand auf `offen` mit der Begründung, CONTRIBUTING, Code of Conduct und die Vorlagen
fehlten im Repository.** Sie liegen unter `.github/` und sind seit Langem da — gesucht wurde in der
Wurzel. Die Zeile war also nicht zu streng, sondern **falsch**, und eine falsche Zeile in einer
Rückverfolgung ist genau das, was sie verhindern soll.

Aufgefallen ist es beim Versuch, die Dateien zu schreiben. Der Fund steht als BFR-F-0028 in der
Liste, damit die Lehre nicht verschwindet: **Eine Zuordnung aus dem Gedächtnis ist keine Prüfung.**
Die übrigen sechs offenen Zeilen habe ich daraufhin gegengeprüft — für Roadmap (FR-P094) und
Startvorlagen (FR-P017) gibt es wirklich nichts.

**Und ein zweiter Fund im selben Zug:** Die Tor-Tabelle im Beitragsleitfaden nannte **drei von neun
Toren**. Die sechs aus M5 und M6 — Browserwege, Lastmessung, Soak, Kompatibilität,
Großmengen-Auditexport, Keycloak — fehlten. Wer das Repository auscheckt, liest keinen Workflow:
Er fährt diese Nachweise nie und hält einen übersprungenen Test für einen bestandenen. Die Tabelle
ist vervollständigt, und ein fünfter Wächter hält sie gegen den Testcode.

Damit steht **FR-P092** auf `erfüllt`. Offene Must-Anforderungen: **10** statt 11.

### FR-P056: Was ein hängender Lauf hinterlässt

Bis jetzt endete ein Hänger im Job-Timeout, und es blieb nichts übrig, woran man ihn hätte
festmachen können. Drei Dinge überleben ihn jetzt:

| Was | Wer schreibt es | Wo |
|---|---|---|
| **Sequenz** — welche Tests liefen, und welcher noch lief | die Testplattform (`--blame-hang`) | `Sequence.xml` |
| **Dump** — vollständiges Speicherabbild des Testhosts | die Testplattform | `*.dmp` |
| **Aktive Kindprozesse** — Gateway-Instanzen und Testserver mit Id und Startzeit | dieses Repository (`HangWatchdog`) | `artifacts/hang/hang-*.txt` |

**Der dritte ist der Grund für die Arbeit.** Ein Lauf hängt hier selten an sich selbst, sondern an
einer Gegenstelle, die nicht mehr antwortet — und der Speicherabzug sagt nicht, welche das war.

**Die Reihenfolge der beiden Fristen ist die eigentliche Entscheidung.** Der eigene Wächter
schreibt nach **10 Minuten**, die Plattform zieht den Abzug nach **15**. Andersherum wäre die
Kindprozessliste wertlos: `--blame-hang` beendet den Testhost, und wer danach sucht, findet
entweder nichts mehr oder Waisen ohne Bezug.

Die Frist muss zugleich über dem längsten *legitimen* Test liegen — das ist der Chaoslauf mit rund
sieben Minuten. Ein Test hält das fest, damit niemand sie später unter diesen Wert dreht und der
Wächter anfängt, Berichte über gesunde Läufe zu schreiben.

**Geprüft ohne echten Hänger.** Einen zu erzwingen hieße, die Suite zehn Minuten mutwillig zu
blockieren. Geprüft wird stattdessen das, was dann geschrieben würde — mit einem **echten**
Kindprozess, der wirklich läuft und im Bericht mit Id auftaucht. Dazu die Gegenprobe: Ohne
Kindprozess sagt der Bericht das ausdrücklich, statt zu schweigen — der Unterschied entscheidet,
wo man als Nächstes sucht.

**Der Wächter meldet sich selbst an.** Ein Modulinitialisierer in der geteilten Datei; wer sie
einbindet, hat ihn. Vier Projekte starten echte Prozesse, und vier Stellen zum Anmelden wären vier
Stellen, an denen es beim fünften vergessen wird.

Damit steht **FR-P056** auf `erfüllt`.

### Die Finding-Liste gilt jetzt ab M0 — der Vorbehalt ist weg

BFR-F-0026 ist geschlossen. **16 Befunde aus M0 bis M3** sind aus dem Protokoll übernommen
(BFR-F-0030 bis BFR-F-0045), die Abdeckungsangabe steht auf `M0`. Damit sagt der RC-Wächter nicht
mehr „keine offenen Critical/High ab M4", sondern **ohne Zusatz**.

Die Übernahme hat nichts Neues aufgedeckt — alle sechzehn waren dokumentiert und behoben, bis auf
drei, die als `offen` mit Owner, Termin und Mitigation eingetragen sind:

| Id | Befund | Wartet auf |
|---|---|---|
| ~~BFR-F-0039~~ | `docker compose up -d` zieht ein Image, das es nicht gibt | **behoben am 2026-08-05** |
| BFR-F-0044 | wasmtime 47.0.2: zwei LOW-Meldungen in der WASI-Sandbox | nächsten Abhängigkeitslauf |
| ~~BFR-F-0045~~ | das Containerfilesystem-Gate hat keine Gegenprobe | **behoben am 2026-08-05** |

**Zwei von dreien warteten auf dasselbe** — ein veröffentlichtes Release. Beim Aufschreiben ist die
Wartezeit nicht nachgerechnet worden: **M1 ist seit dem 2026-08-01 abgenommen**, `v0.12.0` ist
veröffentlicht. Beide Befunde waren am Tag ihrer Übernahme bereits erledigt und standen nur deshalb
auf `offen`, weil die Zeile aus dem Protokoll übernommen wurde statt gegen den Ist-Stand gehalten.
Nachgeholt am 2026-08-05, siehe unten.

### Der Fehlschlag ohne Namen hat jetzt einen Mechanismus statt einer Gewohnheit

BFR-F-0025 lautete: Ein Solution-Lauf meldete einen Fehlschlag, und der Name ging verloren, weil
die Ausgabe durch einen Filter lief. Die Mitigation hieß „künftig die volle Ausgabe aufheben" —
eine Absicht.

`.runsettings` schreibt jetzt **je Assembly eine `trx`** nach `TestResults/`. Wer filtert, filtert
nur noch den Bildschirm; die Datei bleibt. Ausdrücklich **ohne** festen `LogFileName` — genau daran
ist der Skip-Wächter der CI einmal gescheitert (BFR-F-0011), weil jede Assembly dieselbe Datei
überschrieb.

Der ursprüngliche Fehlschlag bleibt unaufgeklärt. Wiederholt er sich, ist der Name da.

### WP7.2 und WP7.3: vorbereitet, nicht durchgeführt

Beide Pakete sind Beschaffungen. Was hier entstehen konnte, ist die Unterlage, mit der sie
beauftragt werden — und beide sagen als Erstes, was sie **nicht** sind.

**Der Review-Scope** nennt Bezugscommit (bei Beauftragung einzutragen), die acht Bereiche aus dem
Pflichtenheft mit ihrer jeweiligen Zusage, und die Unterlagen, die der Prüfer bekommt — **inklusive
der offenen Befunde.** Ein Review, dem man die bekannten Schwächen verschweigt, prüft die falsche
Hälfte. Dazu ein eigener Abschnitt: *Was wir selbst für die schwächste Stelle halten* — fünf
Punkte, angeführt vom nie geprüften Releaseworkflow. Ein Prüfer soll seine Zeit nicht damit
verbringen, das zu finden, was wir ohnehin wissen.

**Die Pilotcheckliste** verlangt Backup **und** Restore, nicht nur das Erste — und der Restore gilt
erst als bewiesen, wenn auf der zweiten Installation etwas läuft, nicht wenn er fehlerfrei endet.
Zurückgemeldet wird von Hand: Telemetrie gibt es nicht, und der opt-in-Metrikpfad ist eine
Produktentscheidung, kein Nebenprodukt dieses Pakets.

### WP7.2 und WP7.3, zweiter Anlauf: was sich doch fahren ließ

Die Einschätzung „nicht startbar" stimmte für **beide Pakete als Ganzes** und stimmt weiter. Sie
stimmte aber nicht für das, was ein veröffentlichtes Release möglich macht — und dass es eines gibt,
war bei der ersten Einschätzung nicht mitgedacht.

**WP7.3 — Pilot 1 von 3, gefahren.** Eine echte Installation aus `ghcr.io/lupusmalusdeviant/bifrost:0.12.0`,
in einem frischen Verzeichnis, über genau den dokumentierten Weg. Erstaufbau, Erstzugang, API-Key,
`doctor`, Backup mit Passphrase, Prüfung, Prüfung mit falscher Passphrase, Restore auf eine zweite
Installation. Protokoll: [`pilot-1-protokoll.md`](../acceptance/pilot-1-protokoll.md).

Der Restore ist **nicht** über „Exit 0" abgenommen worden, sondern über die Frage, ob danach etwas
läuft: Der API-Key aus Pilot 1 authentifiziert gegen Pilot 2, der eigene Schlüssel von Pilot 2 ist
tot. Genau dabei fiel der schärfste Befund an.

**Zehn Befunde, einer davon High.** [BFR-F-0047](../acceptance/findings.md): `bifrost restore`
meldet Erfolg, und der laufende Gateway bedient bis zum Neustart weiter den überschriebenen Stand —
einschließlich des Wegwerf-Zugangs, den man nur angelegt hat, um den Restore aufrufen zu können.
Der RC-Wächter stand damit rot; das ist seine Aufgabe.

**BFR-F-0047 ist am selben Tag behoben.** Der Gateway verriegelt sich nach einem eingespielten
Restore selbst: `RestartRequiredMiddleware` läuft **vor** der Authentifizierung und beantwortet jede
Anfrage mit `503` und `restart-required` — auch `/healthz`, denn ein Container, der „healthy" meldet
und nichts mehr beantwortet, ist genau dasselbe Auseinanderfallen noch einmal. Der Neustart löst den
Riegel; einen Befehl dafür gibt es bewusst nicht.

**Die weiße Seite und die Anleitung, die `null` sagte.**

`BFR-F-0053`: Eine unbekannte Adresse antwortete mit `404` und null Bytes. Der Statuscode war
richtig, der Browser zeigte nichts. Behoben über `UseStatusCodePages` — ganz außen im Pipeline-Bau
und ausschließlich dort wirksam, wo niemand sonst geschrieben hat. Das ist die eigentliche
Anforderung: Eine Absage mit eigener Begründung — der API-Key-Wächter, der Riegel aus `BFR-F-0047` —
darf nicht durch eine Allerweltsformel ersetzt werden. Dafür gibt es einen eigenen Test, der genau
das prüft; ohne ihn wäre die Korrektur eine Verschlechterung mit Fehlerseite.

Beim Schreiben des Tests fiel nebenbei auf, dass ein unbekannter `/api`-Pfad **ohne** Schlüssel
`401` bekommt und nicht `404` — der API-Key-Wächter greift vor dem Routing. Das ist richtig so und
bleibt: Einem unangemeldeten Aufrufer zu verraten, welche API-Pfade existieren, wäre eine Auskunft
ohne Anlass. Die Erwartung im Test war falsch, nicht das Produkt.

`BFR-F-0055`: Der in der Prüfanleitung dokumentierte Kurzblick auf das Zertifikat lieferte unter
cosign 3 `Repo: null`, und der Schritt danach verlangte genau diesen Wert. Jetzt kommen Repository,
Tag und Commit aus der signierten SLSA-Herkunftsbescheinigung, mit dem Weg ohne `gh` als Gegenprobe.
**Beide Befehle sind gegen `v0.12.0` gefahren worden, bevor sie in der Anleitung standen** — dieselbe
Regel, an der der alte Aufruf gescheitert ist.

**Für 0055 gibt es keinen Test, und das steht so da.** Ein Wächter dafür bräuchte cosign, `gh` und
ein veröffentlichtes Artefakt in der Suite — also genau die Art Tor, das in der CI als Erstes
übersprungen wird. Stattdessen eine stehende Anweisung im Prüfprotokoll: Vor jedem Release wird die
Anleitung einmal gefahren, Ergebnis daneben abgelegt. Der Fehler hat vier Monate überlebt, weil
niemand sie gefahren hat; was ihn künftig findet, ist derselbe Handgriff, der ihn gefunden hat.

**Zwei weitere Pilotbefunde sind mit behoben** — und der erste hat unterwegs einen dritten
freigelegt.

`BFR-F-0048` sah nach einem Schönheitsfehler aus: angekündigt `…-051215.zip`, geschrieben
`…-051216.zip`, weil Plan und Anwendung den Zeitstempel jeweils selbst bildeten. Beim Beheben stellte
sich heraus, dass die Ursache breiter war. Der Plan geht als JSON hinaus und kommt als **neues
Objekt** zurück; das Handle wies nur nach, *dass* ein Plan bestand, nicht *was* darin stand. Die
Anwendung entschied aber anhand der zurückgereichten Felder, ob die Vorsicherung entsteht — ein
Aufrufer mit `targetIsEmpty: true` hätte sie abbestellt und damit überschrieben, ohne den Ausweg,
den ADR-0024 E5 zur Bedingung macht (`BFR-F-0057`). Modus, Leere, Anwendbarkeit und Pfad stehen jetzt
im serverseitigen Kontext; es reist wirklich nur noch das Handle, so wie der Nachtrag zum M2-Vertrag
es immer beschrieben hat.

Der Regressionstest läuft mit **gestellter Uhr**. Mit der echten wäre er meistens grün und
gelegentlich rot — also wertlos: Der Befund hängt an einer Sekundengrenze zwischen zwei Aufrufen.

`BFR-F-0050` und `BFR-F-0051` sind derselbe Absatz in `.env.example`. Die Datei behauptete, das
Passwort des Key-Rings könne nicht aus einer Datei kommen; `_FILE` gibt es seit FR-P048, und sowohl
die Compose-Datei als auch `bifrost doctor` sagen es. Eine falsche Zeile in genau der Datei, die
jeder Betreiber kopiert — und sie drängte zum Klartextpasswort. Die verlangte Entscheidung
`BIFROST_KEYRING_PROTECTION` fehlte dort ganz.

Dagegen steht jetzt kein besserer Text, sondern ein Wächter, und er ist **allgemein** statt auf
diesen Namen gemünzt: Jede geheimnistragende Einstellung in der Beispieldatei muss ihre `_FILE`-Form
zeigen (`ExampleConfigurationTests`). Alle vier Korrekturen sind einzeln zurückgedreht worden; jede
macht ihren eigenen Wächter rot.

**Was ausdrücklich nicht gewählt wurde:** den Prozesszustand im laufenden Betrieb neu aufzubauen.
Das wäre eine Zusage über jeden Zwischenspeicher, jede offene Upstream-Verbindung und jede Sitzung
gewesen — nicht prüfbar, nur behauptbar, und beim nächsten hinzugefügten Zwischenspeicher lautlos
falsch. Der Riegel ist eine Zusage über *eine* Sache, und sie ist fail-closed.

Gegenprobe: Ohne die eine Middleware-Zeile fällt
`RestoreRestartLatchTests.A_restore_locks_the_gateway_until_it_is_restarted` mit `200` statt `503`.
Der Test hat eine **eigene Fixture** — ein eingerasteter Riegel gilt für den ganzen Prozess und
hätte sonst jeden nach ihm laufenden Test derselben Klasse mitgenommen, ohne dass in einem
Fehlschlag stünde, woran es liegt.

Einer der zehn ist mein eigener: Die Pilotcheckliste vom Vortag verlangte `bifrost audit export` und
`bifrost audit verify` — Befehle aus M6, die in der einzigen veröffentlichten Fassung nicht
existieren. Gegen das Repository geschrieben statt gegen das, was ein Pilot bekommt.

**Was der Pilot nicht zeigen konnte:** Upgrade und Rollback. Es gibt genau **ein** installierbares
Image; `0.11.0` existiert als GitHub-Release, aber nie als Image (BFR-F-0046). Der dokumentierte
Rollbackweg hat heute kein Ziel.

**WP7.2 — der Releaseworkflow ist am Ergebnis geprüft.** Die Betreiber-Anleitung
`docs/security/verifying-releases.md` ist erstmals gegen ein echtes Release gefahren worden, mit
ihren eigenen Negativproben: `cosign verify`, `verify-blob`, `gh attestation verify` (Image und
CLI-Archiv), Prüfsumme, Commit-Abgleich — alles grün; Probe A und Probe B lehnen ab. Dazu die
fehlende Gegenprobe für das Schwachstellen-Gate: derselbe Trivy-Aufruf wie im Workflow endet gegen
das Release-Image mit 0 und gegen `debian:11-slim` mit 1. Protokoll:
[`release-verification-0.12.0.md`](../acceptance/release-verification-0.12.0.md).

Damit sind **BFR-F-0039 und BFR-F-0045 erledigt** und ein neuer Befund entstanden: Der in §2.1 der
Anleitung dokumentierte Kurzblick auf das Zertifikat liefert unter cosign 3 nur `null` — und §3
Schritt 4 verlangt genau diesen Wert (BFR-F-0055).

**Was damit ausdrücklich nicht erledigt ist.** WP7.2 bleibt offen: sieben der acht Scope-Bereiche
sind unangetastet, und die eine Prüfung stammt von derselben Seite, die den Workflow geschrieben
hat. Ein Selbsttest ist kein Review. WP7.3 bleibt offen: eine von drei Installationen, kein
PostgreSQL, kein echter Upstream, kein echter Client, kein Alltag — und der Betreiber war der
Entwickler.

### Was von M5 offen bleibt — und warum es hier nicht zu schließen ist

| Rest | Warum offen |
|---|---|
| Externer OAuth-Anbieter (5.2) | Braucht Zugangsdaten eines echten Anbieters. Gegen Keycloak ist der Weg belegt; ein zweiter Anbieter ist eine Beschaffung, keine Programmierarbeit. |
| ~~Reverse-Proxy-Sprung (5.3)~~ | **Erledigt am 2026-08-04.** nginx als Container vor dem Gateway, dieselbe Last einmal direkt und einmal durch den Proxy: **1,6 ms Anteil an der p95**, keine Fehler. Siehe [performance.md](../acceptance/performance.md). |
| 72-Stunden-Soak (5.4) | Braucht 72 Stunden. Der Vertrag verweist ihn selbst auf M7. |
| Reale Clients in der Matrix (5.1) | Braucht installierte Fremdsoftware (Claude Desktop, Cursor, …). Die Matrix führt sie als `untested` — sichtbar, nicht weggelassen. |
| Linux-/Kleine-VM-Läufe | Eine Frage der CI-Matrix und der Hardware, nicht des Codes. |

## Laufender Meilenstein: M6 — Differenzierung

Vertrag: [m6-differentiation-contract.md](m6-differentiation-contract.md). **Kein 1.0-Blocker** —
kollidiert ein Paket hier mit einem M5-Rest, gewinnt der M5-Rest.

| WP | Status | Nachweis | Nächster Schritt |
|---|---|---|---|
| 6.1 Versionierte Policy Packs | `implementiert` | 37 Tests grün (10 Pack, 8 Importweg, 4 Wertetypen, 6 API, 8 CLI, 1 Browserweg) — darunter der **Rückweg am laufenden Gateway** und ein Test, der eine naive Rücknahme rot färbt | Abnahme durch fremde Augen |
| 6.2 Auditexport und Integritätskette | `implementiert` | 46 Tests grün (26 Kern, 5 Persistenz — je auf SQLite **und** PostgreSQL gefahren, 5 API, 6 CLI, 2 Browserwege, 2 Grossmengen-Nachweise): **1 MB bei 100 000 Zeilen, 0 MB bei 1 000 000** | Abnahme durch fremde Augen |
| 6.3 Approval-Benachrichtigungen | `implementiert` | 8 Kerntests grün; Zustellung nachweislich vom Entscheidungsweg getrennt | SMTP, Eskalation, Diagnosecheck |
| 6.4 Nutzungsbasiertes Token-Cockpit | `implementiert` | 13 Tests (10 Kern, 3 API) grün; Simulation lässt das Profil nachweislich unberührt | Abnahme durch fremde Augen |
| 6.5 Connector Developer Kit | `implementiert` | 10 Tests grün; die dokumentierte Befehlsfolge **ist** der erste Test | Abnahme durch fremde Augen |

### WP6.1 — der Unterschied zwischen „Rolle geändert" und „drei Agenten dürfen mehr"

Ein Policy Pack ist hier eine **Konfigurationsnutzlast mit Namen und Signatur** — kein zweiter
Importweg. Die Objektebene beantwortet der vorhandene Weg aus M2 (Plan, Konflikte, Handle), die
Vertrauensfrage der vorhandene Publisher-Trust-Store aus ADR-0016, und signiert wird mit demselben
Ed25519-Verifizierer wie ein Connector-Paket.

**Neu ist genau eine Sache: der Rechte-Diff.** Ein Diff auf Objektebene sagt „Rolle `entwickler`
hat sich geändert". Das liest sich harmlos und kann bedeuten, dass drei Agenten ab sofort auf jedem
Server schreiben dürfen. Der Plan nennt deshalb **jede Identität**, die gewinnt, samt Wirkungsebene
— und die Wegnahmen daneben, weil ein Pack auch unbemerkt Rechte entziehen kann.

**Ein Befund aus dem Bau:** Der Konfigurationsexport enthält Rollen, aber **keine Identitäten**. Ein
Pack bringt also Rollen mit, keine Menschen — die Zuordnung bleibt lokal. Der Diff verrechnet
folglich die Rollen des Packs mit der hiesigen Zuordnung, und Rollen werden über den **Namen**
zugeordnet, nicht über die Id: Ein Pack ist zum Weitergeben gedacht, und Guids sind auf jeder
Instanz andere.

**Belegt** (10 Kerntests): Signatur über Manifest **und** Nutzlast (ein getauschtes Manifest bricht
sie), entzogener Herausgeber trägt nicht mehr, ein Pack aus einer neueren Generation wird nicht
teilweise angewendet, der Diff nennt beide betroffenen Identitäten und keine unbeteiligte, eine vom
Pack ausgelassene Rolle erscheint als Wegnahme, ein globaler Grant steht als „alles" da statt als
leere Zelle.

**Die Adapter, zweiter Schritt:** Der REST-Weg steht — `POST /api/v1/policy/plan` und `…/apply`,
admin-only, die Übernahme auditiert mit Pack-Kennung, Version und Nutzlast-Fingerabdruck. Vier
Integrationstests am laufenden Gateway halten fest, was zählt: Der Plan nennt die gewinnende
Identität namentlich, **und die Rolle in der Datenbank ist danach unverändert** — planen ändert
nichts. Dazu: ein Pack von einem unbekannten Herausgeber wird abgewiesen, eine Übernahme ohne Handle
ebenso, und ein Nicht-Administrator sieht keinen Plan (er nennt fremde Identitäten samt Rechten).

**Ein Fund im eigenen Test:** Der erste Lauf scheiterte daran, dass mein Test seine *eigene* Antwort
nicht lesen konnte — der Server schreibt Enums als Text, die Testoptionen kannten den Konverter
nicht. Das sah aus wie ein Fehler im Endpunkt und war einer im Test.

### Der Rollback-Nachweis fiel negativ aus — zweimal, und wurde dann aufgelöst

Der Auftrag war, den Rückweg zu belegen. Belegt war zunächst das Gegenteil, und zwar an zwei
Stellen. Der Befund steht hier unverändert; was daraus wurde, steht darunter.

**1. Ein Policy Pack kann keine bestehende Rolle ändern.** Der Importweg aus M2 **legt nur an**; eine
Rolle, deren Name schon vergeben ist, ist dort ein Konflikt. Das ist dort richtig und ausdrücklich
begründet — die Rücknahme dieses Weges entfernt genau das, was er Sekunden zuvor angelegt hat, und
darf deshalb nichts Bestehendes anfassen. Für Policy Packs heißt es aber: **Der interessante Fall —
dieselbe Rolle, andere Rechte — ist heute nicht anwendbar.** `CanApply` ist dann `false`, mit
benanntem Konflikt.

**Mein eigener Test hat das zuerst nicht bemerkt.** Er prüfte den Rechte-Diff und das Handle, nie
die Anwendbarkeit. Jetzt steht beides da.

**2. Die Momentaufnahme vor der Übernahme ist kein Rollback.** Spielt man sie über denselben
Importweg wieder ein, bleibt eine neu angelegte Rolle **bestehen** — weil der Import nichts entfernt.
Der Test hält genau das fest. Das Feld heißt deshalb nicht mehr `RollbackPayload`, sondern
`SnapshotBeforeApply`, mit der Grenze in der Beschreibung.

**Was daraus folgt — eine Produktentscheidung, keine Nacharbeit.** „Atomare Aktivierung und
Rollback" (Pflichtenheft WP6.1) verlangt ein Importverfahren, das **aktualisieren und entfernen**
kann, mitsamt Kompensation für beides. Das ist ein Eingriff in den M2-Vertrag und gehört vor die
Umsetzung, nicht hinein. Drei Wege stehen offen: den Importweg um Update/Delete erweitern (und seine
Rücknahme neu begründen), Policy Packs auf „nur neue Objekte" beschränken (dann ist die Hälfte des
Nutzens weg), oder Rollback über Backup/Restore lösen (Wartungsmoment nötig, ADR-0024 E5).

WP6.1 stand deshalb auf **`blockiert`** statt `teilweise`: Was gebaut war, trug; was fehlte, war
keine Fleißarbeit, sondern eine Entscheidung.

### Die Entscheidung: der Importweg kann jetzt ersetzen — und begründet seine Rücknahme neu

Gewählt wurde der erste der drei Wege. Der Importweg hat einen **Modus** bekommen:
`AddOnly` bleibt die Vorgabe und verhält sich unverändert, `Update` ersetzt bestehende **Rollen und
Profile**. Ausdrücklich nur diese beiden: Upstreams, Guard-Regeln, Freigaben und Skills bleiben
additiv. Ein Modus, der alles ersetzt, wäre eine Synchronisierung — und die nimmt auch weg, was die
Nutzlast nur nicht kennt.

**Die alte Begründung der Rücknahme fällt weg, und das ist der Kern.** Sie lautete: „Jede Rücknahme
entfernt genau das, was der Vorgang Sekunden zuvor angelegt hat, und kann nichts treffen, was vorher
da war." Sobald ersetzt wird, stimmt das nicht mehr. Die neue Begründung ist eine andere Sache und
kein Zusatz zur alten: **Der Vorgang schreibt den vorigen Inhalt mit** und gibt ihn als
`ConfigurationImportUndo` zurück — den vorigen Stand der ersetzten Objekte und die Kennungen der
angelegten. Zurückgenommen wird daraus einzeln: Ersetztes wird **zurückgeschrieben**, Angelegtes
**entfernt**. Wer die alte Rücknahme unverändert übernommen hätte, würde eine ersetzte Rolle löschen
— und damit jeder Identität, die an ihr hängt, sämtliche Rechte entziehen. Genau dafür gibt es einen
Test, der eine solche Umsetzung rot färbt
(`ConfigurationImportUpdateTests.Taking_back_an_import_restores_a_replaced_role_instead_of_deleting_it`).

**Die Id der Zielinstanz gewinnt.** Ein Pack bringt eigene Guids mit; übernähme man sie, hinge jede
Identität an einer Rolle, die es unter ihrer alten Id nicht mehr gibt — die Zuordnung
Identität→Rolle läuft über die Id, nicht über den Namen. Der Inhalt kommt aus dem Pack, die Id bleibt
hiesig.

**Der Rollback-Nachweis fällt jetzt positiv aus** — am laufenden Gateway, über `POST
/api/v1/policy/revert`: Eine bestehende Rolle wird durch ein Pack auf „alles" erweitert, danach
zurückgenommen, und sie steht wieder mit ihrem engen Recht **und derselben Id** da
(`PolicyPackApiTests.The_undo_from_applying_really_takes_a_widened_role_back`). Die Momentaufnahme
bleibt daneben stehen und bleibt ausdrücklich **kein** Rollback — der Test, der das belegt, bleibt
ebenfalls stehen.

**Ein Halbwahrheit im Schreib-Port, nebenbei behoben:** Die Operation hieß `AddRoleAsync` und saß
unter der Haube längst auf einem Upsert — sie ersetzte also, obwohl der Name das Gegenteil sagte.
Die Test-Attrappe hängte dagegen wirklich nur an. Ein Test hätte damit „ersetzt" gemeldet, wo im
Betrieb zwei Rollen desselben Namens entstanden wären. Beide heißen jetzt `SaveRoleAsync` /
`SaveProfileAsync`, und die Attrappe verhält sich wie die echte Ablage.

### Ein Produktfehler, gefunden über den Rückweg: `NamespacedToolName` überlebte kein JSON

Der Ende-zu-Ende-Test des Rückwegs schlug fehl, obwohl der Kern grün war. Ursache war keine
Kleinigkeit im Test:

**System.Text.Json nimmt bei Structs den impliziten parameterlosen Konstruktor.** `NamespacedToolName`
und `CapabilityId` sind `readonly record struct`s mit einer `Value`-Eigenschaft **ohne Setter** —
beim Lesen blieb der Wert also `null`. Ohne Ausnahme, ohne Meldung. Positionale `record struct`s wie
`ServerId` sind nicht betroffen; deshalb fiel es nie auf.

**Die Wirkung ist eine Rechteausweitung.** Ein Grant mit `Scope.Tool = "github__read"` wird beim
Lesen zu einem Grant **ohne** Werkzeugeinschränkung. Aus „darf genau dieses eine Werkzeug" wird
„darf alles" — die Einschränkung verschwindet lautlos in die falsche Richtung. Betroffen ist jeder
JSON-Weg, der eine Berechtigung trägt, nicht nur der neue Rückweg.

Behoben mit `[JsonConstructor]` an beiden Konstruktoren (das Drahtformat bleibt unverändert), belegt
in `ValueTypeJsonTests` — vier Tests, darunter einer, der den Schaden benennt statt nur den nackten
Wertetyp zu prüfen, und einer, der festhält, dass positionale Wertetypen nie betroffen waren.

### Die Adapter, dritter Schritt: CLI und Oberfläche

**CLI** — `bifrost policy plan|apply|revert`. Zwei Dinge sind darin nicht verhandelbar und deshalb
je durch einen Test festgenagelt:

- **`--yes` überspringt die Rückfrage, nicht die Anzeige.** Der Rechte-Diff steht immer da, jede
  zusätzliche Berechtigung einzeln mit Identität und Wirkungsebene. Die Rückfrage nennt ihre
  **Zahl** — „Wirklich fortfahren?" ohne diese Zahl ist eine Frage, auf die jeder ja sagt.
- **Ohne `--undo-out` steht dort eine Warnung**, keine Beruhigung: Der Rückweg lebt nur in der
  Antwort und wird nirgends aufbewahrt.

Belegt in `OperationsCliTests` (8 Tests). Der schärfste prüft nicht den Exit-Code, sondern dass
**die zweite Anfrage nie hinausgeht**: Ohne Bestätigung wird nichts geschrieben, und eine Prüfung
nur auf den Rückgabewert hätte einen Apply-Aufruf übersehen.

**Oberfläche** — Seite `/policy`, Admin, in der Feinsteuerung neben Rollen und Profilen. Der
Rechte-Diff steht **ganz oben**, vor allen Objektlisten: Wer nur den Anfang liest, soll die
Rechteausweitung sehen und nicht die Herausgeberbeschreibung. Der Übernehmen-Knopf bleibt gesperrt,
bis jemand ausdrücklich bestätigt, den Zuwachs gesehen zu haben — ohne diese Sperre wäre die
Anzeige Dekoration.

Belegt durch einen Browserweg (`PolicyJourneyTests`, gated über `BIFROST_RUN_E2E=1`), der den ganzen
Bogen fährt: Rolle und Identität anlegen, Herausgeber pinnen, Pack einfügen, prüfen — die Identität
steht namentlich mit „alles" auf der Seite —, Knopf gesperrt, bestätigen, übernehmen, **in der
Datenbank nachsehen**, zurücknehmen, **wieder nachsehen**. Der Beweis kommt aus der Ablage, nicht
aus der Anzeige.

**Ein Fund dabei:** Die Verwaltungs-API gibt Rollen als **Datenbankzeile** zurück, nicht in der
Vertragsform — die Grants stehen als eingebettetes JSON in PascalCase darin. Mein erster Testentwurf
las die Vertragsform und war rot. Der Test liest jetzt, was wirklich da steht, und sagt im Kommentar
warum.

**Offen und benannt — zwei Grenzen, absichtlich gezogen:**

1. **Der allgemeine Konfigurationsimport über die API bleibt `AddOnly`.** Der Modus ist dort nicht
   erreichbar. Ihn zu öffnen hieße, `POST /config/import/apply` müsste den Rückweg
   zurückgeben statt `204` — eine Änderung an einer bestehenden Schnittstelle, die zu dieser
   Entscheidung nicht gehört. Der Motor kann es; der Adapter gibt es nicht frei.
2. **Es gibt kein „Delete" im Sinne eines Abgleichs.** Ein Import entfernt nichts, was in der
   Nutzlast fehlt — das wäre eine Synchronisierung, und die nimmt weg, was die Nutzlast nur nicht
   kennt. Entfernt wird ausschließlich im Rückweg, und dort nur, was **dieser** Vorgang angelegt hat.

Der Rückweg wird als Teil der Übernahme-Antwort zurückgegeben und muss vom Aufrufer aufbewahrt
werden — `--undo-out` in der CLI, das Textfeld auf der Seite. **Eine Ablage der Rückwege im Gateway
gibt es nicht**; das wäre eine eigene Entscheidung (Aufbewahrungsfrist, Sichtbarkeit, Verfall) und
steht aus. Beide Adapter sagen das an der Stelle, an der es zählt, statt es zu verschweigen.

### Ein unaufgeklärter Fehlschlag, festgehalten statt weggewischt

Ein Solution-Lauf meldete **einen** Fehlschlag in `Bifrost.Integration.Tests` (443 statt 444). Zwei
weitere volle Läufe danach und ein Lauf der Suite allein waren grün. **Welcher Test es war, ist
nicht bekannt** — mein eigener Ausgabefilter hatte die Zeile mit dem Namen verworfen, und ohne
Namen gibt es keine Diagnose, nur eine Vermutung (Assemblies laufen im Solution-Lauf parallel; die
Integrationstests teilen sich Ports und Dateien).

Das steht hier, weil „lief beim zweiten Mal durch" keine Aussage über die Ursache ist. Wer den
nächsten Lauf fährt, sollte die volle Ausgabe aufheben — genau das war hier der Fehler.

### WP6.2 — der Export streamt, und die Kette sagt, was sie nicht kann

Der Blocker aus dem Vertrag (§3: „wartet, bis die Persistenz-Welle gelandet ist") ist aufgelöst —
die Verlustsichtbarkeit liegt im Baum und ist über `BFR-DB-0007` verdrahtet; am Audit-Schreibpfad
arbeitet nichts mehr parallel.

**Der Cursor ist eine Zeilennummer, keine Seitenzahl.** `Skip(n).Take(m)` setzt voraus, dass sich
die Menge zwischen zwei Seiten nicht ändert. Beim Audit ändert sie sich immer — nebenher wird
geschrieben. Der Test dazu schreibt deshalb *zwischen* den beiden Abrufen und prüft danach beides:
keine Lücke und keine Dopplung.

**Gelesen wird in Blöcken, nicht mit einem offenen Leser über alles.** Der Speicherbedarf wäre so
oder so beschränkt; entscheidend ist die Sperre. Ein Leser, der über eine Million Zeilen offen
bleibt, hält auf SQLite eine Lesetransaktion — und die steht dem Audit-Schreiber im Weg, der nach
ADR-0007 nie warten soll.

**Der Nachweis aus dem Vertrag, mit zwei Größen statt einer.** Ein einzelner Millionen-Lauf, der
durchgeht, belegt nur, dass der Rechner groß genug war; „nicht linear" ist eine Aussage über das
Verhältnis. Gemessen wird der **gehaltene** Speicher (mit erzwungener Sammlung), nicht die Summe
aller Zuweisungen: **1 MB bei 100 000 Zeilen, 0 MB bei 1 000 000**. Der erste Entwurf maß ohne
erzwungene Sammlung und kam auf 12 MB gegen 49 MB — eine Zahl, die nur zeigt, wann der GC zuletzt
lief, und die gegen eine Schranke von 52 MB in der CI geflattert hätte.

### Die Kette: ein Abschnitt je Schreiberlauf, und der Anspruch steht in der Ausgabe

Verkettet wird über eine kanonische Fassung — feste Feldfolge, invariantes Zahlen- und Zeitformat,
ein eigener Marker für „Feld fehlt" gegenüber „Feld ist leer". Ohne diese Unterscheidung hätten
zwei verschiedene Ereignisse denselben Hash, und genau darauf zielte ein Fälscher.

**Zwei Prüfungen, und beide werden gebraucht:** Der Eintrag wird gegen seinen eigenen gespeicherten
Vorgängerhash nachgerechnet — das findet ein geändertes Feld. Und der gespeicherte Vorgängerhash
wird gegen den tatsächlichen Vorgänger gehalten — das findet eine entfernte oder umsortierte Zeile.
Nur die erste Prüfung wäre eine Prüfsumme je Zeile; wer eine Zeile löscht, käme damit durch. Belegt
ist beides **gegen die echte Ablage**: Ein Test ändert eine Zeile direkt in der Datenbank, ein
zweiter löscht eine.

**Ein Abschnitt je Schreiberlauf, nicht eine globale Kette.** Eine durchgehende Kette setzt genau
einen Schreiber voraus. Zwei Instanzen auf derselben Datenbank — oder ein Neustart — verschränkten
ihre Ketten, und der Prüfer meldete Manipulation, wo keine war. **Ein falscher Alarm über
Manipulation ist schlimmer als keine Kette:** Er entwertet den echten Fund. Der Preis steht
daneben: Dass ein ganzer Abschnitt fehlt, ist von hier aus nicht erkennbar.

Der Prüfer trägt seine Grenzen **in der Antwort**, nicht nur in der Dokumentation (Vertragspflicht
aus §3) — und ein Test hält das fest. Er nimmt außerdem **keine Inhaltsfilter** an: Über einen
gefilterten Ausschnitt lässt sich über die Kette nichts sagen, jede herausgefilterte Zeile sähe aus
wie eine gelöschte.

Zeilen aus der Zeit vor der Kette tragen keine Glieder. Sie werden gezählt und als „nicht gedeckt"
ausgewiesen. **Nachträglich verkettet wird nichts** — ein Hash, den die Instanz sich selbst
nachträglich ausrechnet, belegt nichts.

### Eine Vertragsabweichung, benannt statt stillschweigend

Der M6-Vertrag §3 verortet die Kanonisierung in `Bifrost.Core/Audit/**`. Sie liegt jetzt in
`Bifrost.Abstractions`. Grund: Der Schreibpfad liegt in der Persistenz, der Prüfer im Kern — beide
brauchen dieselbe Rechnung. Sie im Kern zu lassen hieße, die Persistenz auf den Kern zeigen zu
lassen, also eine neue Kante im Abhängigkeitsgraphen für eine einzige statische Klasse. Der Vertrag
sagt selbst, die Kanonisierung sei Teil des Vertrags; dann steht sie auch in der Vertragsassembly.
Der Verifizierer bleibt im Kern, wie vorgesehen.

### Drei Funde aus dem Bau

**1. Zeitstempel liegen als UTC-Ticks in der Ablage, nicht als Text.** Meine Grossmengen-Fixture
schrieb sie roh als ISO-8601 und bekam Jahr 1 zurück — die Kette meldete prompt „nach dem Schreiben
geändert". Der Fehler lag in der Fixture, nicht im Produkt; aber er zeigt, dass die Prüfung
anschlägt.

**2. `?format=jsonl` wurde mit 400 abgewiesen.** Die Standardbindung von Enums an Query-Parameter
ist case-sensitiv. Das ist ein Produktfehler, kein Testproblem, und ist behoben — Gross- und
Kleinschreibung ist in einer URL keine Aussage.

**3. Der Browser-Harness lief gegen eine alte Veröffentlichung.** Er übersprang das Publish, weil
die Executable schon existierte — vom vorigen Paket. Der neue Weg war rot, und die Ursache zeigte
auf die Oberfläche statt auf den Harness. **Schlimmer wäre der umgekehrte Fall gewesen:** ein
grüner Lauf gegen einen alten Stand. Der Harness vergleicht jetzt den Zeitstempel der
Veröffentlichung gegen den jüngsten Quellstand unter `src/`.

Nebenbei: Der Zielpfad im Export-Formular bindet auf `oninput` statt auf das Vorgabe-Ereignis —
sonst gilt der Pfad erst, wenn das Feld den Fokus verliert, und der Knopf daneben bleibt gesperrt,
obwohl etwas dasteht.

### WP6.3 — der wichtigste Test ist der, in dem nichts zugestellt wird

Der Zusteller hängt am **vorhandenen** signierten Webhook (ADR-0013): Dieselbe HMAC-Berechnung, mit
der der Gateway eingehende Rufe prüft, signiert jetzt die ausgehenden. Ein Empfänger verifiziert
also mit der Beschreibung, die er ohnehin hat — und der Test prüft die Signatur mit genau dem
Verifizierer des Produkts, statt mit einem nachgebauten.

Angeschlossen wird über einen **Mantel** um `IApprovalStore`, nicht über eine Änderung daran: Eine
Benachrichtigung ist eine Nebenwirkung, die niemand braucht, damit eine Freigabe gilt. Wer sie in
den Speicher schriebe, hätte zwei Aufgaben in einer Klasse — und der Tag, an dem ein Zustellfehler
eine Freigabe verhindert, wäre nur eine Frage der Zeit.

**Die DoD als Test:** Das Ziel wirft bei jedem Versuch, und die Freigabe entsteht trotzdem sofort.
Ohne diesen Fall wäre „Zustellung getrennt von Entscheidung" eine Absicht.

**Drei Entscheidungen:**

- **Die Nutzlast trägt keine Argumente** — auch keine redigierten. Sie geht an ein fremdes System,
  oft in einen Chat; was dort steht, steht dauerhaft dort. Mit geht ein Deep-Link, und wer ihn
  öffnet, steht vor der Anmeldung.
- **Ein totes Ziel wird sichtbar**, nicht still: Nach dem letzten Versuch steht der Vorgang als
  Dead-Letter in der API und in der Oberfläche neben der Warteschlange.
- **Eine abgelaufene Anfrage wird nicht zugestellt** — wer sie liest, kann nichts mehr entscheiden.
  Der Vorgang bleibt trotzdem mit Grund stehen.

**Ein Fund im eigenen Test:** Der Rückversuch wartet auf der *übergebenen* Uhr. Das ist im Betrieb
richtig und machte den ersten Testlauf blind — er sah aus, als würde gar nicht wiederholt, weil die
gestellte Uhr nicht weiterlief. Der Test stellt sie jetzt selbst vor.

**Der Befund, den ein fremder Wächter fand:** `SsrfCentralFetcherTests` wurde rot — der Zusteller
baute sich einen eigenen Client und rief die vom Betreiber genannte Adresse **ungeprüft**. Genau die
Stelle, an der ein Gateway zum Werkzeug wird, um interne Dienste zu erreichen (Cloud-Metadaten,
Admin-Port auf Loopback, Nachbar im Firmennetz). Die naheliegende Ausrede — „die Adresse kommt aus
der Konfiguration" — ist die, die dieser Test nicht akzeptiert, und er hat recht: Eine Adresse wird
einmal gesetzt und danach von niemandem mehr angesehen. Derselbe Test hatte dieselbe Lücke laut
seiner Ausnahmeliste schon einmal bei der OAuth-Sonde gefunden.

Behoben wurde sie auf dem produktüblichen Weg statt mit einem Eintrag in der Ausnahmeliste: Ein
`SsrfGuardingHandler` schickt jedes Ziel durch dieselbe zentrale Prüfung wie ein Schemaimport, und
`BIFROST_APPROVAL_WEBHOOK_ALLOW_PRIVATE` ist der ausdrückliche Schalter für ein Ziel im eigenen Netz
— dieselbe Bauform wie bei OAuth-Upstreams. Nebenwirkung: Weiterleitungen werden nicht mehr verfolgt;
ein 302 hätte Signatur und Deep-Link an eine nie geprüfte Adresse getragen.

**Offen und benannt:** SMTP (im Pflichtenheft optional), Eskalation nach Ablauf, und ein
Diagnosecheck für tote Ziele — Letzterer würde `DiagnosticCodes.cs` anfassen, das die Persistenz-Welle
offen hat (M6-Vertrag §7).

### WP6.4 — die Zahl, die nichts bedeutet, wäre die Summe

Das Cockpit stellt zwei Größen nebeneinander, die man leicht verrechnet: **Exposure** ist, was ein
Profil einem Agenten an Schema zumutet — jede Anfrage, unabhängig von der Nutzung. **Invocation**
ist, was davon im Fenster wirklich aufgerufen wurde. Die Grundlage lag bereits:
`CatalogEntry.EstimatedSchemaTokens` trägt seit FR-15 den Kommentar „für das Cockpit".

**Es ändert nichts** — und das ist als Test festgehalten, nicht als Vorsatz: Nach einer Simulation
steht das Profil am laufenden Gateway unverändert da.

**Drei Entscheidungen, die im Code stehen:**

- **„ungenutzt im Fenster", nie „unnötig".** Ein Werkzeug ohne Aufruf kann das sein, was einmal im
  Quartal den Betrieb rettet. Die Oberfläche schreibt den Zeitbezug in dieselbe Zelle.
- **Die Konsequenz steht neben der Einsparung.** Die Simulation nennt nicht nur die gesparten
  Tokens, sondern die entfernten Werkzeuge, **die benutzt wurden** — ein Cockpit, das nur die
  Ersparnis zeigt, ist ein Verkaufsprospekt. Das ist die DoD dieses Pakets.
- **Grenzen vor der Rechnung.** Auditseiten, Listenlänge und Fensterlänge sind gedeckelt, und jede
  gezogene Grenze steht im Ergebnis: Ein abgeschnittener Auditlauf meldet seine Zahlen ausdrücklich
  als Untergrenzen.

Zwei Fälle, die sonst still verschwänden: ein Werkzeug, das über den Lazy-Pfad benutzt wird, ohne im
Profil zu stehen (sonst sähe ein sparsames Profil aus, als arbeite niemand) — und eines, das es
nicht mehr gibt, aber im Fenster benutzt wurde (sonst sähe eine Instanz nach einem Upstream-Verlust
aus wie eine, in der nie etwas benutzt wurde).

**Der Befund: die Seite war nicht erreichbar.** Nach dreizehn grünen eigenen Tests fiel im Lauf über
alle Suiten `UiNavigationModelTests.Every_authorized_page_appears_in_the_navigation` um — die
Cockpit-Seite existierte, war autorisiert, hatte eine Route und stand in keinem Menü. Inhaltlich
dieselbe Klasse wie der Blazor-Ausfall aus WP5.5: gebaut und kommt trotzdem nicht an. Derselbe
Wächter hat dann gleich die Einordnung korrigiert — ein `Parent` zeigt in dieser Liste auf einen
Abschnitt **derselben** Seite, nicht auf eine eigene.

Das ist der **dritte** vorhandene Wächter in dieser Sitzung, der eigene Arbeit gefangen hat (nach
dem Redaktionskorpus und dem Skip-Gate). Alle drei stammen von jemand anderem. Die Regel aus §1 des
M6-Vertrags — „eine Fertigmeldung zählt die Suite, nicht die eigenen Tests" — hat sich damit zum
dritten Mal als die richtige erwiesen, und zum zweiten Mal daran, dass sie beinahe wieder gebrochen
worden wäre.

### WP6.5 — drei eigene Fehler, alle vom eigenen Lauf gefunden

Das Kit ist ein eigenes Werkzeug (`bifrost-connector`), kein Befehl in `bifrost`: Die Gateway-CLI
hält bewusst keine Projektreferenz, das Kit braucht `Bifrost.Core` — beides in einer Datei hieße,
eine der beiden Entscheidungen aufzugeben. Der Vertrag ist entsprechend berichtigt.

**Drei Befunde aus dem eigenen Bau, alle behoben:**

1. **Der erste Ende-zu-Ende-Lauf scheiterte an der DoD selbst.** Ein WASI-Paket deklariert eine
   abgetrennte Signatur der Component; mein `build` überließ sie dem Entwickler, und die Probe
   lehnte zu Recht ab. Genau diese Reibung soll das Kit beseitigen — wer den Schlüssel hat, kann
   auch die Component signieren. `build` erzeugt sie jetzt mit.
2. **Meine „Probe" prüfte zweimal dasselbe.** Schema und Vertragsversion prüft der Leser des
   Produkts längst; meine Zusatzprüfung konnte nie greifen — toter Code, der Gründlichkeit
   vortäuscht. Er ist weg. Was `test` jetzt wirklich hinzufügt: die **abgetrennte Signatur des
   Einstiegspunkts**, die der Paketleser nicht prüft und die sonst erst der WASI-Host auf dem
   Rechner des Betreibers ablehnt.
3. **Ein vertippter Schalter mit Wert wurde stillschweigend geschluckt.** `--ausgabe paket.mcpkg`
   sah für den Parser aus wie ein gültiges Paar; der Aufrufer hätte sein Paket an einem Ort
   bekommen, den er nicht gemeint hat. Jede Unterbefehlsebene nennt jetzt ihre erlaubten Schalter,
   und die Prüfung steht **vor** der Arbeit statt dahinter.

**Der Nachweis:** `docs/connectors/developer-kit.md` beschreibt vier Befehle — und genau diese
Befehlsfolge ist der erste Test. Wird die Dokumentation falsch, wird der Test rot statt der Leser
ratlos. Dazu Gegenproben für verändertes Byte, fremden Herausgeber, fremde Vertragsversion, falsche
Component-Signatur und den privaten Schlüssel, der in keiner Ausgabe erscheinen darf.

## Laufender Meilenstein: M7 — Release Candidate

Vertrag: [m7-release-candidate-contract.md](m7-release-candidate-contract.md).

**Die Eintrittsbedingung ist formal nicht erfüllt** — der INDEX verlangt „alle 1.0-Musskriterien",
und M5 hat drei benannte Reste. M7 beginnt trotzdem, und zwar mit **WP7.1**: Genau dieses Paket
beantwortet die Frage, ob die Kriterien erfüllt sind. Ein Feature Freeze, den niemand gegen die
Anforderungen gehalten hat, ist eine Behauptung über eine Liste, die es nicht gibt.

| WP | Status | Nachweis | Nächster Schritt |
|---|---|---|---|
| 7.1 Feature Freeze und Finding-Liste | `implementiert` | 24 Tests grün: Wächter über die Tore (5), Finding-Liste mit 29 Befunden (5+2), Rückverfolgung über **alle 72 Anforderungen** (6+2), Gegenprüfung Changelog/Migration/Rollback (4) | Abnahme durch fremde Augen |
| 7.2 Externer Security-Review | `vorbereitet` | [security-review-scope.md](../acceptance/security-review-scope.md) — Scope, Bezugscommit, Unterlagen, und was wir selbst für die schwächste Stelle halten | braucht einen beauftragten externen Prüfer |
| 7.3 Pilotrelease | `vorbereitet` | [pilot-checklist.md](../acceptance/pilot-checklist.md) — was ein Pilot installiert, ausführt und zurückmeldet | braucht drei getrennte Installationen und echte Betreiber |

### Der Wächter zuerst — weil er als Einziges sofort rot werden kann

`tests/Bifrost.Governance.Tests` prüft **Regeln über dem Code**, nicht Code. Deshalb hat das Projekt
bewusst keine Referenz auf das Produkt: Was hier geprüft wird, steht in Dateien und nicht in Typen.

1. **Kein `[Fact(Skip=…)]`.** Ein Attribut-Skip läuft nie wieder an — es gibt keinen Lauf, der ihn
   zurückholt, und keine Meldung, die daran erinnert. Er ist ein gelöschter Test, der wie ein
   vorhandener aussieht. Bedingungen gehören in den Test (`Assert.SkipUnless`).
2. **Jede Gate-Variable wird von einem CI-Job gesetzt** (M5-Vertrag §1.2, jetzt geprüft statt
   eingehalten). Neun Tore im Testcode, neun in den Workflows.
3. **Keine Unterdrückung ohne Begründung** — weder `#pragma warning disable` noch `NoWarn`.
   Generierter Code ist ausgenommen: EF schreibt in jede Migrationsdatei ein Pragma ohne Kommentar,
   und den von Hand nachzubearbeiten hieße, gegen den nächsten Generatorlauf zu arbeiten.

**Zwei Funde, beide klein und beide behoben:** Zwei `#pragma warning disable CA1848` in
`Program.cs` hatten keine Begründung. Und der Wächter hat sich beim allerersten Lauf selbst
gemeldet — sein einziger Fund war der Kommentar, der die Regel erklärt. Ein Prüfer, der das Reden
über eine Regel bestraft, sorgt dafür, dass niemand mehr darüber schreibt; Kommentarzeilen sind
jetzt ausgenommen, mit dem Fund als Begründung im Code.

### Die Finding-Liste — und was sie über den RC sagt

`docs/acceptance/findings.json` ist die Quelle, `findings.md` wird daraus erzeugt. **26 Befunde,
23 behoben, 3 offen** — übernommen aus diesem Protokoll und dem CHANGELOG, nicht neu erfunden.

**Kein offener Critical- oder High-Befund** — und dieser Satz steht nicht in einem Dokument,
sondern in einem Test: `No_open_critical_or_high_finding_blocks_the_release_candidate`. Es gibt
bewusst **kein Feld „akzeptiert"**. Wer einen solchen Befund für tragbar hält, ändert den
Schweregrad und begründet das im Datensatz; dann steht die Entscheidung da, wo sie hingehört,
statt in einem Ausnahmefeld zu verschwinden.

Die drei offenen sind alle `medium` und tragen deshalb Owner, Termin und Mitigation — auch das
erzwungen, nicht erbeten:

| Id | Befund | Termin |
|---|---|---|
| BFR-F-0015 | Speicherwachstum unter Sättigung, 110 MB/min ohne Abflachen | vor M8/WP8.1 |
| BFR-F-0025 | ein Solution-Fehlschlag, dessen Name verloren ging | vor M7/WP7.2 |
| BFR-F-0026 | die Befunde aus M0–M3 sind nicht übernommen | vor M7-Abschluss |

**Der dritte ist der unangenehmste, und er steht absichtlich drin.** Die Liste beginnt bei M4, weil
das Protokoll ab dort durchnummeriert führt. Solange das so ist, kann niemand aus dieser Datei
allein „keine offenen Critical/High" sagen — nur „keine ab M4". Die Abdeckungsangabe steht im Kopf
der Datei, und ein Test besteht darauf, dass sie eine Begründung hat.

**Die härteste Regel des Vertrags ist angewendet:** Ein Beleg, der wie ein Testname aussieht, muss
einer sein. `Every_test_named_as_evidence_really_exists` hält jeden Beleg gegen die Suite — ein
Verweis auf einen gelöschten Test ist schlimmer als gar keiner, weil er wie ein Nachweis aussieht.

**Beide Wächter wurden gegengeprüft.** Ein Befund wurde testweise auf `high` gesetzt und ein Beleg
auf einen erfundenen Testnamen: Beide Tests wurden rot, dann wurde zurückgesetzt. Ein Tor, das nie
zugefallen ist, ist kein Tor.

### Die Rückverfolgung — und die Zahl, die jetzt feststeht

`docs/acceptance/traceability.json` ist die Quelle, `traceability.md` wird daraus erzeugt. **Alle 72
Anforderungen des Lastenhefts haben eine Zeile** — 50 Must, 21 Should, 1 Could.

| | |
|---|---|
| erfüllt | 53 |
| teilweise | 12 |
| **offen** | 7 |

**12 der 50 Must-Anforderungen sind nicht erfüllt.** Das ist die Zahl, um die es beim Release
Candidate geht, und sie steht jetzt oben im Bericht statt in niemandes Kopf. Jede dieser Zeilen
trägt einen Grund — auch das erzwingt ein Test, nicht eine Bitte.

**Die Titel werden nicht abgetippt.** Sie stammen aus dem Lastenheft, und ein Test hält beide
Richtungen gegeneinander: Wer dort eine Anforderung ergänzt, färbt den Lauf rot, bis sie hier eine
Zeile hat; eine Zeile ohne Anforderung im Lastenheft ist erfunden und fällt genauso auf.

**Die härteste Regel des Vertrags ist angewendet.** 56 der 72 Zeilen nennen einen Test, und
`Every_named_test_really_exists` hält jeden Namen gegen die Suite. Gegengeprüft: Ein erfundener
Testname färbt den Lauf rot, dann zurückgesetzt.

Die sieben offenen Anforderungen sind keine Überraschungen, sondern die bekannten Lücken —
Startvorlagen (FR-P017), 72-Stunden-Soak (FR-P051), Diagnose bei Hängern (FR-P056), reale Clients
(FR-P061), Vier-Augen-Freigabe (FR-P085, die einzige Could), CONTRIBUTING und Templates (FR-P092),
öffentliche Roadmap (FR-P094). Die zwölf `teilweise` sind überwiegend Nachweise, die ein
veröffentlichtes Artefakt oder fremde Zugangsdaten brauchen.

### Die Gegenprüfung Changelog, Migration, Rollback — und was sie gefunden hat

Der letzte Punkt des Pflichtenhefts. Geprüft wurde nicht die Zusage, sondern der Baum dagegen.

**Migrationen: sauber.** 16 je Anbieter, **namensgleich** auf SQLite und PostgreSQL, und jede hat
ein `Down` mit Inhalt. Beides steht jetzt als Test da: Fehlt eine Migration auf PostgreSQL, bricht
dort der Start — und zwar erst beim Betreiber, weil die Entwicklung auf SQLite läuft. Ein leeres
`Down` ist kein Rückweg, sondern eine Einbahnstraße mit einer Methode davor.

**Changelog: geführt.** Mechanisch prüfbar ist nur, dass der offene Abschnitt existiert, Rubriken
hat und nicht aus drei Zeilen besteht — ob er das Richtige nennt, entscheidet ein Mensch. Was der
Test verhindert, ist der Fall, in dem eine ganze Welle gar keinen Eintrag bekommen hat.

**Und ein Fund, der es in sich hat: vier Betriebsoptionen standen in keiner
Betriebsdokumentation.** `BIFROST_ALLOW_HOST_EXECUTION` — die schärfste Option des Produkts, sie
erlaubt Upstream-Programmen den nativen Zugriff auf alles, was dem Dienstbenutzer offensteht —
stand nur im ADR und im Pflichtenheft, also dort, wo ein Betreiber nicht nachsieht. Dazu die drei
Schalter der Approval-Benachrichtigung aus WP6.3, darunter das **Signaturgeheimnis** und der
ausdrückliche Weg an der SSRF-Prüfung vorbei.

Alle vier sind jetzt in `docs/operations.md` und im englischen Spiegel dokumentiert, mit Default,
Risiko und Empfehlung. Der Wächter dazu hält jede `BIFROST_*`-Option des Produktcodes gegen die
Betriebsdokumente — ein ADR zählt dabei ausdrücklich **nicht**: Es ist eine Entscheidung, kein
Handbuch.

**Gegengeprobt, und die erste Probe war zu schwach.** Ich habe die Zeile aus der deutschen
Dokumentation entfernt — der Test blieb grün, weil der englische Spiegel sie noch trug. Das ist
richtiges Verhalten des Wächters und ein falscher Schluss meinerseits gewesen; erst das Entfernen
aus beiden färbte den Lauf rot. Eine Gegenprobe, die grün bleibt, beweist nichts.

Damit steht **FR-P091** („jede sicherheitsrelevante Option dokumentiert Default, Risiko und
Empfehlung") auf `erfüllt` statt `teilweise` — und trägt jetzt einen Test statt einer Absicht.
Offene Must-Anforderungen: **11** statt 12. Die härteste Regel steht im Vertrag §1: Eine Zeile zählt nur, wenn der
genannte Test wirklich existiert und wirklich läuft.

## Vorige Welle: M5 — Kompatibilität und Betriebsnachweise

**Nicht abgeschlossen.** Die benannten Reste sind Voraussetzung für M7 — nicht für M6. **Zwei davon
sind erledigt:** die fünf offenen Browserwege und der Reverse-Proxy-Sprung (beide unten). Offen
bleiben externer OAuth-Anbieter, 72-Stunden-Soak und reale Clients in der Matrix; alle drei brauchen
etwas, das hier nicht herstellbar ist — fremde Zugangsdaten, 72 Stunden oder installierte
Fremdsoftware.

Vertrag: [m5-compatibility-contract.md](m5-compatibility-contract.md) (Stand 2026-08-02). M5 baut
fast keinen Produktcode, sondern Nachweise; entsprechend regelt der Vertrag Artefakte, Formate und
die Frage, wann ein Ergebnis als Beleg zählt.

| WP | Status | Nachweis | Nächster Schritt |
|---|---|---|---|
| 5.1 Client-Kompatibilitätsharness | `teilweise` | Schema, Bericht und SDK-Probelauf laufen (13 Tests grün, lokal Windows) | reale Clients aufnehmen, Linux-Zeile in CI aufzeichnen |
| 5.2 Reale OAuth-Testumgebungen | `teilweise` | 6 Tests gegen ein echtes Keycloak grün (19 s), ein Produktfehler dabei gefunden und behoben; Rotation nach dem Audit wirklich belegt | externer Anbieter, Widerruf, Ablauf, Consent |
| 5.3 Netzwerkrealistischer Performanceharness | `teilweise` | vollständiger Lauf (3 × 30 s × 3 Szenarien, ~92 000 Aufrufe je Szenario) **plus der Reverse-Proxy-Sprung: 1,6 ms an der p95 über echtes nginx** | kleine VM, Linux-Lauf |
| 5.4 Soak- und Chaos-Harness | `teilweise` | 7-Minuten-Chaoslauf grün (103 036 Aufrufe, 8 Störungen, keine Waisen); Gegenproben für Fehlerpfad **und** Waisen-Detektor | Nachtlauf, 72-h-Lauf (M7), Befund unten |
| 5.5 Browser-E2E | `implementiert` | **18 Wege grün** in Chromium gegen eine veröffentlichte Instanz über TLS (1 m 42 s); die fünf offenen Wege sind gefahren und haben zwei Produktfehler gefunden | Firefox/WebKit im Nachtlauf; Abnahme durch fremde Augen |
| 5.6 Releasecandidate-Support-Bundle | `implementiert` | 19 Tests (8 Kern, 4 API, 4 CLI, 3 Korpus) plus ein Browserweg; Canary-Korpus taucht in keinem Archiv auf | zweite Abnahme durch fremde Augen |

### Audit gegen den Code (2026-08-02)

Geprüft wurde nicht die Fertigmeldung, sondern der Code dagegen: Vertrag gegen Umsetzung, Behauptung
gegen Lauf, und die Frage, ob ein Test überhaupt rot werden kann. Zehn Befunde, alle behoben.

**1. Schwer: Ein vorhandener Wächter war rot, und die Fertigmeldung kam trotzdem.**
`RedactionCorpusTests.A_new_redactor_forces_a_decision_about_the_corpus` prüft, dass **jede** Stelle,
die maskiert, gegen den Negativkorpus läuft — genau der Wächter, den es gibt, weil ein siebter
Transport einmal vergessen wurde. WP5.6 hat einen neuen Redigierer eingeführt, der Wächter fiel um,
und ich habe die Security-Suite nach dem Einbau **nie laufen lassen**. Die Meldung „14 Tests grün"
war wahr und meinte die selbst geschriebenen.

Behoben: zwei echte Korpustests für `SupportBundleRedactor` (Feldnamen und Freitext) plus der
Inventareintrag. Die Lehre gehört nicht in den Code, sondern in die Arbeitsweise — **eine
Fertigmeldung zählt die Suite, nicht die eigenen Tests.**

**2. Mittel: Die Grenze des Maskierungslaufs klang nach mehr, als sie ist.** „Kein Korpuswert im
Bundle" stimmte, weil die Quellen ihre Secrets unter secret-artigen Feldnamen führen. Ein Wert unter
einem harmlosen Namen und ohne erkennbare Form bleibt stehen. Das ist die richtige Bauweise — der
Durchgang ist ein Netz, kein Inhaltsscanner —, war aber nirgends als Aussage festgehalten. Jetzt
steht sie als Test da, mitsamt der Folge: Wer eine neue Quelle anschließt, darf sich auf den
Durchgang nicht verlassen.

**3. Mittel: Die Browserwege prüften ein Debug-Publish.** `dotnet publish` ohne `-c` nimmt immer
Debug — auch wenn die CI in Release baut und testet. Die Wege hätten also unbemerkt ein anderes
Artefakt geprüft als das ausgelieferte. Behoben; das Verzeichnis trägt die Konfiguration jetzt im
Namen, sonst würde ein alter Stand wiederverwendet.

**4. Mittel: Der Waisen-Detektor hatte keine Gegenprobe.** „Keine verwaisten Prozesse" beruhte auf
einem Prüfer, der in jedem Lauf eine leere Liste meldete — von einem kaputten nicht zu
unterscheiden. Jetzt bekommt er einen echten Nachzügler vorgesetzt und muss ihn benennen.

**5. Mittel: „rotates" bewies keine Rotation.** Der Test prüfte, dass ein neues Refresh-Token
zurückkommt — nicht, dass das alte danach wertlos ist. Ohne das läge nach jeder Erneuerung ein
weiterer gültiger Schlüssel herum. Das Realm setzt jetzt `revokeRefreshToken`, und der Test versucht
die Wiederverwendung ausdrücklich.

**6.–8. Leicht: Vertrag gegen Code, dreimal auseinander.** Der E2E-Job hieß `chromium` statt
`ui-e2e` (an einem Jobnamen hängt eine Branch-Protection-Regel) — Code angepasst. §2 schrieb
„Vorschau ist Pflicht", der Dienst erzwingt sie nicht — Text korrigiert, mit der Begründung, warum
ein Handle wie beim Restore hier das falsche Mittel wäre. §7 nannte eine `docker-compose.oauth.yml`,
gebaut wurde ein Testcontainer — Zeile berichtigt.

**9.–10. Leicht: Zwei ungeprüfte Wege im Support-Bundle.** Die Kategorie `Logs` war nur in ihrer
*abwesenden* Form geprüft, und der Vorgabepfad ohne `--out` gar nicht. Beide haben jetzt einen Test;
der Logtest prüft zugleich, dass Freitext beschnitten **und** maskiert wird.

**Der Lauf nach den Korrekturen** — solutionweit mit exakt dem Filter der Hauptpipeline:
**1 836 bestanden, 0 Fehler, 9 übersprungen** (Cli 53, Soak 7, Core 1 031, Security 132, Upstream
144, Upgrade 44/8 übersprungen, Integration 425/1 übersprungen). Die gegateten Suiten getrennt:
Kompatibilität 12+1, Perf 3+3, Soak 7+2, Browserwege 7, Keycloak 6.

### Nachtrag: der CI-Nachbau — und zwei weitere Befunde

Statt zu pushen wurde der Job **lokal nachgestellt**: Release-Build, exakt der Filter der
Hauptpipeline, `BIFROST_REQUIRE_POSTGRES=1` und `BIFROST_REQUIRE_CONTAINER=1`, Zählung aus derselben
`trx`.

**11. Der Skip-Wächter zählte nur eine einzige Assembly.** Der Lauf schrieb
`--logger "trx;LogFileName=results.trx"` — und mit festem Dateinamen **überschreibt jede
Testassembly dieselbe Datei**. Nachgesehen: Die Datei enthielt 426 Ergebnisse, alle aus
`Bifrost.Integration.Tests`; die sechs anderen Suiten waren verschwunden. Das Gate, dessen einziger
Zweck es ist, stille Skips zu verhindern, las also die Skips **einer** Suite. Acht übersprungene
Upgrade-Tests wären unbemerkt durchgegangen. Behoben: eigene `trx` je Assembly, Zählung über alle.

**12. Mein eigener Waisen-Test war nicht laufplan-fest.** Er verlangte „genau ein neuer Prozess" —
im Lauf über die ganze Solution starten andere Suiten gleichzeitig eigene Testserver. Allein grün,
im Verbund rot. Jetzt prüft er, dass **sein** Nachzügler benannt wird, nicht dass er der einzige ist.

**Die Antwort auf die eigentliche Frage:** Mit den scharfen Variablen bleiben **null übersprungene
Tests** — die neun, die lokal ohne sie stehen bleiben, laufen dann wirklich. Das Gate hält also
nicht, weil es nachsichtig ist, sondern weil nichts mehr übersprungen wird.

**Was dabei rot wurde und warum es nicht das Gate betrifft:** zehn Fehlschläge (acht Upgrade, zwei
Integration), alle aus derselben Familie — sie brauchen `pg_dump`/`pg_restore` auf dem Wirt, und
dieser Windows-Rechner hat sie nicht. Die CI installiert `postgresql-client` in einem eigenen
Schritt. **Belegt ist damit nichts**, erklärt schon: Der Nachbau kann die Gate-Arithmetik zeigen,
nicht den Linux-Lauf ersetzen.

**Die Gegenprobe zum Wächter selbst.** Nach der Korrektur schreibt der Lauf zehn Ergebnisdateien
statt einer, und die Zählung über alle ergibt **neun** übersprungene Tests — dort, wo die alte
Fassung **einen** sah. Der Wächter misst jetzt das, was er behauptet: In dieser lokalen Umgebung
(ohne `pg_dump`, ohne die scharfen Variablen) würde er anschlagen; mit ihnen sind es null. Beides
ist richtig, und vorher war beides unsichtbar.

Der Solution-Lauf nach allen Korrekturen: **1 836 bestanden, 0 Fehler** — der Waisen-Test hält jetzt
auch im Verbund.

**Was weiterhin aussteht:** Der echte CI-Lauf. Er verlangt einen Push, und im Arbeitsbaum liegt
neben M5 die unfertige Persistenz-Welle — teils in denselben Dateien. Diese Entscheidung gehört dem
Product Owner; sie ist getroffen worden: **kein Push**, der Nachweis bleibt ausstehend, bis die
Persistenz-Welle landet. Lokal bleiben
neun übersprungene Tests stehen (acht Upgrade-, ein Integrationsfall), die in der CI durch
`BIFROST_REQUIRE_POSTGRES` und `BIFROST_REQUIRE_CONTAINER` scharf werden. Der Nachweis dafür entsteht
erst im ersten CI-Lauf — bis dahin ist er offen, nicht erbracht.

### WP5.1 — was steht und was ausdrücklich nicht

Die Matrix in `tests/compatibility/matrix.json` ist die Quelle; `docs/acceptance/compatibility.md`
wird daraus **erzeugt**, und ein Test hält fest, dass beide nicht auseinanderlaufen. Der Validator
lässt kein `pass` ohne Beleg zu und kein Platzhalterfeld mit Beleg — acht Negativfälle prüfen, dass
er dabei wirklich rot wird (Vertrag §1.1).

Der Probelauf fährt das offizielle C#-SDK gegen den echten Gateway-Host und prüft **die Matrix
gegen die Wirklichkeit**, statt sie fortzuschreiben: Jedes `pass` in der veröffentlichten Tabelle
ist damit eine Zusicherung, die rot wird, wenn sie nicht mehr stimmt. Gegenprobe gemacht: Ein
falsch gesetztes `oauth: pass` lässt den Lauf fehlschlagen.

**Der veröffentlichte Stand ist heute 51 von 56 Zellen ungeprüft.** Belegt sind `tools`,
`resources`, `prompts` und `skills` für das SDK auf Windows; `listChanged` steht auf `unsupported`,
weil die ausgehandelten Serverfähigkeiten es seit ADR-0023 nicht mehr anbieten. Alle sechs
Pflichtclients aus FR-P061 stehen als Platzhalter drin — sichtbar ungeprüft, statt zu fehlen.

Nicht belegt und im Bericht so benannt: kein TCP, kein TLS, kein Reverse Proxy (der Probelauf
nutzt den In-Process-Host — Netzstrecke ist WP5.3), keine Linux-Zeile, keine Handprotokolle.

### WP5.3 — der Messstand hat zuerst sich selbst widerlegt

Der Harness startet den Gateway als **eigenen Prozess** hinter Kestrel, holt sich den Zugang über
`--issue-bootstrap-key` (also den Weg eines Betreibers ohne Oberfläche), legt den Upstream über die
REST-API an und fährt Dauerlast über TCP, wahlweise mit TLS. Gemessen wird der Median aus drei
Läufen; ein kürzerer Lauf schreibt sich selbst als `"complete": false` ins Artefakt und taugt damit
nicht für eine Abnahmeaussage.

**Der erste Durchgang meldete 11 ms Gateway-Overhead — das Achtfache des heutigen Werts.** Die
Ursache lag im Messstand: Er zog die *angeforderten* 20 ms Upstream-Zeit ab, während
`Task.Delay(20)` unter Windows regelmäßig gut 31 ms dauert. Die Timerauflösung erschien als
Gateway-Anteil. Der Upstream misst seinen Span jetzt selbst und der Harness zieht die gemessene
Zeit je Aufruf ab.

Belegt ist damit: p50 1,4 ms und p95 2,2 ms Gateway-Overhead, über TCP wie über TLS, in beiden
Betriebsarten, bei null Fehlern in ~92 000 Aufrufen je Szenario. TLS kostet ~50 % mehr CPU und
keine Latenz. Offen bleiben der Reverse-Proxy-Sprung, die kleine VM als Referenz, GC-Kennzahlen und
der Linux-Lauf — alle vier stehen im Abnahmedokument als nicht belegt.

### WP5.4 — der Harness urteilt jetzt auch darüber, wann er nicht urteilen darf

Der Soak fährt eine deterministische Lastmischung (Liste, Lesen, Schreiben, Skill, Audit; feste
Saat) gegen einen echten Gateway-Prozess und stört ihn periodisch: Upstream hart abschießen,
Upstream mitten im Aufruf abstürzen lassen, Verbindung ab- und wieder anschalten. Die Messreihe
läuft **Zeile für Zeile** in `artifacts/soak/<run-id>/metrics.jsonl` — das hat sich sofort bezahlt
gemacht: Ein Lauf, der am Ende an einem Abbruchfehler starb, hinterließ trotzdem 42 auswertbare
Messpunkte.

**Stand des Nachweises** (7 Minuten, 242 Aufrufe/s, 8 Störungen): 103 036 erfolgreiche und 4 087
gescheiterte Aufrufe — Letztere sind die Störungen, ohne sie hätte das Chaos nicht gewirkt. Handles
692 → 702, Threads 48 → 45, Kindprozesse konstant 2, **keine verwaisten Prozesse**, und nach der
letzten Störung bedient der Gateway wieder. Die Gegenprobe mit eingebautem Fehler (jeder
Schreibaufruf zerlegt seinen Upstream) meldet 50 922 Fehlschläge mit Begründung, während Lesen und
Listen weiterlaufen.

**Zwei Korrekturen am Harness, beide durch den ersten Lauf ausgelöst:**

1. **Ein Fenster von 25 Sekunden ergibt kein Urteil mehr.** Der erste Lauf rechnete daraus eine
   Steigung von 285 MB *pro Minute* hoch und meldete trotzdem „kein Wachstum" — gerettet allein von
   der zweiten Bedingung, und zwar knapp. Unter fünf Minuten Messfenster sagt die Auswertung jetzt
   ausdrücklich „keine Aussage", und ein Lauf, der als Betriebsnachweis gilt, muss ein Urteil
   hergeben.
2. **Der Soak hat Wartezeit zwischen den Aufrufen bekommen.** Ohne sie liefen 3 500 Aufrufe je
   Sekunde — das ist Sättigung, nicht Dauerbetrieb. Für die Sättigungsgrenze gibt es WP5.3.

### Befund aus WP5.4: Speicherwachstum unter Sättigung

Der erste Lauf **ohne** Wartezeit (~3 500 Aufrufe/s) zeigte über 6,7 Minuten ein stetiges Wachstum:
RSS 615 → 1 485 MB, Private Bytes 544 → 1 399 MB, rund **110 MB je Minute und ohne Abflachen**,
während Handles (815 → 851) und Threads (67 → 58) flach blieben. Beim realistischen Lauf mit 242
Aufrufen/s bleiben davon 213 → 289 MB über sechs Minuten übrig — nicht als ungebremst gewertet,
aber auch nicht abgeflacht.

Das ist ein **Befund, keine Diagnose**: Flache Handles bei wachsendem Speicher passen auf einen
Rückstau (Verdacht: Audit-Pfad) genauso wie auf eine Server-GC, die ohne Speicherdruck nicht
aufräumt. Nach der Regel dieser Welle wird er gemeldet und bekommt ein eigenes Paket; das
Testprojekt repariert keinen Produktionscode (M5-Vertrag §7). Der Beleg liegt in
`artifacts/soak/20260802-155800-load-and-chaos/metrics.jsonl`.

Wer ihn aufnimmt, hat mit dem Nachtlauf das Werkzeug: Vier Stunden bei realistischer Rate
entscheiden, ob die Kurve abflacht oder nicht.

### WP5.5 — der Browsertest hat zuerst das Startlayout widerlegt

Sechs Wege laufen gegen eine **veröffentlichte** Instanz über TLS, mit echter Datenbank und echtem
Anmeldeformular: falsches Passwort wird abgewiesen, Anmelden und Abmelden, geschützte Seite ohne
Sitzung, die Oberfläche wird tatsächlich interaktiv, ein Server wird über das Formular angeschlossen
und meldet `Healthy`, und ein Kommando, das es nicht gibt, wird als Fehler angezeigt. Zusammen
34 Sekunden.

**Der Fund beim ersten Lauf:** Gegen `bin/Debug` liefert der Server `_framework/blazor.web.js` mit
Status 200 und **null Bytes** aus. Der Blazor-Circuit startet dann nie — die Seiten werden weiter
serverseitig gerendert und sehen benutzbar aus, aber kein Knopf tut etwas. Das ist derselbe
Ausfall wie im ersten echten Betrieb (Badwolf, 2026-07-29), diesmal aber **kein Produktfehler**: Das
Veröffentlichungsergebnis enthält die Datei mit 200 KB. Die Konsequenz gehört trotzdem
festgehalten — ein Browsertest gegen das Entwicklungslayout hätte die halbe Oberfläche für kaputt
erklärt und nichts über das Ausgelieferte gesagt. Der Harness veröffentlicht deshalb einmal je Lauf
und fährt gegen dieses Ergebnis; `InteractivityJourneyTests` hält die Bedingung im Browser fest,
während `BlazorScriptDeliveryTests` sie weiterhin im Bauergebnis prüft.

**Beobachtung ohne Änderung:** Die Serverliste lädt ihre Zustände beim Öffnen und nach einer
Aktion, einen Ticker gibt es nicht. Direkt nach dem Anschließen steht dort `Starting`, und erst ein
Neuladen zeigt `Healthy`. Der Weg bildet das ab, wie ein Betreiber es täte; geändert wurde an der
Oberfläche nichts (M5-Vertrag §7).

**Test-IDs:** fünf Stück, alle additiv in `Servers.razor` — die Formularfelder tragen weder `id`
noch Beschriftungsbezug, und Platzhaltertexte sind Text, den jemand jederzeit umformuliert. Die
Anmeldung braucht keine: `#username`, `#password` und der Absendeknopf reichen.

**Offen:** Freigabe, Tool-Pin-Änderung, Rolle/Profil, Keyausgabe, Backup/Restorestatus und das
Erstsetup. Sie stehen in der WP-Tabelle als offen, nicht als „läuft schon".

### WP5.6 — das einzige Paket dieser Welle mit Produktionscode

Der Vertrag aus §2 liegt jetzt als `src/Bifrost.Abstractions/Support.cs`, der Dienst in
`src/Bifrost.Core/Operations/SupportBundle/`. CLI, REST und Oberfläche sind **Adapter** darauf; die
Frage, was in ein Bundle darf, wird genau einmal beantwortet.

**Die Entscheidungen, die im Code stehen und nicht nur im Vertrag:**

- **Auditinhalte und Credentialmaterial sind keine Kategorie.** Nicht abschaltbar, nicht per Flag —
  ein Test hält das Enum daran fest, damit es niemand versehentlich aufweicht.
- **Vorschau vor Erzeugung**, in allen drei Adaptern. In der Oberfläche ist der Erzeugen-Knopf ohne
  Vorschau gesperrt, und der Browserweg prüft genau das.
- **Ein zentraler Maskierungslauf** statt Vertrauen in die Quellen. Die Quellen sind für sich schon
  zurückhaltend — genau darauf beruht die Gefahr: Jede neue Quelle erbt diese Annahme, ohne sie zu
  prüfen. Der Durchgang am Ausgang gilt auch für die Quelle, die morgen dazukommt.
- **Die Feldnamenliste ist dieselbe wie in der Audit-Redaktion**, verlinkt statt kopiert.

**Der Nachweis** ist der Negativkorpus aus `SecretCorpus`: Er steckt in der Konfiguration, in einer
Diagnose-Sonde, in einer Verbindungszeichenfolge und — im Integrationstest — in der Umgebung eines
echten Upstreams. In keinem erzeugten Archiv taucht der Marker auf. Dazu: Prüfsummen im Manifest
gegen die Dateien im Archiv, admin-only, Auditeintrag, und ein Browserweg, der das Archiv nach dem
Erzeugen öffnet und durchsucht.

**Was ehrlich fehlt:** Die Kategorie `Logs` liefert auf einer Installation ohne Logdatei kein
Dokument, sondern eine Anleitung — der Gateway schreibt auf die Standardausgabe, und die gehört dem
Prozessverwalter. Das steht als übersprungene Kategorie im Bundle, in der Vorschau und in der
Oberfläche. Ein Bundle ohne Logs soll nicht aussehen wie eine Installation ohne Vorfälle.

### WP5.2 — der erste Lauf gegen ein echtes Keycloak hat einen Produktfehler gefunden

**Der Befund.** Die Suche nach den Metadaten eines Authorization Servers probiert drei
Standardpfade der Reihe nach. Ein HTTP-Fehlerstatus kam aber als `HttpRequestException` aus dem
Fetcher, während die Kandidatenschleife nur `OAuthDiscoveryException` abfing — sie brach beim
**ersten 404** ab. Die beiden Ausweichpfade waren damit toter Code, und gegen jeden Server, der
seine Metadaten nur unter `{issuer}/.well-known/openid-configuration` ausliefert (Keycloak unter
anderen), scheiterte die Anbindung.

Kein Test hätte das finden können: Alle bisherigen OAuth-Tests liefen gegen Nachbauten, und ein
Nachbau antwortet auf dem Pfad, den man beim Schreiben im Kopf hatte. Behoben in
`OAuthDiscovery.FetchJsonAsync` — der Abruffehler wird jetzt zum Kandidatenfehler, womit die
Schleife tut, wofür sie geschrieben wurde. Das ist die eine Produktänderung, die WP5.2 vorsieht:
Findings dürfen der OAuth-Owner beheben.

**Belegt ist jetzt** (6 Tests, 16 Sekunden, Container inklusive): Discovery gegen einen fremden
Server, Autorisierungsanfrage mit PKCE S256 und `resource`-Indikator, Anmeldung, Einlösung,
Erneuerung. Dazu drei Gegenproben — ein falscher `code_verifier` wird abgewiesen, ein fremder
Antwort-Issuer entwertet den Vorgang, und Klartext-Discovery bleibt ohne `AllowPrivateTargets`
verboten.

**Zwei Dinge am Harness sind erwähnenswert**, weil sie wie Abkürzungen aussehen und keine sind:
Der Test meldet sich über das echte Anmeldeformular an (Formular holen, POST, Weiterleitung lesen)
statt über die Admin-API — der abgekürzte Weg überspränge genau den Teil, der in der Praxis bricht.
Und die Sitzungscookies übernimmt der Harness von Hand, weil Keycloak sie mit `Secure` setzt und ein
.NET-Client sie über HTTP nicht zurückschickt. Beides betrifft den Browser-Ersatz, nicht den
Gateway.

**Offen:** der Lauf gegen einen externen, gehosteten Anbieter (Job eingerichtet, meldet ohne
Zugangsdaten ausdrücklich „nicht gelaufen"), Widerruf, Ablauf (die Uhr des Validators lässt zwei
Minuten Toleranz — ein Ablauftest kostet mehr Wartezeit als ein PR verträgt) und der Gateway als
Resource Server gegen echte Token: Dessen Metadatenabruf besteht auf HTTPS, und dafür müsste dem
Testzertifikat maschinenweit vertraut werden.

### Änderungen außerhalb der WP-Zonen (Lead)

1. **`ci.yml` schließt `Bifrost.Compatibility.Tests`, `Bifrost.Performance.Tests` und
   `SoakRunTests` aus dem Solutionlauf aus.** Alle drei hängen an einer Gate-Variable, und das
   dortige Skip-Gate erlaubt genau einen übersprungenen Test — ohne den Ausschluss wäre die
   Hauptpipeline allein durch das Hinzufügen der Projekte rot geworden. Die Läufe gehören in
   `compatibility.yml`, `soak.yml` und den `benchmark`-Job, und alle drei setzen ihre Variable auch
   (M5-Vertrag §1.2). Ausgeschlossen ist beim Soak nur die Lauf-Klasse: Die Auswertung
   (`GrowthAnalysisTests`) ist eine reine Rechnung und läuft bei jedem PR mit — sie ist der Prüfer,
   der über jeden Nachtlauf urteilt.
2. **Der `benchmark`-Job zeigt jetzt auf `tests/Bifrost.Performance.Tests`** und lädt
   `artifacts/perf` als CI-Artefakt hoch. Ohne Artefakt wäre jede Messung nach dem Lauf verschwunden.
3. **`SlowServer` hat ein zweites Werkzeug bekommen** (`sleep_measured`), das die tatsächlich
   verbrauchte Zeit zurückgibt. `sleep` bleibt unverändert — ein bestehender Test prüft dessen
   Wortlaut, und eine Änderung daran wäre eine Nebenwirkung dieser Welle gewesen.

## Laufender Meilenstein: M4 — Onboarding und Bedienbarkeit

Vertrag in `src/Bifrost.Abstractions/Importing.cs`. Die Einfrierung der ersten Welle ist
**aufgehoben**; Änderungen werden im Vertrag selbst begründet.

| WP | Status |
|---|---|
| 4.1 Providerneutrales Importmodell | `implementiert` |
| 4.2 Parser (Claude, Cursor, VS Code, Codex) | `implementiert` |
| 4.3 Setup- und Import-API | `implementiert` |
| 4.5 Basic/Advanced-Informationsarchitektur | `implementiert` |
| 4.6 Einheitliche Upstream-Diagnose | `implementiert` |
| 4.7 Dokumentation, i18n, Contributor-Basis | `implementiert` |
| 4.4 Geführter Setup-Wizard | `implementiert` — neun Schritte unter `/first-run` |

### Vier Befunde aus der Abnahme von 4.1–4.3 — behoben

Ein Audit gegen den Code (nicht gegen die Häkchen) hat vier Sachen gefunden. Alle vier sind
behoben; die ersten beiden verlangten eine Vertragsänderung in `Importing.cs`.

**1. Teilimport.** `ImportPlan.CanApply` galt planweit: Ein einziger kaputter Eintrag machte eine
Datei mit dreißig Servern unanwendbar. Für den geführten Erstaufbau (WP4.4) war das die
Einschränkung, an der die Sache scheitert — wer dreißig Server mitbringt, hat mit ziemlicher
Sicherheit einen darunter, der nicht mehr stimmt.

`ImportFinding` trägt jetzt einen `Scope` (`Document` oder `Entry`), **Vorgabe `Document`**: Ein
Befund gilt für alles, bis jemand ausdrücklich hinschreibt, dass er nur eine Stelle betrifft. So
herum blockiert ein vergessener Bereich zu viel statt zu wenig — die Verharmlosung eines planweiten
Fehlers zu einem Einzelbefund wäre der teure Irrtum. `ImportPlan.IsApplicable(kandidat)` fasst die
drei Bedingungen zusammen; `CanApply` heißt jetzt „etwas geht", nicht mehr „alles geht".

Bestätigungen gelten seitdem der **Auswahl** (`ConfirmationsFor`): Wer drei von dreißig Servern
übernimmt, bestätigt die Risiken dieser drei und die planweiten. Eine Bestätigung, die pauschal für
alles gilt, wird zur Formalie.

**Die Stelle, an der der Teilimport aufhört:** Wer einen gesperrten Server ausdrücklich in `servers`
(beziehungsweise `--only`) benennt, bekommt eine Absage statt eines stillen Auslassens. Ohne
Auswahl werden die anwendbaren übernommen und die übrigen in der Antwort unter `skipped` genannt —
ein Teilimport, der die Differenz verschweigt, sieht aus wie ein vollständiger.

**2. Der Ort in den zentralen Befunden war falsch.** Die zentrale Nachbearbeitung setzte den Pfad
aller von ihr erzeugten Befunde fest auf `mcpServers/<name>` — und das sind die meisten Befunde
überhaupt: Risiko, Zugangsdaten und Normalisierung entstehen alle dort. Bei Claudes
`projects`-Karte, bei VS Code (`servers/…`, `mcp/servers/…`) und bei Codex (`mcp_servers/…`) zeigte
das auf eine Stelle, die es in der Quelldatei nicht gibt. Ein Ort, der nicht stimmt, ist schlechter
als keiner: Er schickt jemanden an die falsche Zeile, und wer dort nichts findet, glaubt eher, den
Befund missverstanden zu haben. `ImportCandidate.SourcePath` kommt jetzt vom Parser;
`ImportFindingLocationTests` prüft das über alle Beispielkonfigurationen.

**3. TOML: die Entscheidung ist, es draußen zu lassen.** Zur Wahl stand, die Parser *vor* der
JSON-Prüfung nach Zuständigkeit zu fragen. Das hätte an der Sache nichts geändert — ohne TOML-Leser
beansprucht kein Parser eine `config.toml`, und ein TOML-Leser ist weder vorhanden noch ohne
Rückfrage zu ziehen. Gekostet hätte es die Schärfe der Meldung „kaputtes JSON". Stattdessen ist die
**Absage** deutlicher: `BFR-IMP-0006` sagt „das ist TOML, dieser Weg liest JSON, so schreibst du es
um" statt „Syntaxfehler in Zeile 1". Wer Codex wirklich unterstützen will, braucht einen TOML-Leser
— das ist gemeldet, nicht umgangen.

**4. Falschpositiv in der Maskenerkennung.** `LooksMasked` erkannte eine Verweisform nur als
*ganzen* Wert; `"Authorization": "Bearer ${env:TOKEN}"` galt deshalb als Klartextgeheimnis. Jetzt
zählt eine Verweisform auch mitten im Wert — aber nur, wenn nach ihrem Entfernen nichts Wertartiges
übrig bleibt. `Bearer ${env:TOKEN}` hinterlässt ein Schemawort und ist maskiert; `sk-abc${SUFFIX}`
hinterlässt ein halbes Geheimnis und bleibt ein Klartextfund.

### Offene Produktentscheidung: Container-Import braucht ein Image, das niemand mitbringt

Im abgeschirmten Modus ist `Isolation.Image` Pflicht — und **kein Quellformat bringt eines mit**.
Wer eine Claude-Konfiguration voller `npx`-Server auf einer frischen Instanz einliest, bekommt für
jeden Eintrag eine Absage, bis er ein Image nennt.

Der Wizard sagt das an drei Stellen deutlich und kann es nicht auflösen. Das ist keine
Oberflächenfrage: Entweder gibt es eine Vorgabe je Ökosystem (`npx` → ein Node-Image, `uvx` → ein
Python-Image), oder es bleibt Handarbeit. Eine Vorgabe wäre bequem und zugleich eine
Vertrauensentscheidung, die das Produkt dem Betreiber abnimmt — offen für den Product Owner.

### Ein dritter Wächter fehlt: `Bifrost.Web`

Die Architekturtests zu ADR-0025 E4 prüfen `Bifrost.Core` und `Bifrost.Server`. Der Wizard baut in
`Bifrost.Web` eine `UpstreamServerConfig` und geht damit korrekt über `SecureUpstreamDefaults` und
den Supervisor — **aber der Wächter würde es nicht merken, wenn das jemand ändert.**

Gemeldet von WP4.4, nicht nebenbei erledigt: Ein dritter Wächter ist eine Entscheidung, und die
Aufzählung, die dabei entstünde, ist genau das Problem, das diese Tests lösen sollen.

### Entscheidung: Der Setup-Endpunkt bleibt auf Loopback beschränkt

WP4.3 hat `/setup/import/preview` auf Loopback begrenzt und dazu angemerkt, dass ein Betreiber, der
die Einrichtung vom Laptop aus öffnet, darüber nicht durchkommt.

**Entschieden: Die Beschränkung bleibt.** Der Endpunkt ist der einzige, der ohne Anmeldung
erreichbar ist — er hängt allein am Erstzugangs-Token, und dieses Token liegt in einer Datei auf dem
Server. Ihn übers Netz zu öffnen hieße, den Erstzugang zu einem Ratespiel für jeden zu machen, der
den Port erreicht. Loopback ist hier kein Komfortverlust, sondern die zweite Hälfte der Absicherung.

Die Setup-Oberfläche braucht ihn nicht: Sie ist Blazor Interactive Server und läuft **im**
Serverprozess. Sie ruft `IConfigurationImporter` und die Vorschauprojektion direkt auf, ohne den
Umweg über HTTP. Der HTTP-Weg bleibt für lokale Werkzeuge — genau dafür ist er gebaut.

Das ist eine Auflage für WP4.4, keine Einschränkung: Wer den Wizard über HTTP gegen den
Setup-Endpunkt baut, hat ihn falsch gebaut.

### Drei Wächter für dieselbe Regel — und drei Pakete, die je einen übersahen

Der Architekturtest aus ADR-0025 E4 („jeder Weg, der eine `UpstreamServerConfig` baut, ist
eingeordnet") existiert **dreifach**, weil er drei Assemblies prüft:

| Test | Assembly |
|---|---|
| `HostExecutionArchitectureTests` | `Bifrost.Core.Tests` |
| `HostExecutionPolicyContractTests` | `Bifrost.Security.Tests` |
| `HostExecutionServerArchitectureTests` | `Bifrost.Integration.Tests` |

WP4.1 prüfte nur Core und riss den in Security. WP4.3 prüfte Core und Security — und riss den in
Integration. Beide Male hat der Wächter getan, was er soll; beide Male fiel es erst im Gesamtlauf
auf.

**Der Fehler liegt in den Aufgabenkarten, nicht bei den Paketen.** Sie zählten Testprojekte auf,
und eine Aufzählung ist genau das, was diese Tests selbst vermeiden. Ab sofort lautet die
Pflichtprüfung in jeder Karte `./build.sh verify-dotnet` — der Gesamtlauf — statt einer Liste von
Projekten. Der Preis sind sieben Minuten je Paket; der Preis der Liste war zweimal ein roter
Gesamtlauf und eine Nachbesserung durch den Lead.

## Abgeschlossener Meilenstein: M3 — Sichere Vorgaben

**Alle sechs Pakete implementiert**, 1271 Tests grün. Die drei Rückstände sind abgeräumt:

| Rückstand | Erledigt |
|---|---|
| wasmtime 47.0.2 mit zwei RUSTSEC-Meldungen | auf 47.0.3; `cargo audit` meldet nichts mehr, 110 Rust-Tests und die zwei Real-Host-Tests grün |
| `.trivyignore.yaml` fehlte, `release.yml` verwies darauf | angelegt und **bewusst leer** — Ausnahmen laufen über das befristete Register, nicht über eine Ignorierliste, die niemand liest |
| CODEOWNERS fehlte vollständig | angelegt, kurz gehalten: Gates, Release-Pipeline, `CryptographicNames.cs`, ADRs |

**Einschränkung zu CODEOWNERS, die nicht untergehen darf:** Die Datei allein erzwingt nichts. In
den Branch-Protection-Regeln für `main` muss „Require review from Code Owners" eingeschaltet sein,
sonst ist der Eintrag ein Vorschlag. Das ist eine Repository-Einstellung und lässt sich nicht
mitliefern; sie steht als Hinweis im Kopf der Datei. Bis dahin bleibt die PO-Freigabepflicht
dokumentiert und **nicht durchgesetzt**.

Zum wasmtime-Sprung: Beide Meldungen waren LOW und hätten kein Gate blockiert. Betroffen war aber
ausgerechnet die WASI-Sandbox selbst — eine Schwachstelle in genau der Komponente, deren Aufgabe die
Isolation ist, verdient keine Einstufung nach Punktzahl. Die Real-Host-Tests wurden gesondert
gefahren, weil `verify-dotnet` sie ausschließt und ein veralteter Release-Stand dort hängt statt
fehlzuschlagen.

### Verlauf

Entscheidung vorab in [ADR-0025](../adr/0025-host-ausfuehrung-verbieten-und-bestehende-instanzen-migrieren.md),
Vertrag eingefroren in [`m3-secure-defaults-contract.md`](m3-secure-defaults-contract.md).

### WP3.6 hat zwei SSRF-Lücken gefunden — beide nachgeprüft, beide echt

Das Paket sollte Regressionstests für bekannte Fehlerklassen bauen. Es hat dabei zwei **neue**
Lücken gefunden und liefert sie als **rote** Tests ab, statt sie grün zu reden. Das ist genau das
gewünschte Verhalten.

**F1 — MCP-über-HTTP ist der einzige Transport ganz ohne Zielprüfung.**
`HttpTransportOptions` (`src/Bifrost.Abstractions/Upstream.cs:66`) trägt **kein**
`AllowPrivateTargets`; `OpenApiTransportOptions`, `OpenRpcTransportOptions` und
`UpstreamOAuthOptions` tragen es. `StreamableHttpUpstreamConnector` reicht `options.Endpoint`
direkt in den Transport — nachgeprüft: Die einzige Zielprüfung in der Datei betrifft den
**OAuth-Issuer**, nicht den Endpunkt des Upstreams. Ein Administrator, der
`http://169.254.169.254/…` oder einen Verwaltungsport auf `127.0.0.1` einträgt, bekommt genau den
Abruf, den `RemoteSpecFetcher` für OpenAPI und OpenRPC verhindert.

Strukturelle Ursache: `RemoteSpecFetcher` ist `internal` zu `Bifrost.Upstream` — `Bifrost.Core` und
`Bifrost.Server` können ihn gar nicht wiederverwenden. Eine zentrale Prüfung, die man nicht
erreichen kann, ist keine zentrale Prüfung.

**F2 — Der OAuth-Connect-Endpunkt probt, bevor er prüft.**
`src/Bifrost.Server/UpstreamOAuthEndpoints.cs:161` ruft `probe.GetAsync(endpoint, ct)` gegen die
vom Betreiber genannte Adresse. **Drei Zeilen später** wird `oauth.AllowPrivateTargets` an jede
Discovery-Anfrage weitergereicht. Der Code kennt den Schalter also und benutzt ihn unmittelbar
danach — der Probe-Aufruf davor wurde schlicht übersehen.

**Beide behoben.** F2 war eine übersehene Zeile: `OAuthDiscovery.EnsureTargetAllowedAsync` läuft
jetzt vor der Sonde, mit demselben Schalter, den die Zeile darunter ohnehin weiterreicht. Der Weg
dorthin ist der eigentliche Fix — `RemoteSpecFetcher` bleibt `internal`, bekommt aber einen
öffentlichen Zugang über `OAuthDiscovery`, damit `Bifrost.Server` die zentrale Prüfung überhaupt
erreichen kann.

F1 verlangte eine Entscheidung, weil dort Verhalten kippt. `HttpTransportOptions` hat jetzt
`bool? AllowPrivateTargets`, und **`null` heißt „nicht entschieden", nicht „verboten"**:

- Eine Bestandsinstanz hat den Schalter nie gesetzt. Ein MCP-Server im eigenen Netz ist bei diesem
  Produkt der Regelfall, nicht die Ausnahme — die produktive Instanz auf Badwolf spricht selbst
  einen Upstream unter `192.168.178.61` an.
- Sie beim nächsten Neustart abzuklemmen wäre genau die stille Verhaltensänderung, die
  [ADR-0025 E3](../adr/0025-host-ausfuehrung-verbieten-und-bestehende-instanzen-migrieren.md)
  für die Hostausführung ausdrücklich ablehnt. Dieselbe Frage, dieselbe Antwort.
- Ausdrückliches `false` weist private Ziele ab.

**Die Lücke ist damit nicht geschlossen, sondern verschoben**, und das gehört so gesagt: Solange die
Erzeugungswege (Formular, API, Paketimport) den Wert nicht **setzen**, bleibt er `null` und damit
erlaubt — auch bei neuen Konfigurationen. Der Schalter existiert, die Prüfung greift, aber die
Vorgabe für Neuanlagen fehlt. Das gehört zu WP3.2, wo die Erzeugungswege ohnehin angefasst werden.

### Stand M3

| WP | Status | Nachweis |
|---|---|---|
| 3.1 Host-Execution-Policy + Bestandsübernahme | `implementiert` | 607 Core-Tests, Architekturtest über Reflexion und IL |
| 3.2 Container als Standard | `implementiert` | ein Startmodell für stdio und CLI, echte Container-Tests, +50 Tests |
| 3.3 Key-Ring-Setup | `implementiert` | drei Betriebsmodi, Verlusterkennung mit zwei Zeugen, FR-P048 erledigt |
| 3.4 Bootstrap statt Log-Credentials | `implementiert` | Log-Mithörer belegt: kein Secret im Log; bestehende Admins bleiben |
| 3.5 Security- und Supply-Chain-Gates | `implementiert` | Negativnachweis je Gate außer Containerscan |
| 3.6 Sicherheitsinvarianten | `implementiert` | 62 Tests, zwei echte SSRF-Funde |

### Der Abbruch von WP3.2

Beide Agenten der zweiten Welle endeten an einem **Kontolimit**, nicht an einem Fehler im Code.
WP3.3 kam nicht über die Pflichtlektüre hinaus. WP3.2 war mitten in der Umbenennung — und ein
halb vollzogener Rename ist der ungünstigste Abbruchzeitpunkt, den dieses Paket hat: Der Vertrag in
`Bifrost.Abstractions` kannte nur noch `IsolationOptions`, drei Aufrufstellen in `Bifrost.Upstream`
und `Bifrost.Core` noch `CliIsolationOptions`. **Die Lösung baute nicht.**

Die Teilarbeit liegt vollständig auf `wip/wp3.2-isolation` — inklusive eines neuen Ordners
`src/Bifrost.Upstream/Isolation/` mit vier Dateien, der neben dem alten
`Cli/ContainerLaunchPolicy.cs` steht. Welcher von beiden bleibt, ist die unerledigte Kernfrage des
Pakets: Zwei Launchmodelle wären genau die zwei Wahrheiten, die der Auftrag verhindern sollte.

`main` wurde auf `3ceb5e3` zurückgesetzt und baut. Nichts von der Teilarbeit ist verloren, und
nichts davon ist in einem Zustand, in dem man darauf aufbauen sollte, ohne den Rename zu Ende zu
führen.

**Nachtrag: neu geschnitten, nicht übernommen.** Der zweite Anlauf lief von `main` aus, mit der
Auflage, in jederzeit bauenden Schritten zu arbeiten — der halbe Rename war die Lehre aus dem
Abbruch, nicht nur sein Ergebnis. Vom Zweig übernommen wurden geprüfte Bausteine
(`ImageReference`, `ContainerIdentity`, `ContainerMountPolicy`); vier Dinge wurden dabei korrigiert,
darunter ein Pfad in beiden Mount-Listen, der zwei `--volume` auf dasselbe Ziel erzeugt hätte
(von der Runtime abgelehnt), und ein fehlender Lebenszyklus — ohne ihn lief ein Kommando nach einer
Zeitüberschreitung im Container weiter, denn den Client zu töten reicht nicht.

Die Kernfrage ist entschieden: **ein** Startmodell. `Cli/ContainerLaunchPolicy.cs` ist gelöscht,
stdio und CLI gehen durch dieselbe Mindestpolicy und unterscheiden sich nur in der Lebensdauer
(`PerInvocation` gegen `Session`) und darin, ob stdin offen bleibt.

### WP3.4: Ein Widerspruch in der Aufgabenkarte, offengelegt statt aufgelöst

Die Karte verlangte in Auftrag 3 die Ausgabe „an eine interaktive CLI oder in eine restriktive
Bootstrapdatei" und verbot in der Stop-Bedingung „ein Token im Klartext in irgendeiner Ablage, auch
nicht kurz". Beides zusammen geht nicht. Der Agent hat den Widerspruch benannt, seine Lesart
begründet (dauerhafte Ablage trägt nur den Hash, die Übergabedatei ist kein Speicher) und die Stelle
markiert, die zurückzudrehen wäre — statt sich still für eine Seite zu entscheiden.

Bestätigt: Die Bootstrapdatei stammt aus dem Pflichtenheft, die Stop-Bedingung meinte die dauerhafte
Ablage. Das Restrisiko — eine Sicherung *innerhalb* der Frist trägt die Datei mit — steht in
`docs/operations.md`.

**Der Nachweis zum DoD ist der wertvollste Teil.** Er holt das *tatsächlich ausgestellte* Token aus
der Übergabedatei und prüft, dass weder es noch eines seiner Acht-Zeichen-Bruchstücke in irgendeinem
Logkanal auftaucht — nicht ein erfundenes Token gegen einen erfundenen Pfad. Davor steht der
Nachweis, dass überhaupt mitgeschrieben wurde und überhaupt ein Token existierte; ohne den wäre der
Vergleich auch dann grün, wenn nichts passiert ist.

**Nachtrag des Lead:** Der `README`-Abschnitt zum Erstzugang schickte Betreiber weiterhin ins Log
nach einem Passwort, das dort bewusst nicht mehr steht — die schlimmste Sorte veralteter Doku, weil
sie wie ein Fehler im Produkt aussieht. Nachgezogen.

### WP3.3: Der Verlust wird jetzt erkannt, statt überschrieben

Der Kern war nicht die Zertifikatsverwaltung, sondern ein Ausfallmodus, den dieses Projekt schon
einmal getroffen hat: Fehlt der Key-Ring, ist jeder gespeicherte Geheimtext unlesbar — **und der
Dienst startet trotzdem und meldet sich als bereit**. Beim v0.11.0-Umstieg hat dieselbe Kombination
an der Datenbank zugeschlagen.

Erkannt wird der Verlust über **zwei unabhängige Zeugen**, und das ist die eigentliche Arbeit:

1. `config/keyring.json` hält Anzahl und Ids der zuletzt gesehenen Schlüssel fest. Zeuge da,
   Verzeichnis leer → Verlust. Zeuge **unlesbar** → ebenfalls Verlust, nie „frische Instanz".
2. Geheimtext in der Datenbank. Der erste Zeuge liegt im selben Volume wie der Ring — verschwinden
   beide zusammen, trägt die Datenbank die Beweislast. Genau der Fall eines umbenannten Volumes.

Folge: Exit-Code 78, Critical-Log, Audit-Eintrag, **kein neuer Ring**. Ein vollständig
ausgetauschter Ring blockiert dagegen nicht — so sieht auch eine legitime Wiederherstellung aus —,
wird aber laut protokolliert. Die Prüfung sitzt bewusst *nach* den Recovery-Kommandos, damit
`--reset-ui-admin` erreichbar bleibt.

Eine ungeschriebene Annahme hat der Agent dabei selbst offengelegt: `config/keyring.json` reist
**nicht** im Backup mit, weil `BackupSections.Config` nur `instance.json` sichert. Für die
Verlusterkennung ist das richtig — ein zurückgespielter Zeuge ohne Ring wäre ein Falschbefund —,
aber ein künftiger Ausbau des Config-Bereichs könnte es still kippen.

### Der Blindfleck, den WP3.2 selbst gemeldet hat

`ServerDiagnosticService` zählte für die Runtime-Bereitschaft nur `Config.Cli?.Isolation`. Eine
Instanz mit ausschließlich stdio-Container-Upstreams hätte damit „keine Runtime nötig" gemeldet —
eine Diagnose, die genau den Fall verschweigt, für den sie da ist. Nachgeprüft und behoben; beide
nativen Transporte zählen jetzt.

### WP3.5 hat einen M1-Fehler gefunden, keinen M3-Fehler

`release.yml` rief die Trivy-Action mit `trivyignores: .trivyignore.yaml` auf — **diese Datei gibt
es nicht** (nachgeprüft im committeten Stand, Zeile 666). Die Action bricht bei fehlendem
Ignorefile ab. Der allererste Releaselauf wäre daran gescheitert, und zwar nicht an einem
Scanergebnis, sondern an einem Pfad; `supply-chain.md` §4.2 behauptet an der Stelle das Gegenteil.

Das ist der zweite Beleg für dasselbe Muster: M1 gilt als implementiert und nicht abgenommen, weil
kein Releaselauf stattgefunden hat — und jedes Mal, wenn jemand genau hinsieht, findet er etwas,
das erst ein echter Lauf gezeigt hätte.

Weitere Funde:

- **`dotnet list package --vulnerable` endet mit Exit 0**, auch bei High-Funden. Das naheliegende
  Ein-Zeilen-Gate wäre eines gewesen, das nie rot werden kann. Die Tabellenausgabe ist zudem
  lokalisiert (`Schweregrad` statt `Severity`) — ein `grep` hätte hier zufällig funktioniert.
- **wasmtime 47.0.2: RUSTSEC-2026-0222 und -0223.** Beide LOW, blocken also nicht — betroffen ist
  aber die WASI-Sandbox selbst. Empfehlung ≥ 47.0.3.
- **Der Nachweis für das Containerfilesystem-Gate fehlt** und wird ausdrücklich nicht als erfüllt
  gemeldet: Es gibt kein Release-Image, an dem sich zeigen ließe, dass das Gate scheitern kann.

`actionlint` unabhängig nachgeprüft: nur die zwei vorbestehenden Warnungen in `ci.yml`.

### Ein Zeitlimit, das die Maschine maß statt das Produkt

Der erste Gesamtlauf nach der Zusammenführung war rot: Zwei `RestFacadeTests` liefen nach je 17 s in
eine Zeitschranke, während sie auf einen stdio-Testserver warteten. Einzeln laufen dieselben Tests
12/12 grün in 10 s, der Wiederholungslauf war vollständig grün.

Ursache ist die gewachsene Last — die Suite hat mit `Bifrost.Security.Tests` und
`Bifrost.Upgrade.Tests` zwei Projekte bekommen, die selbst Prozesse und Container starten. Die
Schranke stand auf 15 s und maß damit die Auslastung der Maschine, nicht das Verhalten des Produkts.

Angehoben auf 30 s, und die Meldung nennt jetzt die tatsächlich verstrichene Zeit. Ein Zeitlimit
heraufzusetzen ist die schwächste aller Antworten; sie ist hier trotzdem richtig, weil ein Test, der
gelegentlich grundlos rot wird, mehr kostet als er sichert — man gewöhnt sich an rote Läufe. Die
Schranke bleibt scharf genug für den Fall, für den sie da ist: ein Upstream, der gar nicht hochkommt.

### Was WP3.6 sonst gebaut hat

Die Tests zählen nirgends die heute bekannten Fälle auf. Die Endpunktmatrix liest die Routen aus
der `EndpointDataSource` des laufenden Hosts und ist fail-closed — alles gilt als Management, außer
es steht ausdrücklich auf der offenen Liste. Der Auditvollständigkeitstest liest die Ausgänge aus
`InvocationStatus`; ein neuer Enum-Wert ohne Test macht ihn rot. Und die Verallgemeinerung der
OpenRPC-Lehre von heute: Statt über Transporte läuft der Korpustest über **jede** Eigenschaft im
Konfigurationsmodell, deren Name nach Geheimnis aussieht — ein neues `ClientSecret` an einem
bestehenden Transport wird gefunden, nicht nur ein neuer Transport.

Als Gegenprobe wurde der historische Fehler nachgestellt (`OpenRpcCredential` aus dem Korpus
entfernt) — der Test wird rot und nennt die Stelle.

## Abgeschlossener Meilenstein: M2 — Wiederherstellbarkeit

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
| WP2.6 Upgrade-Kompatibilitätsmatrix | `implementiert` | 50 Tests, 15 Migrationsstände × 2 Provider, Archivpfad auf beiden; **hat E6 widerlegt** |
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

- ~~**PostgreSQL-Backup fehlt.**~~ **Erledigt** (ADR-0024 E2): Sicherung über
  `pg_dump --format=custom`, Wiederherstellung über `pg_restore --single-transaction`, dasselbe
  Archivformat wie bei SQLite. FR-P020 ist damit auch für Postgres erfüllt — **unter einer
  Bedingung:** `pg_dump` und `pg_restore` müssen auf dem Wirt erreichbar sein. Fehlen sie, lehnt der
  Aufruf mit einer Meldung ab, die sagt, was fehlt und wo man es herbekommt; einen Zeilenexport gibt
  es weiterhin nicht. Geprüft in `PostgresBackupRestoreTests` gegen einen echten Server, siehe
  `docs/upgrade-matrix.md` §2.1.
- **E7 hängt jetzt an den Werkzeugen statt am Provider.** `Always` gilt bei SQLite mit einer Datei
  dahinter und bei PostgreSQL mit erreichbarem `pg_dump`; sonst `WhenAvailable`. Ein `Always`, das
  an einem fehlenden Werkzeug scheitert, wäre ein Startverbot statt einer Zusage — deshalb wird
  beim Zusammenbau gemessen statt angenommen. Für Datenbanken im Arbeitsspeicher bleibt es dabei,
  dass es nichts zu sichern gibt.
- **Offen und ausdrücklich nicht entschieden: `postgresql-client` im mitgelieferten Image.** Ohne
  ihn ist das Feature *im Container* tot, obwohl es im Code lebt. Das Gewicht steht gegen das
  350-MB-Gate; die Messung und die Optionen gehören in eine eigene Entscheidung.
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
