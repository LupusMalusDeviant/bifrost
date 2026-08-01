# Tutorial: small team

> **Source of truth.** This tutorial has no German original — it *is* the source version (see
> [`docs/i18n.md`](../../i18n.md)). For operational detail it defers to
> [`docs/operations.md`](../../operations.md) (German), which is authoritative; **on contradiction,
> that page wins.**

> **Not executed.** No command in this tutorial was run against a live instance while writing it,
> and no output is shown that was not actually produced.

**Scenario.** Four to ten people, a dozen agents between them — some read-only, some write-capable —
and a set of MCP servers that not everyone should reach. Goal: one gateway where "who may do what"
is a configuration rather than a convention, and where the audit log answers a question after the
fact instead of prompting a guess.

Assumes you have already worked through [Quickstart](../quickstart.md) or the
[solo tutorial](solo.md).

---

## 1. Decide the shape before you create anything

Three decisions that are painful to change later:

**Database.** SQLite is fine until the audit log gets large. A team generates far more calls than a
solo operator, and audit retention on SQLite is an operational obligation, not a nicety. Moving to
PostgreSQL is a real option — but one condition comes with it:

- **PostgreSQL backup runs through `pg_dump`/`pg_restore`, and they must be on the host.** With
  them, `bifrost backup`/`restore` work exactly as on SQLite, and the pre-migration backup is
  demanded before every schema change.
- **Without them, both refuse with a message.** Then you own `pg_dump` **plus** the `keys/`
  directory, together, forever — and every upgrade runs without a way back, since there is no
  downgrade path.

