# Security — what protects you, and what does not

> **Source of truth.** The policy — supported versions, how to report a vulnerability, what is in
> scope — is [`SECURITY.md`](../../SECURITY.md) (English, authoritative). The detailed gate
> description is [`docs/security-gates.md`](../security-gates.md) (German, authoritative for the
> gates). This page is an operator-facing summary of both; **on contradiction, those two win**.
> See [`docs/i18n.md`](../i18n.md).

> **No command on this page was executed while writing it.** The measured results quoted below come
> from the runs recorded in [`docs/security-gates.md`](../security-gates.md) §2–§3 (2026-07-31) and
> from the first release run recorded in
> [`docs/plans/product-readiness-status.md`](../plans/product-readiness-status.md) (2026-08-01).
> Nothing here is a re-measurement.

---

## The one-sentence version

Every call — MCP, REST, UI, webhook — runs through the same invoker: rate limit → RBAC → schema
validation → guardrail → approval → audit. There is no second path. That is what makes the
properties below true *by construction* rather than by convention, and it is also the reason a
defect in the pipeline is a defect everywhere at once.

---

## What the design actually promises

- **Credential concentration is real.** Upstream credentials are stored encrypted (ASP.NET Data
  Protection); agent API keys are stored only as hashes. The gateway host remains a high-value
  target — harden it accordingly: dedicated user, TLS via reverse proxy, restricted network
  exposure.
- **Default-deny RBAC.** Agents see and reach only what a role explicitly grants. Visibility follows
  permission. A tool visible or callable without a grant is a vulnerability — please report it.
- **Audit integrity.** Every call, including denied ones, is logged with secret redaction. A bypass
  of redaction or of audit logging is a vulnerability.
