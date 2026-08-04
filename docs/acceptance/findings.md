# Befunde — Stand der Release-Candidate-Prüfung

> **Generiert aus `findings.json`.** Änderungen gehören in die Quelle, nicht hierher —
> `FindingsReportTests` färbt den Lauf rot, sobald beides auseinanderläuft.

**Offen:** 4 · **Behoben:** 41 · **Abdeckung ab:** M0

Vollstaendig ab M0. Die Befunde aus M0-M3 sind am 2026-08-04 aus den jeweiligen Abschnitten des Fortschrittsprotokolls uebernommen worden (BFR-F-0026). Damit gilt die Aussage "kein offener Critical- oder High-Befund" fuer das ganze Programm und nicht mehr nur ab M4.

**Kein offener Critical- oder High-Befund.** Das ist die Bedingung aus WP7.1 — im Rahmen der oben genannten Abdeckung.

## Offen

### BFR-F-0015 · Speicherwachstum unter Saettigung: 110 MB je Minute ohne Abflachen (medium)

_Quelle: M5/WP5.4 · Beleg: `artifacts/soak/20260802-155800-load-and-chaos/metrics.jsonl`_

Der Lauf ohne Wartezeit (~3 500 Aufrufe/s) zeigte ueber 6,7 Minuten RSS 615 -> 1 485 MB bei flachen Handles und Threads. Beim realistischen Lauf (242/s) bleiben 213 -> 289 MB ueber sechs Minuten — nicht ungebremst, aber auch nicht abgeflacht. Befund, keine Diagnose: Flache Handles passen auf einen Rueckstau (Verdacht Audit-Pfad) genauso wie auf eine Server-GC ohne Speicherdruck.

**Owner:** unbesetzt · **Termin:** vor M8/WP8.1 (72-h-Soak)

**Mitigation:** Der 4-Stunden-Nachtlauf bei realistischer Rate entscheidet, ob die Kurve abflacht. Bis dahin gilt die Betriebsempfehlung: Speicher beobachten, Retention aktiv halten. Kein bekannter Ausfall im Betrieb.

### BFR-F-0039 · Der Standardweg der Installation ist bis zum ersten Releaselauf kaputt (medium)

_Quelle: M1/WP1.4 · Beleg: `docker-compose.yml`_

docker compose up -d zieht ein Image, das es noch nicht gibt. Das ist die vom Pflichtenheft verlangte Richtung, aber die Luecke schliesst erst der erste veroeffentlichte Tag: Der Vorgabewert der Version in docker-compose.yml und .env.example gehoert daran gekoppelt.

**Owner:** unbesetzt · **Termin:** mit dem ersten veroeffentlichten Release (M1-Abnahme)

**Mitigation:** Bis dahin gilt der dokumentierte Weg ueber den lokalen Build; docs/operations.md nennt ihn. Kein Betreiber ausserhalb dieses Repositories ist betroffen, weil es noch kein veroeffentlichtes Image gibt.

### BFR-F-0045 · Der Nachweis fuer das Containerfilesystem-Gate fehlt (medium)

_Quelle: M3/WP3.5, ausdruecklich nicht als erfuellt gemeldet · Beleg: `.github/workflows/release.yml`_

Es gibt kein Release-Image, an dem sich zeigen liesse, dass das Gate scheitern kann. Ein Gate ohne Gegenprobe ist eine Behauptung.

**Owner:** unbesetzt · **Termin:** mit dem ersten veroeffentlichten Release (M1-Abnahme)

**Mitigation:** Das Gate ist im Workflow vorhanden und laeuft; ungeprueft ist nur, ob es rot werden kann. Der erste Releaselauf liefert das Image, an dem sich das zeigen laesst.

### BFR-F-0044 · wasmtime 47.0.2: zwei RUSTSEC-Meldungen in der WASI-Sandbox (low)

_Quelle: M3/WP3.5 · Beleg: `spikes/wasi-component-runtime/Cargo.toml`_

