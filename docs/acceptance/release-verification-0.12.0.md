# `v0.12.0` selbst überprüft — Protokoll

**Datum:** 2026-08-05 · **Paket:** M7/WP7.2 (Teilstück) · **Gegenstand:** das veröffentlichte
Release `v0.12.0` vom 2026-08-01

> **Was das ist.** Der Scope für den externen Review nennt den Releaseworkflow als einzigen Bereich
> mit dem Zusatz *„ungeprüft am Ergebnis"* und führt ihn in §4 als die von uns selbst vermutete
> schwächste Stelle. Dieser Zusatz gilt jetzt nicht mehr: Die
> [Anleitung für Betreiber](../security/verifying-releases.md) ist gegen das echte Release gefahren
> worden, einschließlich ihrer Negativproben.
>
> **Was das nicht ist.** Kein externer Review. Es prüft die Lieferkette am Ergebnis — nicht Auth,
> nicht die Invoker-Invarianten, nicht SSRF, nicht Bootstrap, nicht Backupcrypto, nicht Package/WASI,
> nicht den Containerlaunch. Und es ist von derselben Seite gefahren, die das Release erzeugt hat.

## Werkzeuge

| | |
|---|---|
| `cosign` | `ghcr.io/sigstore/cosign/cosign:v3.1.2` — dieselbe Fassung, die `release.yml` über `cosign-installer` festnagelt |
| `gh` | 2.88.1 |
| `trivy` | `aquasec/trivy@sha256:cffe3f51…accdd6f` (0.72.0) — derselbe Digest wie im Workflow |

Identität und Issuer wörtlich aus der Anleitung, **nicht** gelockert.

## Ergebnis

| Schritt der Anleitung | Erwartung | Ergebnis |
|---|---|---|
| §1 Digest auflösen | ein Index-Digest | `sha256:3ab92c41…f7e4b45` |
| §2.1 `cosign verify` (Image) | Exit 0 | **Exit 0**, drei Signaturen: CycloneDX, SLSA-Provenance, Cosign |
| §2.1 „nur das Wesentliche sehen" (jq) | Repo, Tag, Commit | **`Repo: null · Tag: - · Commit: -`** → BFR-F-0055 |
| §2.2 `gh attestation verify` mit `--signer-workflow` | Exit 0 | **Exit 0** — ohne jede Ausgabe, siehe unten |
| §3 Schritt 1 `cosign verify-blob` | `Verified OK` | **`Verified OK`**, Exit 0 |
| §3 Schritt 2 Prüfsumme | `OK` | **`bifrost-cli-0.12.0-win-x64.zip: OK`** |
| §3 Schritt 3 Herkunft des Archivs | Exit 0 | **Exit 0** |
| §3 Schritt 4 Commit gegenprüfen | CLI-Commit = Zertifikat-Commit | **`71e4acfb…ee101`** aus `bifrost --version`, identisch mit `org.opencontainers.image.revision` des Images |
| §5 Probe A: fremde Signierer-Identität | **muss scheitern** | **Exit 1** — „expected SAN value to match regex …, got `…/release.yml@refs/tags/v0.12.0`" |
| §5 Probe B: echter, aber unsignierter amd64-Digest | **muss scheitern** | **Exit 10** — „no signatures found" |
| Negativprobe zu §2.2 (fremdes Repository) | **muss scheitern** | **Exit 1**, HTTP 404 |

**Beide Negativproben der Anleitung lehnen ab.** Damit sagt der grüne Durchlauf etwas.

## Zwei Beobachtungen zur Anleitung

**§2.1 liefert nicht, was sie verspricht.** Unter cosign 3 bleibt `optional` im Payload leer; der
angegebene `jq`-Aufruf gibt `null` und zwei Striche zurück. Das ist kein Sicherheitsproblem — die
Identität *wird* geprüft, Probe A zeigt sie sogar wörtlich in ihrer Fehlermeldung. Es ist ein
Kettenbruch in der Anleitung: §3 Schritt 4 verlangt den Vergleich mit dem Commit, „der in Schritt 1
im Cosign-Zertifikat stand" — und dieser Wert erscheint nirgends. Der gangbare Ersatz steht oben in
der Tabelle: `org.opencontainers.image.revision` am Image. (BFR-F-0055)

**`gh attestation verify` schweigt außerhalb eines Terminals.** Erfolg heißt Exit 0 und sonst
nichts — die Erfolgsmeldung geht nur an ein TTY. Wer den Abschnitt „Alles in einem Durchgang"
automatisiert, sieht bei Erfolg eine leere Ausgabe. Das Skript in §4 macht es richtig (`set -e`);
der Fließtext sollte den Exit-Code als das Signal benennen.

## Das Containerfilesystem-Gate — Gegenprobe nachgeholt

BFR-F-0045 stand offen, weil es kein Release-Image gab, an dem sich zeigen ließ, dass das
Schwachstellen-Gate scheitern kann. Es gibt jetzt eines.

Derselbe Aufruf wie in `release.yml` (`--scanners vuln --severity CRITICAL,HIGH
--ignore-unfixed=false --exit-code 1`), zweimal:

```
gegen ghcr.io/lupusmalusdeviant/bifrost@sha256:3ab92c41…   ->  Exit 0   (0 Funde, ubuntu 24.04)
gegen debian:11-slim                                        ->  Exit 1   (u.a. CVE-2023-45853 CRITICAL)
```

Das Gate kann rot werden. Nebenbei: Das veröffentlichte Image ist auch vier Tage nach dem Release
gegen eine heute geladene Schwachstellendatenbank ohne Critical- oder High-Fund — geprüft wurde also
nicht nur der Stand zum Zeitpunkt des Builds.

## Was offen bleibt

Der externe Review. Sieben der acht Scope-Bereiche sind hier **nicht** angefasst worden, und die
achte Prüfung stammt von derselben Seite, die den Workflow geschrieben hat. Die Auftragsunterlage
([`security-review-scope.md`](security-review-scope.md)) gilt unverändert — mit einer Änderung: Die
Zeile „Releaseworkflow" trägt den Zusatz „ungeprüft am Ergebnis" nicht mehr.
