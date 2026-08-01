# Tutorial: an approval-gated deployment

> **Source of truth.** This tutorial has no German original — it *is* the source version (see
> [`docs/i18n.md`](../../i18n.md)). For operational detail it defers to
> [`docs/operations.md`](../../operations.md) (German), which is authoritative; **on contradiction,
> that page wins.**

> **Not executed.** No command in this tutorial was run against a live instance while writing it,
> and no output is shown that was not actually produced. In particular, **no example approval
> dialogue or queue screenshot is reproduced here**, because none could be generated.

**Scenario.** An agent may trigger a deployment, delete a record, or send something outward — and you
want a human in the loop for exactly that, without turning every other call into a ceremony.

RBAC answers *may this identity call this tool at all*. Approvals answer a different question:
*should this particular call, with these particular arguments, happen right now*. Do not use one to
do the other's job — an approval prompt on a tool that should simply be denied is a habit that
trains people to click yes.

Assumes a working gateway with roles and per-agent keys — see [small team](small-team.md).

---

## 1. Choose what to gate, and be strict about how little

Gate the tools where an unwanted call is expensive and irreversible: deploys, deletions, outbound
messages, anything touching money or production data.

**Do not gate a frequently used tool.** This is not a style preference; it is the failure mode. If
every routine call needs a human, someone will eventually switch the requirement off for that tool —
and then it protects nothing at all. A gate that gets disabled is worse than a gate that was never
placed, because the protection is now believed to exist.

Which tools are approval-gated is switchable at runtime, in the UI under **Approvals**, without a
restart (FR-32, [ADR-0012](../../adr/0012-approval-flows-asynchron.md)).

---

## 2. Understand which of the two paths your client will take

There are two, and which one applies depends on the client's protocol revision
([ADR-0023](../../adr/0023-stateless-kern-und-mrtr.md)):

| Client revision | Path | What the human sees |
|---|---|---|
| `2026-07-28` and newer | **MRTR** — the call ends with `input_required`, the client shows a form, and repeats the call with the answer | A prompt in the agent client, at the moment of the call |
| `2025-11-25` and older | classic **elicitation** — only in session mode (`BIFROST_MCP_STATELESS=0`) | The same, but only if you run the gateway session-based |
| anything that cannot show a form | **the queue** | The *Approvals* page in the web UI |

The queue is always the fallback, and **no call is ever lost**. If the gateway cannot ask, the
request waits.

> **A client on the new revision that cannot display a form** gets an error from its own SDK
> (*"no ElicitationHandler is registered"*) rather than a queue message. Since `2026-07-28` no client
> advertises an elicitation capability, so there is nothing left to distinguish on. **The request is
> still in the queue** — it just did not say so. If your team's client behaves this way, tell people
> where the queue is before this happens, not after.

Which revision a connected client speaks, and whether it can be asked, appears in the log on its
first call. When no prompt happens, the reason is logged too — deliberately, because from the
outside a queued call looks identical whether nobody was asked, the question failed, or a human
declined.

---

## 3. Turn it on

In the web UI, **Approvals** (admin area): mark the tool as approval-required. Nothing else changes;
the tool stays visible and callable to anyone whose role grants it — the call now just does not
execute on its own.

Test it with the agent that will actually use it, not with a curl command. The point of the test is
to see which of the three paths above your real client takes.

---

## 4. What a granted approval actually covers

This is the part worth reading twice, because it is more restrictive than people assume, on purpose:

An approval binds to **`(identity, tool, argument fingerprint)`**, expires after **one hour**, and is
**single-use**.

- An approval for `deploy{env:"staging"}` does **not** cover `deploy{env:"production"}`. Changing an
  argument means asking again.
- A second run needs a second approval. Granted consent never becomes a standing permit.
- After approval, the agent issues **the same call again**, and it passes through **once**.
- **Nothing blocks while waiting.** The per-call timeout (FR-09) is untouched, so there is no hanging
  agent holding a connection open.

The human sees the same **masked** arguments in the prompt as in the queue — the popup never shows
more than the UI does. And it remains a human decision: the question goes to the client, the answer
comes from the person in front of it. The agent holds no approval key and cannot fabricate one.

