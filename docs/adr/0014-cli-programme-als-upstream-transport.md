# ADR-0014: CLI-Programme als vierter Upstream-Transport

- **Status:** Akzeptiert als Transport; Sicherheitsmodell durch ADR-0017/0018 eingeschränkt
- **Datum:** 2026-07-24
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

> **Security-Update 2026-07-24:** Ein festes Binary und shell-freie Argumente bilden keine
> Sandbox. Der gehärtete Hostmodus verlangt absolute/kanonische Pfade, isoliertes Environment,
> typisierte Manifeste und Limits; für nicht vertrauenswürdige Programme sind WASI oder Container
> gemäß ADR-0017/0018 erforderlich.

Das Gateway bündelt heute drei Werkzeug-Quellen uniform hinter einem aggregierten Katalog und der einen Invoker-Pipeline: MCP-Server über stdio und Streamable HTTP (ADR-0005) sowie REST-APIs über OpenAPI-Import (ADR-0008). Was fehlt, ist die größte real existierende Werkzeug-Klasse: beliebige **Kommandozeilen-Programme** (`git`, `ffmpeg`, `kubectl`, eigene Skripte), die weder MCP sprechen noch eine OpenAPI-Spec mitbringen. Der Anspruch ist, *jede* Werkzeug-Art anschließen zu können, sodass ein Agent sie über dieselben Meta-Tools (`search_tools`/`describe_tool`/`invoke_tool`) findet und aufruft — ohne dass zwischen MCP, API und CLI ein Unterschied spürbar ist und ohne das Werkzeug selbst umzubauen.

Die Upstream-Schicht ist interface-first geschnitten: `IUpstreamConnector` deklariert sein `Kind`, der `UpstreamSupervisor` wählt den Connector per `connectors.ToDictionary(c => c.Kind)`. Ein neuer Transport ist damit ein rein additives Feature. Die entscheidende Frage ist deshalb nicht, *ob* CLI in die Architektur passt, sondern *wie sicher* man beliebige Prozessausführung in ein Gateway lässt, das ein Agent autonom bedient.

**Kernfrage:** Wie binden wir beliebige CLI-Programme als vollwertige, transport-agnostische Upstream-Tools ein, ohne die Katalog-, Invoker- und Persistenz-Schicht zu ändern und ohne dem Gateway eine unkontrollierte Remote-Code-Execution-Fläche aufzureißen?

## Anforderungen

### Funktional

- CLI-Programme erscheinen als normale Tools im aggregierten Katalog, mit Namespacing wie jeder Upstream (FR-03).
- Aufruf über dieselbe Pipeline und dieselben Meta-Tools wie MCP-/API-Tools — für den Aufrufer kein Unterschied.
- Ein Upstream bündelt ein Programm mit mehreren benannten Kommandos (je Kommando ein Tool).
- Das Werkzeug selbst bleibt unverändert — keine Wrapper-Pflicht beim Nutzer.

### Nicht-Funktional

- **Minimaler Eingriff:** keine Änderung an Katalog, Invoker oder Persistenz (die Upstream-Config ist ein DataProtection-verschlüsselter Blob, ADR-0007 — ein neues Feld reist mit).
- **Sicherheit:** keine Shell-Interpretation (keine Injection, keine Befehlsverkettung); ein fest konfiguriertes Binary pro Upstream als implizite Allowlist; Ausführung als non-root Gateway-User; Timeout mit Prozess-Kill; Output-Begrenzung gegen Memory-DoS.
- **Governance bleibt wirksam:** RBAC (ADR-0006), Guardrail (ADR-0011), Approval (ADR-0012) und Audit greifen automatisch, weil jeder Aufruf durch denselben `IToolInvoker` läuft.

## Betrachtete Optionen

### Option 0: Kein CLI-Support (Status quo)

Bei MCP + OpenAPI bleiben. CLIs sind nur erreichbar, wenn jemand sie vorab in einen MCP-Server oder eine REST-API verpackt.

**Positiv:**
- Null neue Angriffsfläche, kein Code.
- Das Gateway führt niemals selbst ein beliebiges Programm aus.

**Negativ:**
- Verwirft die Kern-Anforderung „jede Werkzeug-Art".
- Der gesamte Bestand an CLI-Tooling bleibt außen vor.
- Verlagert die Arbeit vollständig auf den Nutzer.

### Option 1: CLI extern als selbstgebauter stdio-MCP-Wrapper

Der Nutzer schreibt pro CLI einen kleinen stdio-MCP-Server, der die Kommandos ausführt, und hängt diesen als vorhandenen Stdio-Upstream (ADR-0005) an.

**Positiv:**
- Null Gateway-Code, nutzt den bereits existierenden Stdio-Transport.
- Volle Freiheit im Wrapper.

**Negativ:**
- Bürde und Boilerplate pro CLI.
- Jeder Wrapper erfindet Ausführung, Timeout, Output-Handling und Sicherheit neu — uneinheitlich und fehleranfällig.
- Die Sicherheitsmaßnahmen liegen außerhalb des Gateways und sind zentral weder erzwingbar noch prüfbar.

### Option 2: Nativer CliUpstreamConnector als vierter Transport

