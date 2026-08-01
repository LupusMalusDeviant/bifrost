<!--
Thanks for the change. Four questions below; the third and fourth are the ones that
usually decide whether a PR goes in as-is. See .github/CONTRIBUTING.md for the house rules.

Security problems do not go here or in an issue — use private vulnerability reporting.
-->

## What changed

<!-- What this does for someone using it, not which methods you touched. -->

Closes #

## What proves it

<!--
A work item is not done because it compiles and the tests are green. Name the reproducible proof.

Strong answers:
  - "New test X; removing the fix makes it red (checked)."
  - "Counter-proof included: Y deliberately broken, same assertion goes red."
  - "Ran `./build.sh verify-dotnet`: N tests, 0 failures."
  - "Command in the docs was executed; the output in the block is the real output."

Weak answers, which will get asked about:
  - "Existing tests still pass."  (they would also pass with the feature removed)
  - "Manually verified."          (how, on what, with what result?)

If a test would pass with your change removed, it is not a test for your change.
-->

- [ ] `dotnet test Bifrost.slnx` (or `./build.sh verify-dotnet`) run locally — result:
- [ ] Rust touched? `./build.sh verify-rust` run — result:
- [ ] Any new gate or check has been observed **failing** as well as passing

## What I deliberately did not do

<!--
Findings outside your scope, follow-ups, known remaining gaps. This section is not an admission,
it is the most useful part of the PR: several of this project's worst defects were things somebody
noticed and did not write down. "Found in src/X, not touched" is a complete entry.
-->

## Operator-visible behaviour

- [ ] **No** change to behaviour for existing installations
- [ ] Behaviour changes for **new** installations only — existing ones keep theirs, and the
      migration is written down / audited / visible in the diagnostic report
- [ ] Behaviour changes for **existing** installations — described below, and flagged for a decision

<!--
This project does not silently change behaviour on an existing install. If your change would,
say so and let it be decided rather than deciding it in the implementation (ADR-0025).
-->

## Documentation

- [ ] No documentation change needed
- [ ] Documentation updated — **source-language page first** (`docs/i18n.md` says which that is)
- [ ] Every internal link I added was actually checked, not eyeballed
- [ ] Every command block I added was either **executed** (real output) or marked as not executed —
      **no invented output**
- [ ] `CHANGELOG.md` updated under `[Unveröffentlicht]` if an operator would notice this

## Secrets and telemetry

<!-- Only relevant if you touched logging, audit, telemetry, config or the connectors. -->

- [ ] No credential can reach a log, a span, a command line, or an error message
- [ ] Nothing changes the four DataProtection identifiers in
      `src/Bifrost.Persistence/CryptographicNames.cs` (renaming one makes every stored ciphertext on
      every existing installation unreadable, with no error at start)
