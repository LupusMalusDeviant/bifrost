# Quickstart

> **Source of truth.** This page has no German original — it *is* the source version (see
> [`docs/i18n.md`](../i18n.md)). Where it summarises operational behaviour, the authoritative
> description is [`docs/operations.md`](../operations.md) (German); **on contradiction, that page
> wins.**

> **About the commands on this page.** Nothing here was executed against a live instance while
> writing it. The only commands that were actually run are the `docker compose config -q`
> validations in [Verify the compose files](#verify-the-compose-files-runnable-now). Everything
> else is transcribed from the German operations manual and is marked where output would depend on
> your machine. **No sample output on this page is invented** — where a command's output matters
> and could not be produced, the page says so instead of showing a fake.

Goal: a running gateway, an administrator account, one API key, one agent connected. Fifteen
minutes, most of it waiting for a pull.

---

## 0. What you need

- **Docker** with Compose v2 (`docker compose version`). Linux containers — on Windows that means
  the WSL2 backend.
- A **TLS reverse proxy** if anything other than `localhost` will reach the web UI. Not optional,
  and not for the reason you expect — see [step 3](#3-open-the-web-ui).
- Roughly 400 MB of disk for the image plus whatever your audit log grows to.

You do **not** need the .NET SDK, and you do not need to build anything. The standard path pulls a
published image.

---

## 1. Configure and start

```bash
git clone https://github.com/LupusMalusDeviant/bifrost.git
cd bifrost
cp .env.example .env
chmod 600 .env
```

Now open `.env` and **set the version explicitly**:

```bash
BIFROST_VERSION=0.12.0
```

> **Do not skip this.** There is no `latest` tag, on purpose: a moving pointer turns a restart —
> a power cut, a `restart: unless-stopped` — into an unnoticed upgrade. The version that runs
> belongs in a file you read before you change it.
>
> **Known gap (2026-08-01):** the checked-in `.env.example` and the fallback inside
> `docker-compose.yml` still name `0.11.0`, while `0.12.0` is the current release. Copying the
> example unedited therefore gets you the previous version. Set the line yourself.

For production, pin the **digest** rather than the tag — a tag can be re-pointed in the registry, a
digest cannot:

```bash
BIFROST_IMAGE=ghcr.io/lupusmalusdeviant/bifrost@sha256:<64-hex>
```

The digest is printed in the release output. From an already-pulled image:

```bash
docker image inspect --format '{{index .RepoDigests 0}}' ghcr.io/lupusmalusdeviant/bifrost:0.12.0
```

Then:

```bash
docker compose up -d
curl -fsS http://localhost:8080/healthz
```

### Verify the compose files (runnable now)

These need no image, no instance and no network, and they were **actually executed** while writing
this page — all three exited `0` against Docker Compose v5.3.0:

```bash
docker compose config -q
docker compose -f docker-compose.yml -f docker-compose.postgres.yml config -q
docker compose -f docker-compose.yml -f docker-compose.build.yml config -q
```

And the one line that has cost this project the most:

```bash
docker compose config --volumes
```

Executed here, it prints `bifrost-data`. The **effective** volume name carries the Compose project
name as a prefix — `<project>_bifrost-data` — and the project name defaults to the lowercased
directory name. Rename the directory, move the compose file, or rename the key, and you point at a
**different** volume: Docker silently creates it empty, the gateway initialises a fresh database
and reports itself healthy. **The failure looks like a successful start.** Pin it once:

```bash
COMPOSE_PROJECT_NAME=bifrost      # in .env, new installations only
```

---

## 2. Collect the setup token

The first start creates **no** account. It issues a single-use, short-lived setup token and writes
it to a file readable only by the service account:

```bash
docker compose exec bifrost cat /data/config/bootstrap-token.txt
```

It is **never** printed to the log. A credential in the log is a credential in every support
ticket, every log aggregator and every backup of the log directory — and it is the one place
nobody ever rotates.

The token is valid for **one hour** (`BIFROST_BOOTSTRAP_TTL_MINUTES`) and exactly **once**. If it
expires before anyone sets up, the next start issues a new one. Once an account exists, there is
**no second token over the network** — recovery then goes through the console commands in
[Operations → Resetting access](operations.md#resetting-access).

> **Residual risk, stated plainly:** the handover file lives in the data directory. A backup taken
> *inside* that one-hour window carries the token in cleartext. After redemption or expiry the
> file is deleted. That is the deliberate trade against a permanent log entry.

---

## 3. Open the web UI

Go to `http://localhost:8080/setup`, paste the token, and choose **your own** username and
password. Redeeming the token deletes the file.

> **The session cookie needs HTTPS, or the login will not stick.** Outside `Development` the
> session cookie always carries `Secure`. A browser **silently discards** such a cookie over plain
> HTTP: the login succeeds, the server answers `302`, and the next page is the login form again.
> There is no error message anywhere — the symptom does not point at the cause.

| Setup | Login sticks? |
|---|---|
| TLS proxy in front, sets `X-Forwarded-Proto: https` | yes |
| direct HTTPS | yes |
| `http://localhost:8080` (including via SSH tunnel) | yes — browsers treat `localhost` as a secure origin |
| `http://<ip-or-hostname>:8080` | **no** |

Behind a TLS proxy you must **also** set `BIFROST_TRUSTED_PROXIES`, or the gateway only ever sees
HTTP and builds its redirects from that — a logged-out visit to a protected page then bounces from
`https` to `http` and the proxy answers `400 The plain HTTP request was sent to HTTPS port`.

```bash
BIFROST_TRUSTED_PROXIES=any        # only if the gateway is reachable exclusively via the proxy
BIFROST_TRUSTED_PROXIES=172.17.0.1,10.0.0.0/8
```

Opt-in is deliberate: if the gateway sits directly on the network, any client could claim
`X-Forwarded-Proto: https` and defeat both the redirect logic and the warning. A typo in the value
aborts the start rather than silently falling back to "off".

`/mcp` and `/api` are **not** affected — agents authenticate with an API key header, not a cookie.

---

## 4. Create an API key and connect an agent

In the web UI: **RBAC → Keys**. The key is shown **once**, together with a ready-made client
configuration.

```bash
claude mcp add --transport http bifrost http://localhost:8080/mcp \
  --header "Authorization: Bearer <API-KEY>"
```

The agent then sees the meta-tools `search_tools` / `describe_tool` / `invoke_tool` plus whatever
its profile pins. That is the whole token-saving mechanism: the long tail of tools stays out of the
context window until something asks for it.

> Behind a reverse proxy, check the address in the generated snippet. It comes from your browser's
> request and is not necessarily the address agents will use.

---

## 5. Add an upstream server

Everything else — upstream servers, roles, profiles, guardrails — is managed from the web UI or the
REST API. There are no config files to edit.

Two things worth knowing before you add the first one:

- **stdio upstreams run with the gateway's privileges.** There is no sandbox between the gateway
  and an stdio MCP server child process ([ADR-0005](../adr/0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md)).
  Connect only servers you trust — exactly as you would when attaching them to an agent directly.
  Since 2026-07-28 the child no longer inherits the gateway's environment, but that reduces blast
  radius; it is not isolation.
- **Newly created stdio and CLI upstreams run in a container** (non-root, read-only rootfs, no
  capabilities, no network without an explicit target list). If no container runtime is available,
  the upstream does **not** come up — there is no fallback to the host, because falling back would
  silently remove the property you chose the container for.

---

## Where to go next

- [Operations](operations.md) — TLS, key ring protection, backups, upgrades, diagnostics
- [Security](security.md) — what the pipeline guarantees and, more usefully, what it does not
- [Troubleshooting](troubleshooting.md) — if the start failed or the UI is dead
- [Solo tutorial](tutorials/solo.md) — the same path, but with a working setup at the end

## Building from source (not an installation path)

```bash
dotnet build Bifrost.slnx
dotnet test Bifrost.slnx
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Bifrost.Server
```

> **Keep `Development` when running from source.** Outside it, ASP.NET does not load the static web
> assets: `_framework/blazor.web.js` is served with `200` and **zero bytes**, the Blazor circuit
> never starts, and *no button in the admin UI does anything* — while pages still render
> server-side and therefore look perfectly usable. The published Docker image is unaffected.
> (`dotnet run --environment Development` does **not** help — that argument is swallowed; the
> variable has to come from the environment.)

Building the image locally is possible but is explicitly **not** an installation path — the result
carries neither provenance nor signature, which is why it is named `bifrost-local:dev` and never
what the release image is named:

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
```