RUSTSEC-2026-0222 und -0223, beide LOW und damit nicht blockierend — betroffen ist aber die Sandbox selbst. Empfehlung: mindestens 47.0.3.

**Owner:** unbesetzt · **Termin:** vor M8/WP8.1

**Mitigation:** Beide Meldungen sind LOW und betreffen Pfade, die dieser Gateway nicht nutzt (geprueft im Rahmen von WP3.5). Der Versionssprung ist ein Einzeiler und gehoert in den naechsten Abhaengigkeitslauf.

## Behoben

### BFR-F-0019 · Eine auf ein Werkzeug eingeschraenkte Berechtigung wurde beim Lesen aus JSON unbeschraenkt (critical)

_Quelle: M6/WP6.1, gefunden ueber den Ende-zu-Ende-Test des Rueckwegs · Beleg: `ValueTypeJsonTests.A_grant_restricted_to_one_tool_stays_restricted`_

NamespacedToolName und CapabilityId sind Wertetypen mit Value ohne Setter; System.Text.Json nimmt bei Structs den impliziten parameterlosen Konstruktor. Ein Grant mit Scope.Tool = 'github__read' wurde beim Deserialisieren zu einem Grant OHNE Werkzeugeinschraenkung — aus 'darf genau dieses Werkzeug' wurde 'darf alles'. Ohne Ausnahme, ohne Meldung. Betroffen war jeder JSON-Weg, der eine Berechtigung traegt.

### BFR-F-0004 · Ein vorhandener Waechter war rot, und die Fertigmeldung kam trotzdem (high)

_Quelle: M5-Audit 2026-08-02, Befund 1 · Beleg: `RedactionCorpusTests.A_new_redactor_forces_a_decision_about_the_corpus`_

WP5.6 fuehrte einen neuen Maskierungsdurchgang ein; RedactionCorpusTests fiel um, und die Security-Suite wurde nach dem Einbau nie gelaufen. Die Meldung '14 Tests gruen' war wahr und meinte die selbst geschriebenen.

### BFR-F-0011 · Der Skip-Waechter der CI zaehlte nur eine einzige Testassembly (high)

_Quelle: M5-Audit, CI-Nachbau, Befund 11 · Beleg: `.github/workflows/ci.yml`_

Mit festem trx-Dateinamen ueberschreibt jede Assembly dieselbe Datei; gezaehlt wurden die Skips der zuletzt geschriebenen. Acht uebersprungene Upgrade-Tests waeren unbemerkt durchgegangen — bei einem Waechter, dessen einziger Zweck es ist, stille Skips zu verhindern.

### BFR-F-0013 · Upstream-OAuth fand die Metadaten mancher Authorization Server nicht (high)

_Quelle: M5/WP5.2, erster Lauf gegen echtes Keycloak · Beleg: `src/Bifrost.Upstream/OAuth/OAuthDiscovery.cs`_

Ein HTTP-Fehlerstatus kam als HttpRequestException heraus, waehrend die Schleife nur den eigenen Fehlertyp abfing. Sie brach beim ersten 404 ab; die beiden Ausweichpfade waren toter Code. Gegen jeden Server, der seine Metadaten nur unter {issuer}/.well-known/openid-configuration ausliefert, scheiterte die Anbindung.

### BFR-F-0016 · Die Oberflaeche war im Entwicklungslayout tot (high)

_Quelle: M5/WP5.5 · Beleg: `BlazorScriptDeliveryTests`_

_framework/blazor.web.js kam mit Status 200 und null Bytes, weil die Datei erst beim Veroeffentlichen nach wwwroot gelangt. Die Seiten sahen benutzbar aus, kein Knopf tat etwas. Dreizehn gruene eigene Tests hatten das nicht bemerkt.

### BFR-F-0017 · Der Approval-Zusteller rief seine Zieladresse ungeprueft (high)

_Quelle: M6/WP6.3, gefunden vom bestehenden SSRF-Waechter · Beleg: `SsrfCentralFetcherTests`_

Der Dispatcher oeffnete einen eigenen HttpClient an der zentralen SSRF-Pruefung vorbei. Ein Ziel im internen Netz waere damit erreichbar gewesen.

