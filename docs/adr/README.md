# Architecture Decision Records

Chronologisches Verzeichnis aller Architekturentscheidungen dieses Repos. Jede Zeile führt zu einem einzelnen ADR.

## Was ist ein ADR?

Ein Architecture Decision Record dokumentiert eine einzelne, wichtige Architekturentscheidung — inklusive des damaligen Kontexts, der betrachteten Optionen und der bewusst in Kauf genommenen Konsequenzen. ADRs sind unveränderliche Zeitkapseln: Sie werden nicht überschrieben, sondern bei Revisionen durch neue ADRs ersetzt (Supersede).

## Status-Legende

- **Vorgeschlagen** — in Review, noch nicht beschlossen.
- **Akzeptiert** — beschlossen und gültig.
- **Abgelehnt** — Vorschlag wurde verworfen (bleibt als Lernerfahrung im Log).
- **Veraltet** — durch ein neueres ADR ersetzt.

## Decision Log

| Nr. | Titel | Status | Datum | Ersetzt durch |
|-----|-------|--------|-------|----------------|
| [0001](./0001-zentraler-proxy-gateway-statt-direktanbindung.md) | Zentraler Proxy-Gateway statt Direktanbindung | Akzeptiert | 2026-07-17 | — |
| [0002](./0002-dotnet-mit-offiziellem-csharp-mcp-sdk.md) | .NET 10 mit offiziellem C# MCP SDK als Technologie-Basis | Akzeptiert | 2026-07-17 | — |
| [0003](./0003-hybride-token-strategie-lazy-discovery-plus-profile.md) | Hybride Token-Strategie — Lazy Discovery plus Tool-Profile | Akzeptiert | 2026-07-17 | — |
| [0004](./0004-blazor-server-als-web-ui.md) | Blazor (Interactive Server) als Web-UI | Akzeptiert | 2026-07-17 | — |
| [0005](./0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md) | Hot-Swap-Modell — Upstreams als verwaltete Kindprozesse mit Supervisor | Akzeptiert | 2026-07-17 | — |
| [0006](./0006-rollenbasiertes-rbac-mit-default-deny.md) | Rollenbasiertes RBAC mit Default-Deny und Sichtbarkeit-folgt-Berechtigung | Akzeptiert | 2026-07-17 | — |
| [0007](./0007-ef-core-mit-sqlite-default-postgres-optional.md) | Persistenz — EF Core mit SQLite als Default, PostgreSQL optional | Akzeptiert | 2026-07-17 | — |
| [0008](./0008-api-mcp-bridge-als-erstklassige-fassaden.md) | API↔MCP-Bridge als zwei Fassaden über gemeinsamem Invocation-Kern | Akzeptiert | 2026-07-17 | — |
| [0009](./0009-sse-legacy-transport.md) | HTTP+SSE nur upstream unterstützen, downstream nur Streamable HTTP | Akzeptiert | 2026-07-18 | — |
| [0010](./0010-sampling-elicitation-nicht-durchreichen.md) | Sampling/Elicitation nicht durchreichen — Korrelation strukturell nicht lösbar | Akzeptiert | 2026-07-18 | — |
| [0011](./0011-secret-erkennung-als-guardrail.md) | Secret-Erkennung als hot-swappable Guardrail im Invoker | Akzeptiert | 2026-07-20 | — |
| [0012](./0012-approval-flows-asynchron.md) | Approval-Flows asynchron (sofort ablehnen, Freigabe pro Call) statt blockierend | Akzeptiert | 2026-07-20 | — |
| [0013](./0013-webhook-trigger.md) | Webhook-Trigger — HMAC-signiert, ein Tool, feste Identität (Ketten als v2) | Akzeptiert | 2026-07-20 | — |
| [0014](./0014-cli-programme-als-upstream-transport.md) | CLI-Programme als vierter Upstream-Transport | Akzeptiert | 2026-07-24 | — |
| [0015](./0015-protokollneutrales-capability-modell.md) | Protokollneutrales Capability-Modell — Vokabular umgesetzt, Invocation-Migration offen | Vorgeschlagen | 2026-07-24 | — |
| [0016](./0016-versionierter-connector-plugin-vertrag.md) | Versionierter Connector-/Plugin-Vertrag — Laufzeitteil umgesetzt, Packaging/Manifest/Trust-Stufen offen | Vorgeschlagen | 2026-07-24 | — |
| [0017](./0017-wasi-component-runtime.md) | WASI Component Runtime als bevorzugter isolierter Pluginpfad | Akzeptiert (Vorrang unter Vorbehalt) | 2026-07-25 | — |
| [0018](./0018-native-prozess-und-container-isolation.md) | Native Prozess- und Container-Isolation | Vorgeschlagen | 2026-07-24 | — |
| [0019](./0019-langlaufende-tasks-und-events.md) | Persistentes Task-Modell (Polling als Vertrag; Freigaben gehen darin auf) | Akzeptiert | 2026-07-25 | ergänzt [0010](./0010-sampling-elicitation-nicht-durchreichen.md), löst [0012](./0012-approval-flows-asynchron.md) technisch ab |
| [0020](./0020-wasi-runtime-out-of-process-rust-host.md) | WASI-Runtime als Out-of-Process-Rust-Host hinter dem Connector-Vertrag | Akzeptiert | 2026-07-24 | — |

## Beitragen

Neue ADRs werden über den `adr-writer`-Skill erzeugt. Manuell geht auch:

1. Nächste Nummer ermitteln (höchste existierende + 1, 4-stellig).
2. Datei anlegen unter `docs/adr/<NNNN>-<slug>.md`.
3. Template aus `adr-writer/references/template.md` folgen.
4. Diesen Index um einen Eintrag erweitern.
5. Status-Wechsel nur nach den Regeln aus `adr-writer/references/status-guide.md`.
