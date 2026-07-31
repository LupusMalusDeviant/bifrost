# PRD 0001 — B.I.F.R.O.S.T: Self-hosted Meta-MCP-Gateway

| | |
|---|---|
| **Status** | Aktiv |
| **Datum** | 2026-07-17 |
| **Autor** | Senior PM (Claude) im Auftrag des Product Owners |
| **Product Owner / Sponsor** | Projektinhaber (kr4nk1mk0pf@googlemail.com) |
| **Consulted** | Senior-Tech-Specialist (Architektur, Machbarkeit, Performance) |
| **Verwandte Dokumente** | ADRs unter `docs/adr/`, Plan unter `docs/plans/` |

---

## 1. Problem / Motivation

**Business-/User-Sicht:**
Wer heute mehrere KI-Agenten (Claude Code, eigene Agenten, andere MCP-fähige Clients) mit Werkzeugen versorgen will, muss jeden MCP-Server einzeln an jeden Agenten anschließen. Das führt zu:

1. **Konfigurations-Wildwuchs** — jede Agent-Config kennt jeden Server; Änderungen (neuer Server, neuer API-Key, Server umgezogen) müssen an N Stellen nachgezogen werden.
2. **Kontext-Verschwendung** — jeder angeschlossene MCP-Server lädt seine kompletten Tool-Schemas in den Kontext des Agenten. Bei 10+ Servern sind das schnell zehntausende Tokens pro Konversation, die Geld kosten und die Aufmerksamkeit des Modells verwässern.
3. **Blindflug** — es gibt keine zentrale Stelle, die beantwortet: *Welcher Agent hat wann welches Tool mit welchen Parametern aufgerufen?* Weder für Debugging noch für Sicherheits-Audits.
4. **Keine Zugriffskontrolle** — jeder Agent, der einen Server angeschlossen hat, kann *alle* dessen Tools nutzen. Es gibt keine Möglichkeit, einem Agenten nur lesende Tools zu geben und einem anderen auch schreibende.
5. **Kein Hot-Swap** — Server hinzufügen, entfernen oder neu konfigurieren erfordert Neustart der Agenten-Session.

**Was bricht, wenn nichts passiert:** Mit jedem weiteren Agenten und jedem weiteren MCP-Server wächst der Verwaltungsaufwand quadratisch (Agenten × Server). Sicherheits- und Kostenrisiken bleiben unsichtbar. Die Marktrecherche (Q1/2026) zeigt: Bestehende Gateways decken jeweils nur Teilmengen ab; keine Lösung existiert auf .NET-Basis.

## 2. Ziele

| # | Ziel | Messgröße |
|---|---|---|
| Z-1 | Ein einziger Anschlusspunkt pro Agent | Ein Agent benötigt genau 1 MCP-Eintrag in seiner Config, egal wie viele Upstream-Server angeschlossen sind |
| Z-2 | Token-Verbrauch für Tool-Definitionen drastisch senken | ≥ 80 % weniger Schema-Tokens im Agenten-Kontext gegenüber Direktanschluss aller Server (gemessen an einem Referenz-Setup mit 10 Servern / 100 Tools) |
| Z-3 | Vollständige Nachvollziehbarkeit | 100 % aller Tool-Calls werden mit Wer/Was/Wann/Ergebnis-Status persistent geloggt |
| Z-4 | Zugriff steuerbar pro Agent/Rolle | Jedes Tool kann pro Rolle erlaubt/verboten werden; Verstöße werden abgelehnt und geloggt |
| Z-5 | Betrieb ohne Agenten-Neustart | Server hinzufügen/entfernen/ändern zur Laufzeit, sichtbar für verbundene Agenten ohne Reconnect (`tools/list_changed`) |
| Z-6 | Verwaltung ohne Config-Datei-Editieren | Alle Verwaltungsaufgaben über Web-UI erledigbar |

## 3. Non-Goals

