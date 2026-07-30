# MCP-MCP

[![ci](https://github.com/LupusMalusDeviant/mcp-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/LupusMalusDeviant/mcp-mcp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

**A self-hosted meta-MCP gateway on .NET** — connect one endpoint to your agents, and manage all your MCP servers behind it.

> **[Pre-release v0.8.2](https://github.com/LupusMalusDeviant/mcp-mcp/releases/tag/v0.8.2)** is the current build. It brings everything that had accumulated on `main` since v0.5.0 into a tagged state: WASI plugins with an out-of-process Rust host, container isolation for CLI upstreams, long-running tasks, the capability model, OpenRPC, signed connector packages, upstream OAuth, rug-pull protection, OTel traces, and skills with a schema, an editor and package delivery.
>
> The version is deliberately below 1.0 — and it is marked as a **pre-release** for a reason: the code is feature-complete for its scope and covered by tests against SQLite *and* real PostgreSQL, but **it still has no operational uptime behind it**. 1.0 follows from running it, not from adding features.

## The problem

Every agent × every MCP server = a config entry, a credential copy, and a pile of tool schemas eating your context window. No central log answers *which agent called which tool with which arguments*, no access control separates read-only agents from write-capable ones, and every server change means restarting agent sessions.

## What MCP-MCP does about it

MCP-MCP is a reverse proxy for the Model Context Protocol: to your agents it is a single MCP server, to your MCP servers it is a single client. Every call flows through one enforcement pipeline — rate limit → RBAC → schema validation → guardrail → approval → audit — which is what makes the features below possible *by construction* rather than by convention. There is no second path around it: REST, MCP, the web UI and webhook triggers all end up in the same invoker.

| Feature | How |
|---|---|
| 🔌 **One endpoint per agent** | All upstream servers aggregated behind one Streamable-HTTP endpoint, tools namespaced `server__tool` |
| 🔄 **Hot-swappable servers** | Add/remove/reconfigure upstreams at runtime; connected agents get `tools/list_changed`, no restarts |
| 🪙 **Token saving** | Per-agent profiles: pin frequently used tools with full schemas, expose the long tail via `search_tools` / `describe_tool` / `invoke_tool` meta-tools (≥ 96 % schema-token reduction in the reference setup) |
| 🌉 **Protocol bridges** | Every tool is also callable via REST (generated OpenAPI 3.1). Existing **REST APIs** (OpenAPI), **JSON-RPC services** (OpenRPC) and **CLI programs** can be imported and appear as MCP tools |
| 📜 **Full audit log** | Who / what / when / result for every call — including denied ones — with secret redaction before persistence |
| 🔐 **RBAC** | Per-agent API keys, roles with server/tool/action-level grants, default-deny, visibility follows permission |
| 🛡️ **Secret guardrail** | Tool arguments and results are scanned for credentials and blocked before they reach the upstream or the model's context ([ADR-0011](docs/adr/0011-secret-erkennung-als-guardrail.md)) |
| ✋ **Human approval** | Selected tools are gated behind an explicit release, bound to identity, tool and argument fingerprint ([ADR-0012](docs/adr/0012-approval-flows-asynchron.md)) |
| ⏳ **Long-running work** | Approvals, and anything else that outlives one call, are *tasks*: persisted, cancellable, polled ([ADR-0019](docs/adr/0019-langlaufende-tasks-und-events.md)) |
| 🧩 **WASI plugins** | Signed WebAssembly components run in an out-of-process Rust host with per-interface capability grants, fuel/memory/time limits and an audit record of every grant ([ADR-0017](docs/adr/0017-wasi-component-runtime.md), [ADR-0020](docs/adr/0020-wasi-runtime-out-of-process-rust-host.md)) |
| 📦 **Connector packages** | Signed `.mcpkg` packages install through quarantine with a real probe, activate atomically, and roll back ([ADR-0016](docs/adr/0016-versionierter-connector-plugin-vertrag.md)) |
| 🔑 **Upstream OAuth** | Hosted MCP servers that require OAuth can be connected: discovery per RFC 9728, authorization code with PKCE S256 and the RFC 8707 `resource` indicator, tokens stored encrypted and refreshed automatically |
| 🔍 **Tool-definition pinning** | An upstream that silently changes a tool's description or schema has it held back until an admin accepts the new version — the classic rug-pull path, which no MCP standard covers |
| 📈 **Observability** | Metrics and OpenTelemetry traces per call, with a child span isolating upstream time from gateway overhead. Spans deliberately carry no arguments or results |
| 🖥️ **Web UI** | Blazor admin panel: server management, tool explorer, RBAC, live dashboard, log search, token cockpit, approvals, tasks, packages |
| 📚 **Central skill distribution** | Versioned text assets (skills/prompts/instructions) served to all agents as MCP prompts/resources |

Dockerized (< 300 MB, non-root, amd64 **and** arm64). [Formal NFR-01 benchmark](docs/acceptance/performance.md) on reference hardware: **p95 = 7.3 ms** per call, ~6400 calls/s, 0 errors under 20 sessions / 100 in-flight.

## Architecture at a glance

```
Agents (MCP) ──┐                                                    ┌─ stdio / Streamable HTTP
REST clients ──┼─►  AuthN ─► rate limit ─► RBAC ─► validation ─►    ├─ OpenAPI / OpenRPC
Web UI ────────┤     guardrail ─► approval ─► routing ─►            ├─ CLI (host or container)
Webhooks ──────┘     timeout ─► audit                               └─ WASI component (Rust host)
                     (one pipeline, no bypass)
```

Built on the [official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk). Runs as a single Docker container (or bare `dotnet run`), SQLite by default, PostgreSQL optional.

## Quickstart (Docker)

```bash
docker compose up -d
```

```bash
docker compose logs mcpmcp
```

The first start prints an agent **API key** (`mcpk_…`) and a **UI password** for user `admin`, once. Then:

```bash
claude mcp add --transport http mcpmcp http://localhost:8080/mcp --header "Authorization: Bearer <API-KEY>"
```

The web UI is at `http://localhost:8080`. Add upstream servers, roles and profiles from there or via the REST API — no config files. Always run behind a TLS reverse proxy in production; see [docs/operations.md](docs/operations.md).

## Building from source

```bash
git clone https://github.com/LupusMalusDeviant/mcp-mcp.git
```

```bash
dotnet build MCPMCP.slnx
```

```bash
dotnet test MCPMCP.slnx
```

```bash
dotnet run --project src/McpMcp.Server
```

Requires the .NET 10 SDK. The integration tests spawn reference MCP servers (`tests/McpMcp.TestServers/*`) as real stdio/HTTP processes.

Some proofs need extra infrastructure and **skip** without it — deliberately, so a machine without Docker or Rust doesn't fail the build. Set the matching variable to turn a skip into a failure, which is what CI does:

| Variable | Turns on | Needs |
|---|---|---|
| `MCPMCP_REQUIRE_POSTGRES=1` | PostgreSQL persistence tests | Docker (Testcontainers) |
| `MCPMCP_REQUIRE_CONTAINER=1` | CLI container isolation against a live runtime | Docker in Linux-container mode |
| `MCPMCP_REQUIRE_WASI_HOST=1` | WASI tests against the real Rust host | `cargo build --release` in `spikes/wasi-component-runtime` |

## Security

Read this part before exposing the gateway to anything you care about.

- **The pipeline is the security boundary.** Every call — MCP, REST, UI, webhook — runs through the same invoker. Connectors never see the database, the RBAC store or the approval queue.
- **Pattern-based secret detection catches what has a pattern** (`AKIA…`, `ghp_…`, PEM blocks). A random 32-character password is indistinguishable from a file id. The guardrail is a layer, not a substitute for keeping secrets out of tool results.
- **Not every isolation path is a sandbox.** CLI programs in *host* mode run as child processes with the gateway's rights; the hardening (canonical paths, root allowlists, optional SHA-256 pin, byte and time limits, isolated environment) reduces the attack surface but is not a kernel boundary. Container mode and WASI components are ([ADR-0018](docs/adr/0018-native-prozess-und-container-isolation.md)).
- **WASI is a sandbox, not a guarantee.** Its safety depends on the host functions, directories, sockets and secrets you grant. Grants default to none and are audited on every load.
- **A security audit was performed early on and missed a real defect** — a redaction gap found later by an independent requirement-versus-code review. Treat the audit as one input, not a clean bill of health. Findings and accepted residual risks are documented in the [threat model](docs/security/threat-model.md).

## Documentation

The full design documentation lives in [`docs/`](docs/) — written in **German**:

- [`docs/prd/`](docs/prd/) — requirements (Lastenheft): 41 functional requirements, NFRs, acceptance criteria
- [`docs/adr/`](docs/adr/README.md) — architecture decisions, from proxying and governance to capabilities, connectors, WASI and tasks
- [`docs/plans/`](docs/plans/) — implementation plans (Pflichtenheft): work packages with definitions of done, test strategy, coding rules
- [`docs/operations.md`](docs/operations.md) — running it: TLS, key ring, backups, connector packages, internal network targets
- [`docs/security/threat-model.md`](docs/security/threat-model.md) — findings, fixes and accepted residual risks
- [`docs/gateway-cli.md`](docs/gateway-cli.md) — the official CLI client

## Roadmap

| Milestone | Scope | Status |
|---|---|---|
| M1 "Skeleton talks" | Foundation, upstream connectors, supervisor with crash-restart | ✅ done |
| M2 "Enforcement stands" | Catalog, RBAC, audit, MCP endpoint, hot-swap | ✅ done |
| M3 "Both bridges carry" | REST facade, OpenAPI import | ✅ done |
| M4 "Web UI and hardening" | Blazor admin panel, security audit, Docker release | ✅ done |
| M5 "Gap closure" | 23 planned-but-missing items found by independent requirement-versus-code audits | ✅ done |
| M6 "Guardrails" | Secret detection in the invoker, runtime-editable rules ([ADR-0011](docs/adr/0011-secret-erkennung-als-guardrail.md)) | ✅ done |
| M7 "All optional reqs" | Approval flows ([ADR-0012](docs/adr/0012-approval-flows-asynchron.md)) and signed webhook triggers ([ADR-0013](docs/adr/0013-webhook-trigger.md)) built; FR-04 documented as a deviation | ✅ done |
| M8 "v0.5.0 release" | [Acceptance against the actual state](docs/acceptance/v1.2.md), then the release | ✅ [released](https://github.com/LupusMalusDeviant/mcp-mcp/releases/tag/v0.5.0) |
| CLI transport | Typed manifests, byte caps, isolated environment, path roots, optional SHA-256 pin, process-tree lifecycle — plus container mode proven against a live runtime ([ADR-0014](docs/adr/0014-cli-programme-als-upstream-transport.md), [ADR-0018](docs/adr/0018-native-prozess-und-container-isolation.md)) | ✅ on `main` |
| Gateway CLI | Official public-contract client for status, tool discovery/invocation, servers, approvals and audit ([usage](docs/gateway-cli.md)) | ✅ on `main` |
| WASI plugin path | Signed components, out-of-process Rust host, per-interface grants, module cache, IPC contract v4 with correlation, concurrency and confirmed cancellation | ✅ on `main`; `stream`/`future` deferred |
| Capability model | Protocol-neutral descriptors and results with stable gateway error codes ([ADR-0015](docs/adr/0015-protokollneutrales-capability-modell.md)) | ✅ on `main`; artifacts and pagination open |
| Tasks | Approvals generalised into persisted, cancellable tasks; polling is the contract ([ADR-0019](docs/adr/0019-langlaufende-tasks-und-events.md)) | ✅ on `main`; event delivery deferred |
| OpenRPC | JSON-RPC services as upstreams, fail-closed schema import | ✅ on `main` |
| Connector packages | Signed `.mcpkg`, quarantine install with probe, update/rollback, trust levels ([ADR-0016](docs/adr/0016-versionierter-connector-plugin-vertrag.md)) | ✅ on `main`; WASI transport only |
| Upstream OAuth | Authorization-code flow with PKCE against upstream authorization servers; discovery targets run through the same SSRF guard as schema imports | ✅ on `main`; not yet exercised against a real authorization server |
| Rug-pull protection | Tool definitions are fingerprinted per discovery; a changed tool is withheld until accepted | ✅ on `main`; trust-on-first-use |
| Observability | Metrics plus OTel traces, exported when an OTLP endpoint is configured | ✅ on `main`; no alerting rules shipped |
| gRPC / GraphQL | Unary gRPC has a design spike; GraphQL has a decision matrix | ⏳ open |
| Skills | Declared metadata (when-to-use, references, required tools), validation against the live catalog, version history with rollback, size limit, and `list_skills` / `read_skill` as meta-tools | ✅ on `main` |
| Skills in packages | A connector package carries the skills that explain its connector; consent is bound to the text, not the publisher ([ADR-0021](docs/adr/0021-skills-in-paketen.md)) | ✅ on `main`; a package type for skill bundles without a connector is decided but not built |
| M9 "v0.6.0 pre-release" | Everything since v0.5.0 brought into a tagged build | ✅ [pre-release](https://github.com/LupusMalusDeviant/mcp-mcp/releases/tag/v0.6.0) |
| First real operation | An instance running on real hardware — which surfaced three defects no test could have found: a silently dropped session cookie over HTTP, `http://` redirects behind a TLS proxy, and **an admin UI that was never interactive at all** because the Blazor entry point was never served | ✅ fixed in [v0.6.1](https://github.com/LupusMalusDeviant/mcp-mcp/releases/tag/v0.6.1) and [v0.6.2](https://github.com/LupusMalusDeviant/mcp-mcp/releases/tag/v0.6.2) |
| "1.0" | Real-world operation over time — the one thing tests can't provide | ⏳ open |

## License

[MIT](LICENSE)
