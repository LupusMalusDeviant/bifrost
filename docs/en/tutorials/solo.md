# Tutorial: solo operator

> **Source of truth.** This tutorial has no German original — it *is* the source version (see
> [`docs/i18n.md`](../../i18n.md)). For operational detail it defers to
> [`docs/operations.md`](../../operations.md) (German), which is authoritative; **on contradiction,
> that page wins.**

> **Not executed.** No command in this tutorial was run against a live instance while writing it, and
> no output is shown that was not actually produced. Where a step's result matters, the text
> describes what to look for.

**Scenario.** One person, one machine, SQLite, a handful of MCP servers you already run by hand, one
agent (Claude Code). Goal: stop copying credentials into every agent config, and get one audit log
that answers "which agent called what".

Time: about 30 minutes, plus whatever your upstream servers need.

---

## 1. Decide two things before you install

**Where will it be reachable from?** If the answer is "only this machine", you can use
`http://localhost:8080` and skip TLS — browsers treat `localhost` as a secure origin, so the session
cookie survives. If the answer is anything else, including "my laptop over Tailscale", you need a TLS
reverse proxy, because the session cookie carries `Secure` and a browser drops it over plain HTTP
without a word. There is no middle option that works.

**How will you protect the key ring?** The DataProtection key ring decrypts every upstream
credential you are about to store. Pick one of `certificate`, `file-secret`, or an explicit `none`.
Choosing `none` deliberately is fine for a single machine with restrictive directory permissions —
what is not fine is leaving it unset, because then the ring lies in cleartext *and* nobody decided
that. `bifrost doctor` will report it as a warning until you choose.

For a solo setup, this is a reasonable answer:

```bash
BIFROST_KEYRING_PROTECTION=none    # in .env — a decision, not an omission
```

If you would rather protect it, the server creates the certificate and password file for you with
correct permissions — see [Operations → Key ring protection](../operations.md#key-ring-protection).

---

## 2. Install

```bash
git clone https://github.com/LupusMalusDeviant/bifrost.git
cd bifrost
cp .env.example .env
chmod 600 .env
```

In `.env`:

```bash
BIFROST_VERSION=0.12.0
COMPOSE_PROJECT_NAME=bifrost
BIFROST_KEYRING_PROTECTION=none
```

`COMPOSE_PROJECT_NAME` is the line that saves you later. Without it, the volume name depends on the
directory name, and moving or renaming the directory gives you a **new, empty** volume that starts
cleanly and reports itself ready. Set it now, on a fresh install, and it never bites.

```bash
docker compose config -q          # validates the file; exits 0 with no output
docker compose up -d
curl -fsS http://localhost:8080/healthz
```

---

## 3. First access

```bash
docker compose exec bifrost cat /data/config/bootstrap-token.txt
```

Open `http://localhost:8080/setup`, paste the token, choose your own username and password. The
token is single-use and expires after an hour; redeeming it deletes the file.

If you miss the window and nobody is set up yet, the next start issues a new one. If you *are* set
up and lose the password, there is no route over the network — use
`docker compose run --rm bifrost dotnet Bifrost.Server.dll --reset-ui-admin`.

---

## 4. Add your first upstream

**Servers → Add.** Pick the transport you already use:

- **stdio** — the common case for local MCP servers. Note that newly created stdio upstreams run in
  a container by default (non-root, read-only rootfs, no network unless granted). If you have no
  container runtime available, the upstream will not come up, and there is deliberately no fallback
  to the host.
- **HTTP (Streamable)** — a hosted MCP server. **Set `AllowPrivateTargets: false`** unless the
  endpoint really is on your own network. The field defaults to "not decided", which currently means
  permitted; see [Security](../security.md#allowprivatetargets-is-undecided-for-existing-http-upstreams--which-means-allowed).
- **OpenAPI / OpenRPC / CLI** — importing an existing REST API, JSON-RPC service or command-line
  program as MCP tools.

Credentials go in the form and are stored encrypted. This is the point of the exercise: they now
exist in one place instead of in every agent's config file.

Watch the server list: an upstream that fails shows as `Failed` with a reason. Nothing runs half-up
silently.

---

## 5. Connect your agent

**RBAC → Keys → new key.** It is displayed **once**, with a ready-made client configuration.

```bash
claude mcp add --transport http bifrost http://localhost:8080/mcp \
  --header "Authorization: Bearer <API-KEY>"
```

Your agent now sees `search_tools`, `describe_tool` and `invoke_tool` plus whatever its profile
pins. That is the token-saving design: the long tail of tools stays out of the context window until
something asks for it. If you have a handful of tools you use constantly, pin those in the profile
and leave the rest lazy.

Remove the direct MCP server entries from your agent config once this works. Two configs for the
same server is how you end up debugging the wrong one.

---

## 6. Turn on the things you will forget otherwise

**Guardrails** are on by default and scan both directions. Leave them on. The direction that matters
is result → agent: a tool returning a `.env` or a Kubernetes secret otherwise pushes it into the
model's context, and from there into its logs. Remember the limit: pattern-based detection finds
`AKIA…` and `ghp_…`, not a 32-character random password.

**Audit retention** defaults to 30 days with an hourly cleanup job. On SQLite this is an operational
obligation, not a nicety — a very large log (> ~10 GB) is a reason to move to PostgreSQL.

**A backup habit.** On SQLite you get a product path, and it runs against the running gateway:

```bash
bifrost backup create --out /data/backups/bifrost-$(date +%F).zip
bifrost backup verify /data/backups/bifrost-$(date +%F).zip
```

> **A full backup is a secret.** It contains the key ring — the key to every upstream credential you
> just stored. Put it somewhere as protected as the data directory, or encrypt it:
> `--passphrase-env NAME` (never `--passphrase` as an argument; it would land in your shell
> history).

---

## 7. Check your work

```bash
bifrost doctor
```

Every finding has a stable code. Two you should expect to care about:

- `BFR-KEY-0002` — no key ring mode chosen. If you set `BIFROST_KEYRING_PROTECTION=none` in step 2,
  this is green: you decided.
- `BFR-NET-*` — ports, public address, proxy trust. Worth reading if you added a reverse proxy.

`bifrost doctor` reads only, with one exception: it creates and immediately deletes a
`.bifrost-doctor-*.tmp` file in the data directory, because writability cannot be checked reliably
read-only.

---

## What you have now

- One endpoint for your agent instead of one config entry per MCP server.
- Credentials stored encrypted in one place instead of copied into agent configs.
- An audit log that answers who called what with which arguments, including denied calls.
- Hot-swappable upstreams: adding a server does not require restarting your agent session. The
  change becomes visible after at most one list-cache TTL (default 60 s).

## What you do not have yet

- **Access control that means anything.** With one key and one identity, RBAC has nothing to
  separate. That is fine for solo use — see the [small team tutorial](small-team.md) when it stops
  being fine.
- **A second pair of eyes on destructive tools.** See the
  [approval-gated deployment tutorial](approval-deployment.md).
- **TLS**, unless you added it. `localhost` is genuinely safe here; anything else is not.

## Next

- [Operations](../operations.md) — upgrades, diagnostics, the full configuration reference
- [Troubleshooting](../troubleshooting.md) — if any step above did not behave as described
- [Security](../security.md) — before you point this at anything you care about