- **Kein LLM-Gateway / Model-Router.** B.I.F.R.O.S.T routet Tool-Calls, keine Chat-Completions. (Kein Konkurrent zu LiteLLM/Portkey.)
- **Kein öffentlicher SaaS-Dienst.** Self-hosted, Single-Tenant. Mandantenfähigkeit ist nicht Scope v1.
- **Kein SSO/OIDC in v1.** Auth erfolgt über API-Keys pro Agent; menschliche Nutzer der Web-UI über lokales Login. OAuth 2.1-Durchreichung an Upstream-Server ist v2-Kandidat.
- **Keine eigene MCP-Server-Implementierung von Fachlogik.** B.I.F.R.O.S.T stellt keine eigenen Fach-Tools bereit, nur Meta-Tools (Discovery, Invoke, Health).
- **Kein Marketplace/Registry-Hosting.** Es wird keine öffentliche Server-Registry betrieben; Kataloge Dritter können referenziert werden (v2).
- **Keine Windows-GUI/Desktop-App.** Web-UI only.
- **Kein automatisches Sandboxing der Upstream-Server-Prozesse in v1.** Prozess-Isolation (Container pro Server) ist v2-Kandidat; v1 dokumentiert das Risiko.

## 4. Zielgruppen / Personas

| Persona | Beschreibung | Kernbedürfnis |
|---|---|---|
| **P1 — Power-User/Betreiber** (primär) | Entwickler:in, betreibt mehrere Server (Docker), nutzt Claude Code + eigene Agenten. Identisch mit dem Product Owner. | Zentral verwalten, Kosten senken, sehen was passiert |
| **P2 — Agent** (maschinell) | MCP-fähiger Client (Claude Code, SDK-Agent, IDE). | Stabiler Endpoint, schnelle Tool-Discovery, geringer Kontext-Overhead |
| **P3 — Nicht-MCP-Client** (maschinell) | Skripte/Services, die per REST Tools aufrufen wollen, ohne MCP zu sprechen. | Einfache HTTP-API auf dieselben Tools |
| **P4 — Auditor** (sekundär, = P1 in anderer Rolle) | Person, die nachträglich prüft, was Agenten getan haben. | Durchsuchbares, vollständiges, unverfälschtes Log |

## 5. Funktionale Anforderungen

Priorität: **M** = Must (v1), **S** = Should (v1 wenn möglich), **C** = Could (v2+).

### FR-Gruppe A — Gateway-Kern & Aggregation

- **FR-01 (M):** Das System stellt genau einen MCP-Endpoint (Streamable HTTP) bereit, über den Agenten alle freigegebenen Tools aller angeschlossenen Upstream-Server erreichen.
- **FR-02 (M):** Das System kann sich mit Upstream-MCP-Servern über die Transporte `stdio` (lokaler Prozess) und `Streamable HTTP` (remote) verbinden. SSE-Legacy-Transport: S.
- **FR-03 (M):** Tool-Namen werden pro Server namespaced (z. B. `github__create_issue`), sodass Namenskollisionen zwischen Servern ausgeschlossen sind.
- **FR-04 (M):** Neben Tools werden auch Resources und Prompts der Upstream-Server durchgereicht (Aggregation mit Namespacing). Sampling/Elicitation-Durchreichung: C. — *Muss-Teil erfüllt. Der Kann-Teil (Sampling/Elicitation) wird **bewusst nicht umgesetzt**, siehe [ADR-0010](../adr/0010-sampling-elicitation-nicht-durchreichen.md): Eine server-initiierte Anfrage lässt sich keinem konkreten Downstream-Agenten zuordnen — das Protokoll trägt keine Korrelation, der SDK-Handler bekommt keinen Kontext, und die Upstream-Verbindung ist geteilt. Zudem könnte der Gateway die Spezifikationszusage „ein Mensch kann immer ablehnen" architektonisch nicht einlösen.*
- **FR-05 (S):** Der Gateway kann selbst als Upstream eines weiteren B.I.F.R.O.S.T dienen (Federation-fähig, keine Endlosschleifen — Loop-Detection).

### FR-Gruppe B — Hot-Swap & Lifecycle

