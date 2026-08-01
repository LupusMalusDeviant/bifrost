# B.I.F.R.O.S.T — English documentation

> **Language and precedence.** The full design documentation lives in [`docs/`](../) and is written
> in **German**. These pages are the English core set for running the gateway and contributing to
> it. Where an English page is a translation, its header names the German original, and **the
> German original wins on contradiction**. The complete rule and the per-document table are in
> [`docs/i18n.md`](../i18n.md).

## Start here

| Page | For |
|---|---|
| [Quickstart](quickstart.md) | Getting a gateway running and one agent connected, from zero |
| [Operations](operations.md) | Running it: configuration, TLS, key ring, backup, upgrades, diagnostics |
| [Security](security.md) | The security model, the gates, and the limits you must know before exposing it |
| [Troubleshooting](troubleshooting.md) | Symptoms → causes, including everything the first real release run cost us |
| [Support and releases](support.md) | Supported versions, release channels, how to report a vulnerability |

## Tutorials

| Tutorial | Scenario |
|---|---|
| [Solo operator](tutorials/solo.md) | One person, one machine, SQLite, a handful of upstream servers |
| [Small team](tutorials/small-team.md) | Several people and agents, roles, per-agent keys, audit that answers questions |
| [Approval-gated deployment](tutorials/approval-deployment.md) | A write-capable tool behind an explicit human release |

## Where the rest lives

Everything below is German and has no English translation — deliberately. See
[`docs/i18n.md`](../i18n.md) for why, and why a full ADR translation is not a 1.0 blocker.

- [`docs/adr/`](../adr/README.md) — architecture decision records, the *why* behind the behaviour
- [`docs/prd/`](../prd/) — requirements (Lastenheft)
- [`docs/plans/`](../plans/) — implementation plans and milestone contracts (Pflichtenheft)
- [`docs/operations.md`](../operations.md) — the authoritative operations manual
- [`docs/security-gates.md`](../security-gates.md) — what blocks, when, and what proves it can block
- [`docs/upgrade-matrix.md`](../upgrade-matrix.md) — what the upgrade harness checks and what it does not
- [`docs/security/threat-model.md`](../security/threat-model.md) — findings, fixes, accepted residual risk
- [`docs/security/verifying-releases.md`](../security/verifying-releases.md) — verifying signatures and provenance yourself
- [`docs/gateway-cli.md`](../gateway-cli.md) / [`docs/cli-installation.md`](../cli-installation.md) — the `bifrost` CLI

## Contributing

[`.github/CONTRIBUTING.md`](../../.github/CONTRIBUTING.md) — English, and the authoritative version.
