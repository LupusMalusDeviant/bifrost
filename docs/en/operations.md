# Operations

> **Source of truth.** This page is a translation of [`docs/operations.md`](../operations.md)
> (German), which is **authoritative**. On contradiction, the German page wins — it is the one that
> changes first. The full rule is in [`docs/i18n.md`](../i18n.md). The German original is also
> considerably longer; this page covers what an operator needs and links onward for the rest.

> **About the commands on this page.** None of them were executed against a live instance while
> writing this page — there was none. The `docker compose config -q` validations in
> [Quickstart](quickstart.md#verify-the-compose-files-runnable-now) were run and passed. **No
> sample output below is invented.** Where a command's output matters, this page describes what to
> look for rather than showing a transcript that never happened.

Audience: self-hosted single-operator deployments ([ADR-0001](../adr/0001-zentraler-proxy-gateway-statt-direktanbindung.md)).

- [Known limitations](#known-limitations) ← read this before you plan a deployment
- [Configuration](#configuration-environment-variables)
- [TLS and reverse proxy](#tls-and-reverse-proxy)
- [Protocol revision](#protocol-revision-stateless-or-session-based)
- [Guardrails](#guardrails)
- [Approvals and tasks](#approvals-and-tasks)
- [Private network targets](#private-network-targets)
- [Key ring protection](#key-ring-protection)
- [Backup and restore](#backup-and-restore)
- [Upgrades](#upgrades)
- [Diagnostics](#diagnostics)
- [Configuration export and import](#configuration-export-and-import)
- [Observability](#observability)
- [Resetting access](#resetting-access)

---

## Known limitations

These are product gaps, not documentation gaps. They are listed first because each one changes how
you would plan a deployment, and each one is easy to discover too late.

| Limitation | Consequence | Detail |
|---|---|---|
| **PostgreSQL backup needs `pg_dump`/`pg_restore` on the host.** Without them the call is refused with a message rather than silently exporting rows | Install the PostgreSQL client package (or set `BIFROST_POSTGRES_BIN`). Without it, backup on PostgreSQL stays entirely your own responsibility: `pg_dump` **plus** the `keys/` directory | [Backup](#backup-and-restore), [ADR-0024 E2](../adr/0024-backup-restore-und-migrationssicherheit.md), [upgrade matrix §4.5](../upgrade-matrix.md) |
| **Whether the shipped container image carries `postgresql-client` is an open decision** (image weight vs. the 350 MB CI gate) | If it does not, PostgreSQL backup inside the container needs the client mounted or installed. Everything else keeps running; only backup and restore report the refusal | [Backup](#backup-and-restore) |
| **Without those tools, no pre-migration backup is taken on PostgreSQL.** The start warns and migrates anyway | **That upgrade runs without a way back.** With the tools present the server demands the backup (`Always`); without them, take your own dump before every upgrade | [Upgrades](#upgrades), [upgrade matrix §4.8](../upgrade-matrix.md) |
| **A `pg_restore` cannot be undone by moving files back.** Its safety net is the pre-backup taken beforehand, plus `--single-transaction` while it runs | On SQLite a failed switch-over rolls back; on PostgreSQL a *completed* restore is final. Keep the pre-backup | [Backup](#backup-and-restore) |
| **`AllowPrivateTargets` is still undecided for *existing* HTTP upstreams — which means allowed** | An MCP-over-HTTP upstream configured before the switch existed still reaches private, loopback and link-local addresses. `null` means "not decided", not "forbidden" | [Private network targets](#private-network-targets) |
| **Config import and restore keep `AllowPrivateTargets` as it was** | Deliberate: those paths reproduce an existing configuration, they do not create one. A restore that silently tightened a setting would change what it claims to restore. Newly created upstreams *do* get an explicit `false` | [Private network targets](#private-network-targets) |
| **`CODEOWNERS` enforces nothing on its own** | Without "Require review from Code Owners" in the branch protection rules for `main`, the security-gate approval requirement is documented but **not enforced** | [Security](security.md#the-approval-requirement-is-not-enforced-yet) |
| **The upgrade harness checks old *schema* with today's *code*** | It proves migrations do not damage existing rows. It does **not** prove that a format change is safe — it never writes the data in the old form. A serialisation regression from an earlier build is invisible to it | [Upgrades](#upgrades), [upgrade matrix §4.3](../upgrade-matrix.md) |
| Backup/restore was never exercised across machines or accounts | On Windows the key ring is DPAPI-bound to the executing user; a full backup restored on another machine or under another account yields rows without readable content. Not an issue for Linux containers | [upgrade matrix §4.6](../upgrade-matrix.md) |

---

## Installation

Three commands. Nothing is built — the standard path pulls a published image. See
[Quickstart](quickstart.md) for the walk-through.

```bash
cp .env.example .env           # set BIFROST_VERSION to a published release
docker compose up -d
curl -fsS http://localhost:8080/healthz
```

### There is no `latest`

The image is `ghcr.io/lupusmalusdeviant/bifrost`, tagged with `<version>` (`0.12.0`),
`<major>.<minor>` (`0.12`) and `sha-<short-sha>`. A `latest` tag is deliberately **not** set: a
moving pointer turns a restart into an unnoticed upgrade. Which version runs belongs in a file you
read before you change it — that file is `.env`.

For production, pin the digest with `BIFROST_IMAGE`. `docker compose pull` is still worth running
with a digest: it fetches exactly that manifest and fails if it no longer exists, instead of
silently using an older local copy.

### The volume name — the most expensive line on this page

Compose prefixes every volume with the **project name**, which defaults to the lowercased name of
the directory containing the compose file. `bifrost-data` therefore becomes
`<project>_bifrost-data`.

```bash
docker compose config --volumes        # the keys
docker compose config | tail -6        # the full names, including the prefix
```

Rename the key, rename the directory, or move the compose file elsewhere, and you point at a
**different** volume. Docker creates it silently and empty, the gateway finds an empty database,
initialises it and reports itself ready — with no servers, no roles, no key ring. **The failure
looks like a successful start.** It surfaces only when someone calls a tool or tries to log in.

Set `COMPOSE_PROJECT_NAME=bifrost` once, on a new installation, to decouple the name from the
directory. Doing it on an existing installation changes the volume name too — then you need the
migration path below.

### Migrating an MCP-MCP installation

An installation from before the rename (2026-07-31) has service `mcpmcp` and volume
`<project>_mcpmcp-data`. Today's compose file names both `bifrost`. Look before you act:

```bash
docker volume ls | grep -E 'mcpmcp|bifrost'
docker compose config | tail -6
```

If they differ, the contents must be moved. Docker cannot rename volumes; copy through a throwaway
container with the gateway **stopped**. `cp -a` preserves ownership — the container runs as
non-root `app`, and a copy without `-a` hands it a directory it cannot write. The exact commands
are in [`docs/operations.md` → *Umstieg einer MCP-MCP-Installation*](../operations.md).

Two things the gateway handles by itself: an existing `mcpmcp.db` keeps being used (no empty
`bifrost.db` appears next to it), and old `MCPMCP_*` environment variables are adopted as
`BIFROST_*` and named once in the log. The DataProtection application name stays `MCPMCP` on
purpose — it feeds the key derivation, and changing it would make every stored ciphertext
unreadable.

---

## Configuration (environment variables)

Configuration lives in `.env`, not in the compose file. `.env` is in `.gitignore`; use `chmod 600`.
Because its entire contents go into the container environment, only `BIFROST_*`, `POSTGRES_*`,
`COMPOSE_*` and `OTEL_*` belong there — a `PATH=` line would overwrite the container's environment.

Every `BIFROST_*` setting can also be supplied from a file via a `<NAME>_FILE` suffix. If both a
value and its `_FILE` form are set, **the start aborts**: a precedence rule between two sources of
the same secret is a rule you would misremember, and then you would be running with the wrong one.

| Variable | Default | Purpose |
|---|---|---|
| `BIFROST_DATA_DIR` | `data` (`/data` in the container) | Directory for the SQLite database **and** the DataProtection key ring |
| `BIFROST_DB_PROVIDER` | `sqlite` | `sqlite` or `postgres` |
| `BIFROST_DB_CONNECTION` | `Data Source=<datadir>/bifrost.db` | Connection string (mandatory for Postgres) |
| `BIFROST_AUDIT_MODE` | `best-effort` | `best-effort` drops under overload and counts the drops; `compliance` reports overload explicitly and retries DB failures with backpressure |
| `BIFROST_AUDIT_RETENTION_DAYS` | `30` | Audit retention in days; older events are deleted daily (FR-25) |
| `BIFROST_AUDIT_DEBUG_PAYLOADS` | *(off)* | `1`/`true` writes full response payloads to the audit log, redacted. Debug aid, not for continuous operation |
| `BIFROST_BOOTSTRAP_TTL_MINUTES` | `60` | Lifetime of the setup token. An invalid value falls back to the default |
| `ASPNETCORE_URLS` | `http://+:8080` (container) | Bind address/port |
| `BIFROST_TRUSTED_PROXIES` | *(unset)* | `any`, or a comma list of addresses and CIDR ranges. Unset means forwarded headers are **ignored** |
| `BIFROST_PUBLIC_BASE_URL` | *(unset)* | Public address of this gateway; required for the upstream OAuth redirect URI |
| `BIFROST_KEYRING_PROTECTION` | *(unset)* | Explicit mode: `certificate`, `file-secret` or `none`. Unset = **no choice made**, and the start warns |
| `BIFROST_KEYRING_CERT_PATH` | *(unset)* | PFX certificate encrypting the key ring |
| `BIFROST_KEYRING_CERT_PASSWORD` | *(unset)* | PFX password. **Lives in the process environment** and is readable via `docker inspect` |
| `BIFROST_KEYRING_CERT_PASSWORD_FILE` | *(unset)* | The same password as a **file secret** (FR-P048). Setting both forms aborts the start |
| `BIFROST_KEYRING_CERT_PATH_PREVIOUS` | *(unset)* | The **previous** certificate during a rotation. It no longer encrypts, but still decrypts |
| `BIFROST_OAUTH_ISSUER` | *(unset)* | Authorization server trusted for **inbound** agent tokens. Set = the gateway is also an OAuth resource server |
| `BIFROST_OAUTH_AUDIENCE` | `BIFROST_PUBLIC_BASE_URL` | Canonical address of this gateway; a token must be addressed to it |
| `BIFROST_WASI_HOST` | *(unset)* | Path to the WASI host binary. **Required to install connector packages** — without it a package cannot be probed, and nothing unprobed is activated |
| `BIFROST_MCP_STATELESS` | `1` | `0`/`false` reverts to the session-based operation of the previous protocol revision |
| `BIFROST_MCP_LIST_TTL_SECONDS` | `60` | How long a client may treat tool/resource/prompt lists as fresh. `0` = no hint |
| `BIFROST_MAX_RESULT_CHARS` | *(off)* | Truncates tool results above this character count (FR-16) |
| `BIFROST_GUARD_ENABLED` | `1` | `0`/`false` disables the secret guardrail globally (emergency stop) |
| `BIFROST_GUARD_MAX_SCAN_CHARS` | `262144` | Payloads above this are **rejected**, not passed unscanned |
| `BIFROST_GUARD_ALLOW_CUSTOM_PATTERNS` | *(off)* | Allows admins to enter free-form regex in the UI |
| `BIFROST_BACKUP_PASSPHRASE` | *(unset)* | Encrypts the **automatic** pre-migration backups. Without it they are written unencrypted; with it they are worthless without the passphrase |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(unset)* | Target for metrics **and** traces |

---

## TLS and reverse proxy

The gateway terminates no TLS of its own. **Always put it behind a reverse proxy** (Caddy, nginx,
Traefik) with TLS — it holds upstream credentials and API keys, and cleartext transport is
unacceptable (NFR-04).

```
gateway.example.com {
    reverse_proxy localhost:8080
}
```

The proxy must forward `X-Forwarded-Proto` **and** the port in `Host` (`proxy_set_header Host
$http_host` — `$host` drops the port). The web UI additionally needs WebSockets (Blazor Interactive
Server) and unbuffered responses for `/mcp` (server-sent events). Set `BIFROST_TRUSTED_PROXIES`, or
the gateway builds `http://` redirects behind your `https://` proxy — see
[Quickstart step 3](quickstart.md#3-open-the-web-ui) for the full symptom table.

The UI cookie is `SameSite=Strict` + `HttpOnly`, and `Secure` outside `Development`.

---

## Protocol revision: stateless or session-based

The gateway speaks MCP spec revision **`2026-07-28`**, which removed the `initialize` handshake and
`Mcp-Session-Id` — every request stands on its own. That is the default and the right setting for
most installations ([ADR-0023](../adr/0023-stateless-kern-und-mrtr.md)).

**Older clients keep working unchanged.** A client still speaking `2025-11-25` is served by the SDK
as before. Two things do require a standing session and are therefore missing for such a client in
stateless operation:

- **In-call approval prompts.** A `2026-07-28` client gets them via MRTR (the call ends with a
  question, the client repeats it with the answer). An older client does not — for it, the approval
  queue in the UI remains the path.
- **`tools/list_changed`.** Replaced by the list cache hint (`BIFROST_MCP_LIST_TTL_SECONDS`): the
  client re-fetches after expiry. A catalogue change is therefore visible after at most one TTL,
  not immediately.

`BIFROST_MCP_STATELESS=0` makes sense only when *all* connected clients are on the old revision and
the in-call prompt is needed. **The switch is gateway-wide, not per client:** in session mode a
`2026-07-28` request is rejected with `-32022 UnsupportedProtocolVersion`, after which the client
negotiates the old revision itself. It keeps working — but the whole installation then speaks the
old revision, including to clients that have long moved on.

An **upstream** on `2026-07-28` likewise no longer announces catalogue changes, so the gateway
re-polls such servers on a schedule (default: every minute) and forwards only real changes.

---

## Guardrails

The gateway scans tool **arguments** and tool **results** for credentials
([ADR-0011](../adr/0011-secret-erkennung-als-guardrail.md)). Rules are managed under **Guardrails**
in the web UI: enable/disable individually, switch between *block* and *observe*, add your own —
all at runtime, no restart.

The important direction is **result → agent**: a tool that returns a `.env`, a Kubernetes secret or
a database row otherwise pushes the value into the model's context window, and from there into its
logs and follow-up answers.

| Direction | Behaviour on block |
|---|---|
| Arguments | The call is aborted **before** the upstream. No side effect. |
| Result | The call **has already run**; only the result is withheld. |

The second case is the one that matters: for a writing tool, the action has happened. The error
message says so explicitly and tells the caller **not** to retry — otherwise an agent files the same
issue twice. In the audit log the event carries its own status `GuardBlocked`, distinguishable from
an RBAC `Denied`.

### Limits — read this before relying on it

Detection finds what has a **pattern**: `AKIA…`, `ghp_…`, `sk-ant-…`, PEM blocks, Slack webhooks.
It does **not** find what has none — a 32-character random password is indistinguishable from a file
id. Entropy heuristics are deliberately absent: they fire on git commit SHAs and UUIDs essentially
100 % of the time, and under "block" every false positive is an aborted piece of work rather than a
log line.

The guardrail is an **additional layer**, not a substitute for keeping credentials out of tool
results.

- **Findings never contain the found value.** Rule id, direction, mode and a hash are logged.
  Position and length live in the finding itself and do not reach the log. A secret detector that
  logs its finds in cleartext copies secrets into a second, usually less protected system.
- **Above the scan limit, payloads are rejected**, not passed — otherwise the limit would be
  exactly the blind spot to aim for. Combine with `BIFROST_MAX_RESULT_CHARS` if you expect large
  results: truncation happens first, and the truncated result passes the scan.

New custom rules always start in **observe** mode. Arming a rule you have never seen fire will, in
doubt, abort productive work.

---

## Approvals and tasks

Individual tools can be made approval-gated (FR-32,
[ADR-0012](../adr/0012-approval-flows-asynchron.md)). Which tools, is switchable at runtime under
**Approvals** in the admin area.

If the calling client can be asked, the gateway asks at the moment of the call — a dialogue in front
of a human, and consent lets exactly that one call through. If it cannot be asked, the request goes
to the queue; no call is lost. Which path applies depends on the client's protocol revision: MRTR
for `2026-07-28` and newer, classic elicitation for `2025-11-25` and older (session mode only).

> A client on the new revision that **cannot display a form** gets an error from its own SDK
> (*"no ElicitationHandler is registered"*) instead of the queue message, because since
> `2026-07-28` no client advertises an elicitation capability to distinguish on. **The request is
> not lost** — it is in the queue under *Approvals*.

An approval binds to `(identity, tool, argument fingerprint)`, expires after one hour, and is
**single-use**:

- No hanging agent — the per-call timeout from FR-09 is untouched, nothing blocks waiting.
- An approval for `delete_file{path:/tmp/x}` does **not** cover `delete_file{path:/etc/passwd}`.
- A repeat requires a fresh approval, so granted consent never becomes a standing permit.

Since 2026-07-26 approvals are stored as **tasks**
([ADR-0019](../adr/0019-langlaufende-tasks-und-events.md)) — behaviour unchanged, storage unified.
A waiting request is a task in state `input-required`, a granted approval one in `working`, a
denied one a failed task with code `approval-denied`.

```bash
curl -H "Authorization: Bearer $API_KEY" http://localhost:8080/api/v1/tasks
```

- `GET /api/v1/tasks` — paginated list (`page`, `pageSize`, filters `state` and `tool`).
  **Visibility follows ownership:** without a global grant you see only your own tasks. Someone
  else's task is `404`, not `403` — otherwise the status code would reveal which ids exist.
- `GET /api/v1/tasks/{id}` — a single task.
- `POST /api/v1/tasks/{id}/cancel` — cancellation. While **nothing is running**, it is final:
  `200`, state `Cancelled`, `Cancellation: Confirmed`, and **the approval can no longer be
  redeemed** — which is the actual point. An already redeemed or completed task answers `409`.

State is **pulled, not pushed** (ADR-0019). There is no subscription and no promise that a
notification arrives; if you need the state, ask for it. A background service marks overdue tasks
`expired` every five minutes — that is visibility, not enforcement, since the redemption path
checks expiry itself. Expired tasks are never deleted; they remain auditable in a terminal state.

---

## Private network targets

Both the OpenAPI and the OpenRPC connector fetch addresses an administrator configured. Without a
check, the gateway would be a tool for reaching **internal** services — the cloud metadata service
on `169.254.169.254`, an admin port on `127.0.0.1`, a neighbour on the corporate network. Both the
**source of the description** and the **target API** are checked: the hostname is resolved and *all*
its addresses are validated, and redirects during loading are each re-validated.

In the **call path** no connector follows a redirect. A `302` from the far side would otherwise
point at an address that was never checked; instead the call fails with an error naming the target.

For a service on your own network — the normal case in development setups — set the switch
explicitly, per upstream:

```json
"OpenApi": {
  "SpecLocation": "http://localhost:8080/openapi.json",
  "AllowPrivateTargets": true
}
```

In the UI this is the checkbox "Ziele im internen Netz erlauben" in the OpenAPI form.

> **Migration:** the default is `false`. Existing OpenAPI upstreams pointing at `localhost` or a
> private network will not come up after the update until the switch is set. The error names the
> address and the switch; the upstream shows as `Failed` — nothing continues silently.

### The gap: MCP-over-HTTP upstreams

**`AllowPrivateTargets` is still undecided for HTTP upstreams, and undecided means allowed.**

Until recently, MCP-over-HTTP was the one transport with no target check at all: the endpoint went
straight into the transport while OpenAPI, OpenRPC and the OAuth issuer rejected private addresses.
That was fixed — `HttpTransportOptions` now carries `bool? AllowPrivateTargets`, and the check runs.
But the tri-state is deliberate:

| Value | Meaning |
|---|---|
| `true` | private targets allowed |
| `false` | private targets rejected |
| `null` | **not decided — allowed** |

`null` is what every existing installation has, because the switch did not exist when those
upstreams were configured. Cutting them off at the next restart would be exactly the silent
behaviour change that [ADR-0025 E3](../adr/0025-host-ausfuehrung-verbieten-und-bestehende-instanzen-migrieren.md)
rejects for host execution — and an MCP server on your own network is the normal case for this
product, not the exception.

**For newly created upstreams the gap is closed.** The form and the API both call
`SecureUpstreamDefaults.ForNewUpstream` and write an explicit `false` before anything is stored.
That is verified against the *stored* record rather than the API response
(`A_newly_created_http_upstream_no_longer_stores_an_undecided_ssrf_switch`) — what is stored is what
applies at the next start.

**What remains open is the existing stock**, and two paths deliberately leave `null` alone:

| Path | Behaviour | Why |
|---|---|---|
| Form, API | writes `false` | Creation — there is a decision to make here |
| Config import, restore | leaves the value as it was | These paths **reproduce** a configuration, they do not create one. A restore that silently tightened something would not restore what it claims to |

So for operations: for upstreams configured before the switch existed, treat the endpoint as
unvalidated and set `AllowPrivateTargets: false` yourself wherever a private, loopback or link-local
target is not intended.

---

## Key ring protection

The DataProtection key ring under `<datadir>/keys/` decrypts **every** at-rest encrypted upstream
credential, OAuth token and webhook secret of this instance. How it is protected is one of **three**
modes — and none of them is a default state.

| Mode | Requires | Protects | Does not protect |
|---|---|---|---|
| `certificate` | `BIFROST_KEYRING_CERT_PATH` (PFX with private key), password in `BIFROST_KEYRING_CERT_PASSWORD` | The key files are encrypted. **A backup or volume dump alone** is no longer enough to reach the credentials | The password sits in `.env` and in the process environment — readable by anyone who may run `docker inspect` |
| `file-secret` | the same, but the password via `BIFROST_KEYRING_CERT_PASSWORD_FILE` (Compose/K8s secret) | additionally: the password never leaves the secret store. Neither `.env` nor `docker inspect` nor `/proc/<pid>/environ` show it | Whoever is root on the machine reaches both. That is the limit of any file-based scheme |
| `none` | explicitly `BIFROST_KEYRING_PROTECTION=none` | **nothing** — the key files are in cleartext. Defensible for a single instance with restrictive directory permissions | Every backup of the data directory then contains the upstream credentials |

If **nothing** is set, none of these modes applies: the ring lies in cleartext as with `none`, but
nobody decided that. The start warns, `bifrost doctor` reports `BFR-KEY-0002` as a warning, and
`--keyring-check` exits `3`. Choosing unprotected operation makes it a decision instead of a gap,
and turns the diagnosis green.

The server process brings the setup path with it — it creates certificate **and** password file and
sets restrictive permissions on both (Unix `0600`, Windows an ACL without inheritance), which is
exactly the step an `openssl` line from a tutorial does not do:

```bash
docker compose run --rm bifrost dotnet Bifrost.Server.dll --keyring-setup --cert /secrets/keyring.pfx
docker compose exec bifrost dotnet Bifrost.Server.dll --keyring-check
```

An existing certificate is **never** overwritten. Keep the certificate **next to** the data
directory, not inside it — otherwise it travels in every backup and protects against nothing.

**Rotation: rehearse before you switch.** `--keyring-rotate --new-cert … --new-password-file …`
copies the ring, tries to open it with the new **and** the old certificate, and answers either
"safe" with the lines to set, or "DO NOT SWITCH" (exit code 4). Keep the previous certificate in
`BIFROST_KEYRING_CERT_PATH_PREVIOUS` as long as a single key is still encrypted with it —
DataProtection does not re-encrypt existing keys.

### A missing key ring stops the start

Previously, DataProtection simply created a new ring when the directory was empty: the service came
up, reported "ready" — and could not decrypt a single stored credential. That is exactly what hit
the v0.11.0 migration (renamed volume, empty store, error-free start).

The start now checks two independent witnesses: `<datadir>/config/keyring.json` (how many keys this
instance last had, and which), and **ciphertext in the database**. If either indicates loss, the
start **aborts with exit code 78** and creates no replacement ring. The same applies when the ring
is present but cannot be opened with the configured certificate.

A **completely replaced** ring does *not* abort the start — that is also what a legitimate restore
looks like — but it is logged loudly and recorded as an audit event. The recovery commands run
**before** this check and stay reachable when the key ring is the second problem.

---

## Backup and restore

Everything persistent lives in the data directory (`BIFROST_DATA_DIR`): `bifrost.db`, `keys/`
(the key ring), `packages/`, and `config/instance.json` (the stable identifier of this
installation, which appears in every backup manifest).

> **A full backup is a secret.** It contains the key ring — the key to every stored upstream
> credential, OAuth token and webhook secret. Whoever has it, has the instance
> ([ADR-0024 E3](../adr/0024-backup-restore-und-migrationssicherheit.md)). It belongs on a target
> protected as well as the data directory itself, or it gets a passphrase.

All commands below run through `bifrost` against a **running** gateway:

```bash
bifrost backup create --out /data/backups/bifrost-2026-08-01.zip
bifrost backup create --out /data/backups/db-only.zip --sections database,config
bifrost backup verify /data/backups/bifrost-2026-08-01.zip
bifrost restore /data/backups/bifrost-2026-08-01.zip
bifrost restore /data/backups/bifrost-2026-08-01.zip --replace
```

A passphrase is **never** accepted as an argument — it would appear in the process list and the
shell history. Use `--passphrase-env NAME` or `--passphrase-prompt`.

`--replace` asks back; the answer is the word `replace`, and `--yes` supplies it in scripts. Before
overwriting, a backup of the previous state is taken automatically — no overwrite without a way out
(ADR-0024 E5). **A restore needs a maintenance window:** it swaps database and key ring, which
cannot be kept atomic under live writes. Stop, restore, start.

### PostgreSQL: needs `pg_dump` and `pg_restore`

`bifrost backup` and `bifrost restore` work on PostgreSQL too, via `pg_dump`/`pg_restore`
([ADR-0024 E2](../adr/0024-backup-restore-und-migrationssicherheit.md)). Archive format, manifest,
checksums, key ring and encryption are the same as for SQLite; only the payload under `database/`
is named `bifrost.dump` instead of `bifrost.db`.

The format is **`custom`** (`pg_dump --format=custom`): one file rather than a directory tree, data
rather than an executable SQL script, and the only format that supports `pg_restore
--single-transaction` — either the restore completes or the database is untouched (ADR-0024 E5).

**If the tools are missing, that is an error with a message — never a silent row export.** The
message names what is missing and where to get it. Install the PostgreSQL client package
(`apt-get install postgresql-client`, `apk add postgresql17-client`, `dnf install postgresql`,
`brew install libpq`), or point `BIFROST_POSTGRES_BIN` at the directory holding them. When that
variable is set, **only** that directory is searched.

**The client major version must be at least the server's.** `pg_dump` refuses to dump a newer
server (`aborting because of server version mismatch`). This is the likely case, not an edge one:
Ubuntu 24.04 ships client **16**, a current server is **17** or **18** — that combination has no
backup at all, including the pre-migration one (E7).

> **`bifrost doctor` says so up front: `BFR-DB-0006`.** It compares the major version of the
> `pg_dump` actually found with the server the gateway actually runs against: "client 16, server 17
> — this client cannot dump this server; >= 17 is required". If either number cannot be determined,
> the finding says exactly that instead of claiming compatibility.

Remedy: install a client from the PGDG apt mirror (`postgresql-client-17`/`-18`; the distribution
package only ever carries its own version), `apk add postgresql17-client` on Alpine, or point
`BIFROST_POSTGRES_BIN` at a directory holding a matching client. A hand-written row export is not a
substitute (ADR-0024 E2).

The password reaches `pg_dump` through a `PGPASSFILE` created for the duration of the call (mode
`0600` on Unix) and deleted afterwards — never on the command line, which is visible in every
user's process list.

> **The way back after a PostgreSQL restore is the pre-backup, not an undo.** For SQLite the old
> file is moved aside and can be moved back; a `pg_restore` cannot be taken back that way. If it
> aborts, PostgreSQL rolls it back itself (single transaction) — but once it has completed, only
> the archive taken beforehand helps.

> The German page is authoritative: [`docs/operations.md`](../operations.md).

### Exit codes

| Code | Meaning |
|---:|---|
| `0` | Success, no warning |
| `1` | Unexpected error (including: gateway unreachable) |
| `2` | Usage error — missing argument, missing permission, not applicable on this instance |
| `3` | Diagnosis with a **warning** |
| `4` | Diagnosis with an **error** |
| `5` | Archive invalid, damaged or incompatible (also: import with conflicts) |
| `6` | Target instance not empty and no `--replace`, or `--replace` not confirmed |

A skipped diagnostic check (`Skipped`) is **neutral** and yields `0`: it appears in the report with
a reason, and that is the statement.

---

## Upgrades

```bash
docker compose down                      # stops containers, volumes stay
# set BIFROST_VERSION (or BIFROST_IMAGE) in .env to the new version
docker compose pull && docker compose up -d
docker compose logs -f bifrost           # confirm the migration message
```

Back up the data directory first. The schema is migrated automatically at start, and **a migration
is not reversible**. A rollback is the previous line in `.env`, not a second `up`.

At start, exactly one of three things happens; the result is in the log
(`Datenbank initialisiert (…)`):

| Found | Action | Log |
|---|---|---|
| Empty/new database | Create schema from migrations | `CreatedFromMigrations` |
| **Legacy database** from a build before migration management (created via `EnsureCreated`, no migration history) | Stamp the initial migration as a baseline (**no DDL, no data change**), then migrate | `BaselinedLegacySchema` |
| Already migration-managed | Apply pending migrations | `Migrated` |

Each provider has its own migrations assembly (`Bifrost.Persistence.Migrations.Sqlite` /
`.Postgres`); both ship in the image and the choice follows `BIFROST_DB_PROVIDER`.

### The pre-migration backup — and where it does not exist

On **SQLite**, a full backup is created automatically before a schema-changing migration, under
`<datadir>/backups/pre-migration-*.zip`, and **without it there is no migration** (ADR-0024 E7). The
path is recorded in the migration journal and named in the failure message.

That backup is **unencrypted** by default — it sits in the same protection domain as the database it
came from. `BIFROST_BACKUP_PASSPHRASE` encrypts it, at the price of being worthless without that
passphrase.

> **On PostgreSQL it depends on the tools.** At startup the server checks once whether `pg_dump`
> and `pg_restore` are reachable. If they are, `PreMigrationBackupRequirement` is `Always` — no
> migration without a backup. If they are not, it stays at `WhenAvailable`: the start warns and
> migrates anyway, and **that upgrade runs without a way back**. `Always` on an instance without the
> tools would be a start prohibition rather than a promise, so the check measures instead of
> assuming. Either install the client package, or take your own `pg_dump` plus `keys/` before every
> upgrade — nothing will stop you if you forget.

### What the upgrade harness proves — and what it does not

`tests/Bifrost.Upgrade.Tests` runs 50 cases across 15 published migration states × 2 providers, plus
the archive path on both providers, with real ciphertext written by the real stores. Full detail:
[`docs/upgrade-matrix.md`](../upgrade-matrix.md) (German). The limits that matter operationally:

- **Old schema, today's code.** The fixture schema is genuinely old; the code that fills it is
  today's. A regression in the **serialisation format** of an earlier build — a changed JSON shape,
  a different hash format, a renamed field inside a protected blob — is **not** found, because the
  test never writes the data in the old form. This is the harness's most serious known gap, and
  only fixtures produced by an earlier build (i.e. release artefacts) can close it.
- **`AuditEvents` and `Assets` are not part of the fixture data.** Data loss in exactly those two
  tables would not turn the matrix red.
- **Restore on a different machine or under a different account is untested.** On Windows the key
  ring is DPAPI-bound to the executing user; a full backup restored elsewhere would give you rows
  without readable content. Linux container operation is unaffected (cleartext ring).
- **No downgrade path.** `Down` migrations are neither run nor tested. The way back out of a failed
  upgrade is the pre-migration backup — which, again, does not exist on PostgreSQL.

### If the start reports BFR-DB-0101

`BFR-DB-0101` means an earlier migration run aborted mid-way, the schema state is unknown, and the
gateway **refuses write operation** by not coming up at all. It repairs nothing by itself; that is
deliberate (ADR-0024 E7).

1. **Assess the database.** If the journal entry names a `backupPath`, that is the backup taken
   immediately before the run. When in doubt, restore it.
2. **Release the block** — only once the state has been checked:

```bash
# The gateway is NOT running — so do this in the server process, not via the CLI:
docker compose run --rm bifrost dotnet Bifrost.Server.dll --db-unblock
# without a container:
dotnet run --project src/Bifrost.Server -- --db-unblock
```

3. Start and confirm `Datenbank initialisiert` in the log.

`bifrost db unblock` does the same thing but talks HTTP to a *running* gateway — useful where a
second instance is still up (PostgreSQL, multiple nodes), not in the normal `BFR-DB-0101` case.
Neither path assesses the schema state. They release what the operator has checked.

---

## Diagnostics

```bash
bifrost doctor                       # everything
bifrost doctor --scope database,network
bifrost --json doctor                # machine-readable
```

The report only reads. The single exception is the write probe in the data directory: it creates a
`.bifrost-doctor-*.tmp` file and deletes it immediately — writability cannot be answered reliably
read-only on either operating system.

Every finding carries a **stable code** (`BFR-DB-0002`) that a runbook can rely on; the text next to
it may change, the code may not.

| Prefix | Area |
|---|---|
| `BFR-CFG-*` | Configuration, environment variables, data directory |
| `BFR-DB-0001…0099` | Database, migrations, provider |
| `BFR-DB-0100…0199` | Start coordination: lock, journal, schema state |
| `BFR-KEY-*` | DataProtection key ring |
| `BFR-NET-*` | Ports, public address, proxy trust |
| `BFR-RT-*` | Container runtime, WASI host |
| `BFR-UP-*` | Upstreams |

The same view exists in the UI under **Betrieb** (admins only), together with creating a backup.
`bifrost doctor` and that page never name the **location** of key material: of the PFX and the
password file only the filename appears. The password appears nowhere, in any form.

---

## Configuration export and import

A configuration export is **not a backup** (ADR-0024 E8): it does not restore the same instance, it
builds a comparable one — and therefore contains **no secret values**, only references and masks.

```bash
bifrost config export --out configuration.json
bifrost config export --include-secrets --passphrase-env EXPORT_PASSPHRASE --out full.json
bifrost config import configuration.json --dry-run
bifrost config import configuration.json
```

The import is two-stage and **purely additive**: it creates, never overwrites, never deletes.
Conflicts and missing dependencies appear in the plan and cause exit `5` before anything is written.

| | Backup | Configuration export |
|---|---|---|
| Purpose | restore the same instance | build a comparable instance |
| Contains | everything, including the key ring | servers, roles, profiles, rules, skills |
| Secrets | yes (hence protect it) | no — references or masks |
| Format | ZIP with manifest | JSON, versioned |

Known gaps: **publisher trust is not exported** (without it a WASI upstream is not loadable on the
target instance), `GuardOptions` are a start-time singleton from the environment and cannot be
imported, and a half-applied skill import cannot be fully rolled back — the remainder appears
visibly in the compensation backlog rather than being reported as "fully reverted".

All operations endpoints (`/api/v1/operations/*`) require an identity with a **global grant**, the
same threshold as RBAC administration and package installation. Every writing operation is audited.

---

## Importing a foreign MCP configuration

If you already have a `claude_desktop_config.json`, an `mcp.json` or a Cursor configuration, you
should not have to retype it. The import reads such a file, **judges it**, and only then creates
anything — the order is the whole point.

```bash
# Look only. Creates nothing, changes nothing:
bifrost import preview ~/.config/claude/claude_desktop_config.json

# Take over. Shows the same preview first, then commits:
bifrost import apply mcp.json --isolation container --image ghcr.io/own/mcp-runner:1

# Selected servers only, with a connection test first:
bifrost import apply mcp.json --only github,filesystem --probe --isolation host --confirm-risks

# Show what would happen, write nothing:
bifrost import apply mcp.json --dry-run
```

**The CLI reads the file, not the gateway.** The content travels in the request body. `--origin
<path>` exists so a finding can name its location (`mcpServers/github/args[2]` in `~/.config/…`);
the gateway **never** opens that path. An endpoint that opens a file server-side because a client
named its path is a tool for reading foreign files, whatever it is called.

### What the preview shows — and what it does not

Visible are the **structural facts**: slug, display name, transport kind, the program that would
start, the target address without query and user info, the container image.

Not visible are the **values**: arguments, environment values, header values, credentials, WASI
secrets. From those fields only **names and counts** travel, never contents. Whether a place carries
a credential, and how that was recognised, appears as a finding — the value does not.

This is a positive list, not masking: a field reaches the preview only because someone wrote it in
there deliberately. A new field on the configuration does *not* show up by itself. The reason is
concrete — the most common shape of a credential in such files is `"args": ["--api-key", "ghp_…"]`,
and a masking list only knows the fields where somebody already noticed.

The values are not lost: they stay with the service behind a **handle** that is valid for 30
minutes, usable exactly **once**, and owned by the identity that requested it — the same pattern as
the restore plan. An unknown, expired, spent or foreign handle is refused, not guessed.

### Confirm, isolate, roll back

- A **Risk** finding does not block but requires `--confirm-risks`. `npx -y` is a legitimate
  configuration and at the same time one that pulls arbitrary code at startup; that difference gets
  made visible rather than decided away.
- A server that starts a foreign program needs an **explicit isolation decision**
  (`--isolation container --image …` or `--isolation host`, ADR-0025 E2/E5). The import is a
  creation path; it does not guess.
- The commit is **atomic**. A slug colliding with the existing stock changes nothing at all; if a
  server fails midway, the ones already created are removed again.
- All three steps are audited — with slug and outcome, **without** values.

### Endpoints

| Method | Route | Permission | Limits |
|---|---|---|---|
| `POST` | `/api/v1/import/preview` | global grant | 1 MiB, `application/json` or `text/plain`, 12/min |
| `POST` | `/api/v1/import/probe` | global grant | 12/min; does not spend the handle |
| `POST` | `/api/v1/import/commit` | global grant | 12/min; handle is single-use |
| `POST` | `/setup/import/preview` | setup token **and** loopback | 1 MiB, 12/min; only while first access is pending |

The counter applies unconditionally, even to an identity that RBAC would leave unlimited: an import
is expensive regardless of what a role says.

`/setup/import/preview` exists **only** while a setup token is outstanding. It requires all three:
the pending first access, a request from the gateway machine itself, and the setup token in the
`X-Bifrost-Setup-Token` header. It only shows; creating happens after redemption over the
authenticated path. Once first access is redeemed it answers `404` — locally too, token or not.

### Exit codes of the import commands

These follow the table of the other `bifrost` commands, not the operations table above:

| Code | Meaning |
|---:|---|
| `0` | imported, or the plan is applicable |
| `2` | usage error — unknown option, wrong content type, document too large |
| `3` | not authorised |
| `5` | plan not applicable, handle unknown/spent, slug conflict, probe failed |
| `6` | confirmation missing (`--confirm-risks`) or isolation decision missing |
| `10` | gateway unreachable |

---

## Observability

Every tool call is measured (FR-26) under the meter `Bifrost.Gateway`:

| Instrument | Meaning | Dimensions |
|---|---|---|
| `bifrost.tool_calls` | Counter of all calls — gives calls/s and error rate | `server`, `tool`, `status`, `origin` |
| `bifrost.tool_call_duration` | Latency histogram (ms) — gives percentiles | `server`, `tool`, `status` |

Export is **off** until a target is configured:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://collector:4317
```

Export is via **OTLP**. For Prometheus, put an OTel collector in front that accepts OTLP and offers
a scrape endpoint — a direct Prometheus exporter is not yet stably published in the .NET ecosystem.

The same switch enables **traces** from the source `Bifrost.Gateway`:

| Span | Meaning |
|---|---|
| `bifrost.tool_call` | The whole call through the pipeline |
| `bifrost.upstream_call` | Only the upstream portion, as a child span |

The difference between the two is the **gateway overhead** — exactly the question NFR-01 asks.

> **Spans carry no arguments and no results.** The audit log is redacted; a telemetry backend is
> not. A payload in a span would be the most convenient way around redaction, into a place that is
> often less protected than the database. A test enforces this.

`/healthz` and `/readyz` are excluded from tracing. No alerting rules ship with the product.

### Health, readiness and audit modes

- `GET /healthz` — process is alive (anonymous).
- `GET /readyz` — database reachable + upstream states (anonymous).

The container healthcheck uses `dotnet Bifrost.Server.dll --healthcheck` (self-ping, since the slim
runtime image has no `curl`). The container runs as non-root `app`.

`best-effort` does not hold up tool calls when the audit pipeline is overloaded; drops are counted,
reported in `/readyz` and logged at shutdown. `compliance` throws an explicit
`AuditUnavailableException` on a full channel, marks readiness as not ready, and retries failed
database batches — which can deliberately block shutdown or processing during a longer database
outage. **For HA, a durable external spool/queue backend is still required before production use.**

`/readyz` returns `auditMode`, `auditHealthy` and `auditDropped`. Alert on `auditHealthy=false`,
`auditDropped>0` and HTTP 503.

Audit retention defaults to 30 days with an hourly cleanup job (FR-25). On SQLite, retention is an
**operational obligation** ([ADR-0007](../adr/0007-ef-core-mit-sqlite-default-postgres-optional.md))
— very large logs (> ~10 GB) are a reason to move to PostgreSQL.

---

## PostgreSQL instead of SQLite

For larger setups (high audit volume, several instances against one database), add the override
file. It sets provider and connection string itself; only the password needs entering:

```bash
echo 'POSTGRES_PASSWORD=…' >> .env
docker compose -f docker-compose.yml -f docker-compose.postgres.yml up -d
```

The password is deliberately **not** in the compose file. A placeholder like the former `CHANGE_ME`
gets adopted and never changed — whereas a missing one keeps the database from starting at all,
with an unambiguous message.

> **The data directory is still required with PostgreSQL.** The DataProtection key ring under
> `/data/keys` is not in the database; without it the encrypted upstream credentials are useless.
> Both belong in the same backup.

And, restating the two gaps that PostgreSQL brings with it: [no product backup](#backup-and-restore)
and [no pre-migration backup](#upgrades).

---

## Resetting access

A setup token appears by itself **only** while the installation has no access yet. After that there
is no route to a second one over the network — that is the point. For lost access there are commands
that run against the configured database, print the credential **once on the console**, and exit
without starting the gateway:

```bash
# Issue a new setup token (even on an installation with existing accounts):
docker compose run --rm bifrost dotnet Bifrost.Server.dll --bootstrap-init

# Reset the UI password (default user "admin"; role unchanged, a missing user is created as admin):
docker compose run --rm bifrost dotnet Bifrost.Server.dll --reset-ui-admin
docker compose run --rm bifrost dotnet Bifrost.Server.dll --reset-ui-admin operator

# Emergency API key: creates a NEW agent identity with a global grant (existing ones untouched):
docker compose run --rm bifrost dotnet Bifrost.Server.dll --issue-bootstrap-key

# Release the BFR-DB-0101 block — only after checking the database:
docker compose run --rm bifrost dotnet Bifrost.Server.dll --db-unblock
```

Without a container, use `dotnet run --project src/Bifrost.Server -- --reset-ui-admin`. Remove the
emergency access afterwards if it only served recovery.

`--bootstrap-init` issues a token on an installation with existing accounts only if it can prove
**write access to the data directory**, and it proves it by doing: a probe file is created, read
back and removed. Whoever can write there could swap the database and restart with an empty volume
anyway — what the proof reliably excludes is the route that matters: **over the network**. The HTTP
endpoint offers no way to *request* a token, only to *redeem* one that already exists.

All three routes write an audit entry (`Kind=Authentication`, `Origin=System`, `Tool=recovery`) into
the database, not just into the log of the exited process.

---

## Not covered here

The German original goes further on: connector packages and publisher trust, WASI components
(persistent instances, concurrency, cancellation, module cache), CLI programs in container mode,
OpenRPC upstreams, upstream and agent OAuth, webhook triggers, skills, result compression, and the
rug-pull protection for changed tool definitions. See [`docs/operations.md`](../operations.md).
