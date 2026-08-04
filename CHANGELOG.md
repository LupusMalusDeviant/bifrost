# Changelog

Alle nennenswerten Änderungen an B.I.F.R.O.S.T. Format nach
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung nach
[Semantic Versioning](https://semver.org/lang/de/) — mit der Einschränkung, die eine `0.x`-Linie
ausmacht: **Breaking Changes können in einer Minor-Version auftreten**; sie stehen dann hier unter
„Geändert" mit dem Wort *Breaking* und einem Upgrade-Hinweis.

Die Einträge nennen, was ein Betreiber merkt — nicht jeden Commit. Was ein Release *nicht* belegt,
steht ausdrücklich dabei: Diese Datei ist auch der Ort, an dem offene Nachweise sichtbar bleiben.

## [Unveröffentlicht]

### Behoben

- **Der Skip-Wächter der CI zählte nur eine einzige Testassembly** (Befund aus dem M5-Audit). Der
  Testlauf schrieb alle Suiten in dieselbe `results.trx`, und mit festem Dateinamen überschreibt
  jede Assembly die vorige — geprüft wurde also die Skipzahl der zuletzt geschriebenen. Acht
  übersprungene Upgrade-Tests wären unbemerkt durchgegangen, bei einem Wächter, dessen einziger
  Zweck es ist, stille Skips zu verhindern. Jede Assembly schreibt jetzt ihre eigene Datei, gezählt
  wird über alle.
- **Der Wächter über die Redigierer war rot** (Befund aus dem M5-Audit). Das Support-Bundle brachte
  einen neuen Maskierungsdurchgang mit; der bestehende Test, der **jede** maskierende Stelle gegen
  den Negativkorpus verlangt, fiel damit um — und die Fertigmeldung für das Bundle war erfolgt, ohne
  dass die Security-Suite gelaufen wäre. Der Durchgang hat jetzt seine Korpustests, für Feldnamen
  und für Freitext. Zusätzlich ist die **Grenze** des Durchgangs als Test festgehalten: Ein Secret
  unter einem harmlosen Feldnamen bleibt stehen — er ist ein Sicherheitsnetz, kein Inhaltsscanner.
- **Die Tor-Tabelle im Beitragsleitfaden nannte drei von neun Toren.** Die sechs aus M5 und M6 —
  Browserwege, Lastmessung, Soak, Kompatibilität, Großmengen-Auditexport, Keycloak — fehlten. Wer
  das Repository auscheckt, liest keinen Workflow: Er fährt diese Nachweise nie und hält einen
  übersprungenen Test für einen bestandenen. Tabelle vervollständigt; ein Wächter hält sie jetzt
  gegen den Testcode.
- **Vier Betriebsoptionen standen in keiner Betriebsdokumentation.** `BIFROST_ALLOW_HOST_EXECUTION`
  — die schärfste Option des Produkts — stand nur im ADR und im Pflichtenheft, also dort, wo ein
  Betreiber nicht nachsieht; dazu die drei Schalter der Approval-Benachrichtigung
  (`…_WEBHOOK_URL`, `…_WEBHOOK_SECRET`, `…_WEBHOOK_ALLOW_PRIVATE`). Alle vier stehen jetzt mit
  Default, Risiko und Empfehlung in `docs/operations.md` und im englischen Spiegel; ein Wächter
  hält künftig jede `BIFROST_*`-Option gegen die Betriebsdokumente.
- **Toolargumente verschwanden bei chunked übertragenen Anfragen.** Der Invoke-Endpunkt las den
  Rumpf nur bei gesetztem `Content-Length`; bei `Transfer-Encoding: chunked` ist der `null`. Jeder
  Client, der die Länge nicht vorab berechnet — darunter `HttpClient.PostAsJsonAsync` von .NET —
  verlor damit **still** seine Argumente: Der Aufruf lief mit leeren Argumenten weiter und
  scheiterte an der Schemaprüfung mit „Pflichtfeld fehlt", also an der falschen Stelle. Mit
  `curl -d` war nie etwas zu sehen, weil curl die Länge setzt. Betrifft
  `POST /api/v1/tools/{name}/invoke` und `POST /api/v1/capabilities/{id}/invoke`.
- **`?format=jsonl` wurde mit 400 abgewiesen.** Die Standardbindung von Enum-Query-Parametern ist
  case-sensitiv; Gross- und Kleinschreibung ist in einer URL aber keine Aussage. Der Auditexport
  nimmt das Format jetzt unabhängig davon entgegen und nennt bei einem echten Tippfehler die
  möglichen Werte.
- **Eine auf ein Werkzeug eingeschränkte Berechtigung konnte beim Lesen aus JSON zu einer
  unbeschränkten werden.** `NamespacedToolName` und `CapabilityId` sind Wertetypen mit einer
  `Value`-Eigenschaft ohne Setter; System.Text.Json nimmt bei Structs den impliziten parameterlosen
  Konstruktor, und der Wert blieb `null` — ohne Ausnahme, ohne Meldung. Ein Grant mit
  `Scope.Tool = "github__read"` wurde damit beim Deserialisieren zu einem Grant **ohne**
  Werkzeugeinschränkung: Aus „darf genau dieses eine Werkzeug" wurde „darf alles". Betroffen war
  jeder JSON-Weg, der eine Berechtigung trägt. Behoben über `[JsonConstructor]`; das Drahtformat
  bleibt unverändert. Gefunden hat es der Ende-zu-Ende-Test des Policy-Pack-Rückwegs.
- **Der Schreib-Port des Konfigurationsimports hieß `AddRoleAsync` und ersetzte trotzdem** — er saß
  unter der Haube längst auf einem Upsert, während die Test-Attrappe wirklich nur anhängte. Ein Test
  hätte „ersetzt" gemeldet, wo im Betrieb zwei Rollen desselben Namens entstanden wären. Heißt jetzt
  `SaveRoleAsync` / `SaveProfileAsync`; die Attrappe verhält sich wie die echte Ablage.
- **Upstream-OAuth fand die Metadaten mancher Authorization Server nicht** (Befund aus M5/WP5.2).
  Die Suche probiert drei Standardpfade der Reihe nach — aber ein HTTP-Fehlerstatus kam als
  `HttpRequestException` heraus, während die Schleife nur den eigenen Fehlertyp abfing. Damit brach
  sie beim **ersten 404** ab, und die beiden Ausweichpfade waren toter Code: Gegen jeden Server, der
  seine Metadaten nur unter `{issuer}/.well-known/openid-configuration` ausliefert — Keycloak unter
  anderen —, scheiterte die Anbindung mit „nicht abrufbar". Gefunden hat es der erste Lauf gegen ein
  echtes Keycloak.

### Hinzugefügt

- **Policy Packs: der Plan nennt, wer danach mehr darf** (M6/WP6.1) — ein signiertes Pack wird
  vollständig geprüft, **bevor** sich eine Berechtigung ändert: Herausgeber und Vertrauensstufe,
  Objektänderungen, hier unbekannte Werkzeuge und vor allem der **Rechte-Diff je Identität**.
  Erreichbar über `bifrost policy plan|apply|revert`, über die Seite **Policy Packs** (Admin) und
  über `POST /api/v1/operations/policy/{plan,apply,revert}`. Die Übernahme ist auditiert mit
  Kennung, Version und Nutzlast-Fingerabdruck.
  - **Kein Objekt-Diff.** „Rolle geändert" liest sich harmlos und kann bedeuten, dass drei Agenten
    ab sofort auf jedem Server schreiben dürfen. Der Plan nennt jede Identität, die gewinnt — und
    die Wegnahmen daneben, weil ein Pack auch unbemerkt Rechte entziehen kann.
  - **Kein zweiter Importweg, kein zweites Vertrauensmodell:** Objektebene über den
    Konfigurationsimport, Signatur über denselben Ed25519-Verifizierer und Publisher-Trust-Store wie
    bei Connector-Paketen. Ein Pack aus einer neueren Format-Generation wird nicht teilweise
    angewendet.
  - **Die Anzeige ist nicht überspringbar.** In der CLI überspringt `--yes` die Rückfrage, nicht den
    Rechte-Diff; die Rückfrage nennt die Zahl der zusätzlichen Berechtigungen. In der Oberfläche
    bleibt der Übernehmen-Knopf gesperrt, bis jemand ausdrücklich bestätigt, den Zuwachs gesehen zu
    haben.
  - **Der Rückweg muss aufgehoben werden, sonst ist er weg:** `--undo-out <datei>` in der CLI (ohne
    die Angabe warnt der Befehl), das Textfeld auf der Seite. Im Gateway wird er **nicht**
    aufbewahrt — eine Ablage wäre eine eigene Entscheidung und steht aus.
  - **Die Momentaufnahme vor der Übernahme bleibt kein Rollback** und heißt weiterhin so. Der
    Rückweg ist eine andere Sache: Er berührt genau die Objekte, die das Pack berührt hat.

- **Konfigurationsimport kann Rollen und Profile ersetzen** (M6/WP6.1) — neuer Modus `Update` neben
  dem bisherigen `AddOnly`, das Vorgabe bleibt und sich nicht ändert. Erreichbar über Policy Packs,
  **nicht** über `bifrost config import`.
  - **Die Rücknahme steht auf neuem Grund.** Bisher galt: Was der Vorgang nur angelegt hat, kann er
    gefahrlos entfernen. Sobald ersetzt wird, stimmt das nicht mehr. Der Vorgang schreibt deshalb
    den vorigen Inhalt mit; zurückgenommen wird einzeln — Ersetztes wird **zurückgeschrieben**,
    Angelegtes entfernt. Eine Rücknahme, die eine ersetzte Rolle löschte, entzöge jeder Identität
    an ihr sämtliche Rechte.
  - **Die Id der Zielinstanz gewinnt:** Inhalt aus der Nutzlast, Id von hier — sonst zeigt die
    Zuordnung Identität→Rolle nach der Übernahme ins Leere.
  - **Kein „Delete" im Sinne eines Abgleichs:** Ein Import entfernt nichts, was in der Nutzlast
    fehlt. Das wäre eine Synchronisierung, und die nimmt weg, was die Nutzlast nur nicht kennt.

- **Ein hängender Testlauf hinterlässt jetzt Spuren** (FR-P056) — Sequenz und vollständigen
  Speicherabzug von der Testplattform (`--blame-hang`, 15 Minuten) und, wichtiger, die **Liste der
  aktiven Kindprozesse** mit Id und Startzeit von einem eigenen Wächter nach 10 Minuten ohne
  Testwechsel. Die Reihenfolge der Fristen ist der Punkt: Der Abzug beendet den Testhost, danach
  leben die Kinder nicht mehr. Ein Lauf hängt hier selten an sich selbst, sondern an einer
  Gegenstelle — und die nennt erst diese Liste beim Namen. Die CI sammelt alle drei ein.

- **Der Reverse-Proxy-Sprung ist gemessen** (M5/WP5.3) — ein echter nginx im Container vor dem
  Gateway, mit der Konfiguration aus der Betriebsdokumentation. Derselbe Lastlauf einmal direkt und
  einmal durch den Proxy: **rund 1,6 ms Anteil an der p95**, keine Fehler, NFR-01 hält auch über
  diese Strecke. Bis dahin stand in der Abnahme „belegt keinen Reverse-Proxy-Sprung"; jetzt stehen
  dort Zahlen ([docs/acceptance/performance.md](docs/acceptance/performance.md)).

- **Auditexport: JSONL und CSV, gestreamt** (M6/WP6.2, FR-P072) — `bifrost audit export`,
  `GET /api/v1/audit/export` (admin-only) und eine Export-Karte im **Audit-Log** der Oberfläche.
  Derselbe Filter wie die Ansicht, dazu ein fortsetzbarer Cursor.
  - **Der Cursor ist eine Zeilennummer, keine Seitenzahl.** Wer über Seiten blättert, verliert oder
    verdoppelt Einträge, sobald nebenher geschrieben wird — und beim Audit wird immer nebenher
    geschrieben. `--after <n>` liefert ausschließlich, was danach kam.
  - **Der Export zieht keinen Arbeitsspeicher mit:** gemessen an einer Fixture mit einer Million
    Einträgen **1 MB gehaltener Speicher bei 100 000 Zeilen, 0 MB bei 1 000 000**. Der Nachweis
    läuft bei jedem Push im CI-Job `audit-scale`.
  - **CSV trägt die maskierten Argumente nicht** — sie sind JSON und gehören nicht in eine
    Tabellenzelle; dafür hat CSV eine feste Spaltenfolge. Felder, die eine Tabellenkalkulation als
    Formel läse (`=`, `+`, `-`, `@`), werden entschärft: Ein Auditexport, der beim Öffnen Code
    startet, wäre die Umkehrung seines Zwecks.

- **Integritätskette über das Auditlog** (M6/WP6.2, FR-P073) — jede Zeile trägt einen SHA-256 über
  ihren Vorgänger **und** eine kanonische Fassung ihrer selbst. `bifrost audit verify` bzw.
  `GET /api/v1/audit/verify` rechnet nach und nennt die **erste** gebrochene Stelle mit Zeilennummer
  und Grund — nicht „ungültig". Exit-Code `4` bei einem Bruch: ein Befund, kein Programmfehler.
  - **Gefunden werden Änderung, Löschung und Umsortierung.** Belegt gegen die echte Ablage: Ein Test
    ändert eine Zeile direkt in der Datenbank, ein zweiter löscht eine. Eine bloße Prüfsumme je
    Zeile fände den zweiten Fall nicht.
  - **Was sie nicht kann, steht in ihrer eigenen Ausgabe** und nicht nur in der Dokumentation: Sie
    ist kein Beweis für Vollständigkeit, sie schützt nicht gegen einen übernommenen Wirt, und ein
    fehlender ganzer Abschnitt ist nicht erkennbar.
  - **Ein Abschnitt je Schreiberlauf, keine globale Kette.** Eine durchgehende Kette setzte genau
    einen Schreiber voraus; zwei Instanzen auf derselben Datenbank meldeten dann Manipulation, wo
    keine war — und ein falscher Alarm über Manipulation entwertet den echten Fund.
  - **Zeilen aus der Zeit vor der Kette werden als „nicht gedeckt" ausgewiesen**, statt
    stillschweigend als geprüft zu gelten. Nachträglich verkettet wird nichts.
  - Zwei additive EF-Migrationen (SQLite und PostgreSQL), keine Änderung an bestehenden Zeilen.

- **Benachrichtigung über wartende Freigaben** (M6/WP6.3, FR-P074) — ein signierter Webhook meldet
  jede neue Freigabe an ein Ziel der Wahl (`BIFROST_APPROVAL_WEBHOOK_URL` und
  `…_SECRET`), mit Wiederholung, Backoff und sichtbarem Dead-Letter-Zustand unter
  `GET /api/v1/approvals/notifications` und in der Oberfläche neben der Warteschlange.
  - **Zustellung ist von der Entscheidung getrennt:** Eine Freigabe gilt, wenn sie entschieden
    wurde — nicht, wenn eine Benachrichtigung ankam. Ein totes Ziel hält die Warteschlange nicht auf.
  - **Die Nutzlast trägt keine Toolargumente**, auch keine redigierten — nur wer, was, wann und
    einen Deep-Link hinter die Anmeldung.
  - **Dieselbe Signatur wie für eingehende Webhooks** (ADR-0013), inklusive Zeitstempel gegen
    Replay. Ohne hinterlegtes Geheimnis startet der Gateway gar nicht erst mit einem Ziel — eine
    unsignierte Benachrichtigung kann jeder fälschen.
  - **Jedes Ziel geht durch dieselbe SSRF-Prüfung wie ein Schemaimport**, bevor die Verbindung
    steht; `BIFROST_APPROVAL_WEBHOOK_ALLOW_PRIVATE` ist der ausdrückliche Schalter für ein Ziel im
    eigenen Netz. Gefunden hat diese Lücke der bestehende Wächter über ausgehende Verbindungen —
    der erste Entwurf rief die konfigurierte Adresse ungeprüft.
  - **Noch nicht dabei:** SMTP, Eskalation nach Ablauf und ein Diagnosecheck für tote Ziele.

- **Token-Cockpit** (M6/WP6.4, FR-15) — unter **Betrieb → Token-Cockpit** und über
  `GET /api/v1/usage/{identity}` sieht ein Administrator, was ein Profil einem Agenten an Schema
  zumutet (*Exposure*) und was davon im Zeitfenster wirklich aufgerufen wurde (*Invocation*).
  - **Die Simulation zeigt Einsparung UND Konsequenz:** wie viele Tokens ein vorgeschlagener
    Profilschnitt spart — und welche der entfernten Werkzeuge im Fenster benutzt wurden.
  - **Es ändert nichts.** Kein Pfad im Cockpit schreibt an Profil, Rolle oder Grant; wer einen
    Vorschlag übernimmt, tut das in der Profilverwaltung.
  - **„Ungenutzt im Fenster", nie „unnötig".** Ein Werkzeug ohne Aufruf kann das sein, was einmal im
    Quartal den Betrieb rettet.
  - **Jede gezogene Grenze steht im Ergebnis:** gekürztes Zeitfenster, gekappte Liste, abgeschnittene
    Auditmenge — Letztere meldet ihre Zahlen ausdrücklich als Untergrenzen.
  - Admin-only: Die Auswertung nennt, was eine fremde Identität benutzt hat.

- **Connector Developer Kit** (M6/WP6.5) — `bifrost-connector` baut, signiert und prüft Pakete
  **ohne laufendes Gateway**: `init`, `build`, `sign`, `verify`, `test`, mit denselben Exitcodes wie
  die Betriebsbefehle. Anleitung und Beispiel: [`docs/connectors/developer-kit.md`](docs/connectors/developer-kit.md)
  und `samples/counter-connector`.
  - **Kein zweites Paketformat:** Gebaut wird mit demselben Bauer und geprüft mit demselben Leser
    wie im Gateway. Was das Werkzeug akzeptiert, akzeptiert der Server — und umgekehrt.
  - **`build` erzeugt die abgetrennte Signatur der Component gleich mit.** Ohne sie lehnt die Probe
    zu Recht ab, und ein Entwickler müsste sie mit einem fremden Werkzeug von Hand erzeugen.
  - **`test` prüft, was der Paketleser nicht prüft:** ob die Signatur des Einstiegspunkts wirklich
    zur Component passt. Sonst fällt das erst beim Laden auf dem Rechner des Betreibers auf.
  - **Der private Schlüssel bleibt in seiner Datei** und erscheint in keiner Ausgabe — auch in
    keiner Fehlermeldung. Ein vorhandener Schlüssel wird nie überschrieben.
  - **Keine Registry.** Ein Connector ist eine Datei, die man weitergibt.

- **OAuth gegen eine echte Gegenstelle** (M5/WP5.2, FR-P062/P063) — Keycloak läuft als Container im
  PR-Lauf (`.github/workflows/oauth.yml`), und der Weg wird vollständig durchgespielt: Discovery,
  Autorisierungsanfrage mit PKCE S256 und `resource`-Indikator, Anmeldung, Einlösung des Codes,
  Erneuerung des Tokens.
  - **Mit Gegenproben, nicht nur mit dem guten Tag:** Ein Code mit falschem `code_verifier` wird
    abgewiesen (sonst belegte der Test nur, dass ein Parameter mitgeschickt wird), ein
    Antwort-Issuer aus fremder Quelle entwertet den Vorgang, und Klartext-Discovery bleibt ohne
    `AllowPrivateTargets` verboten.
  - **Was weiterhin aussteht:** der Lauf gegen einen externen, gehosteten Anbieter. Der Job dafür
    ist eingerichtet, meldet aber ausdrücklich „nicht gelaufen", solange keine Zugangsdaten
    hinterlegt sind — ein grüner Lauf ohne Konfiguration wäre die Behauptung, es sei geprüft worden.

- **Support-Bundle** (M5/WP5.6, FR-P075) — ein weitergabefähiges Archiv für die Fehlersuche:
  `bifrost support-bundle [--preview]`, `POST /api/v1/operations/support-bundle` und die Oberfläche
  unter **Betrieb**. Enthalten sind Version, Konfiguration **ohne Secretwerte**, Diagnosebericht,
  Zustand von Datenbank und Upstreams, Migrationsstand und Laufzeitangaben, dazu ein
  `manifest.json` mit Prüfsumme je Datei.
  - **Vorschau vor Erzeugung ist Pflicht**, nicht Komfort: Ein Archiv, dessen Inhalt man erst nach
    dem Schreiben sieht, ist bereits geschrieben — und danach verlässt es das Haus.
  - **Auditinhalte und Credentialmaterial sind keine Kategorie**, auch nicht per Schalter. Wer
    Auditdaten braucht, nimmt den Auditexport; die Trennung ist der Sinn der Sache.
  - **Ein zentraler Maskierungslauf** deckt jedes Dokument ab — Felder mit Secret-Namen sowie Werte
    wie `Password=…`, `Bearer …` und Zugangsdaten in URLs. Das Manifest nennt, *wie oft* maskiert
    wurde, nicht *was*.
  - **Übersprungene Kategorien werden benannt.** Findet sich keine Logdatei, sagt das Bundle das und
    legt die Befehle bei, mit denen man das Log am Prozessverwalter holt — mit dem Hinweis, dass
    diese Ausgabe nicht durch den Maskierungslauf geht.
  - Die Erzeugung ist admin-only und wird auditiert.

- **Browser-E2E für die kritischen Wege der Oberfläche** (M5/WP5.5, FR-P054) — Anmeldung mit
  falschem und richtigem Passwort, Abmelden, geschützte Seite ohne Sitzung, tatsächliche
  Interaktivität der ausgelieferten Oberfläche, Server über das Formular anschließen bis `Healthy`,
  und ein Kommando, das es nicht gibt, als Fehler. Chromium ist PR-Pflicht, Firefox und WebKit
  laufen nachts (`.github/workflows/e2e.yml`).
  - **Gegen das veröffentlichte Ergebnis, nicht gegen `bin/`.** Im Entwicklungslayout kommt
    `_framework/blazor.web.js` mit Status 200 und null Bytes; der Blazor-Circuit startet dann nie und
    die Oberfläche ist tot, während sie benutzbar aussieht. Im Veröffentlichungsergebnis ist die
    Datei vorhanden — der Browsertest prüft ab jetzt genau das, was ausgeliefert wird.
  - **Kein Auth-Bypass:** Der UI-Benutzer entsteht über `--reset-ui-admin`, also den Weg eines
    Betreibers ohne Oberfläche, und meldet sich danach im Formular an wie jeder andere. Der Lauf
    geht über HTTPS, weil das Anmelde-Cookie außerhalb der Entwicklungsumgebung `Secure` ist.
  - **Beweise nur im Fehlerfall:** Trace, Video und Bildschirmfoto; ein grüner Lauf räumt auf.
  - **Noch nicht abgedeckt:** Freigabe, Tool-Pin-Änderung, Rolle/Profil, Keyausgabe,
    Backup/Restorestatus und das Erstsetup.

- **Soak- und Chaos-Lauf** (M5/WP5.4, FR-P050/P051/P052) — deterministische Dauerlast aus Liste,
  Lesen, Schreiben, Skill und Audit gegen einen echten Gateway-Prozess, mit periodischen Störungen:
  Upstream hart abgeschossen, Upstream mitten im Aufruf abgestürzt, Verbindung ab- und wieder
  angeschaltet. Kurzlauf im PR, Nachtlauf über vier Stunden (`.github/workflows/soak.yml`).
  - **Belegt (7 Minuten, 242 Aufrufe/s, 8 Störungen):** 103 036 erfolgreiche Aufrufe, Handles und
    Threads flach, Kindprozesse konstant, **keine verwaisten Upstream-Prozesse**, und nach der
    letzten Störung bedient der Gateway wieder.
  - **Die Messreihe wird Zeile für Zeile geschrieben**, damit ein abgebrochener Lauf noch etwas
    hergibt — was er beim ersten Abbruch prompt auch tat.
  - **Der Lauf urteilt nicht über zu kurze Fenster.** Unter fünf Minuten sagt die Auswertung
    ausdrücklich „keine Aussage" statt „kein Wachstum".
  - **Offen:** der 72-Stunden-Lauf (Release Candidate), Freigabe-Last, PostgreSQL-Unterbrechung und
    knapper Plattenplatz.

### Bekannte Einschränkung

- **Speicherwachstum unter Sättigung** (Befund aus dem Soak-Lauf, offen): Bei ~3 500 Aufrufen je
  Sekunde wächst der Arbeitsspeicher des Gateways über sieben Minuten stetig um rund 110 MB je
  Minute (615 → 1 485 MB), ohne abzuflachen — bei flachen Handle- und Threadzahlen. Bei
  realistischer Last (242 Aufrufe/s) bleiben davon 213 → 289 MB über sechs Minuten. Ob das ein
  Rückstau ist oder eine Server-GC ohne Speicherdruck, ist **nicht** entschieden; der Nachtlauf
  über vier Stunden ist das Mittel, mit dem sich beides unterscheiden lässt.

- **Lastmessung über die echte Strecke** (M5/WP5.3, FR-P064) — der Gateway läuft dabei als eigener
  Prozess hinter Kestrel, über TCP und wahlweise mit TLS, gegen einen Upstream mit definierter
  Antwortzeit; 32 Clients, 3 × 30 s Dauerlast statt eines Stoßes. Ergebnis und Umstände stehen in
  [`docs/acceptance/performance.md`](docs/acceptance/performance.md), das Rohergebnis als
  trendfähiges JSON unter `artifacts/perf/`.
  - **Der Gateway-Anteil ist jetzt beziffert:** p50 1,4 ms, p95 2,2 ms, über TCP wie über TLS und in
    beiden Betriebsarten praktisch gleich — bei ~92 000 Aufrufen je Szenario ohne einen einzigen
    Fehler. NFR-01 hält damit auch über die echte Strecke.
  - **TLS kostet CPU, keine Latenz** (39 % → 59 % eines Kerns), und sessionloser gegen
    Sitzungsbetrieb nimmt sich in der Latenz nichts — die offene Frage aus ADR-0023 ist gemessen.
  - **Ein Messfehler im eigenen Harness wurde dabei gefunden und behoben:** Der erste Lauf meldete
    11 ms Overhead, weil er die *angeforderte* Upstream-Zeit abzog, während `Task.Delay(20)` unter
    Windows gut 31 ms dauert. Abgezogen wird jetzt der vom Upstream **selbst gemessene** Span.
  - **Was der Durchsatz nicht ist:** eine Kapazitätsgrenze. Der Lauf ist ein geschlossener
    Regelkreis; die Zahl folgt aus Nebenläufigkeit und Antwortzeit.

- **Veröffentlichte Kompatibilitätsmatrix** (M5/WP5.1, FR-P060/P061) —
  [`docs/acceptance/compatibility.md`](docs/acceptance/compatibility.md) sagt je Client, Version,
  OS, Transport und Betriebsart, was geprüft ist und was nicht. Der Bericht wird aus
  `tests/compatibility/matrix.json` **erzeugt**; eine handgepflegte zweite Fassung kann es nicht
  geben.
  - **`untested` ist ein Wert, kein Weglassen.** Alle sechs Pflichtclients stehen in der Tabelle,
    auch die ungeprüften — heute sind 51 von 56 Zellen offen, und genau das steht da.
  - **Ein `pass` ist eine Zusicherung.** Der Probelauf fährt das offizielle MCP-C#-SDK gegen einen
    echten Gateway-Host und prüft die Matrix gegen die Wirklichkeit; eine Behauptung, die nicht
    mehr stimmt, macht den Lauf rot. Belegt sind damit `tools`, `resources`, `prompts` und
    `skills` auf Windows.
  - **Was der Lauf nicht belegt, steht im Bericht:** kein TCP, kein TLS, kein Reverse Proxy (der
    Probelauf nutzt den In-Process-Host), keine Linux-Zeile, keine GUI-Clients.

- **Backup und Restore für PostgreSQL** (FR-P020, ADR-0024 E2) — bis hierher die größte offene
  Lücke aus M2. `bifrost backup` und `bifrost restore` arbeiten dort über `pg_dump --format=custom`
  und `pg_restore --single-transaction`; Archivformat, Manifest, Prüfsummen, Key-Ring und
  Verschlüsselung sind dieselben wie bei SQLite. Die Nutzlast heißt `database/bifrost.dump`.
  - **Das Format ist eine Entscheidung, keine Vorliebe:** `custom` ist eine Datei statt eines
    Verzeichnisbaums, es ist *Daten* statt eines auszuführenden SQL-Skripts (ein Archiv ist
    Fremdeingabe), und es ist das einzige Format, mit dem `pg_restore --single-transaction` geht —
    entweder die Wiederherstellung ist ganz durch, oder die Datenbank ist unberührt (E5).
  - **Voraussetzung: `pg_dump` und `pg_restore` auf dem Wirt.** Fehlen sie, lehnt der Aufruf mit
    einer Meldung ab, die sagt, *was* fehlt und *wo* man es herbekommt. **Einen Zeilenexport als
    Ersatz gibt es nicht** und wird es nicht geben: Er wäre eine zweite, schlechter geprüfte
    Sicherung derselben Daten, und der Unterschied fiele erst beim Zurückspielen auf.
    `BIFROST_POSTGRES_BIN` benennt ein Verzeichnis; ist es gesetzt, wird ausschließlich dort gesucht.
  - **Das Passwort geht über eine `PGPASSFILE`** (auf Unix `0600`, nach dem Aufruf gelöscht) und
    nicht über die Kommandozeile — die steht in der Prozessliste jedes Benutzers auf dem Rechner.
- **`BFR-DB-0006` — „dieser `pg_dump` kann diesen Server nicht sichern", *bevor* jemand sichern
  will.** `bifrost doctor` vergleicht die Hauptversion des tatsächlich gefundenen `pg_dump` mit der
  des Servers, gegen den der Gateway wirklich läuft, und meldet: „Client 16, Server 17 — dieser
  Client kann diesen Server nicht sichern; gebraucht wird >= 17."
  - **Der Anlass ist gemessen, nicht ausgedacht:** Ubuntu 24.04 liefert Client 16, ein aktueller
    Server ist 17 oder 18. Wer beides kombiniert, hat keine Sicherung — auch keine vor einer
    Migration (E7), denn die läuft über dasselbe Programm. Bisher fiel das erst beim Sichern auf,
    also im Ernstfall.
  - **Der Befund rät nicht.** Fehlt eine der beiden Zahlen — kein Client, keine Verbindung, eine
    Version, die sich nicht lesen lässt —, sagt er genau das, statt eine Verträglichkeit zu
    behaupten. Ein unbelegtes „passt schon" beruhigt genau den, der sich gerade auf seinen Rückweg
    verlässt.
  - Scheitert eine Sicherung trotzdem an der Versionslage, trägt die Fehlermeldung des Werkzeugs
    jetzt die Abhilfe (PGDG-Paket, `BIFROST_POSTGRES_BIN`) und den Verweis auf `BFR-DB-0006`.

- **`BFR-DB-0007` — „ist das Audit-Log lückenlos?"** `bifrost doctor` und der Bericht der laufenden
  Instanz sagen jetzt, **wie viele** Audit-Ereignisse verlorengegangen sind und **seit wann** —
  getrennt nach Ursache: bei der Annahme verworfen (Puffer voll, Überlast) oder beim Schreiben
  aufgegeben (die Datenbank hat den Batch mehrfach abgelehnt).
  - **Die Zähler gab es schon, den Weg in einen Bericht nicht.** Ein Verlust stand in einer
    Logzeile und sonst nirgends. Für ein Produkt, dessen erstes Verkaufsargument ein lückenloses
    Audit-Log ist, ist das die unangenehmste Möglichkeit: Nicht der Aufruf schlägt fehl, sondern
    nur sein Nachweis — und wer das Log ansieht, sieht keine Lücke, sondern eine kürzere Liste.
  - Ein einziger Verlust ist ein **Fail**, keine Warnung. Ist keine Sonde verdrahtet (etwa in der
    CLI ohne laufenden Server), meldet der Befund `Skipped` mit Begründung — „keine Auskunft" ist
    ausdrücklich nicht „nichts verloren".

### Geändert

- **SQLite läuft jetzt im WAL-Modus, mit einer Wartezeit auf gesperrte Schreibvorgänge**
  (`journal_mode=WAL`, `busy_timeout=5000`). ADR-0007 hat WAL immer vorausgesetzt („SQLite im
  WAL-Modus schafft die Ziellast"); eingeschaltet war er nie.
  - **Der zweite Schreiber war die Instanz selbst:** Audit-Flush (1 s), Audit-Retention (sofort
    beim Start) und die Fristprüfung der Vorgänge laufen ab dem Startende dauerhaft auf derselben
    Datei. Traf eine Schreibung aus der Oberfläche darauf, war die Datei gesperrt —
    `SQLite Error 5: 'database is locked'`, reproduzierbar unter Last.
  - **Achtung, Bestandsdaten:** `journal_mode` ist eine Eigenschaft der **Datei**, nicht der
    Verbindung. Eine bestehende Installation wird beim ersten Start dauerhaft umgestellt. Neben
    `bifrost.db` entstehen dann `bifrost.db-wal` und `bifrost.db-shm`; **beide gehören zur
    Datenbank** — wer die Datei von Hand kopiert, kopiert ohne sie einen älteren Stand. Sicherung
    und Wiederherstellung über `bifrost backup`/`bifrost restore` sind davon unberührt: Sie
    benutzen seit jeher die Online-Backup-API und nicht das Kopieren der Datei (ADR-0024 E2).
  - **Der Rückweg:** `BIFROST_SQLITE_JOURNAL_MODE=delete` stellt beim nächsten Start auf das
    klassische Rollback-Journal zurück; die `-wal`/`-shm`-Dateien verschwinden dabei. Gedacht ist
    er für Datenverzeichnisse, auf denen SQLite den gemeinsamen Speicher nicht anlegen kann (ein
    Netzlaufwerk etwa) — er kauft den Sperrfehler zurück. Ein unbekannter Wert **bricht den Start
    ab**, statt still auf `wal` zu fallen: Wer `walk` schreibt, hält seinen Rückweg sonst für
    eingelegt.
- **Ein fehlgeschlagener Audit-Batch wird nicht mehr nach dem ersten Versuch verworfen.** Im
  Vorgabemodus `best-effort` wird jetzt bis zu dreimal geschrieben, bevor aufgegeben wird — und ein
  aufgegebener Batch **wird gezählt** und erscheint unter `BFR-DB-0007`.
  - Der Verzicht auf jeden zweiten Versuch war nie durch den Vertrag gedeckt: Dieser gilt der
    Hot-Path-Seite (`IAuditSink.Record` darf einen Tool-Aufruf nie verzögern oder scheitern
    lassen), die Schreibschleife läuft aber im Hintergrunddienst und hält keinen Aufruf auf.
    Der häufigste Grund für einen Fehlschlag war ein Schreib-Lock, den ein anderer gerade hielt —
    und der ist beim zweiten Anlauf weg.
  - **Unbegrenzt wird weiterhin nur im Modus `compliance` wiederholt.** Bei einer dauerhaft kaputten
    Datenbank liefe der Puffer sonst voll, und der Verlust entstünde stattdessen bei der Annahme —
    verschoben, nicht vermieden. `best-effort` bleibt die Vorgabe; neu ist, dass sein Bruch sichtbar
    ist.
- **Vor-Migrationssicherung auf PostgreSQL** (ADR-0024 E7): Der Start prüft einmal beim Hochfahren,
  ob die Werkzeuge erreichbar sind. Sind sie es, gilt dort jetzt `PreMigrationBackupRequirement.
  Always` — **ohne Sicherung keine Migration**, dieselbe Zusage wie bei SQLite. Sind sie es nicht,
  bleibt es bei `WhenAvailable`: Der Start warnt und migriert. Ein `Always`, das an einem fehlenden
  Werkzeug scheitert, wäre kein Schutz, sondern ein Startverbot — deshalb wird gemessen statt
  angenommen.
- Damit ist die Aussage „auf PostgreSQL läuft jedes Upgrade ohne Rückweg" **an eine Bedingung
  geknüpft** statt allgemein wahr. Ohne Clientpaket gilt sie unverändert weiter.
- **CI und Releaselauf installieren `postgresql-client`**, falls der Runner ihn nicht mitbringt.
  Ohne ihn überspringen sich die neuen Backupfelder selbst — und ein übersprungener Nachweis ist auf
  Linux ein stiller Ausfall, kein Ergebnis. Kein Gate wurde dabei angehoben oder gelockert.

### Behoben

- **`SQLite Error 5: 'database is locked'` unter Last** — und, teurer, der **stille Verlust von
  Audit-Einträgen**, den er auslöste. Schrieb die Oberfläche zeitgleich mit dem Audit-Flush, war die
  Datenbank gesperrt; der Audit-Eintrag dazu verschwand ohne Spur. Sichtbar wurde das als
  sporadischer CI-Fehlschlag („der UI-Testaufruf erscheint mit Origin=Ui im Audit-Log", 30 s
  vergeblich gewartet) — der Aufruf war gelungen, nur sein Nachweis fehlte. Behoben durch WAL, die
  Wartezeit auf Schreib-Locks und den wiederholten Schreibversuch (alle drei oben unter „Geändert").
- **Zwei PostgreSQL-Befunde aus dem ersten echten CI-Lauf** — beide in der Prüfung, keiner im
  Sicherungsweg selbst:
  - Das Feld zur **Vor-Migrationssicherung** startete einen fest verdrahteten `postgres:17-alpine`
    und lief damit auf dem Ubuntu-Läufer (Client 16) in genau die Versionsunverträglichkeit, gegen
    die die übrige Suite längst gewappnet war. Die Ableitung „welcher Server passt zum vorhandenen
    `pg_dump`" steht jetzt an **einer** Stelle (`PostgresServerImage`) und gilt für beide Suiten.
    Lässt sich keine passende Version ableiten, wird übersprungen mit Begründung statt auf
    irgendeine Version auszuweichen.
  - Das PostgreSQL-Feld erwartete `BackupSections.All`, legte in seiner Testinstanz aber **kein
    Connector-Paket** an — anders als das SQLite-Gegenstück. Ein Bereich steht nur dann im Manifest,
    wenn wirklich etwas darin liegt; die Erwartung war falsch, die Sicherung vollständig. Die
    Testinstanz legt jetzt dieselbe Datei an wie die SQLite-Seite.

### Offen und ausdrücklich nicht entschieden

- **Ob `postgresql-client` in das mitgelieferte Container-Image gehört.** Ohne ihn ist das Feature
  *im Container* nicht benutzbar, obwohl es im Code lebt. Das vollständige Clientpaket zieht auf
  Ubuntu die Perl-Kette nach (`libperl`, `perl-modules`), weil `/usr/bin/pg_dump` dort ein
  Perl-Wrapper ist — nach den Paketangaben rund 57 MB, womit das 350-MB-Gate reißen würde. Der
  schlanke Weg (nur die beiden Programme plus `libpq5` aus einer Builder-Stufe) liegt bei rund
  4–5 MB. **Beides ist gerechnet, nicht gemessen**, und die Entscheidung steht aus; das Dockerfile
  ist unverändert.
- **„Backup einer älteren Version → Restore in den heutigen Stand" auf PostgreSQL.** Das
  SQLite-Gegenstück ist geprüft, das PostgreSQL-Feld noch nicht — siehe
  `docs/upgrade-matrix.md` §4.5.

### Dokumentation

- **Englische Kernseiten** unter `docs/en/`: Quickstart, Betrieb, Sicherheit, Fehlersuche,
  Support/Releasekanäle sowie drei Tutorials (Einzelbetrieb, kleines Team,
  freigabepflichtiges Deployment). Betrieb und Mitarbeit sind damit ohne Deutschkenntnisse möglich.
- **Eine Sprachregel, und sie steht auf beiden Seiten** (`docs/i18n.md`): Bei Widerspruch gilt die
  Fassung in der **Ursprungssprache** des Dokuments; jede Seite nennt sie im Kopf. Zwei Wahrheiten
  über denselben Betriebsablauf sind schlimmer als eine Fassung in der falschen Sprache. Die
  historischen ADRs bleiben ausdrücklich deutsch und sind **kein 1.0-Blocker**.
- **Beitragsgrundlage:** `.github/CONTRIBUTING.md`, `.github/CODE_OF_CONDUCT.md`, Issue-Vorlagen
  (Fehler, Wunsch, Doku) und eine PR-Vorlage. Die PR-Vorlage fragt nach dem **Nachweis**, nicht nach
  grünen Tests, und nach dem, was bewusst nicht getan wurde.
- **Produktlücken stehen jetzt dort, wo jemand danach sucht**, statt nur im Prüfprotokoll:
  - `docs/operations.md` und `docs/en/operations.md` — dass `AllowPrivateTargets` für **bestehende**
    HTTP-Upstreams unentschieden und damit **erlaubt** ist. Neu angelegte bekommen seit WP3.2 ein
    ausdrückliches `false`; Konfigurationsimport und Restore lassen den Wert bewusst, wie er war,
    weil sie eine Konfiguration reproduzieren und nicht erzeugen;
  - dass auf PostgreSQL **jedes Upgrade ohne Rückweg** läuft (keine Sicherung, kein Downgrade);
  - dass der Upgrade-Harness **altes Schema mit heutigem Code** prüft und eine Formatregression
    eines früheren Builds prinzipiell nicht findet.
- **Neun Befunde aus dem ersten Releaselauf** sind in die Fehlersuche übernommen worden, statt im
  Statusdokument zu bleiben (`docs/en/troubleshooting.md`).
- Veraltete Aussagen nachgezogen, jeweils als datierter Nachtrag statt durch Überschreiben:
  `docs/security-gates.md` (CODEOWNERS existiert; der erste Releaselauf hat stattgefunden — G4 ist
  gelaufen, aber weiterhin nie rot gewesen), `docs/upgrade-matrix.md` (§4.2 Releaseartefakt
  vorhanden, §4.3 `CryptographicNamesTests` angelegt), `SECURITY.md` (`v0.12.0` in der
  Versionstabelle).

- **Sechs tote interne Verweise** repariert — Folgeschaden der Umbenennung: Die Textersetzung hat
  `mcp-mcp` auch in Linkzielen zu `bifrost` gemacht, die Dateien heißen aber weiter
  `0001-mcp-mcp-meta-gateway.md` (in `docs/acceptance/`, `docs/adr/0001`, `docs/adr/0003`,
  `docs/plans/0001`). Geändert wurde ausschließlich der Pfad, kein Inhalt. Geprüft ist das jetzt
  maschinell: **575 interne Verweise über 103 Markdown-Dateien lösen auf**, und der Prüfer wurde
  gegen einen absichtlich kaputten Baum gegengeprüft — ein Linkprüfer, der nichts findet, findet
  auch nichts, wenn etwas kaputt ist.

### Gefunden, nicht geändert

- `.env.example` und der Vorgabewert in `docker-compose.yml` nennen weiterhin `0.11.0`, während
  `0.12.0` das aktuelle Release ist. Wer `.env.example` unverändert kopiert, zieht die
  Vorgängerversion. Beide Dateien liegen außerhalb der Dokumentationszone; in `docs/en/quickstart.md`
  steht der Hinweis, die Zeile selbst zu setzen.

## [0.12.0] — 2026-08-01

Zwei Meilensteine: **Wiederherstellbarkeit** (sichern, zurückspielen, migrieren, diagnostizieren)
und **sichere Vorgaben** (Isolation, Key-Ring-Schutz, Erstzugang ohne Log-Credentials).

**Was diese Version nicht belegt:** Es ist der **erste Lauf dieser Releasepipeline überhaupt**. Sie
wurde in M1 gebaut und war seitdem nie gelaufen — in dieser Zeit sind drei Fehler darin gefunden
worden, die nur ein echter Lauf gezeigt hätte (ein umbenanntes Volume, ein Verweis auf eine
nicht existierende Ignoredatei, und ein Schwachstellen-Gate, das mit Exit 0 endete). Alle drei sind
behoben; ob weitere darin stecken, sagt erst dieser Lauf.


### Hinzugefügt

- **Sicherung und Wiederherstellung** für SQLite-Instanzen: ein ZIP mit vorangestelltem Manifest,
  optional mit Passphrase verschlüsselt, prüfbar ohne Restore. Die Datenbank wird über die
  Online-Backup-API gelesen, nicht kopiert — bei aktivem WAL ist eine Dateikopie eine Sicherung,
  der man nicht ansieht, dass sie älter ist als gedacht. Das Zurückspielen läuft über ein
  Staging-Verzeichnis und sichert vor jedem Überschreiben den Altzustand.
  **Für PostgreSQL gibt es das noch nicht**; der Weg lehnt mit Meldung ab, statt still etwas
  anderes zu tun.
- **Migrationsschutz beim Start:** Genau eine Instanz migriert (Advisory Lock bzw. Dateilock), der
  Zustand steht in einer Journaltabelle statt in einem Log. Ein abgebrochener Lauf wird beim
  nächsten Start erkannt und verweigert den Schreibbetrieb mit Recovery-Hinweis, statt zu
  reparieren. Ein neueres, unbekanntes Schema wird abgelehnt, nicht heruntergestuft.
- **`bifrost doctor`** mit stabilen, maschinenlesbaren Codes (`BFR-CFG-*`, `BFR-DB-*`, `BFR-KEY-*`,
  `BFR-NET-*`, `BFR-RT-*`, `BFR-UP-*`) — darunter der Fall, der im Betrieb am teuersten war:
  falsches Volume, leere Datenbank, Meldung „bereit". Ein Check, der nicht laufen kann, meldet das
  mit Begründung und besteht nicht still.
- **Befehle für den Betrieb:** `backup create` / `backup verify`, `restore`, `doctor`,
  `config export` / `config import`, `db unblock` — alle mit einheitlichen Exit-Codes
  (0 Erfolg · 2 Bedienfehler · 3 Warnung · 4 Fehler · 5 Archiv ungültig · 6 Ziel nicht leer), dazu
  eine Betriebsseite in der Oberfläche und Endpunkte unter `/api/v1/operations/`.
  Eine Passphrase wird **nicht** als Argument angenommen — sie stünde sonst in der Prozessliste und
  in der Shell-Historie.
- **`bifrost-server --db-unblock`** löst den Riegel aus `BFR-DB-0101` auch dann, wenn der Gateway
  wegen ebendieses Riegels gar nicht erst startet. Vorher hätte ein Betreiber eine Datenbankzeile
  von Hand löschen müssen.
- **Konfigurationsexport und -import**, versioniert. Der Standardexport enthält **keine
  Secretwerte**, sondern Referenzen, die allein aus dem Ort abgeleitet sind — wer „die
  Konfiguration" in ein Git-Repository legt, legt dort keine Zugangsdaten ab. Was nicht
  exportierbar ist, steht als solches im Dokument.
- Rückfrage vor zerstörenden Aktionen in der Oberfläche (Rolle, Identität, API-Key, Upstream,
  UI-Nutzer, Webhook, Profil, Guardrail). Zweistufig in der Zeile des Objekts, ohne Dialogfenster.
- `CHANGELOG.md` (diese Datei) und ein Status-Dokument für die Produktreife unter
  `docs/plans/product-readiness-status.md`.

### Geändert

- Tabellen: Beschreibungsspalten sind auf drei Zeilen begrenzt, die Aktionsspalte bleibt am rechten
  Rand stehen. Vorher schob ein langer Beschreibungstext die Knöpfe „Bearbeiten"/„Versionen" aus
  dem sichtbaren Bereich.
- Die Inhaltsbreite ist nicht mehr auf 1180 px festgenagelt; die Zeilenlänge hält jetzt der Absatz
  selbst (82 Zeichen), Tabellen dürfen die Breite nutzen.

- **Native Programme laufen isoliert.** Neu angelegte stdio- und CLI-Upstreams führen ihr Programm
  in einem Container aus: non-root, read-only Wurzeldateisystem, keine Capabilities, kein Netzwerk
  ohne ausdrückliche Zielliste, Grenzen für Speicher, CPU, Prozesse, Ausgabe und Zeit. Bestehende
  Upstreams ändern ihr Verhalten **nicht**.
  Fehlt die Container-Runtime, kommt der Upstream nicht hoch — es gibt keinen Rückfall auf den
  Host. Ein Ausweichen wäre eine stille Herabstufung genau der Eigenschaft, wegen der jemand den
  Container gewählt hat.
- **Erstzugang über ein kurzlebiges Setup-Token statt über das Log.** Der erste Start schreibt kein
  Adminpasswort und keinen API-Key mehr ins Anwendungslog, sondern stellt ein einmalig
  einlösbares Token in eine Datei aus, die nur das Dienstkonto lesen darf. Benutzername und
  Passwort wählt der Betreiber selbst unter `/setup`.
  **Breaking für die Einrichtung:** `docker compose logs bifrost` zeigt keine Zugangsdaten mehr —
  der Weg steht im README. Bestehende Installationen sind nicht betroffen und behalten ihre Admins.
- **Der Key-Ring lässt sich schützen, und sein Verlust wird erkannt.** Zertifikat oder Datei-Secret
  einrichtbar, Passwort über `…_FILE` zuführbar (nicht mehr nur über die Umgebung). Fehlt
  Schlüsselmaterial, das vorhanden sein müsste, bricht der Start ab, statt einen leeren Ring
  anzulegen und sich als bereit zu melden.
- **Hostausführung ist eine Entscheidung, kein Vorgabezustand.** Neue Instanzen verbieten sie;
  bestehende laufen weiter, aber die Übernahme wird geschrieben, auditiert und im Diagnosebericht
  namentlich ausgewiesen (ADR-0025).

### Behoben

- **MCP über HTTP war der einzige Transport ohne Zielprüfung.** Der Endpunkt ging ungeprüft in den
  Transport, während OpenAPI, OpenRPC und der OAuth-Issuer private Adressen abweisen. Ebenso sondete
  der OAuth-Verbindungsendpunkt gegen die genannte Adresse, **bevor** irgendetwas sie geprüft hatte.
  Neu angelegte Konfigurationen weisen private Ziele jetzt ab; bestehende bleiben unverändert, bis
  jemand den Schalter setzt.
- **Ein Archiv aus einer neueren Version wurde eingespielt, statt abgelehnt zu werden.** Das
  Rückwärts-Tor verglich eine Versionsangabe, die das Archiv über sich selbst macht und die für
  jedes Archiv gleich war. Geprüft wird jetzt der Migrationsstand: Kennt diese Installation ihn
  nicht, stammt das Archiv aus einer neueren Version. Gefunden hat das die Upgrade-Matrix, indem
  sie gegen die Zusage getestet hat statt für sie.
- **Ein Zugangsdatum stand im Klartext in Oberfläche und API.** Die Ausgabemaskierung für
  Upstream-Konfigurationen ließ das Credential eines OpenRPC-Servers unmaskiert durch.
- **Bearbeiten konnte einen Upstream stillschweigend zerlegen.** In die Gegenrichtung fehlte die
  Übernahme bestehender Werte für WASI-Secrets und das OpenRPC-Credential: Wer einen solchen
  Upstream in der Oberfläche speicherte, schrieb die Maske `***` als echten Wert. Der Upstream lief
  bis zum nächsten Neustart weiter und scheiterte dann an einem Passwort aus drei Sternen.
- `cargo fmt --check` war rot: Die Umbenennung auf `bifrost_wasi_component_spike` änderte die
  alphabetische Importreihenfolge in `spikes/wasi-component-runtime/src/main.rs`.
- Die Größenangabe des Images im README (`< 300 MB`) widersprach dem geprüften CI-Gate (350 MB) und
  der gemessenen Größe (315 MB).
- Die Versionstabelle in `SECURITY.md` kannte `v0.11.0` nicht.

## [0.11.0] — 2026-07-31

Erste Fassung unter dem Namen **B.I.F.R.O.S.T** (vorher MCP-MCP), zusammen mit dem Umstieg auf die
MCP-Spec-Revision `2026-07-28`.

### Geändert

- **Breaking (Protokoll):** Der sessionlose Kern ist Vorgabe (SEP-2567). Laufende MCP-Sitzungen
  überstehen das Update **nicht** — ein Client mit `Mcp-Session-Id` wird abgewiesen und muss neu
  verbinden. Der Sitzungsbetrieb bleibt über `BIFROST_MCP_STATELESS=0` erreichbar; dort werden
  allerdings *alle* Clients auf `2025-11-25` zurückgehandelt ([ADR-0023](docs/adr/0023-stateless-kern-und-mrtr.md)).
- **Breaking (Namen):** Projekte, Namespaces, Assemblies, Umgebungsvariablen (`MCPMCP_*` →
  `BIFROST_*`), Datenbankdatei, MCP-Serverkennung, Docker-Image und Repository heißen neu.
- Die Freigabe-Rückfrage läuft über MRTR statt server-initiierter Elicitation.
- Listen tragen Cache-Hinweise (`ttlMs`, `cacheScope: private`) statt `tools/list_changed`.
- Der Herzschlag zu Upstreams nutzt `server/discover` statt `ping` (das es nicht mehr gibt).
- Neues Designsystem der Oberfläche.

### Migration

- Alt benannte Umgebungsvariablen werden übernommen und einmal beim Start gemeldet.
- Eine vorhandene `mcpmcp.db` wird weiterbenutzt; es wird **keine** neue Datei angelegt.
- Der DataProtection-Anwendungsname und die drei Verschlüsselungszwecke behalten bewusst ihre alten
  Werte — ein neuer Name würde jeden gespeicherten Geheimtext unlesbar machen.
- Das UI-Cookie heißt neu: einmal neu anmelden.
- Beim Deployment gilt: **Volume- und Service-Namen nicht mitumbenennen.** Der Volume-Name trägt den
  Compose-Projektnamen als Präfix; ein umbenanntes Volume ist ein leeres Volume.

### Nachweise

- 783 .NET-Tests, 110 Rust-Tests grün.
- NFR-01/02 auf dem neuen Protokoll nachgemessen (Median aus fünf Läufen):
  `tools/call` p95 = 9,3 ms, `tools/list` p95 = 14,2 ms, 0 Fehler.
- **Nicht belegt:** Durchsatz (der Harness misst einen 0,1-Sekunden-Stoß), Verhalten über echtes
  Netz/TLS, sowie sessionlos gegen Sitzungsbetrieb im Vergleich.

## Ältere Versionen

Für `v0.5.0` bis `v0.10.0` existieren Git-Tags, aber keine gepflegten Changelog-Einträge; ein Teil
davon hat auch keinen GitHub-Release-Eintrag. Diese Datei beginnt bewusst bei `0.11.0` und wird
nicht rückwirkend erfunden — was dort passiert ist, steht in der Commit-Historie und in den ADRs.
