# Skill-Log: adr-writer

## 2026-11-16 14:45 — Run

**Aufgabe:** Statuswechsel nach der Abnahme durch den Nutzer — ADR-0026, 0027 und 0028 von
„Vorgeschlagen" auf „Akzeptiert".

**Entscheidungen:**
- Der Nutzer hat die drei ADRs und die drei Verträge am 2026-11-16 abgenommen („Abgenommen").
- Geändert wurde je ADR nur die Statuszeile; der Inhalt bleibt, wie er vorgelegt wurde.
- Index und die Statuszeilen der drei Verträge nachgezogen; der Vertrag
  `upstream-wiederverbindung.md` gehört nicht dazu und blieb unberührt.

**Artefakte:**
- docs/adr/0026-umbenennung-auf-faolan.md, 0027-rueckkanal-projektion-im-invoker.md, 0028-maskieren-als-dritter-guard-modus.md (Status)
- docs/adr/README.md (drei Zeilen)
- docs/contracts/umbenennung-faolan.md, rueckkanal-projektion.md, antwort-profiler.md (Status)
- docs/plans/0004-umbenennung-und-rueckkanal-projektion.md (offene Entscheidungen, Prüfliste)

**Status:** abgeschlossen

---

## 2026-11-16 14:20 — Run

**Aufgabe:** Drei ADRs zu Plan 0004 — die Umbenennung auf Faolan (0026), die deterministische
Formung von Antworten im Invoker (0027) und `Mask` als dritter Modus der Guard-Regeln (0028). Dazu
die drei Verträge unter `docs/contracts/`, die der Nutzer vor der Umsetzung abnimmt.

**Entscheidungen:**
- Kontext und Optionen lagen aus der Bestandsaufnahme (Plan 0004) vor → keine Rückfragen.
- Drei ADRs statt der im Plan genannten zwei: Die Rückkanal-Projektion und der Modus `Mask` sind
  zwei Entscheidungen; „eine Entscheidung je Dokument" geht vor.
- Supersede: nein. 0028 ergänzt [ADR-0011](../adr/0011-secret-erkennung-als-guardrail.md); E1 bleibt
  gültig.
- Status „Vorgeschlagen" für alle drei — der Name ist vom Nutzer entschieden, die Wege dorthin und
  die Verträge sind es noch nicht.
- Index: Zeilen für 0026 – 0028 ergänzt und die fehlenden Zeilen für 0024 und 0025 nachgetragen
  (beide „Akzeptiert", 2026-07-31; die Dateien wurden nicht angefasst).
- Reality-Check für alle drei: 2026-12-28.

**Artefakte:**
- docs/adr/0026-umbenennung-auf-faolan.md
- docs/adr/0027-rueckkanal-projektion-im-invoker.md
- docs/adr/0028-maskieren-als-dritter-guard-modus.md
- docs/adr/README.md (fünf Index-Zeilen)
- docs/contracts/umbenennung-faolan.md
- docs/contracts/rueckkanal-projektion.md
- docs/contracts/antwort-profiler.md
- docs/plans/0004-umbenennung-und-rueckkanal-projektion.md (Verweise und gemessener Befund nachgezogen)

**Status:** abgeschlossen

---

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

**Aufgabe:** Initialer ADR-Satz (8 Entscheidungen) für B.I.F.R.O.S.T aus PRD 0001 ableiten.

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