- **Only signed plugins load.** WASI components and connector packages are verified against pinned
  Ed25519 publisher keys; an empty trust store loads **nothing** (fail-closed, not "no
  restriction"). Revoking a publisher stops its running upstreams immediately.
- **Tool definitions are pinned.** Name, description and input schema are fingerprinted on every
  discovery; a changed tool is withheld from the catalogue — not callable, not visible — until an
  administrator accepts the new version. This covers the rug-pull path, which no MCP standard
  addresses.
- **No token passthrough.** The gateway never forwards an agent's credential to an upstream. Where
  an upstream uses OAuth, the token is bound to that upstream via the RFC 8707 `resource` indicator
  and is not usable elsewhere.
- **Untrusted input stays untrusted.** Tool descriptions and results from upstream servers are
  treated as untrusted content (encoded in the UI, never executed).

---

## What it does not promise

This section exists because every item below has, at some point, been mistaken for a guarantee.

### stdio upstreams are not sandboxed

There is no sandbox between the gateway and an stdio MCP server child process
([ADR-0005](../adr/0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md)). Connect only servers
you trust, exactly as you would when attaching them to an agent directly. Since 2026-07-28 the child
no longer inherits the gateway's environment — so the database and key-ring passwords are not
readable from it — but that is blast-radius reduction, not isolation: same user, same filesystem,
same network, including access to the key ring that decrypts every upstream credential.

Newly created stdio and CLI upstreams do run in a container by default, and there is **no fallback
to the host** if no runtime is available. Existing upstreams keep their behaviour.

### Not every isolation path is a sandbox

CLI programs in *host* mode run as child processes with the gateway's rights. The hardening
(canonical paths, root allowlists, optional SHA-256 pin, byte and time limits, isolated environment)
reduces attack surface but is not a kernel boundary. Container mode and WASI components are.

**WASI is a sandbox, not a guarantee.** Its safety depends on the host functions, directories,
sockets and secrets you grant. Grants default to none and are audited on every load. Note also that
`wasmtime` itself has carried advisories — see [dependency findings](#what-the-gates-have-actually-found).

### Secret detection catches what has a pattern

`AKIA…`, `ghp_…`, PEM blocks: found. A 32-character random password: indistinguishable from a file
id, not found. Entropy heuristics are deliberately absent because they fire on commit SHAs and
UUIDs almost always, and under "block" every false positive is an aborted piece of work. The
guardrail is a layer, not a substitute for keeping credentials out of tool results.

### `AllowPrivateTargets` is undecided for existing HTTP upstreams — which means allowed

An MCP-over-HTTP upstream carries `bool? AllowPrivateTargets` where `null` means **not decided, and
therefore permitted**. Every upstream configured before the switch existed has `null`.

Newly created upstreams do **not**: the form and the API write an explicit `false` before storing.
Config import and restore deliberately leave the value untouched, because they reproduce a
configuration rather than create one — a restore that silently tightened a setting would not be
restoring what it claims to.

So the exposure is the existing stock, and anything brought in by import or restore. Treat such an
upstream's endpoint as unvalidated: set
`AllowPrivateTargets: false` explicitly wherever a private, loopback or link-local target is not
intended. Details and the reasoning for the tri-state:
[Operations → Private network targets](operations.md#the-gap-mcp-over-http-upstreams).

### An early security audit missed a real defect

A security audit was performed early on and missed a redaction gap that an independent
requirement-versus-code review found later: on the lazy path (`invoke_tool`), tool arguments reached
the audit log **unredacted**. Treat the audit as one input, not a clean bill of health. Findings and
accepted residual risks are in the [threat model](../security/threat-model.md) (German).

If you ever ran one of the withdrawn early 1.x builds, see the advisory in
[`SECURITY.md`](../../SECURITY.md#advisory-for-anyone-who-ran-an-early-1x-build): inspect the
`AuditEvents` table, delete the affected rows, rotate every credential that appeared there.

---

## The automated gates

Every pull request, every push to `main`, and a weekly scheduled run go through
`.github/workflows/security.yml`; the blocking subset is repeated on the tagged commit in
`release.yml`. **Critical/High blocks the release.**

| # | Gate | Checks | Blocks at |
|---|---|---|---|
| G1 | CodeQL (`csharp`) | own code, `security-and-quality` | `security-severity` ≥ 7.0 |
| G2 | NuGet vulnerabilities | `dotnet list package --vulnerable --include-transitive`, all 22 projects | Critical/High |
| G3 | `cargo audit` | four Rust lockfiles (WASI host + 3 guests) | any advisory |
| G4 | Container filesystem | Trivy against the built image | Critical/High |
| G5 | Working tree | Trivy `fs`: lockfiles + Dockerfile/Compose misconfiguration | Critical/High |
| G6 | Secrets | gitleaks over history (push/schedule) or the PR diff | any new finding vs. baseline |
| G7 | Exception register | expiry, approvals, drift against `.trivyignore.yaml` | expired or incomplete entry |

**No gate is softened with `continue-on-error`.** Where `if: always()` appears, it concerns only
uploading reports *after* a failure, so the finding stays visible. There is no environment variable
that relaxes a gate.

### Why some gates run twice

Checking only at release time means learning about a finding at the latest possible moment. Checking
only in the PR means knowing nothing about the tagged commit, and nothing about advisories published
after the merge — which is exactly what the weekly run covers. The release-time gates sit in the
`verify` job, before the build: a blocking gate *after* the push would have to revoke a published
image instead of preventing it. The price is about five extra minutes on the critical path.

### The exception path

An exception to the blocking rule is possible, but only **explicitly, time-limited and documented**
in `.github/security-exceptions.yml`. It names the approver, carries a reason of at least 40
characters, and expires after at most 90 days.

An **expired** entry makes the run fail rather than quietly becoming ineffective: a silently
ineffective entry is litter nobody clears, while an entry that halts the run is an open task.
Extension requires a **new** reason, not a pushed-forward date. The register is fail-closed — if it
is unreadable, the gates abort rather than assuming "no exceptions" or "all exceptions".

### The approval requirement is not enforced yet

The exception path binds an override to the Product Owner's named approval. `.github/CODEOWNERS`
exists and lists the security gates, the release pipeline, `CryptographicNames.cs` and the ADRs.

**But `CODEOWNERS` on its own enforces nothing.** "Require review from Code Owners" must be enabled
in the branch protection rules for `main`, otherwise the entry is a suggestion. That is a repository
setting and cannot be shipped in the repository. **Until it is enabled, the Product Owner approval
requirement is documented but not enforced** — anyone who can change the exception register can
approve their own exception.

> Note: [`docs/security-gates.md`](../security-gates.md) §5 and §9 still state that CODEOWNERS does
> not exist. That was true when the document was written (2026-07-31) and is no longer true; the
> file was added during M3. The dependency on branch protection is unchanged.

---

## What has actually been proven — and what has not

This distinction is the point of [`docs/security-gates.md`](../security-gates.md). A gate that has
never been red is a claim about future behaviour, not a proof.

### Proven able to fail (negative tests, 2026-07-31)

| Gate | Negative test | Result |
|---|---|---|
| G1 CodeQL | `sarif_gate.py` against a SARIF with `security-severity` 7.5 | exit 1, finding named |
| G1 CodeQL | `sarif_gate.py` against a **missing** SARIF file | exit 2 (fail-closed — "analysis did not run", not "nothing found") |
| G2 NuGet | fixture project with `Newtonsoft.Json 12.0.1` (GHSA-5crp-9r3c-p9vr, High) | exit 1 |
| G3 cargo audit | real state of `spikes/wasi-component-runtime` | exit 1 on 2 findings, exit 0 with both ignored |
| G6 gitleaks | probe file with four credential shapes at a non-allowed path | exit 2, 4 findings |
| G7 register | expired entry / 200-day duration / `approved_by: Team` | exit 1 each |
| G7 register | active exception lets G2 through; **expired** one makes G2 block again | exit 0, then exit 1 |

That last row is the important one: it proves an exception **lapses on its own**. Nobody has to act
for the gate to come back.

### Not proven

- **G4, the image scan, has never been shown able to fail.** It has now *run* — the first release
  run on 2026-08-01 reported Trivy green on both the image and the CLI artefacts — but a green run
  proves the plumbing, not the gate. There is still no run of this configuration against an image
  with a genuine Critical finding. **G4 is the weakest link of this setup.**
- **CodeQL runs only on GitHub.** That `sarif_gate.py` evaluates a CodeQL SARIF correctly is proven
  against a self-produced SARIF, not a real one.
- **The SARIF upload path** (permissions, fork behaviour, whether GitHub accepts the self-produced
  SARIF from `dotnet_vulnerable_gate.py`) is unverified.
- **Two tool pins are maintained by hand.** Dependabot covers Actions, NuGet, Cargo and Docker, but
  not the digest pins of `zricethezav/gitleaks` and `aquasec/trivy` inside workflow scripts —
  Dependabot's `docker` ecosystem reads Dockerfiles and compose files, not workflow steps. Review
  quarterly, or when an advisory concerns the tools. Current state (2026-07-31): gitleaks
  **v8.30.1**, Trivy **0.72.0**.

### What the gates have actually found

- **NuGet: clean.** Zero vulnerable packages, direct or transitive, across all 22 projects.
- **A gate that could never have been red.** `dotnet list package --vulnerable` reports findings and
  **still exits 0**. The obvious one-line workflow step would have been a permanently green gate.
  `.github/scripts/dotnet_vulnerable_gate.py` therefore parses `--format json` instead of trusting
  the exit code. The table output is also localised (`Schweregrad` vs. `Severity`), so a `grep`
  would have worked here by coincidence and failed silently elsewhere.
- **Rust: two real findings, both LOW.** RUSTSEC-2026-0222 and -0223 against `wasmtime` 47.0.2. The
  Critical/High gate does not block them — but the affected component is the sandbox that confines
  untrusted WASI components. "Breaking internal VM state" is a footnote in an ordinary program and a
  description of the boundary itself in a sandbox. Resolved by moving to 47.0.3; `cargo audit`
  reports nothing since.
- **Secrets: 19 hits in history, all synthetic.** Every one inspected individually; all come from
  test fixtures that exist to prove the product redacts or detects secrets. That is the nature of
  this repository — a redaction test **must** contain secret-shaped input, or it tests nothing.
  Handled through a redacted baseline plus eight named fixture files excluded in
  `.github/gitleaks.toml`. **There is no blanket `tests/**` exclusion**: a real credential in a test
  file is just as much a leak as one in `src/`, and test files are the *more* likely place, because
  that is where people experiment against real systems.

  The honest downside: **inside those eight excluded files, a real credential would not be found.**
  The cleaner fix is a per-line marker convention instead of per-file exclusion; it requires changes
  under `tests/**` and has not been done.

---

## Before you expose it

1. **TLS reverse proxy, always** (NFR-04). Also set `BIFROST_TRUSTED_PROXIES` — see
   [Operations](operations.md#tls-and-reverse-proxy).
2. **Decide the key ring mode.** `certificate`, `file-secret` or an explicit `none`. Leaving it
   unset means the ring lies in cleartext *and* nobody decided that;
   [Operations](operations.md#key-ring-protection).
3. **Connect only stdio servers you trust**, and prefer container-isolated CLI or WASI upstreams for
   anything you do not.
4. **Set `AllowPrivateTargets: false`** on HTTP upstreams that have no business reaching your
   internal network.
5. **Run `bifrost doctor`** and treat `BFR-KEY-0002` (no key ring mode chosen) as a real finding.
6. **Verify the release you are running.** Signatures, provenance and SBOMs are verifiable against
   public Sigstore infrastructure and GitHub — no key from us required. The procedure, including the
   part that matters most (proving the verification *rejects* something wrong), is in
   [`docs/security/verifying-releases.md`](../security/verifying-releases.md) (German).

## Reporting a vulnerability

**Do not open a public issue.** Use
[GitHub private vulnerability reporting](https://github.com/LupusMalusDeviant/bifrost/security/advisories/new)
("Report a vulnerability" under the Security tab). Initial response within **7 days**. Full policy,
including what is out of scope: [`SECURITY.md`](../../SECURITY.md).
