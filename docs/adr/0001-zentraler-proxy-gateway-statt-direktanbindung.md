# ADR-0001: Zentraler Proxy-Gateway statt Direktanbindung

- **Status:** Akzeptiert
- **Datum:** 2026-07-17
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

Mehrere Agenten sollen viele MCP-Server nutzen. Bei Direktanbindung wächst der Verwaltungsaufwand mit Agenten × Server, und die vier Kernanforderungen aus [PRD 0001](../prd/0001-mcp-mcp-meta-gateway.md) — zentrales Logging (FR-21), RBAC (FR-27 ff.), Token-Sparen (FR-11 ff.) und Hot-Swap (FR-06) — sind nur an einer Stelle durchsetzbar, durch die *jeder* Call fließt. Der User fragte explizit, ob Steuerung „über einen Proxy-Gateway" machbar ist.

**Kernfrage:** Über welche Grundarchitektur werden Agenten mit MCP-Servern verbunden, sodass Logging, RBAC, Token-Optimierung und Hot-Swap zentral erzwungen werden können?

## Anforderungen

### Funktional

- Ein Agent benötigt genau einen Verbindungseintrag (PRD Z-1).
- Alle Calls müssen an einem Punkt abfangbar sein (Logging, RBAC, Metriken).
- Server-Änderungen zur Laufzeit ohne Agenten-Neustart (PRD Z-5).
- REST-Clients müssen dieselben Tools erreichen (FR-17).

### Nicht-Funktional

- Zusätzliche Latenz ≤ 50 ms p95 pro Call (NFR-01).
- Ausfall eines Upstreams darf andere nicht beeinträchtigen (NFR-03).

## Betrachtete Optionen

### Option 0: Direktanbindung + Config-Sync-Tool

Agenten verbinden sich weiterhin direkt mit jedem Server; ein Tool synchronisiert nur die Config-Dateien der Agenten.

**Positiv:**
- Keine zusätzliche Latenz, kein Single Point of Failure.
- Sehr geringer Implementierungsaufwand.

**Negativ:**
- Logging, RBAC und Token-Optimierung sind prinzipiell nicht durchsetzbar — jeder Agent spricht am Tool vorbei direkt mit den Servern.
- Hot-Swap erfordert weiterhin Client-Neustarts.
- Erfüllt 4 von 7 Keyfeatures nicht.

### Option 1: Zentraler Proxy-Gateway (Reverse Proxy für MCP)

Ein Dienst ist nach außen selbst MCP-Server (ein Endpoint für alle Agenten) und nach innen MCP-Client zu allen Upstream-Servern. Jeder Call wird terminiert, geprüft, geloggt und weitergeleitet.

**Positiv:**
- Einziger Architekturansatz, der alle 7 Keyfeatures an einem Punkt erzwingen kann (Enforcement statt Konvention).
- Standardmuster des Ökosystems (agentgateway, MCPJungle, MetaMCP, IBM ContextForge nutzen es identisch) — validiertes Design.
- REST-Bridge und Web-UI docken natürlich am selben Dienst an.
- Hot-Swap über `tools/list_changed` ohne Client-Beteiligung.

**Negativ:**
- Single Point of Failure: Gateway down = alle Tools down.
- Zusätzlicher Hop mit Latenz- und Betriebskosten.
- Gateway hält alle Upstream-Credentials — attraktives Angriffsziel, erhöhte Sicherheitsanforderungen (NFR-04).

### Option 2: Bibliothek/Sidecar pro Agent

Jeder Agent bekommt eine lokale Vermittlungsschicht (Library oder Sidecar-Prozess), die aggregiert, loggt und filtert.

**Positiv:**
- Kein zentraler Ausfallpunkt, Latenz minimal.
- Skaliert horizontal mit den Agenten.

**Negativ:**
- Logs und RBAC-Zustand sind über N Instanzen verteilt — zentrale Auditierbarkeit (Z-3) erfordert zusätzliche Aggregations-Infrastruktur.
- Konfigurations-Verteilung an alle Sidecars ist genau das Sync-Problem, das gelöst werden sollte.
- Pro Client-Typ (Claude Code, SDK-Agent, …) eigene Integrationsarbeit.

## Vorschlag des Autors

Option 1. Die Keyfeatures 3–6 (Token, Logging, RBAC, UI) sind Kontrollpunkt-Features: Sie funktionieren nur, wenn es genau einen Pfad gibt, den kein Call umgehen kann. Der SPOF-Nachteil ist im Self-hosted-Single-Operator-Kontext akzeptabel und durch Container-Restart-Policies gut beherrschbar; die Credential-Konzentration wird durch NFR-04 (Verschlüsselung, Hash-Keys) adressiert.

## Entscheidung

**Gewählte Option:** „Zentraler Proxy-Gateway"

Nur diese Option erfüllt alle Keyfeatures; das Muster ist marktvalidiert. SPOF und Credential-Konzentration werden bewusst in Kauf genommen und über Betriebs- bzw. Sicherheitsmaßnahmen gemildert.

## Konsequenzen

### Positiv

- Ein Enforcement-Punkt für RBAC, Logging, Token-Politik, Rate-Limits.
- Agenten-Configs schrumpfen auf einen Eintrag; neue Server sind sofort überall verfügbar.

### Negativ

- Gateway-Verfügbarkeit wird kritisch; Monitoring und Restart-Policy sind Pflicht.
- Jeder Call zahlt einen zusätzlichen Netzwerk-Hop.
- Sicherheitshärtung des Gateways (Secrets, AuthN/AuthZ) ist nicht optional.

### Folge-Entscheidungen

- Technologie-Basis des Gateways → [ADR-0002](./0002-dotnet-mit-offiziellem-csharp-mcp-sdk.md)
- Prozess-/Lifecycle-Modell der Upstreams → [ADR-0005](./0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md)
- RBAC-Modell → [ADR-0006](./0006-rollenbasiertes-rbac-mit-default-deny.md)

### Review

**Reality-Check geplant für:** 2026-09-15

## Weitere Informationen

### Referenzen

- [MCP Aggregation, Gateway, and Proxy Tools — State of the Ecosystem Q1/2026](https://www.heyitworks.tech/blog/mcp-aggregation-gateway-proxy-tools-q1-2026)
- [agentgateway](https://agentgateway.dev/), [MCPJungle](https://github.com/mcpjungle), [MetaMCP](https://github.com/metatool-ai/metatool-app)
