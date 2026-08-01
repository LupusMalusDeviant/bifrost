# Troubleshooting

> **Source of truth.** This page has no German original — it *is* the source version (see
> [`docs/i18n.md`](../i18n.md)). Where it describes operational behaviour, the authoritative
> description is [`docs/operations.md`](../operations.md) (German); **on contradiction, that page
> wins.**

> **None of the commands below were executed while writing this page.** There was no running
> instance. Every symptom listed here is transcribed from a place where it was actually observed —
> the operations manual, the readiness protocol, or the first real release run — and each entry says
> where. **No sample output is invented.**

The organising idea of this page: almost every expensive failure in this project has had the same
shape — **it looked like success.** An empty volume reports "ready". A dead admin UI renders
perfectly. A gate that can never be red is green. So the entries below are sorted by *symptom*, not
by component, and each one names what it looked like before anyone understood it.

- [The start succeeded but nothing is there](#the-start-succeeded-but-nothing-is-there)
- [Login and web UI](#login-and-web-ui)
- [The start refuses](#the-start-refuses)
- [Upstream servers](#upstream-servers)
- [Calls, approvals and guardrails](#calls-approvals-and-guardrails)
- [Backup, restore and upgrade](#backup-restore-and-upgrade)
- [Release pipeline — nine findings from the first real run](#release-pipeline--nine-findings-from-the-first-real-run)

---

## The start succeeded but nothing is there

### No servers, no roles, no key ring — and no error

**Cause: you are on a different volume.** Compose prefixes every volume with the project name, which
defaults to the lowercased directory name. Renaming the directory, moving the compose file, or
renaming the volume key points at a *different* volume; Docker creates it silently and empty, the
gateway initialises a fresh database and reports itself ready.

```bash
docker volume ls
docker compose config --volumes        # the key
docker compose config | tail -6        # the effective name, including the prefix
```

If the effective name is not the one holding your data, move the contents — Docker cannot rename
volumes. The copy runs through a throwaway container with the gateway stopped, and it needs `cp -a`:
the container runs as non-root `app`, and a copy without `-a` hands it a directory it cannot write.
Exact commands: [`docs/operations.md` → *Umstieg einer MCP-MCP-Installation*](../operations.md).

**Do not delete the old volume** until the log confirms the data is there (`Migrated` or
`BaselinedLegacySchema`). A few hundred megabytes are cheap.

> This is the failure that cost this project the most, twice: once during the v0.11.0 rename, and
> once as a trap left in the repository — the rename commit dragged the volume name along in
> `docker-compose.yml`, so an existing installation would have received an empty volume and reported
> itself healthy.

### `docker compose up -d` pulls a version you did not expect

The checked-in `.env.example` and the fallback in `docker-compose.yml` still name `0.11.0`, while
`0.12.0` is the current release (state: 2026-08-01). There is no `latest` tag on purpose. Set
`BIFROST_VERSION` — or better, pin `BIFROST_IMAGE` to a digest — explicitly in `.env`.

---

## Login and web UI

### The login succeeds and immediately returns to the login form

**No error appears anywhere.** The server answers `302`, the browser drops the cookie, the next page
is the login form again.

Outside `Development` the session cookie always carries `Secure`, and a browser **silently discards**
such a cookie over plain HTTP. `http://localhost:8080` works (browsers treat `localhost` as a secure
origin); `http://<ip-or-hostname>:8080` does not.

The gateway now says so itself: one line at start when it only listens on HTTP, and an unambiguous
line on every login arriving over HTTP without a proxy having set `X-Forwarded-Proto: https`. Behind
a correct TLS proxy the line does not appear — a warning that fires on correct setups gets ignored.

Fix: put a TLS reverse proxy in front **and** set `BIFROST_TRUSTED_PROXIES`.

### `400 The plain HTTP request was sent to HTTPS port`

You are behind a TLS proxy but `BIFROST_TRUSTED_PROXIES` is unset, so the gateway ignores forwarded
headers, sees only HTTP, and builds `http://` redirects. Visiting a protected page while logged out
bounces you from `https` to `http`, and the proxy rejects it.

```bash
BIFROST_TRUSTED_PROXIES=any                      # only if reachable exclusively via the proxy
BIFROST_TRUSTED_PROXIES=172.17.0.1,10.0.0.0/8
```

Opt-in is deliberate: on a directly reachable gateway, any client could claim
`X-Forwarded-Proto: https`. A typo in the value aborts the start rather than silently disabling the
feature. The proxy must also forward the port in `Host` (`proxy_set_header Host $http_host` —
`$host` drops it).

### The admin UI renders but no button does anything

Pages look perfectly usable because they still render server-side. The Blazor circuit never starts:
`_framework/blazor.web.js` is served with `200` and **zero bytes**.

You are running from source outside `Development`, where ASP.NET does not load the static web
assets. `ASPNETCORE_ENVIRONMENT=Development` must come from the environment —
`dotnet run --environment Development` does **not** work, that argument is swallowed. The published
Docker image is unaffected.

> This one shipped. It was found only when the gateway first ran on real hardware, alongside two
> other defects no test could have produced (a silently dropped session cookie over HTTP, and
> `http://` redirects behind a TLS proxy).

### The dashboard says "Active agents" instead of "Active sessions"

Not a fault. In stateless operation there are no open sessions to count, so the dashboard reports
identities with requests in the last five minutes.

---

## The start refuses

### Exit code 78 — key material is missing

The gateway refuses to start and creates **no** replacement ring. Two independent witnesses drive
this: `<datadir>/config/keyring.json` (how many keys this instance last had) and ciphertext in the
database.

Previously, DataProtection would simply create a new ring for an empty directory: the service came
up, reported ready, and could not decrypt a single stored credential. That is exactly what happened
during the v0.11.0 migration.

1. Does `BIFROST_DATA_DIR` point at the right volume? **A renamed volume looks exactly like this.**
2. Otherwise restore the key ring from a backup — it is in the full backup under `keyring/`.

The same abort happens when the ring exists but cannot be opened with the configured certificate,
because otherwise DataProtection would create a fresh key alongside it and nothing would be
recoverable even with the correct certificate afterwards.

A **completely replaced** ring does not abort the start — that is also what a legitimate restore
looks like — but it is logged loudly and recorded as an audit event.

The recovery commands (`--bootstrap-init`, `--reset-ui-admin`, `--issue-bootstrap-key`,
`--db-unblock`) run **before** this check and stay reachable.

### The start aborts because a secret is configured twice

`BIFROST_KEYRING_CERT_PASSWORD` and `BIFROST_KEYRING_CERT_PASSWORD_FILE` both set. There is
deliberately **no precedence**: a rule between two sources of the same secret is one you would
misremember, and then you would be running an instance with the wrong secret. Remove one. Exactly
one trailing newline is stripped from a secret file (`echo secret > file` writes one); nothing else
is trimmed.

### `BFR-DB-0101` — an earlier migration aborted mid-way

The schema state is unknown, and the gateway refuses write operation by not coming up. It repairs
nothing by itself, on purpose.

1. **Assess the database.** If the journal entry names a `backupPath`, that is the backup taken
   immediately before the run.
2. Release the block — only after checking:

```bash
docker compose run --rm bifrost dotnet Bifrost.Server.dll --db-unblock
# without a container:
dotnet run --project src/Bifrost.Server -- --db-unblock
```

3. Start and confirm `Datenbank initialisiert` in the log.

`bifrost db unblock` does the same but talks HTTP to a *running* gateway — useful where a second
instance is still up, not in the normal case. Neither path assesses the schema. They release what
you have checked.

### `BFR-DB-0102` — the database has migrations this build does not know

The database is newer than the binary. Usual cause: a restore of an archive from a later version, or
a rollback of the image without a rollback of the data. There is no downgrade path; `Down`
migrations are neither run nor tested. Go back to the version that wrote the schema, or restore a
matching backup.

### `config/bootstrap.json` is present but unreadable — start aborts

Deliberate. Treating a read error as "fresh installation" would issue a new setup token on a
production instance. Check the file's permissions or restore it from a backup.

### The setup token expired, and there is no second one

Correct: once access exists, there is no route to a token **over the network**. That is the point.
Issue one from the console instead:

```bash
docker compose run --rm bifrost dotnet Bifrost.Server.dll --bootstrap-init
```

This requires provable write access to the data directory, checked by doing (probe file created,
read back, removed). If the token merely expired *before* anyone set up, the next start issues a new
one by itself — an installation nobody can enter is not a security gain.

---

## Upstream servers

### An OpenAPI or OpenRPC upstream stops coming up after an update

`AllowPrivateTargets` defaults to `false`. Existing upstreams pointing at `localhost` or a private
network fail until the switch is set explicitly, per upstream. The error names the address and the
switch; the upstream shows as `Failed` — nothing continues silently.

```json
"OpenApi": { "SpecLocation": "http://localhost:8080/openapi.json", "AllowPrivateTargets": true }
```

**The inverse case is the one to worry about:** an MCP-over-HTTP upstream configured *before the
switch existed* has `null` there, which means *not decided, and therefore permitted*. Upstreams
created since then get an explicit `false` from the form and the API, so this affects existing
records — and anything brought in by config import or restore, which reproduce a configuration
rather than create one. If a private target is not intended, set `false` yourself. See
[Security](security.md#allowprivatetargets-is-undecided-for-existing-http-upstreams--which-means-allowed).

### "The upstream returned a tool list that cannot be read"

Since `2026-07-28`, `inputSchema` is mandatory on every tool. A server that omits it no longer gets
through; an empty `{}` is enough for it.

### "The tool requires a human prompt (MRTR)"

An upstream's own elicitation requests are **not** forwarded
([ADR-0010](../adr/0010-sampling-elicitation-nicht-durchreichen.md)) — the gateway could not say
which of its many callers the question should go to.

### A catalogue change from an upstream takes up to a minute to appear

Expected. An upstream on `2026-07-28` no longer announces changes, so the gateway re-polls on a
schedule (default: every minute) and forwards only real changes. On the client side, the same effect
comes from the list cache TTL (`BIFROST_MCP_LIST_TTL_SECONDS`, default 60).

### A tool has disappeared from the catalogue

Its definition changed. Name, description and input schema are fingerprinted on every discovery, and
a changed tool is withheld — not visible, not callable — until an administrator accepts the new
version, in the UI under *Server* or via the API:

```bash
curl -X POST http://localhost:8080/api/v1/tool-definitions/<serverId>/<tool>/accept \
  -H "Authorization: Bearer $KEY"
```

Only the changed tool is withheld, not the whole server: a protection that halts operations on every
routine update gets switched off.

### Container mode is rejected on a Windows host

Docker in **Windows container mode** answers readily and then rejects `--read-only`, `--cap-drop`
and `--user`. The gateway checks whether the runtime can *enforce* the policy, not merely whether it
responds, and refuses rather than running unprotected. Use Docker in Linux mode (WSL2 backend).

There is **no silent fallback to the host** anywhere: if a configuration demands a container and no
suitable runtime is present, the upstream does not come up, with that exact message.

### A connector package will not install

`BIFROST_WASI_HOST` is required. Without it a package cannot be probed, and nothing unprobed is
activated. Also required: a pinned publisher — an empty trust store is fail-closed, not
unrestricted. Installation order is signature → manifest → per-file hash → unpack into quarantine →
actually start the connector and query its catalogue → only then activate atomically. A package that
fails the probe never ran in production, and the failure stays visible with a reason.

### WASI: `too-many-calls`, an unknown handle, or a cold cache

- **`too-many-calls`** — 16 concurrent calls per host process is the limit. `MaxMemoryBytes` applies
  per call; without a cap, memory would be the product of limit and request count. If you need more
  sustained concurrency, create the upstream more than once — each gets its own host process.
- **"handle is unknown"** — every resource handle belongs to the caller it was created for, and a
  different caller gets that message whether or not the name exists. Handles also require
  `Wasi.PersistentInstance: true`; without it every call gets a fresh instance.
- **Every host start recompiles (3–7 s)** — set `Wasi.ModuleCacheDirectory`. If `diskHits` stays at
  0 and `diskErrors` rises in the host's `health` signal, the directory permissions are usually
  wrong: it must be owned by the host user and not writable by others.

---

## Calls, approvals and guardrails

### A call was blocked on the way back, and the action already happened

That is the documented behaviour, and the error message says so: on the **result** direction the
call has already run, and only the result is withheld. **Do not retry** — otherwise an agent files
the same issue twice. In the audit log this carries its own status `GuardBlocked`, distinguishable
from an RBAC `Denied`.

On the **argument** direction the call is aborted before the upstream and there is no side effect.

### A large result is rejected instead of passed

Above `BIFROST_GUARD_MAX_SCAN_CHARS` (default 262144) payloads are rejected, not passed unscanned —
otherwise the limit would be exactly the blind spot to aim for. If you expect large results, combine
with `BIFROST_MAX_RESULT_CHARS`: truncation happens first, and the truncated result passes the scan.

### A client errors with "no ElicitationHandler is registered"

A client on `2026-07-28` that cannot display a form gets this from its own SDK instead of the queue
message, because since that revision no client advertises an elicitation capability to distinguish
on. **The request is not lost** — it is in the queue under *Approvals*.

### An approval was granted but the call is still rejected

An approval binds to `(identity, tool, argument fingerprint)`, expires after one hour, and is
single-use. An approval for `delete_file{path:/tmp/x}` does not cover
`delete_file{path:/etc/passwd}`, and a repeat needs a fresh approval. A cancelled task is
deliberately no longer redeemable.

### A call sits in the queue and nobody was asked

The reason is in the log (`Keine Rueckfrage fuer <Tool> — <Grund>. Der Aufruf bleibt in der
Warteschlange.`). This is deliberate: from the outside, a queued call looks identical whether nobody
was asked, the question failed, or a human declined. The client's protocol revision and whether it
can be asked are logged on its first call.

### `/readyz` returns 503 with `auditHealthy=false`

In `compliance` mode a full audit channel marks readiness as not ready and blocks, on purpose. In
`best-effort` mode calls proceed and drops are counted (`auditDropped`). Alert on
`auditHealthy=false`, `auditDropped>0` and HTTP 503. For HA, a durable external spool/queue backend
is still required before production use — it does not exist yet.

---

## Backup, restore and upgrade

### `bifrost backup create` refuses on PostgreSQL

Correct, and deliberate: it refuses rather than silently exporting rows. **There is no PostgreSQL
backup in the product.** Use `pg_dump` **plus** the `keys/` directory from the data directory —
they only work together. FR-P020 is open for PostgreSQL.

### An upgrade on PostgreSQL went wrong and there is no pre-migration backup

There never was one. On SQLite a full backup is created automatically before a schema-changing
migration and the migration does not proceed without it. On PostgreSQL that backup **cannot** be
created, so the start warns and migrates anyway. **Every upgrade on PostgreSQL runs without a way
back**, and `Down` migrations do not exist. Take your own dump before every upgrade.

### An archive from a newer version was restored and now the instance will not start

Known and now fixed, but worth understanding, because it explains the failure mode you may still be
looking at. The backward gate used to compare a **self-declared** `minimumRestoreVersion` from the
manifest — a constant that no version raised, so every archive claimed the same thing and a newer
one passed. It was stopped one stage later by `BFR-DB-0102`, i.e. *after* it had been written.

The check is now against the **migration state** in the manifest versus the migrations this build
knows: if the build does not know them, the archive is newer, without any version bookkeeping. If
the set of known migrations is missing, the gate **warns** rather than staying silent — a protection
that fails quietly is worse than none.

### The upgrade tests are green but a format change broke something

That is within the harness's known limits, not a contradiction. It runs 15 published migration
states × 2 providers with real ciphertext, and proves that **migrations** do not damage existing
rows. It writes those rows with **today's code**, so a regression in the serialisation format of an
earlier build is invisible to it — it never writes the data in the old form. Closing that gap needs
fixtures produced by an earlier build, i.e. real release artefacts.

`AuditEvents` and `Assets` are not part of the fixture data at all: data loss in exactly those two
tables would not turn the matrix red. Full list of limits:
[`docs/upgrade-matrix.md`](../upgrade-matrix.md) §4 (German).

### A restored backup gives rows with unreadable content

The key ring did not come with it, or it cannot be decrypted here. On Windows, an unprotected key
ring is DPAPI-bound to the executing user, so a full backup restored on another machine or under
another account produces exactly this. Configure `BIFROST_KEYRING_CERT_PATH` if you need portability.
Linux container operation is unaffected (cleartext ring).

---

## Release pipeline — nine findings from the first real run

For maintainers cutting a release. `release.yml` was built during M1 and then **never ran** until
2026-08-01. Three dry runs and three tag runs were needed to get `v0.12.0` out. **None of these
would have been visible without a real run** — which is the entire point of recording them here
rather than filing them away.

| # | Where | What |
|---|---|---|
| 1 | Push protection | An invented Slack token in the negative-test corpus, not split up — the push was blocked |
| 2 | Secret gate | The gitleaks baseline did not know two values introduced by WP3.3 |
| 3 | Version test | A literal `"0.11.0"` in a test; it broke on the version bump |
| 4 | **Bootstrap** | **A product defect:** two concurrent redemptions of the setup token and *both* were lost |
| 5 | **WP0.4 proof** | Had **never** worked on Linux — the 15-character limit of `/proc/*/comm` |
| 6 | WP0.4 proof, second attempt | Counting by process name remained unreliable; it now hangs off the process id |
| 7 | `supply-chain` job | **Always** red in dry-run mode — the dry-run mode itself had never run |
| 8 | `release` job | `dist/*` also matched subdirectories; the release was created and the attachments were missing |
| 9 | Backup test | A concurrency proof that depended on the scheduler rather than on the product |

**Finding 5 is the sharpest.** The one work package whose entire purpose was "proof, not assertion"
had checked nothing at all on an entire platform and still reported green. It counted as proven
because it had only ever run on Windows — nothing had been pushed since it was written.

**Finding 8 is the nastiest kind.** The release was created and the attachments were missing: a
promise without cover. Two new gates came out of fixing it — an empty file list aborts, and
duplicate basenames abort (`gh` attaches under the basename, so two identically named files
overwrote each other silently).

**Finding 4 is the one operators care about**: concurrent redemption of the setup token lost both
attempts. Fixed before the release.

### What this means for the next release

- **The signature self-test must run in both directions.** A verification step that only confirms
  will also confirm nonsense. The release run verified that a *wrong* artefact fails, not only that
  a right one passes — keep it that way.
- **A dry-run mode that has never run is not a dry run.** Finding 7 was exactly that.
- **A gate that has never been red is a claim, not a proof.** G4, the container image scan, has now
  run green against a real release image, but it has still never been shown able to fail. See
  [Security → What has actually been proven](security.md#what-has-actually-been-proven--and-what-has-not).

The full record — including the sentence *"implemented but not accepted", which sat in the protocol
for three milestones and sounded like a formality but was not* — is in
[`docs/plans/product-readiness-status.md`](../plans/product-readiness-status.md) (German).

---

## Still stuck

1. `bifrost doctor` — every finding has a stable code (`BFR-DB-0002`) that survives text changes.
2. Check the log at start: `Datenbank initialisiert (CreatedFromMigrations | BaselinedLegacySchema |
   Migrated)` tells you which database the gateway actually found.
3. Search [`docs/operations.md`](../operations.md) (German) — it is longer and more detailed than
   this page and is the authoritative source.
4. Open an issue using the bug template; it asks for the version, the provider, the deployment shape
   and the `doctor` output, because those four answer most questions before anyone replies.
