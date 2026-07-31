# Supply Chain — SBOM, Provenance und Signatur

Stand: 2026-07-31 · Arbeitspaket **WP1.2** · Anforderungen **FR-P002**, **FR-P004** ·
Ergänzt [SECURITY.md](../../SECURITY.md) und den
[eingefrorenen Distributionsvertrag](../plans/m1-distribution-contract.md).

Dieses Dokument begründet **das Verfahren**. Die kopierbaren Befehle für Anwender stehen in
[verifying-releases.md](verifying-releases.md), der übernehmbare Workflow-Block in
[`docs/plans/m1-wp12-workflow-snippet.yml`](../plans/m1-wp12-workflow-snippet.yml).

## 1. Was hier eigentlich behauptet wird

Ein Release besteht aus einem Container-Image und fünf CLI-Archiven. Ohne weitere Maßnahmen kann
ein Anwender genau zwei Dinge feststellen: dass sich etwas herunterladen lässt, und dass es startet.
Er kann **nicht** feststellen, ob es aus diesem Repository stammt, ob es aus dem Commit gebaut wurde,
der behauptet wird, und was darin steckt.

Ziel von WP1.2 sind drei überprüfbare Aussagen — überprüfbar **ohne** projekteigenen Schlüssel:

| Aussage | Mechanismus | Was sie *nicht* sagt |
|---|---|---|
| „Das stammt aus diesem Repo, aus diesem Workflow, von diesem Tag." | Cosign keyless (OIDC) + GitHub Artifact Attestations | Dass der Inhalt frei von Fehlern ist |
| „So wurde es gebaut." | SLSA-Build-Provenance | Dass der Build reproduzierbar ist (ist er nicht — siehe §7) |
| „Das steckt drin." | CycloneDX-SBOM, an den Digest gebunden | Dass die SBOM vollständig ist (siehe §2.3) |

Der Anker ist immer der **Digest**, nie ein Tag. Ein Tag lässt sich umhängen, ein Digest nicht.

## 2. SBOM-Format: CycloneDX

**Entscheidung: CycloneDX 1.7 (JSON), erzeugt mit Syft. Kein zweites Format.**

### 2.1 Warum CycloneDX und nicht SPDX

Beide Formate erfüllen die Anforderung; `actions/attest` akzeptiert beide, Trivy und Grype lesen
beide. Ausschlaggebend war ein projektspezifischer Punkt:

- **VEX gehört zum selben Standard.** Das Pflichtenheft verlangt einen Ausnahmeprozess für
  Critical/High-Funde (§4). Mit CycloneDX ist eine Ausnahme ein maschinenlesbares
  VEX-Statement im selben Ökosystem statt einer Notiz in einem Wiki, die niemand ausliest.
  SPDX kennt kein gleichwertiges, verbreitet unterstütztes Gegenstück.
- **Der Anwendungsfall ist Analyse, nicht Lizenz-Compliance.** SPDX' Stärke ist die
  juristisch belastbare Lizenzaussage. B.I.F.R.O.S.T ist ein Sicherheitsbauteil; die Frage, die
  ein Betreiber stellt, lautet „bin ich von CVE-X betroffen", nicht „welche Lizenzen liegen bei".
- **Ein Format, nicht zwei.** Zwei Stücklisten desselben Artefakts sind zwei Dinge, die
  auseinanderlaufen können. Wer SPDX braucht, konvertiert mit einem Syft-Aufruf
  (`syft convert <datei>.cdx.json -o spdx-json`) — das ist billiger als eine zweite
  Lieferkette zu pflegen.

### 2.2 Umfang

Je Release entstehen sechs SBOMs:

| Datei | Gegenstand |
|---|---|
| `bifrost-<version>-image.cdx.json` | das Image, erfasst über `<image>@<digest>` |
| `bifrost-cli-<version>-<rid>.cdx.json` | je Runtime-ID (`win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`) |

Die Image-SBOM wird über den **Digest** erzeugt, nicht über das Tag. Der Workflow bricht ab, wenn
der signierte Digest nicht in der erzeugten SBOM auftaucht — sonst könnte eine Stückliste ein
anderes Image beschreiben als das signierte, und niemand würde es merken.

### 2.3 Was die SBOM nicht leistet — nachgemessen, nicht geschätzt

Am 2026-07-31 wurde Syft 1.50.0 lokal gegen das vorhandene Image `mcpmcp:latest` ausgeführt
(CycloneDX 1.7, 1,37 MB). Ergebnis:

| Komponententyp | Anzahl |
|---|---:|
| `file` (Dateieinträge ohne purl) | 3673 |
| `deb` (Ubuntu 24.04 Basis) | 97 |
| `dotnet` (NuGet/Assemblies) | 29 |
| `binary` (u. a. `e_sqlite3`) | 3 |
| `operating-system` | 1 |

Daraus folgen zwei ehrliche Einschränkungen:

- **Die 29 `dotnet`-Einträge sind die im Image liegenden Assemblies, nicht der aufgelöste
  NuGet-Graph.** Transitive Pakete, die im Publish-Output zu einer einzigen Assembly
  zusammenfallen, erscheinen nicht als eigene Komponente. Wer den vollständigen Paketgraph
  braucht, muss ihn aus `packages.lock.json` bzw. dem Restore-Graph ziehen — das ist eine
  mögliche Erweiterung, aber keine Zusage dieses Arbeitspakets.
- **Der WASI-Host ist ein Rust-Binary.** Syft erkennt ihn als `binary`-Komponente; die Rust-Crates
  darin stehen nicht einzeln in der Liste, solange das Binary ohne Cargo-Metadaten gebaut wird.
  Für den Crate-Graph wäre ein zusätzlicher `cargo`-Scan über
  `spikes/wasi-component-runtime/Cargo.lock` nötig. **Offen, siehe §8.**

## 3. Provenance und Signatur

### 3.1 Zwei Nachweise, absichtlich nebeneinander

| Nachweis | Werkzeug beim Anwender | Wo liegt er |
|---|---|---|
| GitHub Artifact Attestation (SLSA-Provenance + SBOM) | `gh attestation verify` | GitHub-Attestations-API; zusätzlich als OCI-Referrer beim Image |
| Cosign-Signatur | `cosign verify` / `cosign verify-blob` | Registry (`sha256-<digest>.sig`) bzw. Bundle-Datei im Release |

Das ist bewusst redundant. Wer bereits `gh` installiert hat, braucht nichts weiter. Wer in einer
Kubernetes-Policy (Kyverno, Sigstore-Policy-Controller) prüft, braucht Cosign. Beide Wege führen
zur selben Sigstore-Vertrauenswurzel, keiner verlangt einen projekteigenen Schlüssel.

### 3.2 Keyless — warum es kein Secret gibt

Der Workflow erhält von GitHub ein **kurzlebiges OIDC-Token** (`id-token: write`). Cosign tauscht
es bei Fulcio gegen ein Zertifikat, das wenige Minuten gültig ist und die Herkunft im Zertifikat
festhält: Repository, Workflow-Datei, Referenz (`refs/tags/vX.Y.Z`), Commit. Der private Schlüssel
existiert nur im Arbeitsspeicher des Runners und wird nicht abgelegt. Die Signatur landet zusammen
mit dem Zertifikat im öffentlichen Transparenzlog Rekor.