So: install the client package on the host that runs Bifrost. If nobody on the team will own that
(or a dump schedule instead), stay on SQLite and manage retention.
See [Known limitations](../operations.md#known-limitations).

**TLS.** Not optional here. More than one machine reaches the UI, which means the session cookie
must travel over HTTPS or logins silently fail. Reverse proxy plus `BIFROST_TRUSTED_PROXIES`.

**Key ring protection.** With several people able to reach the host, `file-secret` earns its keep:
the PFX password never enters `.env`, `docker inspect` or `/proc/<pid>/environ`. The server creates
both files with correct permissions:

```bash
docker compose run --rm bifrost dotnet Bifrost.Server.dll --keyring-setup --cert /secrets/keyring.pfx
```

Then uncomment both `secrets:` blocks in `docker-compose.yml` and set, in `.env`:

```bash
BIFROST_KEYRING_PROTECTION=file-secret
BIFROST_KEYRING_CERT_PATH=/run/secrets/bifrost-keyring-pfx
BIFROST_KEYRING_CERT_PASSWORD_FILE=/run/secrets/bifrost-keyring-password
```

A declared but missing secret aborts `up`, so create the files first. Do **not** also set
`BIFROST_KEYRING_CERT_PASSWORD` — both forms together abort the start on purpose, because a
precedence rule between two sources of the same secret is one you would misremember.

Keep the certificate **next to** the data directory, not inside it, or it travels in every backup
and protects against nothing.

---

## 2. Design roles before you create keys

Default-deny is the whole point: an identity with no grants can do nothing, and **visibility follows
permission** — an agent does not see tools it may not call. A tool it cannot see cannot be talked
into calling.

A shape that works for most small teams:

| Role | Grants | For |
|---|---|---|
| `reader` | read-only tools on the servers everyone uses (search, fetch, list) | Agents that summarise, research, answer questions |
| `writer` | the above plus writing tools on specific servers | Agents that file issues, open PRs, update tickets |
| `ops` | the above plus the operationally sensitive servers | A small number of humans' agents |
| *(admin)* | RBAC, servers, packages, guardrails, operations | Humans, in the UI — not agent keys |

Grants are per server, per tool and per action, so `writer` does not have to mean "write everywhere".
Resist starting with one broad role "for now": the audit log becomes far less useful when every call
comes from the same identity, and narrowing a role later means finding out the hard way who relied
on it.

**Do not give an agent key a global grant.** A global grant is the threshold for RBAC administration,
package installation and every operations endpoint. It belongs to a human's admin session, not to
something running in a loop.

---

## 3. One key per agent, not one key per person

**RBAC → Keys.** Issue a separate key for each agent, named for what it is
(`ci-changelog-bot`, `alex-claude-code`, `oncall-triage`). Each key is shown **once**, with a
ready-made client configuration.

This matters for exactly one reason and it is worth the extra minute: the audit log records the
identity, so a shared key turns every question of the form "who called this" into "someone did".
Rotating one agent's key without disrupting everyone else also stops being possible.

```bash
claude mcp add --transport http bifrost https://gateway.example.com/mcp \
  --header "Authorization: Bearer <API-KEY>"
```

> Behind a reverse proxy, check the address in the generated snippet — it comes from the browser
> request that generated it, which is not necessarily the address agents will use.

### Or: OAuth instead of keys

If you already run an identity provider, agents can authenticate with its access tokens instead. The
gateway then acts as an OAuth resource server; it issues no tokens itself.

```bash
BIFROST_OAUTH_ISSUER=https://login.example.com/realms/bifrost
BIFROST_PUBLIC_BASE_URL=https://gateway.example.com
```

Tokens are validated against the issuer's JWKS: signature, issuer, expiry — **and audience**. A
token issued for another service does nothing here; without that check the gateway would be the
place where foreign tokens get redeemed.

On the first valid token from an unknown subject, an identity is created **with no role at all**. It
can do nothing until an administrator grants it something. That is deliberate: the alternative
(rejecting unknown subjects) means nobody ever sees who knocked. API keys keep working alongside;
they are checked first.

---

## 4. Profiles: what each agent sees

A profile decides which tools are pinned with full schemas and which stay behind
`search_tools` / `describe_tool` / `invoke_tool`. Pin the handful an agent uses constantly; leave
the rest lazy. In the reference setup this is a ≥ 96 % reduction in schema tokens.

Profiles are not access control — RBAC is. A profile shapes the context window; a role decides what
is reachable. Do not use one to do the other's job.

If you maintain shared conventions or playbooks, put them in **Skills**: `list_skills` returns names
and one-line descriptions, `read_skill` fetches the text. The model can reach for them by itself,
which the prompt path cannot do (prompts are user-initiated in most clients — the human sees the
list, the model does not).

> **Skills are readable by every authenticated identity** (FR-40), including via `list_skills`.
> Never put credentials in a skill.

---

## 5. Guardrails, tuned for a team

Guardrails are on by default. Two settings worth revisiting with more people involved:

**New custom rules start in observe mode, and should stay there until you have seen them fire.**
Arming a rule you have never watched will, in doubt, abort somebody's productive work — and the
person it happens to is not the person who wrote the rule.

**Free-form regex is off by default.** `BIFROST_GUARD_ALLOW_CUSTOM_PATTERNS=1` enables it, and that
is a trust decision: .NET offers no security boundary against malicious patterns, and the
backtracking-free engine protects against expensive *inputs*, not malicious *patterns*. Whoever sets
that switch is allowing admins to consume CPU inside the gateway process. The guided editor —
prefix, character class, length range — covers essentially every token format without the risk.

Remember which direction is which when a block happens:

| Direction | What happened |
|---|---|
| Arguments | The call was aborted **before** the upstream. No side effect. |
| Result | The call **already ran**. Only the result is withheld — **do not retry**. |

---

## 6. Make the audit log answer questions

The audit log records who, what, when and the result for every call, **including denied ones**, with
secret redaction before persistence. Denied calls are the interesting ones: a role that is too
narrow shows up as a pattern of `Denied`, and a role that is too broad shows up as calls nobody
expected.

Statuses you will want to distinguish:

- `Denied` — RBAC said no.
- `GuardBlocked` — a guardrail caught something. Different problem, different fix.
- `ApprovalRequired` — the call needs a human; see the
  [approval tutorial](approval-deployment.md).

Arguments are redacted using known secret field names, plus per-tool patterns you can maintain in
the UI under **Tools → [tool]**. Results are **not** stored by default — only their size.
`BIFROST_AUDIT_DEBUG_PAYLOADS=1` stores them, redacted, and is a debugging aid rather than a
setting: responses can be large, and the audit table grows accordingly.

**Telemetry carries no payloads.** Spans record timing, tool, server, status, origin and caller —
never arguments or results. The audit log is redacted; a telemetry backend is not, and it is usually
less protected than the database. A test enforces this.

If you export to an OTLP endpoint, the useful measurement is the difference between the
`bifrost.tool_call` span and its `bifrost.upstream_call` child: that difference is the gateway's own
overhead, which is the only way to tell whose fault a slow answer is.

---

## 7. Operating it as a team

**Write down who upgrades.** An upgrade migrates the schema automatically and is not reversible. The
rollback is the previous line in `.env`, restored from a backup — and on PostgreSQL there is no
automatic pre-migration backup at all.

**Back up before every upgrade**, and verify the archive rather than assuming it:

```bash
bifrost backup create --out /data/backups/pre-upgrade-$(date +%F).zip
bifrost backup verify   /data/backups/pre-upgrade-$(date +%F).zip
```

**Operations endpoints are admin-only.** Everything under `/api/v1/operations/*` requires a global
grant, the same threshold as RBAC administration; in the UI, the **Betrieb** page sits behind the
admin role. Every writing operation is audited.

**Configuration export is not a backup.** It builds a comparable instance rather than restoring the
same one, and it contains no secret values — which makes it the right thing to commit to a private
repository, and the wrong thing to rely on for recovery. Known gaps: publisher trust is not
exported (a WASI upstream will not load on the target without it), and guard options cannot be
imported.

---

## What you have now

- Per-agent identity, so the audit log answers questions instead of raising them.
- Roles that make "read-only agent" a real category rather than a promise.
- Tool visibility that follows permission, so an agent cannot be talked into calling what it cannot
  see.
- One place to rotate an upstream credential.

## Next

- [Approval-gated deployment](approval-deployment.md) — for the tools where RBAC is not enough
- [Operations](../operations.md) — upgrades, key ring rotation, diagnostics
- [Security](../security.md) — in particular the limits, before you widen access further