- **FR-06 (M):** Upstream-Server können zur Laufzeit hinzugefügt, entfernt, aktiviert/deaktiviert und umkonfiguriert werden, ohne dass der Gateway oder verbundene Agenten neu starten müssen.
- **FR-07 (M):** Bei Änderungen der Tool-Menge sendet der Gateway `notifications/tools/list_changed` an verbundene Clients.
- **FR-08 (M):** Jeder Upstream-Server hat einen überwachten Zustand (Starting / Healthy / Degraded / Stopped / Failed) mit automatischem Reconnect/Restart nach konfigurierbarer Policy (Backoff, Max-Retries).
- **FR-09 (M):** Ein abgestürzter oder hängender Upstream-Server darf den Gateway und andere Server nicht beeinträchtigen (Fault Isolation; Timeouts pro Call).
- **FR-10 (S):** Konfigurationsänderungen an einem Server erzeugen eine neue Konfigurations-Version; Rollback auf die letzte funktionierende Version ist möglich.

### FR-Gruppe C — Token-Effizienz (Hybrid Lazy + Profile)

- **FR-11 (M):** Pro Agent/Rolle existieren **Tool-Profile**, die festlegen, welche Tools/Server im `tools/list` des Agenten erscheinen.
- **FR-12 (M):** **Lazy-Modus:** Ein Profil kann statt vollständiger Tool-Schemas nur Meta-Tools exponieren: `search_tools` (Suche nach Fähigkeit, liefert kompakte Treffer), `describe_tool` (volles Schema on demand), `invoke_tool` (Aufruf per Name + JSON-Args).
- **FR-13 (M):** Beide Modi sind pro Profil kombinierbar (z. B. 5 Pinned-Tools voll sichtbar + Rest lazy).
- **FR-14 (S):** Tool-Beschreibungen können serverseitig gekürzt/überschrieben werden (Description-Override pro Tool), um Schema-Bloat einzelner Upstream-Server zu bändigen.
- **FR-15 (S):** Das System misst und zeigt pro Profil die geschätzte Token-Last des exponierten Tool-Sets (Basis für Z-2-Nachweis).
- **FR-16 (C):** Ergebnis-Kompression: übergroße Tool-Ergebnisse können (konfigurierbar) truncated/paginiert zurückgegeben werden. — *Umgesetzt (`BIFROST_MAX_RESULT_CHARS`, Default aus). Gekürzte Ergebnisse bleiben gültiges JSON und tragen `_bifrost_truncated`; Paginierung im Sinne eines Cursors ist **nicht** enthalten — Listen werden vorne beschnitten und melden die Gesamtzahl.*

### FR-Gruppe D — API↔MCP-Bridge

- **FR-17 (M):** **REST→MCP:** Jedes freigegebene Tool ist zusätzlich per REST aufrufbar (`POST /api/v1/tools/{namespacedName}/invoke`), mit denselben RBAC- und Logging-Regeln wie MCP-Calls.
- **FR-18 (M):** Für die REST-Fassade wird eine OpenAPI-3.1-Spezifikation generiert (dynamisch aus den aktuell freigegebenen Tools).
- **FR-19 (S):** **API→MCP:** Eine bestehende REST-API (beschrieben per OpenAPI-Dokument) kann als virtueller MCP-Server registriert werden; ihre Operationen erscheinen als Tools (Mapping: OperationId → Tool, Parameter → InputSchema, Auth per hinterlegtem Credential).
- **FR-20 (C):** Webhook-Trigger: eingehende Webhooks können definierte Tool-Ketten auslösen. — *Umgesetzt mit dokumentierter Einschränkung ([ADR-0013](../adr/0013-webhook-trigger.md)): **ein Tool** pro Webhook, nicht eine Kette (Orchestrierung bleibt v2). Der unauthentifizierte Eingang ist eng gesichert — HMAC-SHA256-Signatur über den rohen Body, zeitkonstanter Vergleich, Replay-Schutz über ein 5-min-Fenster. Jeder Webhook ist an **eine feste Identität** gebunden; der ausgelöste Call läuft durch die volle Pipeline (RBAC, Guardrail, Rate-Limit) und erscheint im Audit mit Herkunft `Webhook`. Das Secret liegt DataProtection-verschlüsselt. Kein Enumerations-Leak: unbekannte Id und falsche Signatur liefern beide 401.*

### FR-Gruppe E — Logging & Audit