Damit ist Vertrag §7 („kein langlebiges Signatur-Secret") nicht nur eingehalten, sondern strukturell
erfüllt: **es gibt keinen Schlüssel, den man stehlen könnte.** Ein Angreifer müsste stattdessen einen
Workflow-Lauf in genau diesem Repository unter genau diesem Pfad auslösen — und das stünde
unlöschbar im Transparenzlog.

Die geprüfte Identität lautet:

```
^https://github\.com/LupusMalusDeviant/bifrost/\.github/workflows/release\.yml@refs/tags/v
```

Diese Zeichenkette steht an zwei Stellen: im Workflow (Selbsttest) und in
[verifying-releases.md](verifying-releases.md). Wer die Workflow-Datei umbenennt, muss beide ändern
— sonst schlägt der Selbsttest fehl, bevor ein Anwender es merkt. Das ist Absicht.

### 3.3 Was signiert wird

- **Image:** der Digest des **Multi-Arch-Index**. Die Architektur-Manifeste darunter tragen keine
  eigene Signatur. Anwender müssen gegen den Index-Digest prüfen — genau das ist die Grundlage der
  Negativprobe N2 (§5).
- **CLI:** nicht jedes Archiv einzeln, sondern **`checksums.txt`**. Die Datei enthält die SHA-256
  aller fünf Archive; damit hängt jedes Archiv an derselben Signatur. Ein Anwender braucht zwei
  Befehle statt zehn und übersieht keinen. Die Vertrauenskette lautet:
  `Cosign-Signatur → checksums.txt → einzelnes Archiv`.
  Zusätzlich existiert für jedes Archiv eine eigene GitHub-Provenance-Attestation (über
  `subject-checksums`), sodass auch der Weg ohne `checksums.txt` offen bleibt.

## 4. Schwachstellen-Gate und Ausnahmeprozess

### 4.1 Das Gate

Trivy prüft im Job `supply-chain` zweimal und blockiert jedes Mal:

1. das Image über seinen Digest;
2. die CLI-Artefakte über ihre SBOMs — nicht über das Verzeichnis. Damit prüft das Gate exakt die
   Stückliste, die veröffentlicht wird. Weichen SBOM und Artefakt voneinander ab, fällt es hier auf.

Einstellungen: `--severity CRITICAL,HIGH`, `--exit-code 1`, **`ignore-unfixed=false`**.

Das letzte ist die unbequeme Entscheidung. Ein Fund ohne verfügbaren Fix bleibt ein Fund. Ihn per
Schalter auszublenden würde das Gate leise aushöhlen; stattdessen muss er durch die Ausnahmeliste —
sichtbar und befristet.

### 4.2 Ausnahmen — nur Product Owner, nur befristet, nur dokumentiert

Ausnahmen werden **ausschließlich** in `.trivyignore.yaml` im Repository-Wurzelverzeichnis
geführt. Kein Workflow-Schalter, keine Umgebungsvariable, kein `continue-on-error`.

```yaml
# .trivyignore.yaml — Ausnahmen zum Critical/High-Gate (WP1.2)
# Jeder Eintrag braucht Begruendung, Freigebenden und Ablaufdatum. Ohne expired_at
# wird der Eintrag nicht akzeptiert (Pruefung im Review, siehe unten).
vulnerabilities:
  - id: CVE-0000-00000
    statement: >-
      Nicht erreichbar: die betroffene Funktion wird von B.I.F.R.O.S.T nicht aufgerufen
      (Nachweis: <Link auf Issue/Analyse>). Freigabe: <PO-Name>, <Datum>.
    expired_at: 2026-10-31
```

Regeln:

1. **Nur der Product Owner gibt frei.** Technisch abgesichert über CODEOWNERS auf
   `.trivyignore.yaml` — die Datei kann ohne PO-Review nicht nach `main`.
2. **`expired_at` ist Pflicht und liegt höchstens 90 Tage in der Zukunft.** Läuft der Eintrag ab,
   greift das Gate von selbst wieder. Eine Ausnahme, die nicht von allein verfällt, ist keine
   Ausnahme, sondern eine stille Absenkung der Anforderung.
3. **`statement` nennt die Begründung, den Freigebenden und das Datum.** „Blockiert das Release"
   ist keine Begründung.
4. **Verlängerung nur mit neuer Begründung**, nicht durch Datum-Hochschieben im selben Commit.
5. Jede aktive Ausnahme wird in den Release Notes des betroffenen Releases genannt (FR-P008).

> **Noch anzulegen:** `.trivyignore.yaml` und der CODEOWNERS-Eintrag liegen außerhalb der Dateizone
> von WP1.2 und sind hier bewusst **nicht** erstellt worden. Solange keine Ausnahme existiert,
> braucht es die Datei auch nicht — Trivy verträgt einen fehlenden `trivyignores`-Pfad. Der Lead
> legt beides an, sobald die erste Ausnahme beantragt wird.

## 5. Der negative Nachweis

**Eine Verifikation, die immer gelingt, beweist nichts.** Der Beleg entsteht erst dort, wo das
Falsche nachweislich durchfällt (Lastenheft, Leitsatz 6: „Jede Sicherheitszusage braucht einen
negativen Regressionstest").

Der Workflow führt deshalb nach dem Signieren vier Negativproben aus. Schlägt eine davon *nicht*
fehl, bricht der Job ab und es entsteht kein Release.

| # | Probe | Erwartetes Verhalten | Was sie belegt |
|---|---|---|---|
| **N1** | `cosign verify` mit dem **richtigen** Digest, aber einer **fremden Signierer-Identität** | Ablehnung | Die Signatur ist an dieses Repo und diesen Workflow gebunden — nicht bloß „irgendwie von Sigstore" |
| **N2** | `cosign verify` gegen das **amd64-Untermanifest** des signierten Index | Ablehnung („no signatures found") | Ein echter, existierender, aber nicht signierter Digest wird nicht akzeptiert. Kein erfundener Hash — der Fall, den ein Angreifer tatsächlich versuchen würde |
| **N3** | `sha256sum --check` gegen ein um ein Byte **verändertes Archiv** | Ablehnung | Die Prüfsummenkette hängt am Inhalt, nicht am Dateinamen |
| **N4** | `gh attestation verify --repo actions/checkout` gegen unser Image | Ablehnung | Die Attestation trägt die Herkunft und lässt sich nicht auf ein fremdes Repo umdeuten |

N2 ist die interessanteste Probe: Sie verwendet einen Digest, den es wirklich gibt und der wirklich
zu diesem Release gehört — er ist nur nicht der signierte. Ein Test gegen einen zusammengewürfelten
Hash wäre schwächer, weil er schon beim Auflösen des Manifests scheitert und damit nichts über die
Signaturprüfung aussagt.

**Lokal belegt (2026-07-31):** Der Mechanismus hinter N1/N2 wurde mit Cosign v3.1.2 (Container)
gegen ein unsigniertes öffentliches Image ausgeführt. Ergebnis: `Error: no signatures found`,
**Exit-Code 10**. Das ist der Beweis, dass Cosign bei fehlender Signatur einen von Null
verschiedenen Exit-Code liefert — die Voraussetzung dafür, dass die `if`-Konstruktion im Selbsttest
überhaupt greift. Die vollständigen Proben N1–N4 laufen erst im echten Tag-Lauf, weil sie ein
signiertes Image in GHCR voraussetzen.

## 6. Berechtigungen — und eine Abweichung vom Vertrag

Vertrag §6 legt fest: `id-token: write` und `attestations: write` nur im Job `supply-chain`,
`packages: write` nur im Job `image`.

**Der zweite Teil ist so nicht haltbar.** Cosign legt die Signatur als eigenes Artefakt *in der
Registry* ab (Tag `sha256-<digest>.sig`), und `push-to-registry: true` der Attestation-Action tut
dasselbe. Beides braucht Schreibrecht auf das Package. Es gibt zwei Auswege:

- **(a) empfohlen:** `packages: write` auch im Job `supply-chain`, Vertrag §6 entsprechend ergänzen.
  Der Job bleibt trotzdem eng: kein `contents: write`, also kein Zugriff auf Release oder Repo-Inhalt.
- **(b)** Signieren in den Job `image` verschieben. Dann braucht `image` zusätzlich
  `id-token: write`, und die Trennung „Bauen / Bezeugen" fällt weg — schlechter.

Der Workflow-Block wählt (a) und markiert die Stelle. **Entscheidung liegt beim Lead.**

Unverändert eingehalten: `contents: write` bleibt allein im Job `release`. `supply-chain` lädt
deshalb nichts ans Release, sondern legt SBOMs und Signatur-Bundle als Actions-Artefakt
`supply-chain` ab; `release` hängt sie an.

## 7. Pinning und was daran wirklich schützt

Alle fremden Actions sind auf einen Commit-SHA gepinnt (Vertrag §6). Zusätzlich sind die
**Werkzeugversionen** gepinnt — Cosign `v3.1.2`, Syft `v1.50.0`, Trivy `v0.72.0`. Ohne das zöge der
Default der jeweiligen Installer-Action die Version unbemerkt mit; ein SHA-gepinnter Installer, der
ein ungepinntes Binary aus dem Netz holt, ist Pinning-Theater.

Die aufgelösten SHAs stehen mit Tag-Kommentar im Workflow-Block. Sie wurden am 2026-07-31 über die
GitHub-API ermittelt (`gh api repos/<repo>/commits/<tag> --jq .sha`), nicht aus Dokumentation
abgeschrieben.

**Was Pinning nicht leistet:** Es macht den Build nicht reproduzierbar. Der Basis-Image-Tag im
`Dockerfile`, NuGet-Restore und `apt` sind weiterhin zeitabhängig. Zwei Läufe desselben Commits
ergeben nicht bitgleiche Images. Provenance sagt „so wurde es gebaut", nicht „das kannst du
nachbauen". Reproducible Builds sind ein eigenes Thema und stehen nicht in M1.

## 8. Offene Punkte

1. **Rust-Crates im WASI-Host erscheinen nicht einzeln in der SBOM** (§2.3). Ein zusätzlicher
   Syft-Lauf über `spikes/wasi-component-runtime/Cargo.lock` würde sie liefern; das berührt aber
   die Zusammensetzung der Image-SBOM und ist mit dem Lead zu klären.
2. **Prädikat-Typ der SBOM-Attestation.** Für CycloneDX erwarten wir
   `https://cyclonedx.org/bom`; das ist aus der Dokumentation abgeleitet und **im ersten echten
   Tag-Lauf zu bestätigen**. Die Anleitung nennt deshalb einen Befehl, mit dem Anwender den
   tatsächlichen Typ auslesen können, statt ihn vorauszusetzen.
3. **Trivy-Datenbank-Bezug.** Trivy lädt seine Verwundbarkeitsdatenbank aus einer Container-Registry.
   Läuft die in ein Ratenlimit, fällt das Gate aus einem Grund aus, der nichts mit dem Release zu tun
   hat. Ein Spiegel über `TRIVY_DB_REPOSITORY` ist der übliche Ausweg — im ersten Tag-Lauf beobachten.
4. **Private Repositories.** Siehe §9 — für dieses Repo gelöst, für einen Fork nicht.

## 9. Sichtbarkeit des Repositories — geprüft, nicht angenommen

`LupusMalusDeviant/bifrost` ist **öffentlich** (geprüft am 2026-07-31). Daraus folgt laut
Dokumentation von `actions/attest-build-provenance`:

- Bei **öffentlichen** Repositories signiert die Attestation über die *Public-Good-Instanz* von
  Sigstore. Attestations stehen in allen aktuellen GitHub-Plänen zur Verfügung, und **jeder** kann
  sie mit `gh attestation verify` prüfen.
- Bei **privaten oder internen** Repositories nutzt GitHub die *private Sigstore-Instanz*, und die
  Funktion setzt einen **GitHub-Enterprise-Cloud**-Plan voraus. Auf Free/Pro/Team gibt es
  Attestations dort nicht.

Für B.I.F.R.O.S.T ist der öffentliche Pfad also der zutreffende. **Was daraus folgt und hier nicht
zugesagt wird:** Wer das Projekt in ein privates Repository forkt, kann diesen Workflow nicht
unverändert übernehmen. Der Cosign-Teil funktioniert weiter (Fulcio/Rekor sind öffentlich), der
Attestation-Teil nicht.

## 10. Grenze der Prüfbarkeit — was hier belegt ist und was nicht

Vertrag §8 verlangt diese Trennung ausdrücklich.

**Lokal wirklich ausgeführt (Windows-Arbeitsplatz, 2026-07-31):**

| Prüfung | Ergebnis |
|---|---|
| Syft 1.50.0 gegen `mcpmcp:latest`, CycloneDX 1.7 | erfolgreich, 3803 Komponenten, 1,37 MB |
| Digest-Wächter (§2.2): Syft gegen `alpine@sha256:d9e853e8…` | bestätigt — `metadata.component.version` trägt den Manifest-Digest, der `grep`-Abbruch im Workflow greift also. Bei Aufruf über ein **Tag** stünde dort die Image-ID; genau deshalb erzwingt der Block die Digest-Referenz |
| Trivy 0.72.0 gegen `mcpmcp:latest`, `--severity CRITICAL,HIGH --exit-code 1` | **0 Funde, Exit-Code 0** — das Gate ließe den heutigen Stand passieren |
| Cosign v3.1.2, `verify` gegen ein unsigniertes Image | Ablehnung, `no signatures found`, **Exit-Code 10** |
| `gh attestation verify` vorhanden (gh 2.88.1) | ja |
| YAML-Gültigkeit des Workflow-Blocks (`yaml.safe_load`) | gültig, ein Job, 23 Schritte |
| Auflösung aller Action-SHAs über die GitHub-API | vollständig, keine geraten |

Cosign, Syft und Trivy sind auf dieser Maschine **nicht nativ installiert**; alle Läufe erfolgten in
Containern über das vorhandene Docker. Das geprüfte Image war der lokale Entwicklungsstand
`mcpmcp:latest`, **nicht** ein Release-Image aus GHCR — ein solches existiert noch nicht.

**Erst der erste echte Tag-Lauf zeigt:**

- ob GHCR-Push, Cosign-Signatur und Attestation-Upload mit den gesetzten Berechtigungen
  durchlaufen (insbesondere die Abweichung aus §6);
- ob die Negativproben N1, N2 und N4 tatsächlich ablehnen — hier ist nur der Mechanismus belegt,
  nicht der konkrete Fall;
- ob `needs.image.outputs.digest` in der erwarteten Form ankommt;
- welchen Prädikat-Typ die SBOM-Attestation trägt (§8.2);
- ob der Trivy-Datenbank-Bezug im CI stabil ist (§8.3).

Bis dahin ist alles in diesem Dokument, was den Tag-Lauf betrifft, eine **begründete Annahme —
keine Abnahme.**
