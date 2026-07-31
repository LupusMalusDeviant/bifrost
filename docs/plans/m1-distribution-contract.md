# M1 — Eingefrorener Vertrag für Distribution und Supply Chain

**Stand:** 2026-07-31 · **Eingefroren durch:** Lead/Integrator · **Gilt für:** WP1.1 – WP1.5

Dieses Dokument ist die gemeinsame Grundlage der parallel arbeitenden Pakete. Namen, Pfade und
Kanäle stehen hier **fest**. Wer davon abweichen will, meldet das an den Lead, statt es zu ändern —
sonst bauen vier Pakete gegeneinander.

## 1. Container-Image

| Feld | Wert |
|---|---|
| Registry/Repository | `ghcr.io/lupusmalusdeviant/bifrost` |
| Architekturen | `linux/amd64`, `linux/arm64` |
| Tags je Release | `<version>` (z. B. `0.12.0`), `<major>.<minor>` (`0.12`), `sha-<kurz-sha>` |
| `latest` | **wird nicht gesetzt.** Erst ab dem ersten stabilen Release (FR-P007, WP1.1) |
| Basis | unverändert aus dem vorhandenen `Dockerfile` |

**OCI-Labels** (Pflicht, Werte aus dem Build-Kontext):
`org.opencontainers.image.source`, `.revision`, `.version`, `.licenses`, `.created`,
`.title`, `.description`.

## 2. CLI-Artefakte

| Feld | Wert |
|---|---|
| Runtime-IDs | `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` |
| Archivname | `bifrost-cli-<version>-<rid>.zip` (Windows) bzw. `.tar.gz` (Linux/macOS) |
| Enthält | die CLI-Executable `bifrost` (`bifrost.exe` unter Windows) plus `LICENSE` |
| Prüfsummen | eine gemeinsame `checksums.txt` (SHA-256, ein Eintrag je Archiv) |
| Versionsausgabe | `bifrost --version` nennt SemVer **und** Commit-SHA |

## 3. Versions- und Tag-Regel

- Einzige Quelle der Version ist `<VersionPrefix>` in `Directory.Build.props`.
- Ein Git-Tag `vX.Y.Z` **muss** dazu passen; Abweichung bricht den Release-Lauf ab.
- Pre-Release-Kennzeichnung wird aus dem SemVer-Suffix abgeleitet (`v0.12.0-rc.1` → Pre-Release).
- Der Releaselauf wird durch das **Tag** ausgelöst, nicht durch einen Push auf `main`.

## 4. Workflow-Zuschnitt und Dateibesitz

| Datei | Owner | Regel |
|---|---|---|
| `.github/workflows/release.yml` | **WP1.1** | Einziger Schreiber. Andere liefern Vorschläge als Datei oder Notiz |
| `Dockerfile` | **WP1.1** | – |
| `src/Bifrost.Cli/*.csproj`, CLI-Doku | **WP1.3** | – |
| `docker-compose*.yml`, `.env.example` | **WP1.4** | – |
| `docs/security/*`, Verifikationsanleitung | **WP1.2** | – |
| `.github/workflows/ci.yml` | **niemand in M1** | bleibt unverändert |
| `README.md`, `CHANGELOG.md` | **Lead** | Sammelstelle; Pakete liefern Textbausteine |

## 5. Job-Namen im Release-Workflow (feste Reihenfolge)

```
verify        → baut und testet aus dem getaggten Commit
image         → Multi-Arch-Build, Smoke, Push, gibt den Digest aus
cli           → publish je RID, Archive, checksums.txt
supply-chain  → SBOM, Provenance, Signatur; braucht Digest aus 'image'
release       → GitHub-Release anlegen, Artefakte anhängen
```

`supply-chain` und `release` laufen **nach** `image` und `cli`. Ein fehlgeschlagener Teil darf kein
halb veröffentlichtes Release hinterlassen (WP1.5).

## 6. Berechtigungen

Der Release-Workflow setzt Berechtigungen **je Job**, nicht global:

- `contents: write` nur im Job `release`;
- `packages: write` in den Jobs `image` **und `supply-chain`**;
- `id-token: write` und `attestations: write` nur im Job `supply-chain`;
- überall sonst `contents: read`.

> **Änderung am 2026-07-31 (Lead).** Ursprünglich stand `packages: write` allein beim Job `image`.
> WP1.2 hat die Abweichung gemeldet statt sie zu umgehen: Cosign legt die Signatur als eigenes
> Artefakt **in der Registry** ab (`sha256-<digest>.sig`), ebenso die Attestation mit
> `push-to-registry`. Ohne Schreibrecht kann `supply-chain` seine Aufgabe nicht erfüllen.
>
> Die Alternative wäre gewesen, das Signieren in den Job `image` zu ziehen — dann bräuchte `image`
> zusätzlich `id-token: write`, und die Trennung zwischen *Bauen* und *Bezeugen* fiele weg. Das ist
> der teurere Tausch: Ein Job, der baut **und** bezeugt, kann sich selbst beglaubigen.
>
> `supply-chain` bleibt auch mit dieser Ergänzung eng — insbesondere ohne `contents: write`.

Fremde Actions werden auf einen **Commit-SHA** gepinnt, nicht auf ein Tag.

## 7. Was in M1 ausdrücklich NICHT passiert

- kein `latest`-Tag;
- keine Änderung an `ci.yml`;
- keine Produktionscode-Änderung außer der CLI-Publish-Konfiguration;
- kein langlebiges Signatur-Secret (Signatur läuft keyless über OIDC);
- keine neue Abhängigkeit ohne Rückfrage beim Lead.

## 8. Grenze der lokalen Prüfbarkeit

Ein GitHub-Actions-Workflow lässt sich hier **nicht** ausführen. Was lokal geht: YAML-Gültigkeit,
`docker build`/`docker compose config`, `dotnet publish` je RID, Archiv- und Prüfsummenerzeugung.
Was nicht geht: Push nach GHCR, Attestations, Cosign-Signatur.

Deshalb gilt für M1: Jedes Paket benennt in seiner Abgabe ausdrücklich, **was geprüft wurde und was
erst der erste echte Tag-Lauf zeigt.** Eine Zusage ohne Lauf ist eine Annahme, keine Abnahme.
