# Security Policy

## Supported versions

MCP-MCP is in **pre-release development**. There are no supported release versions yet; security fixes land on `main`. Once v1.0 ships, this table will list supported versions.

| Version | Supported |
|---|---|
| `main` | ✅ best effort |
| `v0.6.1` | ✅ best effort |
| `v0.6.0` | ⚠️ superseded by `v0.6.1` |
| `v0.5.0` | ⚠️ superseded by `v0.6.0` |

### Advisory for anyone who ran an early 1.x build

An early 1.x line was withdrawn and has since been removed entirely — it was never a valid release.
The reason was a redaction defect: on the lazy path (`invoke_tool`), tool arguments reached the
audit log **unredacted**, so credentials passed through that path were persisted in plaintext,
while the same call via `tools/call` was masked correctly.

If you ever ran one of those builds: inspect the `AuditEvents` table, delete the affected rows, and
rotate every credential that appeared there. Details are in the
[threat model](docs/security/threat-model.md) (finding 7).

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Use [GitHub private vulnerability reporting](https://github.com/LupusMalusDeviant/mcp-mcp/security/advisories/new) ("Report a vulnerability" under the Security tab). You will get an initial response within **7 days**. Please include reproduction steps and, if possible, the affected component (gateway pipeline, RBAC, audit, upstream connectors, Web UI).

## Security model — what you should know before deploying

MCP-MCP is a security-relevant component by design: it terminates every tool call and holds credentials for all connected upstream servers. The intended posture (see `docs/adr/`, German):

- **Credential concentration:** Upstream credentials are stored encrypted (ASP.NET Data Protection); agent API keys are stored only as hashes. The gateway host is still a high-value target — harden it accordingly (dedicated user, TLS via reverse proxy, restricted network exposure).
- **stdio upstreams run with gateway privileges** ([ADR-0005](docs/adr/0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md)): there is **no sandbox** between the gateway and stdio MCP-server child processes. Only connect MCP servers you trust, exactly as you would when attaching them to an agent directly. Container isolation exists for the **CLI** transport and WASI components run in a real sandbox — stdio is still the unisolated path. Since 2026-07-28 the child no longer inherits the gateway's environment (so the database and key-ring passwords are not readable from it), but that is blast-radius reduction, not a sandbox: same user, same filesystem, same network — including the key ring that decrypts every upstream credential.
- **Isolated paths and their limits:** CLI upstreams can run per-invocation in a hardened container (read-only rootfs, non-root, all capabilities dropped, no network unless granted — [ADR-0018](docs/adr/0018-native-prozess-und-container-isolation.md)); WASI components run out-of-process with per-interface grants that default to none ([ADR-0020](docs/adr/0020-wasi-runtime-out-of-process-rust-host.md)). WebAssembly is a sandbox boundary, but its safety still depends on the directories, sockets and secrets you grant.
- **Only signed plugins load.** WASI components and connector packages are verified against pinned Ed25519 publisher keys; an empty trust store loads nothing. Revoking a publisher stops its running upstreams immediately ([ADR-0016](docs/adr/0016-versionierter-connector-plugin-vertrag.md)).
- **Default-deny RBAC:** agents see and reach only what a role explicitly grants. If you observe a tool being visible or callable without a grant, that is a vulnerability — please report it.
- **Audit integrity:** every call (including denied ones) is logged with secret redaction. Bypasses of redaction or of audit logging are vulnerabilities.
- **Untrusted input:** tool descriptions and results from upstream servers are treated as untrusted content (encoding in the UI, no execution). Injection paths through upstream metadata are in scope for reports.
- **Tool definitions are pinned.** Name, description and input schema are fingerprinted on every discovery; a changed tool is withheld from the catalogue — not callable, not visible — until an administrator accepts the new version. This covers the rug-pull path, which no MCP standard addresses. The limit is trust-on-first-use: it protects against changes **after** adoption, not against an upstream that was malicious from the start.
- **No token passthrough.** The gateway never forwards an agent's credential to an upstream; it holds its own upstream credentials. Where an upstream uses OAuth, the token is bound to that upstream via the RFC 8707 `resource` indicator and is not usable elsewhere.

## Out of scope

- Vulnerabilities in connected third-party MCP servers themselves
- Deployments that expose the gateway without TLS/reverse proxy despite the documentation
- Denial of service through deliberately misconfigured self-hosted instances