### BFR-F-0023 · Toolargumente verschwanden bei chunked uebertragenen Anfragen (high)

_Quelle: M5/WP5.5, gefunden ueber den Browserweg zur Freigabe · Beleg: `RestFacadeTests.Rest_invoke_also_works_for_a_client_that_sends_no_content_length`_

Der Invoke-Endpunkt las den Rumpf nur bei gesetztem Content-Length; bei Transfer-Encoding: chunked ist der null. Jeder Client, der die Laenge nicht vorab berechnet — darunter HttpClient.PostAsJsonAsync von .NET — verlor still seine Argumente und scheiterte an der Schemapruefung mit 'Pflichtfeld fehlt', also an der falschen Stelle. Der bestehende Test hiess works_like_curl und setzte die Laenge, wie curl es tut.

### BFR-F-0033 · Bootstrap: bei zwei gleichzeitigen Einloesungen verloren beide (high)

_Quelle: M1, erster echter Releaselauf, Befund 4 · Beleg: `BootstrapAccessTests`_

Produktfehler im Erstzugang. Zwei gleichzeitige Einloesungen desselben Setup-Tokens fuehrten dazu, dass keine von beiden zum Zug kam — die Instanz blieb ohne Zugang zurueck.

### BFR-F-0034 · Der WP0.4-Nachweis hatte unter Linux noch nie etwas geprueft (high)

_Quelle: M1, erster echter Releaselauf, Befunde 5 und 6 · Beleg: `ProcessLifecycleTests`_

