# Contributing to B.I.F.R.O.S.T

Thanks for looking. This page is written in English and is the **authoritative** version — there is
no German translation of it (see [`docs/i18n.md`](../docs/i18n.md)).

**You do not need German to contribute.** The design documentation is German, and it stays German
for reasons explained below, but everything you need to build, test, run and change the code is on
this page and in [`docs/en/`](../docs/en/README.md).

- [Before you start](#before-you-start)
- [Build and test](#build-and-test)
- [The house rules](#the-house-rules)
- [Language policy](#language-policy)
- [Commits and pull requests](#commits-and-pull-requests)
- [Documentation changes](#documentation-changes)
- [Architecture decisions](#architecture-decisions)
- [Security](#security)

---

## Before you start

**Security problems do not go in an issue.** Use
[private vulnerability reporting](https://github.com/LupusMalusDeviant/bifrost/security/advisories/new).
See [`SECURITY.md`](../SECURITY.md).

**For anything larger than a fix, open an issue first.** This project has a written requirements
document and written architecture decisions; a change that contradicts one of them needs the
decision revisited, not the code merged. Finding that out after you have written it is nobody's idea
of a good afternoon.

**Small, obviously-correct fixes** — a broken link, a wrong default in a table, a typo in a message —
just send them.

---

## Build and test

Requires the **.NET 10 SDK**. Rust (stable) additionally, if you touch the WASI host under
`spikes/wasi-component-runtime`.

```bash
dotnet build Bifrost.slnx
dotnet test  Bifrost.slnx
```

Running from source:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Bifrost.Server
```

> **Keep `Development`.** Outside it, ASP.NET does not load the static web assets:
> `_framework/blazor.web.js` is served with `200` and **zero bytes**, the Blazor circuit never
> starts, and no button in the admin UI does anything — while pages still render server-side and
> therefore look perfectly usable. `dotnet run --environment Development` does **not** help; that
> argument is swallowed, the variable has to come from the environment.

There are wrapper scripts for the same checks CI runs:

```bash
./build.sh verify-fast          # build.ps1 on Windows
./build.sh verify-dotnet
./build.sh verify-rust          # fmt, clippy -D warnings, cargo test
./build.sh verify-container
```

### Tests that skip instead of failing

Some proofs need infrastructure and **skip** without it — deliberately, so a machine without Docker
or Rust does not fail the build. Set the matching variable to turn a skip into a failure, which is
what CI does:

| Variable | Turns on | Needs |
|---|---|---|
| `BIFROST_REQUIRE_POSTGRES=1` | PostgreSQL persistence tests | Docker (Testcontainers) |
| `BIFROST_REQUIRE_CONTAINER=1` | CLI container isolation against a live runtime | Docker in **Linux**-container mode |
| `BIFROST_REQUIRE_WASI_HOST=1` | WASI tests against the real Rust host | `cargo build --release` in `spikes/wasi-component-runtime` |

> **Build the Rust release binary before running the .NET tests that need it.** The real-host tests
> take `target/release`; a stale build there makes them **hang** rather than fail, which is a much
> worse afternoon than a red test.

The integration tests spawn reference MCP servers (`tests/Bifrost.TestServers/*`) as real stdio and
HTTP processes. If a run is killed hard, one may survive and hold its port and its own executable —
the next build then fails with "used by another process" for a reason two runs old. Check for stray
processes before assuming the build is broken.

---

## The house rules

These are the ones that get changes sent back. They are unusual enough to be worth stating.

### 1. A proof, not an assertion

A work item is not done because it compiles and the tests are green. It is done when there is a
**reproducible proof** that it does what it claims. In particular:

- **A test that would pass if the feature were removed is not a test.** Where a guarantee matters,
  add the counter-proof: break the thing deliberately and show the *same* check goes red. The
  upgrade matrix does this ([`docs/upgrade-matrix.md`](../docs/upgrade-matrix.md) §3), and it is the
  reason its claims are believable.
- **A gate that has never been red is a claim about future behaviour.** If you add one, show it
  failing.
- **Verification must be tested in both directions.** A verification step that only ever confirms
  will also confirm nonsense.

This is not abstract. The first real release run of this project produced nine findings, one of
which was a proof that had **never worked on Linux** and had reported green the entire time. See
[`docs/plans/product-readiness-status.md`](../docs/plans/product-readiness-status.md) and
[the troubleshooting summary](../docs/en/troubleshooting.md#release-pipeline--nine-findings-from-the-first-real-run).

### 2. Fail closed, and loudly

The expensive failures in this project all looked like success: an empty volume that reported
"ready", a dead admin UI that rendered perfectly, a vulnerability gate that exited `0` while
reporting findings.

- Prefer refusing to start over starting in an unknown state.
- Prefer an explicit refusal over a silent fallback. If a configuration asks for a container and no
  runtime can enforce the policy, the upstream must not come up — falling back to the host would
  silently remove the property somebody chose the container for.
- If a protection cannot run, it must **say so**, not pass. A check that fails quietly is worse than
  no check.
- Never introduce a precedence rule between two sources of the same secret. Abort instead. A rule
  you have to look up is a rule somebody will misremember, and then they are running with the wrong
  secret.

### 3. Do not silently change behaviour for existing installations

New installations may get a stricter default. Existing ones keep their behaviour until an operator
decides otherwise, and the migration is written down, audited and visible in the diagnostic report.
See [ADR-0025](../docs/adr/0025-host-ausfuehrung-verbieten-und-bestehende-instanzen-migrieren.md).

If you cannot honour that, say so in the PR and let it be decided — do not decide it in the
implementation.

### 4. Secrets do not travel

- No credential in a log, ever. It ends up in every support ticket, every aggregator and every
  backup of the log directory — and it is the one place nobody rotates.
- No payloads in telemetry spans. The audit log is redacted; a telemetry backend is not.
- A passphrase is never a command-line argument. It would land in the process list and the shell
  history.
- Findings from the secret guardrail never contain the found value.

There are four identifiers that feed the DataProtection key derivation
(`src/Bifrost.Persistence/CryptographicNames.cs`). Renaming one makes **every stored ciphertext on
every existing installation unreadable, with no error at start**. A test pins them against their
values. If you make that test red, you have two options: revert, or write a migration that decrypts
and re-encrypts everything. Adjusting the test is not a third option, and the test says so.

### 5. Do not invent output

If you document a command, either run it and use the real output, or say plainly that it was not
run. Fabricated example output is how documentation stops being trustworthy — and once one block is
suspect, all of them are.

---

## Language policy

The rule, in full, is in [`docs/i18n.md`](../docs/i18n.md). The short version:

| Artefact | Language |
|---|---|
| Code, identifiers, code comments | **English** |
| Log messages, error messages, exception text | **German** (matching the existing code base) |
| UI strings | **German** |
| `README.md`, `SECURITY.md`, this file, `docs/en/**` | **English** |
| `docs/operations.md`, `docs/adr/**`, `docs/prd/**`, `docs/plans/**`, `CHANGELOG.md` | **German** |

**On contradiction between a German and an English version of the same thing, the version in the
document's source language wins.** Each page names which that is, in its header.

Contributing in English is fine, including for documentation. If you change an operational procedure
described in a German page, change the **German page first**; the English translation may follow in
the same PR or a later one, but it must not run ahead. An English page that claims something the
German original does not say is a bug, even when the claim is true.

If you do not read German and need to change German-documented behaviour, say so in the PR. Someone
will handle the German side. That is a smaller cost than a translation that quietly diverges.

---

## Commits and pull requests

**Conventional-commit prefixes**, matching the existing history:

```
feat(keyring): Schutz einrichtbar, Verlust erkennbar, FR-P048 erledigt
fix(release): Anhaenge suchen statt globben
docs(status): M3-Stand und der Abbruch von WP3.2
test(m0): der Nachweis fuer WP0.4
```

Types in use: `feat`, `fix`, `docs`, `test`, `build`, `chore`, `release`. The scope is the area, not
the file.

- **Subject lines in the existing history are German and use ASCII transliteration** for umlauts
  (`Anhaenge`, `laeuft`). **English subjects are welcome** for new contributions — do not machine-
  translate on our account.
- Write what changed **for someone using it**, not which method you touched.
- Keep a commit to one concern. A half-finished repo-wide rename is the worst possible thing to
  stop on, and this project has the scar to prove it.

### Pull requests

The [PR template](pull_request_template.md) asks four things, and they are the same four that get a
change sent back when they are missing: what changed, **what proves it**, what you deliberately did
not do, and whether operator-visible behaviour changed.

- Branch from `main`.
- Run `./build.sh verify-dotnet` (or `dotnet test Bifrost.slnx`) before pushing.
- CI runs CodeQL, dependency scans for NuGet and Cargo, Trivy against the image and the working
  tree, and gitleaks. **Critical/High blocks.** No gate is softened with `continue-on-error`, and
  there is no environment variable that relaxes one.
- If gitleaks goes red on a new test fixture, that is the process working. A human looks once and
  adds the path with a reason to `.github/gitleaks.toml`. There is deliberately **no blanket
  `tests/**` exclusion** — a real credential in a test file is just as much a leak, and test files
  are the *more* likely place because that is where people experiment against real systems.

### Overriding a security gate

Only explicitly, time-limited and documented in `.github/security-exceptions.yml`: named approver, a
reason of at least 40 characters, expiry at most 90 days out. An expired entry makes the run fail
rather than quietly lapsing — an entry that halts the run is an open task, a silently ineffective one
is litter.

> **Honest caveat:** `.github/CODEOWNERS` exists and names the Product Owner for the gates, the
> release pipeline, `CryptographicNames.cs` and the ADRs — but **CODEOWNERS on its own enforces
> nothing.** "Require review from Code Owners" has to be enabled in the branch protection rules for
> `main`, and that is a repository setting that cannot be shipped in the repository. Until it is
> enabled, the approval requirement is documented and **not enforced**.

---

## Documentation changes

Documentation is treated like code here, and the same house rules apply — especially
[rule 5](#5-do-not-invent-output).

- **Check every internal link you add.** Not by eye.
- **Mark unexecuted command blocks as unexecuted.** Do not show output you did not produce.
- **Do not paper over a gap.** If an instruction is only true after a code change, describe the gap
  and report it — do not write the instruction as if the change had happened. Several pages here
  carry sections titled "what this does not prove"; that is the standard, not an apology.
- **Do not fix a translation without reading the source-language original.** You will produce a
  third variant.
- Keep prose to roughly 100 columns, matching the existing files.

`docs/produktreife/` is not in the repository (it is gitignored and maintained externally). Do not
add it.

---

## Architecture decisions

ADRs live in [`docs/adr/`](../docs/adr/README.md), are **German**, and are **immutable time
capsules**: they record what was decided on a given date, with the context and the consequences
accepted at the time. They are not updated when reality moves on — a superseding ADR is written and
the old one is marked `Veraltet` with a cross-link.

Consequences for you:

- **Do not edit an existing ADR** to reflect a new decision. Editing one changes the stated reasoning
  of a decision other people made. `docs/adr/` is in CODEOWNERS for this reason.
- If your change contradicts an accepted ADR, that is a decision to be taken before the code, not a
  detail to be settled in review.
- A full English translation of the historical ADRs is **not** planned and is explicitly **not a 1.0
  blocker**. A stale translation of a decision reads like a different decision, which is worse than
  no translation. Where you need the operational consequence in English, it is in
  [`docs/en/`](../docs/en/README.md).

---

## Security

- **Never open a public issue for a security problem.**
  [Private vulnerability reporting](https://github.com/LupusMalusDeviant/bifrost/security/advisories/new),
  initial response within 7 days.
- Read [`SECURITY.md`](../SECURITY.md) before working on the pipeline, RBAC, audit, redaction or the
  connectors — it states what the design promises, and
  [`docs/en/security.md`](../docs/en/security.md) states what it deliberately does not.
- Assume everything from an upstream server is untrusted input, including tool names, descriptions
  and schemas. A tool description goes verbatim into a model's context and is therefore the most
  convenient place to inject instructions.

## Code of conduct

[`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md). Short version: be decent, argue about the work, and take
"here is the counter-proof" as the compliment it is.

## Licence

By contributing you agree that your contribution is licensed under the [MIT licence](../LICENSE),
the same as the rest of the project.