- **FR-21 (M):** Jeder Tool-Call wird persistent geloggt mit: Zeitstempel, Aufrufer-Identität (Agent/API-Key), Profil/Rolle, Ziel-Server, Tool-Name, Argumente, Ergebnis-Status (Erfolg/Fehler/Denied/Timeout), Dauer, Request-Größe/Antwort-Größe.
- **FR-22 (M):** Auch verweigerte Calls (RBAC-Deny) und Systemereignisse (Server up/down, Config-Änderung, Login) werden geloggt.
- **FR-23 (M):** Logs sind über die Web-UI filterbar (Zeitraum, Agent, Server, Tool, Status) und exportierbar (JSON/CSV).
- **FR-24 (M):** Argumente/Ergebnisse können Felder mit Secrets enthalten; pro Tool konfigurierbare Redaction-Regeln maskieren sie vor Persistierung. Default: Argumente loggen, Ergebnisse nur Metadaten (Größe/Status); vollständige Ergebnis-Payloads nur bei explizit aktiviertem Debug-Modus.
- **FR-25 (S):** Aufbewahrungsrichtlinie (Retention) konfigurierbar; automatische Bereinigung.
- **FR-26 (S):** Metriken-Export (OpenTelemetry/Prometheus): Calls/s, Fehlerquote, Latenz-Perzentile pro Server/Tool.

### FR-Gruppe F — RBAC

- **FR-27 (M):** Identitäten: Jeder Agent authentifiziert sich mit einem individuellen API-Key/Token; jeder Key ist genau einer Identität zugeordnet. Keys sind widerrufbar und rotierbar.
- **FR-28 (M):** Rollenmodell: Identitäten haben ≥ 1 Rolle; Rollen bündeln Berechtigungen mit Wirkungsebenen Server (ganzer Upstream), Tool (einzeln) und Aktion (Tools nutzen / Resources lesen / Prompts nutzen).
- **FR-29 (M):** Default-Deny: Was nicht explizit erlaubt ist, ist unsichtbar und nicht aufrufbar. Sichtbarkeit folgt Berechtigung (ein Agent sieht in `tools/list`/`search_tools` nur, was er nutzen darf).
- **FR-30 (M):** Admin-Rollen für die Web-UI getrennt vom Agenten-RBAC (mindestens: Admin = alles; Operator = Server verwalten, keine Key-/Rollenverwaltung; Auditor = nur Logs lesen).
- **FR-31 (S):** Zeitliche/quantitative Schranken pro Rolle: Rate-Limits (Calls/min) und optionale Gültigkeitsfenster für Keys.
- **FR-32 (C):** Approval-Flows: bestimmte Tools erfordern menschliche Freigabe pro Call (Queue in der Web-UI). — *Umgesetzt ([ADR-0012](../adr/0012-approval-flows-asynchron.md)). Das gelöste Kernproblem war der blockierende Call vs. der Timeout aus FR-09: **asynchron** — der Aufruf wird sofort mit `ApprovalRequired` abgewiesen, eine Anfrage landet in der UI-Queue, nach Freigabe läuft **derselbe** Aufruf (gebunden an Identität + Tool + Argument-Fingerprint) **einmalig** durch. Kein hängender Agent, keine blockierte Reservierung. Freigabepflicht pro Tool zur Laufzeit schaltbar.*

### FR-Gruppe G — Web-UI

- **FR-33 (M):** Dashboard: Zustand aller Upstream-Server (Health, Tool-Zahl, letzte Fehler), aktive Agenten-Sessions, Call-Rate.
- **FR-34 (M):** Server-Verwaltung: Hinzufügen/Bearbeiten/Entfernen von Upstream-Servern über Formulare (stdio: Kommando/Args/Env; HTTP: URL/Headers/Auth), inkl. „Verbindung testen" vor dem Speichern.
- **FR-35 (M):** Tool-Explorer: alle aggregierten Tools durchsuchbar, Schema-Ansicht, Test-Aufruf aus der UI (mit Admin-Rechten, geloggt).
- **FR-36 (M):** RBAC-Verwaltung: Identitäten, API-Keys (Erzeugen/Widerrufen), Rollen, Profil-Zuordnung.
- **FR-37 (M):** Log-Ansicht gemäß FR-23.
- **FR-38 (S):** Token-Cockpit: Anzeige der Schema-Token-Last pro Profil (FR-15) und Call-Volumen-Trends.

### FR-Gruppe H — Multi-Agent / zentrale Steuerung (Keyfeature 7)

