# Releases selbst überprüfen

Stand: 2026-08-05 · gilt ab dem ersten Release, das der Job `supply-chain` erzeugt
(WP1.2, FR-P002/FR-P004).

> **Diese Anleitung ist gegen ein echtes Release gefahren worden** — `v0.12.0`, am 2026-08-05,
> einschließlich der Negativproben aus Abschnitt 5. Protokoll:
> [`docs/acceptance/release-verification-0.12.0.md`](../acceptance/release-verification-0.12.0.md).
> Dabei ist genau ein Fehler aufgefallen, und er stand in dieser Datei; er ist behoben.

Jedes veröffentlichte Artefakt von B.I.F.R.O.S.T ist signiert und trägt eine Herkunftsbescheinigung.
Sie brauchen dafür **keinen Schlüssel von uns** — die Prüfung läuft gegen die öffentliche
Sigstore-Infrastruktur und gegen GitHub.

> **Lesen Sie [Abschnitt 5](#5-der-wichtigste-teil-prüfen-sie-dass-die-prüfung-auch-ablehnt).**
> Ein grüner Haken sagt erst dann etwas aus, wenn Sie einmal gesehen haben, dass dasselbe Werkzeug
> beim Falschen rot wird.

---

## 0. Was Sie brauchen

| Werkzeug | Wofür | Installation |
|---|---|---|
| [`cosign`](https://github.com/sigstore/cosign) ≥ 3.0 | Signaturen prüfen | `brew install cosign` · `winget install sigstore.cosign` · [Releases](https://github.com/sigstore/cosign/releases) |
| [`gh`](https://cli.github.com/) ≥ 2.60 | Herkunft (Provenance/SBOM) prüfen | `brew install gh` · `winget install GitHub.cli` |
| `sha256sum` | Prüfsummen der CLI-Archive | Linux/macOS vorhanden; Windows: `certutil -hashfile <datei> SHA256` |
| `docker` oder `crane` | nur zum Auflösen des Digests | – |

Zwei Werte tauchen in jedem Befehl auf. Setzen Sie sie einmal:

```bash
IMAGE=ghcr.io/lupusmalusdeviant/bifrost
VERSION=0.12.0          # die Version, die Sie prüfen wollen

# Die Herkunft, gegen die geprueft wird. Genau diese Zeichenkette steht auch im
# Release-Workflow des Projekts — sie bindet die Signatur an Repository, Workflow-Datei
# und Tag. Bitte NICHT lockern (z.B. auf ".*"): dann prueft der Befehl nichts mehr.
IDENTITY='^https://github\.com/LupusMalusDeviant/bifrost/\.github/workflows/release\.yml@refs/tags/v'
ISSUER=https://token.actions.githubusercontent.com
```

PowerShell-Fassung:

```powershell
$IMAGE   = "ghcr.io/lupusmalusdeviant/bifrost"
$VERSION = "0.12.0"
$IDENTITY = '^https://github\.com/LupusMalusDeviant/bifrost/\.github/workflows/release\.yml@refs/tags/v'
$ISSUER  = "https://token.actions.githubusercontent.com"
```

---

## 1. Den Digest ermitteln — und danach nur noch ihn benutzen

Ein Tag (`:0.12.0`) kann umgehängt werden. Ein Digest (`@sha256:...`) nicht. **Alle folgenden
Befehle arbeiten mit dem Digest.**

```bash
DIGEST=$(docker buildx imagetools inspect "$IMAGE:$VERSION" --format '{{ .Manifest.Digest }}')
echo "$DIGEST"
# -> sha256:....
```

Ohne Docker, mit [`crane`](https://github.com/google/go-containerregistry):

```bash
DIGEST=$(crane digest "$IMAGE:$VERSION")
```

> **Wichtig:** Das ist der Digest des **Multi-Arch-Index**. Genau dieser ist signiert. Die
> Digests der einzelnen Architekturen darunter sind es *nicht* — siehe
> [Abschnitt 5, Probe B](#probe-b-ein-echter-aber-nicht-signierter-digest).

---

## 2. Das Container-Image prüfen

### 2.1 Signatur (Cosign, keyless)

```bash
cosign verify \
  --certificate-identity-regexp "$IDENTITY" \
  --certificate-oidc-issuer "$ISSUER" \
  "$IMAGE@$DIGEST"
```

Erfolg: Cosign gibt die geprüften Claims als JSON aus und endet mit **Exit-Code 0**. Für das
signierte Image erscheinen dabei drei Einträge — die Signatur selbst, die Stückliste und die
Herkunftsbescheinigung.

> **Der Exit-Code ist die Aussage, nicht der Text.** Ob Repository, Tag und Commit stimmen, hat der
> Befehl bereits entschieden: Genau das steht in `$IDENTITY`, und passt es nicht, endet er mit
> einem Fehler und nennt die tatsächliche Identität ([Probe A](#probe-a-fremde-signierer-identität)
> zeigt das). Ein Auge auf die Ausgabe ersetzt diese Prüfung nicht — es wiederholt sie nur.

**Wer die Werte trotzdem sehen will**, holt sie aus der Herkunftsbescheinigung. Sie ist signiert und
an denselben Digest gebunden:

```bash
gh attestation verify "oci://$IMAGE@$DIGEST" \
  --repo LupusMalusDeviant/bifrost \
  --signer-workflow LupusMalusDeviant/bifrost/.github/workflows/release.yml \
  --format json \
| jq -r '[.[].verificationResult.statement
          | select(.predicateType == "https://slsa.dev/provenance/v1")][0]
         | .predicate.buildDefinition
         | "Repo:   \(.externalParameters.workflow.repository)\nTag:    \(.externalParameters.workflow.ref)\nCommit: \(.resolvedDependencies[0].digest.gitCommit)"'
```

```
Repo:   https://github.com/LupusMalusDeviant/bifrost
Tag:    refs/tags/v0.12.0
Commit: 71e4acfb007beb2aba39c4fa1e509f199cdee101
```

> **Warum nicht aus der Cosign-Ausgabe.** Bis einschließlich dieser Anleitung stand hier ein
> `jq`-Aufruf auf `.[0].optional`. Unter cosign 3 — der Fassung, die der Releaseworkflow selbst
> festnagelt — ist dieses Feld leer; der Aufruf lieferte `Repo: null` und zwei Striche. Aufgefallen
> ist das erst, als die Anleitung zum ersten Mal jemand gegen ein echtes Release gefahren hat
> (BFR-F-0055). Die Signaturprüfung war davon nie betroffen — die Anleitung war es.

### 2.2 Herkunft (GitHub Artifact Attestation)

```bash
gh attestation verify "oci://$IMAGE@$DIGEST" \
  --repo LupusMalusDeviant/bifrost \
  --signer-workflow LupusMalusDeviant/bifrost/.github/workflows/release.yml
```

`--signer-workflow` ist der strengere Weg: er verlangt, dass die Bescheinigung aus *genau dieser*
Workflow-Datei stammt, nicht bloß aus irgendeinem Workflow des Repositorys. Nehmen Sie ihn.

> **In einem Skript schweigt der Befehl bei Erfolg.** Die Bestätigung geht nur an ein Terminal; im
> Skript oder in der CI bleibt die Ausgabe leer und der Exit-Code ist `0`. Leere Ausgabe heißt hier
> also nicht „nichts geprüft", sondern „nichts zu beanstanden" — prüfen Sie den Exit-Code.

### 2.3 Stückliste (SBOM)

Die SBOM liegt als Release-Asset (`bifrost-<version>-image.cdx.json`, CycloneDX 1.7) **und** als
Attestation am Image. Die Attestation ist der belastbare Weg — sie bindet die Stückliste an den
Digest:

```bash
gh attestation verify "oci://$IMAGE@$DIGEST" \
  --repo LupusMalusDeviant/bifrost \
  --format json \
| jq -r '.[].verificationResult.statement.predicateType'
```

Suchen Sie in der Ausgabe den CycloneDX-Prädikat-Typ. Die SBOM selbst holen Sie so heraus:

```bash
gh attestation verify "oci://$IMAGE@$DIGEST" \
  --repo LupusMalusDeviant/bifrost \
  --format json \
| jq '[.[].verificationResult.statement | select(.predicateType | test("cyclonedx"; "i")) | .predicate][0]' \
> bifrost-image.cdx.json
```

Prüfen, ob ein bestimmtes Paket enthalten ist:

```bash
jq -r '.components[] | select(.name | test("openssl"; "i")) | "\(.name) \(.version)"' bifrost-image.cdx.json
```

### 2.4 Erst danach ziehen

```bash
docker pull "$IMAGE@$DIGEST"
```

Ziehen Sie im Betrieb **immer über den Digest**, nicht über den Tag. Nur dann läuft morgen dasselbe
wie heute — und nur dann gilt die Signatur, die Sie eben geprüft haben.

---

## 3. Die CLI-Artefakte prüfen

Laden Sie vom GitHub-Release herunter:

- das gewünschte Archiv, z. B. `bifrost-cli-0.12.0-linux-x64.tar.gz`
  (Windows: `bifrost-cli-0.12.0-win-x64.zip`),
- `checksums.txt`,
- `checksums.txt.cosign.bundle`.

Die Vertrauenskette läuft in dieser Reihenfolge:
**Signatur → `checksums.txt` → einzelnes Archiv.** Beide Schritte sind nötig; der erste allein sagt
nichts über Ihr Archiv, der zweite allein nichts über die Herkunft.

### Schritt 1 — Signatur der Prüfsummendatei

```bash
cosign verify-blob \
  --bundle checksums.txt.cosign.bundle \
  --certificate-identity-regexp "$IDENTITY" \
  --certificate-oidc-issuer "$ISSUER" \
  checksums.txt
```

Erwartete Ausgabe: `Verified OK`, Exit-Code 0.

### Schritt 2 — Prüfsumme Ihres Archivs

```bash
sha256sum --check --ignore-missing checksums.txt
# -> bifrost-cli-0.12.0-linux-x64.tar.gz: OK
```

`--ignore-missing` sorgt dafür, dass die Datei auch dann durchläuft, wenn Sie nur eines der fünf
Archive heruntergeladen haben.

Windows (PowerShell):

```powershell
$erwartet = (Select-String -Path checksums.txt -Pattern "bifrost-cli-$VERSION-win-x64.zip").Line.Split()[0]
$ist      = (Get-FileHash "bifrost-cli-$VERSION-win-x64.zip" -Algorithm SHA256).Hash.ToLower()
if ($erwartet -eq $ist) { "OK" } else { "ABWEICHUNG — nicht verwenden" }
```

### Schritt 3 — Herkunft des Archivs

```bash
gh attestation verify "bifrost-cli-$VERSION-linux-x64.tar.gz" \
  --repo LupusMalusDeviant/bifrost \
  --signer-workflow LupusMalusDeviant/bifrost/.github/workflows/release.yml
```

### Schritt 4 — Version gegenprüfen

```bash
tar -xzf "bifrost-cli-$VERSION-linux-x64.tar.gz"
./bifrost --version
# nennt SemVer UND Commit-SHA.
```

Der Commit muss dem entsprechen, den die Herkunftsbescheinigung des Images nennt
([Abschnitt 2.1](#21-signatur-cosign-keyless)). Gegenprobe ohne `gh`, direkt am Image:

```bash
docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' \
  "$IMAGE@$DIGEST"
# -> 71e4acfb007beb2aba39c4fa1e509f199cdee101
```

Beide Wege müssen denselben Commit nennen wie `bifrost --version`. Tun sie es nicht, gehören CLI und
Image nicht zusammen.

---

## 4. Alles in einem Durchgang

Zum Kopieren — bricht beim ersten Fehler ab:

```bash
#!/usr/bin/env bash
set -euo pipefail

IMAGE=ghcr.io/lupusmalusdeviant/bifrost
VERSION="${1:?Aufruf: $0 <version>, z.B. 0.12.0}"
REPO=LupusMalusDeviant/bifrost
IDENTITY='^https://github\.com/LupusMalusDeviant/bifrost/\.github/workflows/release\.yml@refs/tags/v'
ISSUER=https://token.actions.githubusercontent.com

DIGEST=$(docker buildx imagetools inspect "$IMAGE:$VERSION" --format '{{ .Manifest.Digest }}')
echo "Digest: $DIGEST"

echo "== 1/4 Cosign-Signatur des Images =="
cosign verify --certificate-identity-regexp "$IDENTITY" \
              --certificate-oidc-issuer "$ISSUER" "$IMAGE@$DIGEST" > /dev/null
echo "   ok"

echo "== 2/4 Herkunft des Images =="
gh attestation verify "oci://$IMAGE@$DIGEST" --repo "$REPO" \
   --signer-workflow "$REPO/.github/workflows/release.yml" > /dev/null
echo "   ok"

if [ -f checksums.txt ] && [ -f checksums.txt.cosign.bundle ]; then
  echo "== 3/4 Signatur der Pruefsummendatei =="
  cosign verify-blob --bundle checksums.txt.cosign.bundle \
                     --certificate-identity-regexp "$IDENTITY" \
                     --certificate-oidc-issuer "$ISSUER" checksums.txt
  echo "== 4/4 Pruefsummen der vorhandenen Archive =="
  sha256sum --check --ignore-missing checksums.txt
else
  echo "== 3/4 + 4/4 uebersprungen: checksums.txt bzw. Bundle nicht im Verzeichnis =="
fi

echo
echo "Verifiziert. Im Betrieb bitte per Digest ziehen:"
echo "  docker pull $IMAGE@$DIGEST"
```

---

## 5. Der wichtigste Teil: Prüfen Sie, dass die Prüfung auch ablehnt

Bis hierher haben Sie gesehen, dass die Befehle **gelingen**. Das ist die halbe Aussage. Ein Werkzeug,
das immer „OK" sagt, sagt nichts. Führen Sie die folgenden drei Proben einmal aus — sie **müssen
fehlschlagen**. Erst dann wissen Sie, dass Ihr grüner Durchlauf aus Abschnitt 4 etwas bedeutet.

Wenn eine dieser Proben bei Ihnen **erfolgreich** ist, stimmt etwas Grundlegendes nicht: Ihr
Werkzeug, Ihre Kommandozeile oder das, was Sie geladen haben. Verwenden Sie das Artefakt dann nicht.

### Probe A: fremde Signierer-Identität

Richtiges Image, richtiger Digest — aber wir behaupten, es käme aus einem anderen Repository.

```bash
cosign verify \
  --certificate-identity-regexp '^https://github\.com/example-org/nicht-bifrost/' \
  --certificate-oidc-issuer "$ISSUER" \
  "$IMAGE@$DIGEST"
echo "Exit-Code: $?"
```

**Erwartet:** Fehlermeldung in der Art `none of the given identities matched what was in the
certificate`, Exit-Code ungleich 0.
**Belegt:** Die Signatur ist an *dieses* Repository und *diese* Workflow-Datei gebunden. Sie sagt
nicht bloß „irgendjemand hat mit Sigstore signiert".

### Probe B: ein echter, aber nicht signierter Digest

Signiert ist der Multi-Arch-Index. Die Architektur-Manifeste darunter gehören zum selben Release,
tragen aber keine eigene Signatur. Genau das würde ein Angreifer probieren.

```bash
CHILD=$(docker buildx imagetools inspect "$IMAGE@$DIGEST" --raw \
        | jq -r '.manifests[] | select(.platform.architecture=="amd64" and .platform.os=="linux") | .digest')
echo "Untermanifest: $CHILD"

cosign verify \
  --certificate-identity-regexp "$IDENTITY" \
  --certificate-oidc-issuer "$ISSUER" \
  "$IMAGE@$CHILD"
echo "Exit-Code: $?"
```

**Erwartet:** `Error: no signatures found`, **Exit-Code 10**.
**Belegt:** Ein Digest, der zwar existiert und dazugehört, aber nicht Gegenstand der Signatur ist,
wird nicht akzeptiert. Deshalb prüfen und ziehen Sie **immer** über den Index-Digest aus
Abschnitt 1.

### Probe C: verändertes Archiv

```bash
mkdir -p /tmp/negativprobe && cd /tmp/negativprobe
cp ~/Downloads/"bifrost-cli-$VERSION-linux-x64.tar.gz" .
cp ~/Downloads/checksums.txt .
printf 'ein einziges zusaetzliches Byte' >> "bifrost-cli-$VERSION-linux-x64.tar.gz"

sha256sum --check --ignore-missing checksums.txt
echo "Exit-Code: $?"
```

**Erwartet:** `bifrost-cli-…tar.gz: FAILED` und `WARNING: 1 computed checksum did NOT match`,
Exit-Code ungleich 0.
**Belegt:** Die Prüfung hängt am Inhalt, nicht am Dateinamen.

> Dieselben vier Proben laufen bei jedem Release automatisch im Job `supply-chain`. Lehnt eine davon
> *nicht* ab, wird kein Release veröffentlicht. Details:
> [supply-chain.md, Abschnitt 5](supply-chain.md#5-der-negative-nachweis).

---

## 6. Wenn eine Prüfung fehlschlägt

| Meldung | Wahrscheinliche Ursache | Was tun |
|---|---|---|
| `no signatures found` | Sie prüfen gegen ein Untermanifest oder einen anderen Digest | Digest nach Abschnitt 1 neu ermitteln |
| `none of the given identities matched` | Tippfehler in `$IDENTITY`, oder das Artefakt stammt nicht von uns | Zeichenkette aus Abschnitt 0 kopieren, nicht abtippen; danach melden |
| `MANIFEST_UNKNOWN` | Digest existiert nicht in dieser Registry | Version/Registry prüfen |
| `no attestations found` | Artefakt nicht aus einem Release-Lauf, oder falsches `--repo` | `--repo LupusMalusDeviant/bifrost` prüfen |
| `sha256sum: … FAILED` | Download unvollständig oder verändert | Neu laden. Bleibt es dabei: **nicht verwenden** und melden |

**Meldung:** Wenn Signatur oder Herkunft eines echten Release-Artefakts nicht verifizierbar sind,
öffnen Sie bitte **kein öffentliches Issue**, sondern nutzen Sie den Weg aus
[SECURITY.md](../../SECURITY.md) (private Vulnerability-Meldung). Ein Verifikationsfehler kann
harmlos sein — er kann aber auch bedeuten, dass jemand etwas untergeschoben hat.

---

## 7. Grenzen — was diese Prüfung nicht sagt

Damit klar ist, was Sie in der Hand haben:

- **Sie sagt nichts über die Qualität des Inhalts.** Verifiziert heißt: „das kommt von uns, aus
  diesem Commit, so gebaut". Es heißt nicht „fehlerfrei" und nicht „frei von Schwachstellen".
  (Wir blockieren Releases bei Critical/High-Funden — das ist ein Filter, keine Garantie.)
- **Der Build ist nicht reproduzierbar.** Sie können nicht nachbauen und Bit für Bit vergleichen.
  Basis-Image, NuGet-Restore und `apt` sind zeitabhängig.
- **Sie ersetzt nicht das Härten Ihrer Installation.** Was ein Gateway an Vertrauen bündelt, steht
  in [SECURITY.md](../../SECURITY.md) — insbesondere, dass stdio-Upstreams mit den Rechten des
  Gateways laufen.
- **Die Angaben in diesem Dokument gelten ab dem ersten Release, das den Job `supply-chain`
  durchlaufen hat.** Ältere Vorab-Versionen (`v0.11.0` und früher) tragen weder Signatur noch
  Attestation; für sie gibt es nichts zu prüfen.
