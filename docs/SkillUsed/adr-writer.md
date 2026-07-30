# Skill-Log: adr-writer

## 2026-07-30 21:32 — Run

**Aufgabe:** ADR-0022 — Schärfe und Durchsetzung der Freigabepflicht trennen und eine zweite Tür
`invoke_sensitive_tool` einführen, damit ein MCP-Client seine Rückfrage genau auf die gefährlichen
Werkzeuge legen kann.

**Entscheidungen:**
- Kontext, Optionen und Wahl lagen aus dem Gespräch vor (Product Owner hat Option 3 gewählt,
  Vorschlag des Autors war Option 1) → keine Rückfragen.
- Supersede: nein, ergänzt [ADR-0012](../adr/0012-approval-flows-asynchron.md) — die Warteschlange
  bleibt gültig und Vorgabewert.
- Status „Akzeptiert", weil vom Product Owner entschieden.
- Der abweichende Autorenvorschlag wurde stehen gelassen statt nachträglich angeglichen.

**Artefakte:**
- docs/adr/0022-schaerfe-und-durchsetzung-trennen.md
- docs/adr/README.md (Index-Zeile ergänzt)

**Status:** abgeschlossen

---

## 2026-07-24 — Run

**Aufgabe:** ADR für die M5-Architekturentscheidung — WASI-Runtime als Out-of-Process-Rust-Host hinter dem Connector-Vertrag.

**Entscheidungen:**
- Kontext/Optionen/Entscheidung im Prompt vorgegeben (gestützt auf Recherche: wasmtime-dotnet ohne Component Model) → keine Rückfragen; Status „Akzeptiert" (vom Product Owner bestätigt).
- Supersede: nein — additiv, baut auf ADR-0016/0017/0018 auf.
- Index docs/adr/README.md um Zeile 0020 erweitert.

**Artefakte:**
- docs/adr/0020-wasi-runtime-out-of-process-rust-host.md
- docs/adr/README.md (Index-Zeile 0020)

**Status:** abgeschlossen

---

## 2026-07-24 — Run

**Aufgabe:** ADR für CLI-Programme als vierten Upstream-Transport (UpstreamTransportKind.Cli) schreiben.

**Entscheidungen:**
- Kontext/Optionen/Entscheidung vollständig im Prompt vorgegeben → keine Rückfragen nötig; Status „Akzeptiert" (Bau ist beauftragt).
- Supersede: nein — additiv, ergänzt die Connector-Familie (ADR-0005/0008), ersetzt nichts.
- Index docs/adr/README.md um Zeile 0014 erweitert.

**Artefakte:**
- docs/adr/0014-cli-programme-als-upstream-transport.md
- docs/adr/README.md (Index-Zeile 0014)

**Status:** abgeschlossen

---

## 2026-07-17 — Run

**Aufgabe:** Initialer ADR-Satz (8 Entscheidungen) für MCP-MCP aus PRD 0001 ableiten.

**Entscheidungen:**
- Alle 8 Entscheidungen vom Product Owner vorab bestätigt (AskUserQuestion-Runde vor PRD) → Status direkt „Akzeptiert".
- Supersede: nicht relevant (erste ADRs im Repo).
- Index docs/adr/README.md neu angelegt (existierte nicht; Anlage im Rahmen des beauftragten Gesamt-Doku-Pakets).

**Artefakte:**
- docs/adr/0001-zentraler-proxy-gateway-statt-direktanbindung.md
- docs/adr/0002-dotnet-mit-offiziellem-csharp-mcp-sdk.md
- docs/adr/0003-hybride-token-strategie-lazy-discovery-plus-profile.md
- docs/adr/0004-blazor-server-als-web-ui.md
- docs/adr/0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md
- docs/adr/0006-rollenbasiertes-rbac-mit-default-deny.md
- docs/adr/0007-ef-core-mit-sqlite-default-postgres-optional.md
- docs/adr/0008-api-mcp-bridge-als-erstklassige-fassaden.md
- docs/adr/README.md (Index, neu)

**Status:** abgeschlossen

---