- **FR-39 (M):** Mehrere Agenten können gleichzeitig verbunden sein; Sessions sind isoliert (eigene Identität, eigenes Profil, eigene Subscriptions).
- **FR-40 (S):** **Zentrale Asset-Verteilung:** Der Gateway kann versionierte Text-Assets (Skills/Prompts/Instructions) verwalten und Agenten als MCP-Prompts/Resources bereitstellen — ein zentraler Ort, von dem sich alle Agenten Skills ziehen.
- **FR-41 (C):** Konfigurations-Push: Generierung fertiger Client-Config-Snippets (z. B. `claude mcp add …`, JSON für andere Clients) pro Identität aus der Web-UI. — *Umgesetzt. Die Snippets erscheinen beim Ausstellen eines API-Keys, also genau dann, wenn der Key einmalig sichtbar ist.*

## 6. Nicht-funktionale Anforderungen

- **NFR-01 Performance (M):** Gateway-Overhead pro Tool-Call ≤ 50 ms p95 (gemessen ohne Upstream-Latenz, Referenz-Hardware: 4-Core-VM). `tools/list` für ein Profil mit 100 Tools ≤ 200 ms p95.
- **NFR-02 Nebenläufigkeit (M):** ≥ 20 gleichzeitige Agenten-Sessions und ≥ 100 parallele In-Flight-Tool-Calls ohne Degradation.
- **NFR-03 Zuverlässigkeit (M):** Ausfall eines Upstream-Servers reduziert nie die Verfügbarkeit der übrigen; Gateway-Uptime-Ziel 99,5 % (self-hosted realistisch).
- **NFR-04 Sicherheit (M):** Secrets (Upstream-Credentials, API-Keys) werden verschlüsselt persistiert und tauchen niemals in Logs auf. Transport nach außen TLS-fähig (Betrieb hinter Reverse-Proxy unterstützt). API-Keys werden nur als Hash gespeichert.
- **NFR-05 Deployment (M):** Auslieferung als einzelnes Docker-Image (linux-x64/arm64) + docker-compose-Beispiel; Konfiguration über Env-Vars + persistentes Volume. Muss auch „bare" per `dotnet run` lauffähig sein.
- **NFR-06 Datenhaltung (M):** Betrieb ohne externe Infrastruktur möglich (embedded DB); optional externe DB für größere Setups. Migrationspfad ohne Datenverlust.
- **NFR-07 Beobachtbarkeit (S):** Strukturierte Logs (JSON), Health-Endpoint (`/healthz`, `/readyz`), Metriken (FR-26).
- **NFR-08 Wartbarkeit (M):** Interface-first-Design; Kernlogik ohne UI/Transport testbar; Unit-Test-Abdeckung der Kern-Bibliothek ≥ 80 % Zeilen, kritische Pfade (RBAC-Entscheidung, Routing, Redaction) 100 % Branch.
- **NFR-09 Kompatibilität (M):** MCP-Protokoll-Version gemäß aktueller Spezifikation (2025-06-18 oder neuer); Abwärtskompatibilität zu gängigen Clients (Claude Code, Claude Desktop, MCP Inspector).
- **NFR-10 Lizenz/Kosten (S):** Nur Abhängigkeiten mit permissiven Lizenzen (MIT/Apache-2.0/BSD); keine kostenpflichtigen Laufzeit-Dienste erforderlich.

## 7. User Stories (Auszug, je Keyfeature)

- **US-01 (Hot-Swap):** Als *Betreiber* möchte ich einen neuen MCP-Server über die Web-UI anschließen und sofort in meiner laufenden Claude-Code-Session nutzen können, damit ich keine Sessions neu starten muss.
- **US-02 (Token):** Als *Betreiber* möchte ich meinem Recherche-Agenten nur `search_tools`/`invoke_tool` geben, damit 100 Upstream-Tools ihn nur ~3 Tool-Schemas an Kontext kosten.
- **US-03 (Bridge REST→MCP):** Als *Skript-Autor* möchte ich ein Tool per `curl` mit API-Key aufrufen, damit ich Gateway-Tools auch aus Cron-Jobs nutzen kann.
- **US-04 (Bridge API→MCP):** Als *Betreiber* möchte ich eine OpenAPI-Spec hochladen und die API als Tools nutzbar machen, damit ich keinen eigenen MCP-Server dafür schreiben muss.
- **US-05 (Audit):** Als *Auditor* möchte ich filtern „alle Calls von Agent X auf Server Y letzte 24 h mit Status Fehler", damit ich Vorfälle rekonstruieren kann.
- **US-06 (RBAC):** Als *Betreiber* möchte ich, dass mein öffentlicher Demo-Agent nur lesende GitHub-Tools sieht und nutzen kann, damit er nichts verändern kann — und Verstöße im Log auftauchen.
- **US-07 (Web-UI):** Als *Betreiber* möchte ich auf einem Dashboard sehen, welcher Server unhealthy ist und warum, damit ich nicht in Container-Logs graben muss.
- **US-08 (Multi-Agent/Skills):** Als *Betreiber* möchte ich einen Skill-Text zentral aktualisieren und alle Agenten beziehen ab sofort die neue Version, damit ich Skills nicht in N Repos pflegen muss.