Die Waisenerkennung zaehlte Prozessnamen ueber /proc/*/comm — dort sind sie auf 15 Zeichen gekuerzt. Ausgerechnet das Paket, dessen ganzer Sinn "Nachweis statt Behauptung" war, hat auf einer ganzen Plattform nichts geprueft und trotzdem gruen gemeldet. Er galt als belegt, weil er nur unter Windows gelaufen war.

### BFR-F-0036 · Das Release wurde angelegt, die Anhaenge fehlten (high)

_Quelle: M1, erster echter Releaselauf, Befund 8 · Beleg: `.github/workflows/release.yml`_

dist/* erfasste Unterverzeichnisse nicht wie erwartet. Das Release entstand, die Artefakte fehlten — genau die Zusage ohne Deckung, die der Job verhindern sollte. Beim Beheben kamen zwei Torposten dazu: leere Dateiliste bricht ab, doppelte Basisnamen brechen ab (gh haengt unter dem Basisnamen an, zwei gleichnamige Dateien ueberschrieben einander lautlos).

### BFR-F-0038 · Der Umbenennungs-Commit zog den Volume-Namen mit (high)

_Quelle: M1/WP1.4, Pruefergebnis des Lead · Beleg: `docs/operations.md`_

docker-compose.yml wechselte von mcpmcp-data auf bifrost-data. Eine Bestandsinstallation bekaeme damit ein leeres Volume und meldete sich fehlerfrei als bereit — Datenverlust ohne jede Fehlermeldung. Beim Deploy auf Badwolf aufgefallen und dort umgangen; die Falle blieb im Repository. Der Umstiegsweg steht jetzt in docs/operations.md.

### BFR-F-0040 · MCP ueber HTTP war der einzige Transport ganz ohne Zielpruefung (high)

_Quelle: M3/WP3.6, als roter Test abgeliefert · Beleg: `SsrfCentralFetcherTests`_

HttpTransportOptions trug kein AllowPrivateTargets, waehrend OpenAPI, OpenRPC und Upstream-OAuth es tragen. Ein Administrator, der http://169.254.169.254/... oder einen Verwaltungsport auf 127.0.0.1 eintrug, bekam genau den Abruf, den RemoteSpecFetcher fuer die anderen Transporte verhindert. Strukturelle Ursache: Die zentrale Pruefung war internal zu Bifrost.Upstream — eine zentrale Pruefung, die man nicht erreichen kann, ist keine.

### BFR-F-0042 · release.yml rief Trivy mit einer Ignoredatei auf, die es nicht gibt (high)

_Quelle: M3/WP3.5 — ein M1-Fehler, gefunden in M3 · Beleg: `.github/workflows/release.yml`_

Die Action bricht bei fehlendem Ignorefile ab. Der allererste Releaselauf waere daran gescheitert, und zwar nicht an einem Scanergebnis, sondern an einem Pfad; die Dokumentation behauptete an der Stelle das Gegenteil. Zweiter Beleg fuer dasselbe Muster: Jedes Mal, wenn jemand genau hinsieht, findet er etwas, das erst ein echter Lauf gezeigt haette.

### BFR-F-0001 · Teilimport: ein kaputter Eintrag machte den ganzen Plan unanwendbar (medium)

_Quelle: M4, Abnahme WP4.1-4.3 · Beleg: `docs/plans/product-readiness-status.md#vier-befunde-aus-der-abnahme-von-41-43--behoben`_

ImportPlan.CanApply galt planweit. Ein einziger unlesbarer Eintrag blockierte den Import aller uebrigen — bei einer Fremdkonfiguration mit zwanzig Servern der Normalfall.

### BFR-F-0002 · Zentrale Nachbearbeitung setzte den Fundort fest auf mcpServers/<name> (medium)

_Quelle: M4, Abnahme WP4.1-4.3 · Beleg: `docs/plans/product-readiness-status.md#vier-befunde-aus-der-abnahme-von-41-43--behoben`_

Der Pfad in einem Befund zeigte auf eine Stelle, die es in der gelesenen Datei nicht gab. Wer dort nachsah, fand nichts und hielt den Befund fuer falsch.

### BFR-F-0005 · Die Grenze des Maskierungslaufs klang nach mehr, als sie ist (medium)

_Quelle: M5-Audit 2026-08-02, Befund 2 · Beleg: `docs/plans/product-readiness-status.md#audit-gegen-den-code-2026-08-02`_

'Kein Korpuswert im Bundle' stimmte, weil die Quellen ihre Secrets unter secret-artigen Feldnamen fuehren. Ein Wert unter einem harmlosen Namen bleibt stehen. Die Grenze steht jetzt als Test da.

### BFR-F-0006 · Die Browserwege prueften ein Debug-Publish (medium)

_Quelle: M5-Audit 2026-08-02, Befund 3 · Beleg: `tests/Bifrost.Ui.E2E/Harness/PublishedGateway.cs`_

dotnet publish ohne -c nimmt immer Debug, auch wenn die CI in Release baut. Die Wege haetten unbemerkt ein anderes Artefakt geprueft als das ausgelieferte.

### BFR-F-0007 · Der Waisen-Detektor hatte keine Gegenprobe (medium)

_Quelle: M5-Audit 2026-08-02, Befund 4 · Beleg: `OrphanDetectionTests`_

'Keine verwaisten Prozesse' beruhte auf einem Pruefer, der in jedem Lauf eine leere Liste meldete — von einem kaputten nicht zu unterscheiden.

### BFR-F-0008 · 'rotates' bewies keine Rotation (medium)

_Quelle: M5-Audit 2026-08-02, Befund 5 · Beleg: `docs/plans/product-readiness-status.md#audit-gegen-den-code-2026-08-02`_

Der Test prueste, dass ein neues Refresh-Token zurueckkommt — nicht, dass das alte danach wertlos ist. Ohne das laege nach jeder Erneuerung ein weiterer gueltiger Schluessel herum.

### BFR-F-0014 · Der Messstand rechnete 11 ms Gateway-Overhead, die es nicht gab (medium)

_Quelle: M5/WP5.3 · Beleg: `docs/acceptance/performance.md`_

Abgezogen wurden die angeforderten 20 ms Upstream-Zeit, waehrend Task.Delay(20) unter Windows gut 31 ms dauert. Die Timeraufloesung erschien als Gateway-Anteil. Der Upstream misst seinen Span jetzt selbst.

### BFR-F-0018 · Ein Policy Pack konnte keine bestehende Rolle aendern (medium)

_Quelle: M6/WP6.1 · Beleg: `ConfigurationImportUpdateTests.Taking_back_an_import_restores_a_replaced_role_instead_of_deleting_it`_

Der Importweg aus M2 legte nur an; ein vergebener Name war ein Konflikt. Der interessante Fall eines Packs — dieselbe Rolle, andere Rechte — war damit nicht anwendbar. Aufgeloest durch ConfigurationImportMode.Update samt neu begruendeter Ruecknahme.

### BFR-F-0020 · Der Schreib-Port des Konfigurationsimports hiess AddRoleAsync und ersetzte trotzdem (medium)

_Quelle: M6/WP6.1 · Beleg: `ConfigurationImportUpdateTests`_

Unter der Haube ein Upsert, waehrend die Test-Attrappe wirklich nur anhaengte. Ein Test haette 'ersetzt' gemeldet, wo im Betrieb zwei Rollen desselben Namens entstanden waeren. Heisst jetzt SaveRoleAsync/SaveProfileAsync.

### BFR-F-0022 · Der Browser-Harness lief gegen eine alte Veroeffentlichung (medium)

_Quelle: M6/WP6.2 · Beleg: `tests/Bifrost.Ui.E2E/Harness/PublishedGateway.cs`_

Das Publish wurde uebersprungen, wenn die Executable schon existierte — vom vorigen Paket. Der neue Weg war rot, und die Ursache zeigte auf die Oberflaeche statt auf den Harness. Schlimmer waere der umgekehrte Fall gewesen: ein gruener Lauf gegen einen alten Stand.

### BFR-F-0025 · Ein Solution-Lauf meldete einen Fehlschlag, dessen Name verloren ging (medium)

_Quelle: M6/WP6.1 · Beleg: `docs/plans/product-readiness-status.md#ein-unaufgeklaerter-fehlschlag-festgehalten-statt-weggewischt`_

Ein Lauf ueber die ganze Solution meldete 443 statt 444 bestandene Integrationstests. Drei spaetere volle Laeufe und ein Lauf der Suite allein waren gruen. Welcher Test es war, ist nicht bekannt — der Ausgabefilter des Laufs hatte die Zeile mit dem Namen verworfen. Ohne Namen gibt es keine Diagnose, nur eine Vermutung: Assemblies laufen parallel, die Integrationstests teilen Ports und Dateien. — Erledigt am 2026-08-04: .runsettings schreibt jetzt je Assembly eine trx nach TestResults/. Wer filtert, filtert nur noch die Bildschirmausgabe. Der urspruengliche Fehlschlag bleibt unaufgeklaert; wiederholt er sich, ist der Name da.

### BFR-F-0026 · Die Befunde aus M0-M3 sind nicht in diese Liste uebernommen (medium)

_Quelle: M7/WP7.1 · Beleg: `docs/plans/product-readiness-status.md`_

Diese Liste beginnt bei M4, weil das Fortschrittsprotokoll ab dort durchnummeriert und mit Schweregrad fuehrt. Die frueheren Befunde sind in ihren eigenen Abschnitten als behoben dokumentiert, aber nicht hier. Solange das so ist, kann niemand aus dieser Datei allein sagen 'keine offenen Critical/High' — nur 'keine ab M4'. — Erledigt am 2026-08-04: Die Befunde aus M0-M3 stehen als BFR-F-0030 bis BFR-F-0045 in dieser Liste, die Abdeckungsangabe im Kopf ist auf M0 fortgeschrieben.

### BFR-F-0027 · Vier Betriebsoptionen standen in keiner Betriebsdokumentation (medium)

_Quelle: M7/WP7.1, Gegenpruefung Changelog/Migration/Rollback · Beleg: `ChangelogMigrationRollbackTests.Every_environment_option_is_documented_where_an_operator_looks`_

BIFROST_ALLOW_HOST_EXECUTION — die schaerfste Option des Produkts — stand nur im ADR und im Pflichtenheft, also dort, wo ein Betreiber nicht nachsieht. Dazu die drei Schalter der Approval-Benachrichtigung (URL, SECRET, ALLOW_PRIVATE) aus WP6.3, die nirgends in docs/ auftauchten. Eine sicherheitsrelevante Option, die nur im Code steht, wirkt trotzdem — und niemand weiss es.

### BFR-F-0028 · Eine Zeile der Rueckverfolgung war schlicht falsch (medium)

_Quelle: M7/WP7.1, beim Schreiben von CONTRIBUTING aufgefallen · Beleg: `DisabledGateTests.Every_gate_variable_is_named_in_the_contributing_guide`_

FR-P092 stand auf 'offen' mit der Begruendung, CONTRIBUTING, Code of Conduct und die Vorlagen fehlten im Repository. Sie liegen unter .github/ und sind seit Langem da — gesucht wurde in der Wurzel. Die Zeile war damit nicht zu streng, sondern falsch, und eine falsche Zeile in einer Rueckverfolgung ist genau das, was sie verhindern soll. Korrigiert auf 'erfuellt' mit Beleg; der Fund steht hier, damit die Lehre nicht verschwindet: Eine Zuordnung aus dem Gedaechtnis ist keine Pruefung.

### BFR-F-0029 · Die Tor-Tabelle im Beitragsleitfaden nannte drei von neun Toren (medium)

_Quelle: M7/WP7.1 · Beleg: `DisabledGateTests.Every_gate_variable_is_named_in_the_contributing_guide`_

CONTRIBUTING listete nur BIFROST_REQUIRE_POSTGRES, _CONTAINER und _WASI_HOST. Die sechs Tore aus M5 und M6 — Browserwege, Lastmessung, Soak, Kompatibilitaet, Grossmengen-Auditexport, Keycloak — fehlten. Wer das Repository auscheckt, liest keinen Workflow: Er faehrt die Nachweise nie und haelt einen uebersprungenen Test fuer einen bestandenen. Tabelle vervollstaendigt, ein Test haelt sie jetzt gegen den Testcode.

### BFR-F-0031 · Drei Aussagen im README und in SECURITY.md widersprachen dem Ist-Stand (medium)

_Quelle: M0/WP0.2 · Beleg: `docs/plans/product-readiness-status.md#befunde-aus-m0`_

Die Image-Groesse stand mit "< 300 MB" im README, gemessen waren 315 MB; der Releasestatus war falsch beschrieben; SECURITY.md kannte die aktuelle Version nicht. Ein CHANGELOG gab es nicht. Eine Zusage, die niemand nachrechnet, wird zur Werbeaussage.

### BFR-F-0032 · Ein Startpfad lag ausserhalb der Prozess-Hygiene (medium)

_Quelle: M0/WP0.4 · Beleg: `ProcessLifecycleTests`_

HttpConnectorIntegrationTests startete einen Prozess direkt statt ueber den Produktpfad. Ein hart abgebrochener Lauf liess ihn stehen; er hielt Port und Programmdatei, und der naechste Build scheiterte mit "wird von einem anderen Prozess verwendet" — mit einer Ursache zwei Laeufe frueher.

### BFR-F-0035 · Der Trockenlaufmodus der Lieferkette war selbst nie gelaufen (medium)

_Quelle: M1, erster echter Releaselauf, Befund 7 · Beleg: `.github/workflows/release.yml`_

Der Job supply-chain war im Trockenlauf immer rot. Ein Trockenlauf, den niemand fahren kann, ist kein Trockenlauf.

### BFR-F-0041 · Die Runtime-Diagnose verschwieg genau den Fall, fuer den sie da war (medium)

_Quelle: M3/WP3.2, selbst gemeldet · Beleg: `RuntimeCheckTests`_

ServerDiagnosticService zaehlte nur Config.Cli?.Isolation. Eine Instanz mit ausschliesslich stdio-Container-Upstreams meldete damit "keine Runtime noetig".

### BFR-F-0043 · Ein Ein-Zeilen-Gate, das nie rot werden konnte (medium)

_Quelle: M3/WP3.5 · Beleg: `.github/workflows/security.yml`_

dotnet list package --vulnerable endet mit Exit 0, auch bei High-Funden. Die naheliegende Pruefung waere eine gewesen, die nie anschlaegt. Die Tabellenausgabe ist zudem lokalisiert (Schweregrad statt Severity) — ein grep haette hier zufaellig funktioniert.

### BFR-F-0003 · Falschpositiv in der Maskenerkennung (low)

_Quelle: M4, Abnahme WP4.1-4.3 · Beleg: `docs/plans/product-readiness-status.md#vier-befunde-aus-der-abnahme-von-41-43--behoben`_

LooksMasked erkannte eine Verweisform nur als Maske; ein echter Wert dahinter waere als maskiert durchgegangen.

### BFR-F-0009 · Vertrag gegen Code dreimal auseinander (Jobname, Vorschaupflicht, Compose-Datei) (low)

_Quelle: M5-Audit 2026-08-02, Befunde 6-8 · Beleg: `docs/plans/m5-compatibility-contract.md`_

Der E2E-Job hiess chromium statt ui-e2e (Branch-Protection haengt am Jobnamen); der Vertrag schrieb 'Vorschau ist Pflicht', der Dienst erzwingt sie nicht; der Vertrag nannte eine docker-compose.oauth.yml, gebaut wurde ein Testcontainer.

### BFR-F-0010 · Zwei ungeprueste Wege im Support-Bundle (low)

_Quelle: M5-Audit 2026-08-02, Befunde 9-10 · Beleg: `docs/plans/product-readiness-status.md#audit-gegen-den-code-2026-08-02`_

Die Kategorie Logs war nur in ihrer abwesenden Form geprueft, der Vorgabepfad ohne --out gar nicht.

### BFR-F-0012 · Der eigene Waisen-Test war nicht laufplan-fest (low)

_Quelle: M5-Audit, CI-Nachbau, Befund 12 · Beleg: `OrphanDetectionTests`_

Er verlangte 'genau ein neuer Prozess' und fiel im Lauf ueber die ganze Solution um.

### BFR-F-0021 · ?format=jsonl wurde mit 400 abgewiesen (low)

_Quelle: M6/WP6.2 · Beleg: `AuditExportApiTests.The_export_streams_instead_of_answering_with_one_document`_

Die Standardbindung von Enum-Query-Parametern ist case-sensitiv; Gross- und Kleinschreibung ist in einer URL keine Aussage.

### BFR-F-0024 · Zwei Unterdrueckungen ohne Begruendung in Program.cs (low)

_Quelle: M7/WP7.1, gefunden vom neuen Waechter · Beleg: `DisabledGateTests.Every_suppression_says_why`_

Zwei #pragma warning disable CA1848 ohne Kommentar. Eine Unterdrueckung ohne Begruendung ist nicht nachpruefbar.

### BFR-F-0030 · Eine repo-weite Umbenennung verschob ein Gate, das niemand im Blick hatte (low)

_Quelle: M0/WP0.1 · Beleg: `docs/plans/product-readiness-status.md#befunde-aus-m0`_

cargo fmt --check wurde rot, weil die Kiste umbenannt wurde und der Import seitdem anders sortiert. Kein inhaltlicher Fehler, aber der Beleg dafuer, dass eine Textersetzung ueber das ganze Repository Tore verschiebt, an die dabei niemand denkt.

### BFR-F-0037 · Drei Nachweise hingen am Planer statt am Produkt (low)

_Quelle: M1, erster echter Releaselauf, Befunde 1-3 und 9 · Beleg: `docs/plans/product-readiness-status.md#was-der-erste-lauf-gekostet-hat-neun-befunde`_

Ein erfundener Token im Negativkorpus blockierte den Push; die Secret-Baseline kannte zwei Werte nicht; ein Versionstest trug ein Literal und brach beim Versionssprung; der Nebenlaeufigkeitsnachweis des Backups mass den Planer.