Ein `CliUpstreamConnector` (`Kind = Cli`) führt ein fest konfiguriertes Programm anhand eines Manifests benannter Kommandos aus. Jedes Kommando ist ein Tool; die Ausführung ist einmal zentral gehärtet.

**Positiv:**
- Eine einheitliche, gehärtete Ausführung für alle CLIs statt N Eigenbauten.
- Erscheint uniform im Katalog und in der REST-/OpenAPI-Fassade (ADR-0008); die Werkzeuge bleiben unangetastet.
- Minimaler Eingriff dank interface-first: Enum-Wert + Config-Feld + Connector + DI-Zeile + Validator-Case.
- Governance greift ohne Zusatzcode.

**Negativ:**
- Bringt bewusst eine Prozessausführungs-Fläche ins Gateway.
- Erfordert ein explizites Manifest — im Prototyp kein Auto-Discovery der Kommandos.
- Die Verantwortung, welches Binary freigegeben wird, liegt beim Admin.

## Vorschlag des Autors

Option 2. Die Anforderung „jede Werkzeug-Art, uniform, ohne Umbau" ist nur mit einem nativen Connector sauber erfüllbar. Option 1 erfüllt sie funktional, scheitert aber an der entscheidenden nicht-funktionalen Anforderung: zentral erzwingbare, einheitliche Sicherheit — verteilte Eigenbau-Wrapper liefern das prinzipiell nicht. Das RCE-Risiko ist real, aber es ist im Kern *dasselbe*, das der Stdio-Transport (ADR-0005) längst trägt: Auch dort startet das Gateway beliebige lokale Prozesse. Der einzige neue Faktor ist, dass CLI-Argumente vom Agenten parametrisiert werden — und genau dafür existieren die Guardrail- (ADR-0011) und Approval-Schichten (ADR-0012) bereits.

## Entscheidung

**Gewählte Option:** „Nativer CliUpstreamConnector als vierter Transport"

Ausschlaggebend war die einheitlich erzwingbare Sicherheit plus der minimale Eingriff. Die Ausführung ist strikt shell-frei (`ProcessStartInfo.ArgumentList` — jedes Argument literal, keine Verkettung), mit fixem Executable pro Upstream, Timeout/Kill und Output-Cap; riskante CLIs werden über die bestehende `ApprovalPolicy` freigabepflichtig. Bewusst in Kauf genommen werden das explizite Manifest (statt fragilem `--help`-Parsing) und die zusätzliche Prozess-Fläche.

## Konsequenzen

### Positiv

- Eine zusätzliche große Werkzeugklasse ist erreichbar; eine pauschale sichere Unterstützung jeder
  Werkzeug-Art ist damit ausdrücklich nicht behauptet.
- Der große Bestand an CLI-Tooling wird nutzbar, ohne die Programme anzufassen.
- Die gesamte Governance (RBAC/Guardrail/Approval/Audit) wird ohne Zusatzcode wiederverwendet.
- Rein additiv: kein Eingriff in Katalog, Invoker oder Persistenz.

### Negativ

- Neue Missbrauchsfläche (Prozessausführung). Mitigation: kein Shell, fixes Binary, non-root, Timeout/Kill, Output-Cap, Approval-Gate für riskante Kommandos.
- Fehlkonfiguration liegt beim Admin: ein zu mächtiges Binary ohne Approval ist de facto eine Shell. Das Design verhindert Injection, nicht Fahrlässigkeit.
- Kein Auto-Discovery — neue Kommandos erfordern eine Manifest-Ergänzung.

### Folge-Entscheidungen

- Strukturiertes Sub-Command-Manifest mit feinen JSON-Schemas je Kommando statt des generischen `args`-Arrays.
- UI-Formular auf der Servers-Seite (der Prototyp nutzt nur die REST-Registrierung).
- Ob ein Default-Approval-Zwang für alle `Cli`-Upstreams erzwungen wird (Policy-Voreinstellung).
- Optionales, opt-in `--help`-Parsing zur Manifest-Vorbefüllung.

### Review

**Reality-Check geplant für:** 2026-09-04

## Weitere Informationen

### Scope

Betrifft die Upstream-Connector-Familie in `Bifrost.Upstream` und den Config-Vertrag in `Bifrost.Abstractions`. Der Prototyp ist bewusst generisch (ein `args`-String-Array je Kommando) und **nicht** produktionsgehärtet. Ausgenommen und als Folge-Entscheidungen gelistet: UI, Auto-Discovery, feingranulare Schemas.

### Referenzen

- [ADR-0001](./0001-zentraler-proxy-gateway-statt-direktanbindung.md) — der Rahmen, in dem alle Tools hinter einem Endpoint zusammenlaufen.
- [ADR-0005](./0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md) — CLI ist ein weiterer prozessbasierter Transport in derselben Supervisor-Familie.
- [ADR-0008](./0008-api-mcp-bridge-als-erstklassige-fassaden.md) — CLI-Tools erscheinen wie MCP-Tools auch in der REST-/OpenAPI-Fassade.
- [ADR-0006](./0006-rollenbasiertes-rbac-mit-default-deny.md), [ADR-0011](./0011-secret-erkennung-als-guardrail.md), [ADR-0012](./0012-approval-flows-asynchron.md) — die Governance, die den CLI-Aufruf automatisch umschließt.