## 8. Akzeptanzkriterien / Success Metrics

Das PRD gilt als erfüllt, wenn:

1. **Referenz-Setup** (≥ 3 Upstream-Server, davon 1 stdio + 1 HTTP + 1 OpenAPI-Bridge; ≥ 2 Agenten-Identitäten mit unterschiedlichen Profilen) vollständig über die Web-UI eingerichtet werden kann — ohne Config-Datei anzufassen (Z-6).
2. **Token-Messung:** Im Referenz-Setup mit 10 Servern/100 Tools reduziert das Lazy-Profil die Schema-Tokens im Agenten-Kontext um ≥ 80 % gegenüber Voll-Exposition; Messwert im Token-Cockpit sichtbar (Z-2, FR-15).
3. **Hot-Swap-Nachweis:** Server während aktiver Agenten-Session hinzufügen → Agent kann neues Tool ohne Reconnect aufrufen; Server entfernen → Tool verschwindet, Aufruf liefert sauberen Fehler, Gateway bleibt stabil (Z-5).
4. **RBAC-Nachweis:** Testmatrix aus ≥ 20 Erlaubt/Verboten-Kombinationen wird zu 100 % korrekt durchgesetzt; jeder Deny erscheint im Log (Z-4).
5. **Audit-Nachweis:** 1000 gemischte Calls (Erfolg/Fehler/Deny) → 1000 Log-Einträge, korrekt attribuiert, Secrets maskiert (Z-3, FR-24).
6. **Ausfall-Nachweis:** Kill eines stdio-Upstream-Prozesses während laufender Calls → betroffene Calls enden mit Timeout-Fehler, andere Server unbeeinträchtigt, Auto-Restart greift (FR-08/09, NFR-03).
7. **Performance-Nachweis:** Lasttest belegt NFR-01/NFR-02.

## 9. Offene Fragen

1. **OQ-1:** Sollen Upstream-stdio-Server als Kindprozesse des Gateways laufen (einfach, aber geteiltes Schicksal beim Gateway-Restart) oder perspektivisch als eigene Container (Isolation)? → ADR nötig; v1-Annahme: Kindprozesse.
2. **OQ-2:** Welche konkreten Erst-Server bilden das Referenz-Setup des Betreibers (z. B. ServerWatch, GitHub, Filesystem)? Beeinflusst Priorisierung der Transport-Features.
3. **OQ-3:** Braucht die REST-Bridge (FR-17) synchrone Antworten mit Timeout oder auch einen asynchronen Modus (Job-ID + Polling) für langlaufende Tools? v1-Annahme: synchron mit konfigurierbarem Timeout.
4. **OQ-4:** Skill-Verteilung (FR-40): Reicht MCP-Prompts/Resources als Auslieferungsmechanismus, oder braucht es einen Datei-Sync-Mechanismus für Claude-Code-Skills (`.claude/skills/`)? v1-Annahme: MCP-native Auslieferung, Datei-Sync v2.
5. **OQ-5:** Mehrsprachigkeit der Web-UI (DE/EN)? v1-Annahme: EN-only UI, deutsche Doku.

## 10. Stakeholder-Verantwortung

- **Entscheidet Scope-Änderungen:** Product Owner.
- **Verantwortet technische Machbarkeit:** Senior-Tech-Specialist (ADRs).
- **Abnahme:** Product Owner anhand Abschnitt 8.
