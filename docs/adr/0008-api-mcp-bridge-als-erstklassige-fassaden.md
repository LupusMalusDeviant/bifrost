# ADR-0008: API↔MCP-Bridge als zwei erstklassige Fassaden über einem gemeinsamen Invocation-Kern

- **Status:** Akzeptiert
- **Datum:** 2026-07-17
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

Keyfeature 2 verlangt beide Richtungen: **REST→MCP** (Nicht-MCP-Clients rufen Tools per HTTP, FR-17/18) und **API→MCP** (bestehende REST-APIs erscheinen als Tools, FR-19). Gefahr: Zwei Bolt-on-Konverter mit je eigener Auth-, Logging- und Fehlerlogik — dann divergieren MCP-Pfad und REST-Pfad genau bei RBAC und Audit, den beiden Features, die Vollständigkeit garantieren müssen.

**Kernfrage:** Wie werden beide Bridge-Richtungen integriert, ohne die Enforcement-Garantien (RBAC, Logging) zu duplizieren oder zu verwässern?

## Anforderungen

### Funktional

- REST-Invoke mit identischer RBAC-/Logging-Semantik wie MCP-Calls (FR-17, FR-21).
- Generierte OpenAPI-3.1-Spec, die dem RBAC-gefilterten Toolkatalog des jeweiligen API-Keys entspricht (FR-18).
- OpenAPI-Import als „virtueller Upstream": Operationen → Tools, Parameter/RequestBody → InputSchema, hinterlegte Credentials (FR-19).

### Nicht-Funktional

- Kein doppelter Enforcement-Code (ein Bug-Fix darf nie „auch noch im anderen Pfad" nötig sein).

## Betrachtete Optionen

### Option 0: Gemeinsamer Invocation-Kern, Fassaden nur als Adapter

Eine zentrale Pipeline `IToolInvoker` (AuthN → RBAC → Validierung → Routing → Timeout → Audit) ist der **einzige** Weg zu einem Tool-Call. MCP-Endpoint und REST-Endpoint sind dünne Adapter darauf. Einwärts ist die OpenAPI-Bridge eine normale `IUpstreamConnector`-Implementierung (`OpenApiUpstreamConnector`) neben stdio/HTTP — für Katalog, RBAC und Logging ununterscheidbar von echten MCP-Servern.

**Positiv:**
- Enforcement per Konstruktion identisch auf allen Pfaden; PRD-Kriterien 4/5 (RBAC-/Audit-Vollständigkeit) strukturell abgesichert.
- API→MCP erbt gratis alle Gateway-Features (Profile, Hot-Swap, Health) — eine OpenAPI-Quelle ist einfach ein weiterer Connector.
- Neue Fassaden (Webhooks FR-20, evtl. gRPC) sind später reine Adapter.

**Negativ:**
- Der Kern muss von Tag 1 fassaden-neutral entworfen sein (kein MCP-Typ leakt in die Pipeline) — Disziplin- und Abstraktionsaufwand.
- OpenAPI→Schema-Mapping (Auth-Varianten, `$ref`-Auflösung, Datei-Uploads) ist eigenaufwändig, auch wenn der Rahmen steht.

### Option 1: Separate Konverter-Komponenten

REST-Controller mit eigener Logik; OpenAPI-Import als Codegenerator, der pro API einen internen MCP-Server erzeugt.

**Positiv:**
- Jede Richtung isoliert einfach zu bauen; Codegen-Ergebnis inspizierbar.

**Negativ:**
- Doppelte Auth-/Log-/Fehlerpfade — genau die Divergenz-Falle; Codegen erfordert Redeploy statt Hot-Swap (bricht FR-06 für Bridge-Quellen).

### Option 2: Externes API-Gateway (z. B. Kong/YARP-Ebene) für die REST-Seite

REST-Fassade als separates Gateway-Produkt davor, B.I.F.R.O.S.T nur MCP.

**Positiv:**
- Ausgereifte API-Gateway-Features (Rate-Limit, Keys) geschenkt.

**Negativ:**
- Zweites System mit eigener RBAC-Welt — Audit-Lücke zwischen den Systemen; verletzt Zero-Setup und Single-Artefakt (NFR-05).

## Vorschlag des Autors

Option 0. Sie kostet früh Abstraktionsarbeit, ist aber die einzige, die die zentrale Produktgarantie („kein Call am Enforcement vorbei") strukturell statt disziplinarisch sichert. Das OpenAPI-Mapping wird bewusst minimal geschnitten: v1 unterstützt API-Key/Bearer/Basic-Auth und JSON-Bodies; exotische Spec-Features werden abgelehnt statt halb unterstützt (klare Fehlermeldung beim Import).

## Entscheidung

**Gewählte Option:** „Gemeinsamer Invocation-Kern mit Fassaden-Adaptern"

Die strukturelle Enforcement-Garantie gibt den Ausschlag; der Abstraktionsaufwand fällt einmalig an und zahlt auf jede künftige Fassade ein.

## Konsequenzen

### Positiv

- `IToolInvoker` ist der eine testkritische Punkt: seine Testsuite deckt automatisch MCP- und REST-Verhalten ab.
- OpenAPI-Quellen sind hot-swappable, profilierbar und auditiert wie jeder MCP-Server.

### Negativ

- OpenAPI-Import v1 mit hartem Feature-Schnitt (kein OAuth-Flow-Passthrough, keine multipart-Uploads) — Erwartungsmanagement in Doku nötig.
- Die generierte OpenAPI-Spec der REST-Fassade ist key-abhängig (RBAC-gefiltert) — Caching pro Identität statt global.

### Folge-Entscheidungen

- Asynchroner REST-Modus für langlaufende Tools (PRD OQ-3) — Entscheidung nach ersten Praxisdaten, v1 synchron mit Timeout.

### Review

**Reality-Check geplant für:** 2026-09-15
