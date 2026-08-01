# Support, release channels and reporting

> **Source of truth.** This page has no German original — it *is* the source version (see
> [`docs/i18n.md`](../i18n.md)). The **security policy** it summarises is
> [`SECURITY.md`](../../SECURITY.md), which is authoritative for supported versions and for
> vulnerability reporting; **on contradiction, `SECURITY.md` wins.**

---

## Support matrix

B.I.F.R.O.S.T is **pre-1.0**, and that is the whole statement: the code is feature-complete for its
scope and covered by tests against SQLite *and* real PostgreSQL, but it has very little operational
uptime behind it. 1.0 follows from running it over time, not from adding features.

| Version | Status | Notes |
|---|---|---|
| `main` | ✅ best effort | Security fixes land here first |
| `v0.12.0` | ✅ best effort | Current release (2026-08-01). First release produced by the release pipeline |
| `v0.11.0` | ⚠️ superseded | First release under the name B.I.F.R.O.S.T; protocol revision `2026-07-28` |
| `v0.5.0` … `v0.10.0` | ⚠️ superseded | Tags exist; no maintained changelog entries, and some have no GitHub release |
| early `1.x` builds | ❌ withdrawn | Removed entirely — never a valid release. **If you ran one, act:** see the [advisory](../../SECURITY.md#advisory-for-anyone-who-ran-an-early-1x-build) |

"Best effort" means what it says. There is no support contract, no SLA, and no backport branch. A
fix lands on `main` and reaches you in the next release.

### What is supported

| | |
|---|---|
| **Deployment shape** | Self-hosted, single operator ([ADR-0001](../adr/0001-zentraler-proxy-gateway-statt-direktanbindung.md)). Docker on Linux (amd64 and arm64), or `dotnet run` from source |
| **Database** | SQLite (default) and PostgreSQL — with the [PostgreSQL gaps](operations.md#known-limitations) stated plainly |
| **Runtime** | .NET 10. The container image runs non-root |
| **MCP revisions** | `2026-07-28` (default, stateless) and `2025-11-25` (session mode via `BIFROST_MCP_STATELESS=0`) |
| **Client side** | The gateway speaks **Streamable HTTP only** as a server. Agents that can only do the legacy SSE transport cannot connect. *Upstream* servers may still use SSE — the gateway falls back automatically, switchable per server |

### What is not supported, and is not pretending to be

- **High availability.** `compliance` audit mode needs a durable external spool/queue backend before
  production HA use; it does not exist. Real multi-node operation against one PostgreSQL database
  has never been exercised.
- **PostgreSQL backup and restore.** Not implemented; the command refuses. See
  [Known limitations](operations.md#known-limitations).
- **Downgrades.** `Down` migrations are neither run nor tested. The way back from a failed upgrade
  is the pre-migration backup — which does not exist on PostgreSQL.
- **Throughput claims.** The benchmark measures a 0.1 s burst, which is a peak rate, not sustained
  load. Latency figures are published ([performance](../acceptance/performance.md)); throughput is
  not, because it was not measured.
- **Alerting rules.** Metrics and traces are exported when an OTLP endpoint is configured; no
  alerting rules ship with the product.
- **Batch requests and notifications for OpenRPC upstreams.** A batch would bundle several calls
  into one message, each of which would have to pass RBAC, guardrail, approval and audit separately
  — otherwise it becomes a route around governance.
- **gRPC and GraphQL upstreams.** Unary gRPC has a design spike; GraphQL has a decision matrix.
  Neither is built.

---

## Release channels

### Container image

`ghcr.io/lupusmalusdeviant/bifrost`, tagged:

| Tag form | Example | Use |
|---|---|---|
| `<version>` | `0.12.0` | Normal operation |
| `<major>.<minor>` | `0.12` | Follows patches within a minor line |
| `sha-<short-sha>` | `sha-71e4acf` | Pinning to an exact commit |
| `@sha256:<digest>` | — | **Production.** A tag can be re-pointed in a registry, a digest cannot |

**There is deliberately no `latest`.** A moving pointer turns a restart — a power cut, a
`restart: unless-stopped` — into an unnoticed upgrade. Which version runs belongs in a file you read
before you change it, and that file is `.env`.

Image size: 315 MB for v0.11.0 against a CI gate of < 350 MB. Non-root, amd64 **and** arm64.

### CLI archives

Every release carries one archive per platform plus a shared `checksums.txt`:

| Platform | Archive |
|---|---|
| Windows x64 | `bifrost-cli-<version>-win-x64.zip` |
| Linux x64 | `bifrost-cli-<version>-linux-x64.tar.gz` |
| Linux arm64 | `bifrost-cli-<version>-linux-arm64.tar.gz` |
| macOS Intel | `bifrost-cli-<version>-osx-x64.tar.gz` |
| macOS Apple Silicon | `bifrost-cli-<version>-osx-arm64.tar.gz` |

Each archive contains exactly two files: the program and `LICENSE`. The runtime is bundled, so the
target machine needs neither SDK nor runtime. Installation details:
[`docs/cli-installation.md`](../cli-installation.md) (German).

### What comes with a release

The `v0.12.0` run produced 13 attachments: five CLI archives, six SBOMs, checksums and a signature
bundle — keyless-signed, with six attestations, and Trivy gates green on both the image and the CLI
artefacts.

**Verify it yourself.** You need no key from us; verification runs against public Sigstore
infrastructure and GitHub. The procedure is in
[`docs/security/verifying-releases.md`](../security/verifying-releases.md) (German), and its most
important section is the one that has you confirm the verification **rejects** something wrong — a
green tick means nothing until you have watched the same tool go red on the wrong input.

### Versioning

[Semantic Versioning](https://semver.org/), with the qualification a `0.x` line carries: **breaking
changes can appear in a minor version.** They are then listed in [`CHANGELOG.md`](../../CHANGELOG.md)
under *Geändert* with the word *Breaking* and an upgrade note.

The changelog names what an operator notices, not every commit — and it is also where **open
proofs** stay visible. What a release does *not* demonstrate is written down next to what it does.

### Pre-release marking — an open decision

The requirements document (FR-P007) asks that pre-releases be marked as such and that `latest` be
used only for stable releases. On 2026-07-31, `v0.11.0` was deliberately promoted to a normal
release because GitHub otherwise kept advertising `v0.5.0` as "Latest" — the two switches are not
independent there (`Latest release cannot be draft or prerelease`).

So the requirement currently stands against the actual state, and the decision is open:

1. **Amend FR-P007** — the `0.x` line already carries "not stable" in the version number, and the
   landing page should show the current state.
2. **Amend the actual state** — mark releases as pre-releases again and accept that GitHub shows a
   stale version as "Latest" until the first stable release.

**Until it is decided, the documentation describes the actual state, not the target rule.** That
sentence is itself the policy: no page here will claim a channel discipline the repository does not
practise.

---

## Reporting

### Security vulnerabilities

**Do not open a public issue.** Use
[GitHub private vulnerability reporting](https://github.com/LupusMalusDeviant/bifrost/security/advisories/new)
("Report a vulnerability" under the Security tab).

- **Initial response within 7 days.**
- Include reproduction steps and, if you can, the affected component: gateway pipeline, RBAC, audit,
  upstream connectors, or web UI.
- Explicitly in scope: RBAC bypass, redaction or audit-logging bypass, injection through upstream
  metadata, anything that makes a tool visible or callable without a grant.
- Explicitly out of scope: vulnerabilities in connected third-party MCP servers themselves;
  deployments that expose the gateway without TLS despite the documentation; denial of service
  through a deliberately misconfigured self-hosted instance.

Full policy, including the security model you should read before deploying:
[`SECURITY.md`](../../SECURITY.md). Operator-facing summary: [Security](security.md).

### Bugs and feature requests

GitHub issues, using the templates. The bug template asks for version, database provider, deployment
shape and `bifrost doctor` output up front — those four answer most questions before anyone replies,
and a `doctor` report is safe to paste: it never names the location of key material and never shows
a password.

### Documentation problems

Also an issue, labelled `docs`. If the German and English versions of the same procedure contradict
each other, **say so in the report and name both places** (file plus heading). Follow the source
language version in the meantime — see [`docs/i18n.md`](../i18n.md) for which that is.

### Contributing a fix

[`.github/CONTRIBUTING.md`](../../.github/CONTRIBUTING.md).
