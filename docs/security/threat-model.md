# Threat-Model & Security-Posture — MCP-MCP v1.0

Stand: 2026-07-24. Ergänzt [SECURITY.md](../../SECURITY.md).

## Vertrauensgrenzen

```
[ Agent / REST-/CLI-Client ] --API-Key--> [ GATEWAY ] --stdio/HTTP/OpenAPI/CLI--> [ Upstream ]
[ Mensch ]              --Cookie---> [  (hält alle Credentials)  ]
```

Der Gateway ist der zentrale Vertrauensanker (ADR-0001): Er terminiert jeden Call, erzwingt RBAC/Rate-Limits und hält sämtliche Upstream-Credentials verschlüsselt. Kompromittierung des Gateway-Hosts = Kompromittierung aller angeschlossenen Systeme. Entsprechend härten (non-root — das Container-Image tut das —, TLS-Proxy, minimale Netzexposition).

## Bestätigt sauber (Audit)

- **AuthN/AuthZ:** `/mcp` und `/api` beide hinter API-Key-Middleware (401 ohne gültigen Key); alle Management-Endpoints hinter Global-Grant-Schranke; kein ungeschützter Management-Pfad.
- **SQL-Injection:** keine — ausschließlich parametrisierte EF-LINQ, kein Raw-SQL.
- **XSS:** Blazor-Auto-Encoding; die zwei `MarkupString`-Stellen sind statische Literale; fremde Tool-Beschreibungen/Audit-Inhalte werden encodiert.
- **Secret-Leakage:** `RedactionService` maskiert Secret-Muster in Audit-Argumenten;
  `UpstreamConfigRedactor` maskiert stdio-/CLI-Environment, HTTP-Header und OpenAPI-Credentials in
  Admin-Antworten; Connection-Test- und CLI-Prozessausgaben entfernen bekannte
  Konfigurationssecrets.
- **Crypto:** PBKDF2-SHA256, 100 000 Iterationen, 16-Byte-Salt (CSPRNG), `FixedTimeEquals`.
- **OpenAPI-Parser-DoS:** `$ref`-Tiefe auf 32 gecappt, Zyklen/externe Refs abgelehnt.

## In v1.0 gehärtet (Audit-Findings behoben)

| # | Finding | Fix |
|---|---|---|
| 1 | UI-Cookie ohne `Secure`-Flag | `SecurePolicy = Always` außerhalb Development |
| 3 | OpenAPI-Spec ohne Größenlimit (Memory-DoS) + SSRF/File-Read | 10-MB-Cap beim Laden (Datei + HTTP-Stream), 30-s-Timeout beim Spec-Fetch. ⚠️ **Der SSRF-Teil war damit nicht behoben** — siehe Finding 8. |
| 4 | Username-Enumeration per Timing | Dummy-PBKDF2-Verify im „User nicht gefunden"-Pfad |
| 5 | Header-Parameter-Injection (CR/LF) im OpenAPI-Connector | CR/LF-Werte werden abgelehnt |
| 6 | `/readyz` gab Upstream-Topologie anonym preis | nur noch aggregierte Zahlen |

## Nach v1.0 gefunden und behoben