---

## 5. Working the queue

**Approvals** in the web UI shows the pending requests with their concrete, masked arguments. An
operator or admin decides.

Under the hood these are **tasks** ([ADR-0019](../../adr/0019-langlaufende-tasks-und-events.md)):
a waiting request is a task in state `input-required`, a granted approval one in `working`, a denied
one a failed task with code `approval-denied`. One queue instead of two.

```bash
curl -H "Authorization: Bearer $API_KEY" http://localhost:8080/api/v1/tasks
curl -H "Authorization: Bearer $API_KEY" http://localhost:8080/api/v1/tasks?state=input-required
```

- **Visibility follows ownership.** Without a global grant you see only your own tasks. Someone
  else's task returns `404`, not `403` — otherwise the status code would let you enumerate which ids
  exist.
- **State is pulled, not pushed.** There is no subscription and no promise that a notification
  arrives. If you need the state, ask for it. Event delivery is deferred, not built.

### Cancelling

```bash
curl -X POST -H "Authorization: Bearer $API_KEY" \
  http://localhost:8080/api/v1/tasks/<id>/cancel
```

While **nothing is running** — the task is waiting, or approved but not yet redeemed — cancellation
is final: `200`, state `Cancelled`, `Cancellation: Confirmed`. **A cancelled approval can no longer
be redeemed**, which is the actual purpose. An already redeemed task answers `409`: the call has
run, there is nothing left to stop.

A background service marks overdue tasks `expired` every five minutes. That is visibility, not
enforcement — an elapsed deadline already takes effect because the redemption path checks it itself.
Expired tasks are never deleted; they stay auditable in a terminal state.

---

## 6. Verify the gate the only way that means anything

A gate you have never seen refuse is a claim about future behaviour. Before you rely on it:

1. **Call the tool and decline.** Confirm the call does not execute and the audit entry shows
   `ApprovalRequired` — distinct from an RBAC `Denied` and from a `GuardBlocked`.
2. **Approve, then call again with a changed argument.** It must ask again. If it does not, the
   fingerprint binding is not doing what you think it is.
3. **Approve, wait out the hour, then call.** It must ask again.
4. **Approve, call, then call again.** The second one must ask again — single use.
5. **Cancel a pending request, then approve nothing and retry.** The cancelled approval must not be
   redeemable.

Five minutes of this is worth more than any amount of reading, including this page.

---

## 7. Layer it with the rest of the pipeline

Approval is one stage of one pipeline: rate limit → RBAC → schema validation → guardrail → approval →
audit. There is no second path around it — REST, MCP, the web UI and webhook triggers all end in the
same invoker. Two consequences worth planning around:

**A webhook trigger goes through the full pipeline too**, under the fixed identity bound to it, and
appears in the audit with origin `Webhook`. A webhook can therefore never do more than its identity
may. If that identity may call an approval-gated tool, the webhook's call waits in the queue like any
other — which is either exactly what you want or a surprise at 3 a.m. Decide which.

**A guardrail block on the way back is not an approval problem.** If a gated write tool runs after
approval and its *result* trips a guardrail, the action has already happened and only the result is
withheld. The message says so and tells you not to retry. Retrying would deploy twice.

---

## What this gives you, and what it does not

**Gives you:** a human decision bound to a specific call with specific arguments, recorded in the
audit log, that cannot be reused, cannot be widened by editing an argument, and cannot be
manufactured by the agent.

**Does not give you:**

- **Protection against a tool that should not have been reachable.** That is RBAC's job.
- **A guarantee the human read the arguments.** A gate on a frequently used tool trains people to
  approve reflexively. This is the single most important reason to gate few tools.
- **Multi-step workflows.** A webhook triggers exactly one tool call, not a chain.
- **A notification.** Nothing pushes. Somebody has to look at the queue, or the client has to be one
  that can show a prompt.

## Next

- [Small team](small-team.md) — roles and per-agent keys, which this tutorial assumes
- [Operations → Approvals and tasks](../operations.md#approvals-and-tasks) — the reference
- [Troubleshooting](../troubleshooting.md#calls-approvals-and-guardrails) — when an approval behaves
  unexpectedly