| # | Finding | Fix |
|---|---|---|
| 7 | **Klartext-Secrets im Audit-Log über den Meta-Tool-Pfad.** `MetaToolService` schrieb die Argumente ungefiltert; bei `invoke_tool` enthält `args.arguments` die kompletten Ziel-Argumente. Ein Call über den Lazy-Pfad persistierte damit Passwörter/Tokens im Klartext, während derselbe Call über `tools/call` korrekt maskiert wurde. Gefunden bei einem unabhängigen Abgleich aller Muss-FRs gegen den Code, nicht durch den ursprünglichen Security-Audit. | Der Meta-Pfad läuft durch denselben `IRedactionService`; Regressionstest hält die Invariante. Betroffen sind Bestands-Logs aus v1.0/v1.1 — wer den Lazy-Pfad genutzt hat, sollte die Audit-Tabelle prüfen und ggf. betroffene Zeilen löschen sowie die dort sichtbar gewordenen Credentials rotieren. |
| 8 | **SSRF über den OpenAPI-Konnektor.** Finding 3 hatte nur die Größengrenze geschlossen; die Zielprüfung fehlte weiter. Ein Admin-konfigurierter Upstream konnte damit auf `169.254.169.254`, `127.0.0.1` oder einen Nachbarn im Firmennetz zeigen — und zwar auf drei Wegen: über die Spec-URL, über den `servers`-Eintrag einer harmlos wirkenden (auch lokalen) Spec, und über eine Weiterleitung, der `HttpClient` von sich aus folgte. Aufgefallen beim Bau des OpenRPC-Konnektors, der die Prüfung von Anfang an hatte. | Beide Konnektoren teilen sich `RemoteSpecFetcher`: Auflösung **aller** Adressen des Namens, Abweisung von Loopback/privat/Link-Local/CGNAT, Weiterleitungen einzeln geprüft. Geprüft werden **Spec-Quelle und Ziel-API**. Im Aufrufpfad folgt kein Konnektor mehr automatisch einer Weiterleitung — ein 3xx kommt als Fehler beim Aufrufer an. **Verhaltensänderung:** Ziele im internen Netz brauchen jetzt `AllowPrivateTargets: true` (UI: Häkchen im OpenAPI-Formular); bestehende Upstreams auf `localhost` bleiben ohne diese Angabe stehen und melden es beim Namen. |
| 9 | **Rug Pull: still geänderte Tool-Definitionen.** Ein Upstream, dem einmal vertraut wurde, ändert später die Beschreibung eines Tools und schleust darüber Anweisungen in den Kontext des Modells — ohne dass an der Konfiguration etwas auffällt. Kein MCP-Standard normiert Integrität von Tool-Definitionen; das OWASP MCP Security Cheat Sheet verlangt sie ausdrücklich („pin tool definitions using cryptographic hashes and alert on any changes"), CVE-2025-54136 zeigt den Fall real. Der `SchemaRef.Hash` existierte im Code schon, wurde aber nie mit etwas verglichen — Struktur ohne Verbraucher. | Der Supervisor bildet bei jeder Discovery einen Fingerabdruck über Name, Beschreibung und (kanonisiertes) Eingabeschema und prüft ihn gegen den festgehaltenen Stand. Abweichung ⇒ das Tool wird **zurückgehalten**: nicht im Inventar, nicht im Katalog, nicht aufrufbar, mit eigener Audit-Zeile. Ein Administrator nimmt die neue Fassung ausdrücklich an. Zurückgehalten wird nur das geänderte Tool — ein Schutz, der bei jedem Update den ganzen Server anhält, wird abgeschaltet. **Grenze:** Trust-on-first-use schützt gegen Änderungen nach der Aufnahme, nicht gegen einen von Anfang an bösartigen Upstream. |

## WASI-Pluginpfad (Review 2026-07-25)

Der out-of-process laufende WASI-Host ([ADR-0020](../adr/0020-wasi-runtime-out-of-process-rust-host.md))
ist eine eigene Vertrauensgrenze: Das Gateway hält Component-Bytes, Signatur und Secrets, der Host
prüft die Signatur, setzt Grants durch und führt aus. Vollständiger Review:
[wasi-runtime-security-review.md](wasi-runtime-security-review.md).

**Bestätigt sauber:**

- **Kein Governance-Bypass.** Rate-Limit ist die erste Schranke im `ToolInvoker`, noch vor dem
  Katalog-Lookup, danach RBAC, Schema, Guardrail, Approval, Audit — transportunabhängig, also auch
  für WASI. Es gibt keinen Weg zum Host daran vorbei; der Host spricht nie mit DB oder Stores.
- **Default-deny je Interface, vor der Instanziierung.** Nicht gewährte WASI-Interfaces werden gar
  nicht erst in den Linker gehängt; ein Component, das eines importiert, startet nicht.
- **Nur signierte Components.** Ed25519 gegen den persistierten Trust-Store; leerer Store lädt
  nichts; ein Entzug stoppt laufende Upstreams sofort und wird auditiert.
- **Limits je Aufruf** (Fuel, Epoch-Deadline, Linear-Memory, Output) und **Grant-Audit je Load**
  (Modulhash, Publisher, Runtime, erteilte Grants).

## Akzeptierte / dokumentierte Restrisiken

- **stdio-Upstreams ohne Sandbox** (ADR-0005): Admin-kontrollierter Command/Args/Env läuft ungesandboxt als Kindprozess mit Gateway-Rechten. Trust-Boundary: **nur vertrauenswürdige Server anschließen**; nur Admins dürfen Upstreams anlegen. Container-Isolation pro Upstream ist v2-Kandidat.
- **CLI-Hostmodus ohne Sandbox** (ADR-0014/0018): Absolute kanonische Pfade, Roots, optionaler
  SHA-256-Pin, isoliertes Environment, typisierte Parameter, Byte-/Zeit-/Parallelitätslimits und
  Prozessbaum-Kill reduzieren die Angriffsfläche, bilden aber keine Kernel-Sandbox. Untrusted native
  Programme benötigen den geplanten Containerpfad; neue Plugins sollen WASI Components verwenden.
- **DataProtection-Key-Ring standardmäßig im Klartext auf der Platte** (`<datadir>/keys/`): entschlüsselt die at-rest verschlüsselten Upstream-Credentials. ✅ **v1.1 entschärft:** per `MCPMCP_KEYRING_CERT_PATH` lässt sich der Key-Ring mit einem X509-Zertifikat verschlüsseln (siehe [operations.md](../operations.md#key-ring-schützen)); ohne Konfiguration warnt der Gateway beim Start. Bleibt es beim Default, gilt weiterhin: **Datenvolume-Zugriff restriktiv** halten und wie ein Secret behandeln.
- **Bootstrap-Key/UI-Passwort einmalig im Klartext geloggt**: bewusste Henne-Ei-Ausnahme (nur bei leerer DB, einmalig, LogLevel Warning). Ohne sie wäre eine frische Instanz unbenutzbar.
- **Login-Endpoint ohne Antiforgery-Token**: vor der Anmeldung existiert kein gültiges Token; Login-CSRF-Restrisiko durch `SameSite=Strict` mitigiert.
- **UI-Tool-Test unter Global-Grant-Identität**: UI-Operatoren können jedes Tool testen, unabhängig vom per-Key-RBAC — gerahmt durch die UI-Rollen (nur Operator/Admin). So gewollt („Test-Aufruf mit Admin-Rechten").
- **Existenz-Leak bei `tools/call`-Deny**: ein verbotenes Tool liefert `Denied` (statt `ToolNotFound`), bestätigt also seine Existenz. `describe_tool` leakt bewusst nicht (verhält sich wie „nicht gefunden"). Minor Info-Disclosure.
- **Federations-Loop-Erkennung deckt nur den direkten Selbstbezug** (FR-05): Der Header
  `X-McpMcp-Instance` wird beim Aufbau der Upstream-Verbindung *einmal* gesetzt und kennt den
  auslösenden Request nicht — eine Instanz-Kette lässt sich damit nicht weiterreichen. Erkannt wird
  daher A→A, **nicht** A→B→A. Die Fehlermeldung behauptete zwischenzeitlich „direkt oder transitiv";
  das war eine Zusicherung, die der Mechanismus nie eingelöst hat, und ist korrigiert.
  **Mitigation:** Der Call-Timeout je Upstream (`DefaultCallTimeout`, FR-09) begrenzt den Schaden —
  ein zyklischer Verbund läuft in Timeouts statt in unbegrenzte Rekursion, und die Fehlerquote im
  Dashboard wird sofort auffällig. **Betriebsregel:** Gateway-Verbünde azyklisch konfigurieren.
  Echte transitive Erkennung bräuchte Call-Metadaten statt Verbindungs-Header — v2-Kandidat.
- **WASI: 16 gleichzeitige Aufrufe je Host-Prozess.** Seit Vertrag v4 laufen Aufrufe nebeneinander,
  jeder mit eigenem Store. Das kehrt die frühere Kapazitätsgrenze um und schafft dafür eine neue
  Frage: `MaxMemoryBytes` gilt **pro Aufruf**, der Bedarf wäre also das Produkt aus Limit und
  Anzahl gleichzeitiger Anfragen. Deshalb die harte Obergrenze von 16 mit `too-many-calls` darüber —
  vorher schützte schlicht die Serialisierung. Aufrufe auf einer persistenten Instanz bleiben
  seriell, weil es diese Instanz nur einmal gibt.
- **WASI: Abbruch ist bestätigt, aber nicht garantiert.** `cancel` trappt den Guest über die Epoche
  und wird erst mit `confirmed: true` beantwortet, wenn der Aufruf wirklich geendet hat. Bleibt die
  Bestätigung binnen fünf Sekunden aus, meldet der Host `confirmed: false` — der Guest läuft dann
  weiter, bis Fuel oder Frist ihn stoppen. Ein Guest, der sich nicht trappen lässt, ist damit
  nicht abbrechbar; die Reißleine bleiben die Limits, nicht der Abbruch.
- **WASI: Secrets liegen im Host-Prozessspeicher** über dessen Laufzeit, und ein Secret-Grant zieht
  `wasi:cli/environment` nach sich — das Component kann damit **alle** gesetzten Variablen
  auflisten, nicht nur die eigenen. Bewusste Folge der gewählten Injektionsform; eine eigene
  WASI-Secret-Schnittstelle wäre enger, war aber nicht gewollt. Werte stehen in keiner Antwort und
  keinem Audit.
- **WASI: eine persistente Instanz teilt internen Zustand zwischen Aufrufern.** Mit
  `Wasi.PersistentInstance` (nötig für `resource`-Handles) lebt **eine** Guest-Instanz pro Upstream.
  Handles sind pro Aufrufer getrennt — ein fremdes Handle ist „unbekannt", ohne zu verraten, ob es
  existiert. Der **interne** Zustand des Components (Globals, linearer Speicher) ist davon nicht
  berührt: Ein Component kann quer über Aufrufer hinweg Zustand führen, und ein bösartiges tut das.
  Deshalb ist das Flag **aus** voreingestellt und gehört nur an Upstreams, die Resources brauchen.
  Wer strikte Trennung braucht, legt pro Mandant einen eigenen Upstream an. Eine Instanz je
  Aufrufer wäre die technische Antwort und ist bewusst nicht gebaut — sie kostet Speicher und
  Instanziierungszeit je Aufrufer.
- **WASI: Platten-Cache schützt nicht gegen „gleicher Benutzer".** Kompilate sind ausführbarer
  Code, den die Publisher-Signatur nicht abdeckt; sie tragen deshalb einen HMAC unter einem
  host-lokalen Schlüssel. Das schützt gegen fremden Schreibzugriff und Bitfehler — wer als der
  Host-Benutzer läuft, liest den Schlüssel und könnte ohnehin das Binary austauschen.
  **Betriebsauflage:** Cache-Verzeichnis gehört dem Host-Benutzer, für andere nicht schreibbar.
- **WASI: Trust-Store-Integrität hängt am DB-Schreibzugriff.** Wer in die Datenbank schreibt, kann
  einen Publisher hinzufügen. Das galt vorher genauso (Config-Blob), ist jetzt aber zentralisiert
  und auditiert.
- **WASI: Speichergrenze zählt Einträge, nicht Byte.** Der Modul-Cache hält höchstens 8 Kompilate;
  wasmtime gibt den Speicherbedarf eines fertigen `Component` nicht her. Bei sehr großen Modulen
  bleibt der Verbrauch damit nach oben offen.
- ~~**PBKDF2 100k < OWASP-Empfehlung (600k)**~~ ✅ **in v1.1 behoben:** neue Hashes nutzen 600 000 Iterationen. Bestandshashes tragen ihre Iterationszahl im Format und bleiben verifizierbar (per Test belegt), ein Upgrade sperrt also niemanden aus.

## Reporting

Schwachstellen bitte über [GitHub Private Vulnerability Reporting](https://github.com/LupusMalusDeviant/mcp-mcp/security/advisories/new) melden (siehe SECURITY.md).
