# Security- und Supply-Chain-Gates

Stand: 2026-07-31 (mit Nachträgen vom 2026-08-01) · Arbeitspaket **WP3.5** · Ergänzt
[SECURITY.md](../SECURITY.md), den [Distributionsvertrag](plans/m1-distribution-contract.md) und
[supply-chain.md](security/supply-chain.md).

> **Diese Seite ist die maßgebliche Fassung für die Gates.** Eine englische Zusammenfassung steht in
> [`docs/en/security.md`](en/security.md); sie ist **abgeleitet**, und **bei Widerspruch gilt diese
> Seite**. Sprachregel: [`docs/i18n.md`](i18n.md).

Dieses Dokument beschreibt, **was blockiert, wann es blockiert und womit belegt ist, dass es
überhaupt blockieren kann.** Der letzte Teil ist der eigentliche Inhalt. Ein Gate, das noch nie rot
war, ist eine Behauptung über zukünftiges Verhalten — kein Nachweis.

---

## 1. Die Gates auf einen Blick

| # | Gate | Prüft | Blockiert bei | Wo |
|---|---|---|---|---|
| G1 | **CodeQL** (`csharp`) | eigener Code, `security-and-quality` | `security-severity` ≥ 7.0 | `security.yml` |
| G2 | **NuGet-Schwachstellen** | `dotnet list package --vulnerable --include-transitive` über alle 22 Projekte | Critical/High | `security.yml`, `release.yml` (`verify`) |
| G3 | **cargo audit** | 4 Rust-Lockfiles (WASI-Host + 3 Guests) | jeder Fund (RustSec kennt keine Critical/High-Trennung) | `security.yml`, `release.yml` (`verify`) |
| G4 | **Containerfilesystem** | Trivy gegen das **gebaute Image** | Critical/High | `security.yml`, `release.yml` (`supply-chain`) |
| G5 | **Arbeitsbaum** | Trivy `fs`: Lockfiles + Fehlkonfiguration (Dockerfile, Compose) | Critical/High | `security.yml` |
| G6 | **Secrets** | gitleaks über Historie (Push/Zeitplan) bzw. PR-Diff | jeder neue Fund gegenüber der Baseline | `security.yml`, `release.yml` (`verify`) |
| G7 | **Ausnahmeregister** | Fristen, Freigaben, Drift gegen `.trivyignore.yaml` | abgelaufener/unvollständiger Eintrag | `security.yml`, `release.yml` (`verify`) |

**Kein Gate ist mit `continue-on-error` weichgemacht.** Wo `if: always()` steht, betrifft es
ausschließlich das Hochladen von Berichten *nach* einem Fehlschlag — damit der Befund sichtbar wird,
obwohl der Schritt davor rot ist. Die Gates selbst brechen ab.

### Warum manche Gates zweimal laufen

`security.yml` ist die laufende Prüfung (PR, Push auf `main`, wöchentlich).
`release.yml` wiederholt die blockierenden Gates auf dem **getaggten Commit**.

Das ist keine Redundanz aus Bequemlichkeit:

- Nur im Release geprüft hieße, den Fund zum spätestmöglichen Zeitpunkt zu erfahren — wenn das Tag
  gesetzt ist und alle warten.
- Nur im PR geprüft hieße, nichts über den getaggten Stand zu wissen, und nichts über
  Schwachstellen, die **erst nach dem Merge** bekannt wurden.
- Der wöchentliche Lauf deckt genau den dritten Fall ab: unveränderter Code, neue Advisory-Lage.

Die Gates in `release.yml` sitzen im Job **`verify`**, nicht in einem neuen Job. Zwei Gründe: Der
Vertrag friert die Jobnamen ein (M1 §5), und `verify` trägt bereits die passende Regel — *„Was hier
nicht grün ist, wird gar nicht erst gebaut."* Ein blockierendes Gate **hinter** dem Push müsste ein
veröffentlichtes Image widerrufen statt es zu verhindern.

**Der Preis steht dran:** Die Gates liegen auf dem kritischen Pfad und verlängern `verify` um rund
fünf Minuten, überwiegend durch das Übersetzen von `cargo-audit`.

---

## 2. Wie belegt wurde, dass jedes Gate scheitern kann

Alle Läufe am **2026-07-31**, Windows-Arbeitsplatz, Werkzeuge containerisiert über Docker 29.6.1
(Ausnahme: `dotnet` und `cargo` nativ).

| # | Gate | Negativprobe | Ergebnis |
|---|---|---|---|
| G1 | CodeQL | `sarif_gate.py` gegen eine SARIF mit `security-severity` 7.5 | **Exit 1**, Fund benannt |
| G1 | CodeQL | `sarif_gate.py` gegen eine **fehlende** SARIF-Datei | **Exit 2** (fail-closed, s. §5) |
| G2 | NuGet | Fixture-Projekt mit `Newtonsoft.Json 12.0.1` (GHSA-5crp-9r3c-p9vr, High) | **Exit 1** |
| G3 | cargo audit | echter Bestand `spikes/wasi-component-runtime` | **Exit 1** bei 2 Funden, **Exit 0** mit beiden `--ignore` |
| G6 | gitleaks | Probe­datei mit vier Credential-Formen an nicht erlaubtem Pfad | **Exit 2**, 4 Funde |
| G6 | gitleaks | dieselbe Datei an **erlaubtem** Pfad | unterdrückt — 8 Funde ohne Config, 4 mit Config |
| G7 | Register | Eintrag abgelaufen (`expires` = gestern) | **Exit 1** |
| G7 | Register | Laufzeit 200 Tage (> 90) | **Exit 1** |
| G7 | Register | `approved_by: Team` | **Exit 1** |
| G7 | Register | aktive Ausnahme → G2 lässt durch; **abgelaufene** → G2 blockt wieder | **Exit 0** bzw. **Exit 1** |

Die letzte Zeile ist die wichtigste: Sie belegt, dass eine Ausnahme **von allein verfällt**. Niemand
muss aktiv werden, damit das Gate zurückkommt.

### G4/G5 — was hier fehlt, und warum es dasteht

Für Trivy gibt es **keine eigene Negativprobe dieses Arbeitspakets**. Belegt ist nur der Positivfall:
`trivy fs` über den Arbeitsbaum lief durch und meldete **0 Critical/High** (Exit 0), `trivy image`
gegen das Release-Image ist mangels Release-Image gar nicht gelaufen.

WP1.2 hat seinerzeit belegt, dass Trivy bei einem Fund `--exit-code 1` liefert
([supply-chain.md §10](security/supply-chain.md)). Das ist der Mechanismus — aber **nicht** ein Lauf
dieser Konfiguration gegen ein Image mit einem echten Critical-Fund. G4 ist damit das schwächste
Glied dieser Abgabe. Es als „nachgewiesen" zu führen wäre falsch.

---

## 3. Was die Gates tatsächlich gefunden haben

Ungeschönt, alles am 2026-07-31 wirklich ausgeführt.

### 3.1 NuGet — sauber

`dotnet list package --vulnerable --include-transitive` über alle 22 Projekte:
**null anfällige Pakete**, direkt wie transitiv.

### 3.2 Der Befund, der dieses Gate erst nötig macht

`dotnet list package --vulnerable` **meldet Funde und endet trotzdem mit Exit 0.** Gemessen gegen
das Fixture-Projekt:

```
Für das Projekt "vulnfixture" liegen die folgenden anfälligen Pakete vor.
   > Newtonsoft.Json   12.0.1   12.0.1   High   https://github.com/advisories/GHSA-5crp-9r3c-p9vr
EXIT=0
```

Der naheliegende Workflow-Schritt — `run: dotnet list package --vulnerable --include-transitive` —
wäre ein Gate, das **niemals rot werden kann**. Deshalb wertet
`.github/scripts/dotnet_vulnerable_gate.py` die Ausgabe aus, statt dem Rückgabewert zu glauben.

Zweiter Grund für das Skript: Die Tabellenausgabe ist **lokalisiert** (auf dem Arbeitsplatz
`Schweregrad`, auf dem Runner `Severity`). Ein `grep` darüber hätte hier zufällig funktioniert und
wäre bei anderer Sprachumgebung still durchgefallen. Ausgewertet wird deshalb `--format json`.

> **Nebenbefund, unabhängig von diesem Paket:** `Directory.Build.props` setzt
> `TreatWarningsAsErrors`. Damit wird `NU1903` (NuGet-Audit-Warnung) beim Restore bereits zum
> **Build-Fehler** — nachgemessen: `dotnet build` des Fixtures endete mit Exit 1. Das Repository war
> also nicht ungeschützt. Verlassen sollte man sich darauf nicht: Der Schutz hängt daran, dass
> `TreatWarningsAsErrors` gesetzt bleibt und `NU1903` nicht in `NoWarn` landet, er kennt keine
> Severity-Schwelle und erzeugt weder Bericht noch Artefakt.

### 3.3 Rust — zwei echte Funde, beide LOW

`cargo audit` gegen `spikes/wasi-component-runtime/Cargo.lock` (222 Crates, 1177 Advisories):

| ID | Crate | Titel | Severity | Lösung |
|---|---|---|---|---|
| [RUSTSEC-2026-0222](https://rustsec.org/advisories/RUSTSEC-2026-0222) | `wasmtime` 47.0.2 | Stores can mix up type indices between engines | 3.8 (low) | ≥ 47.0.3 |
| [RUSTSEC-2026-0223](https://rustsec.org/advisories/RUSTSEC-2026-0223) | `wasmtime` 47.0.2 | Preemption and traps during bulk operations enable breaking internal VM state | 2.0 (low) | ≥ 47.0.3 |

Die drei Guest-Workspaces sind sauber.

**Das Critical/High-Gate blockiert diese Funde nicht** — sie sind LOW. Trotzdem gehören sie hierher,
und zwar prominenter, als die Zahl nahelegt: Betroffen ist `wasmtime`, also **genau die Sandbox, die
nicht vertrauenswürdige WASI-Komponenten einsperrt** ([ADR-0020](adr/0020-wasi-runtime-out-of-process-rust-host.md),
SECURITY.md „Isolated paths"). „Breaking internal VM state" ist in einem gewöhnlichen Programm eine
Randnotiz; in einer Sandbox ist es die Beschreibung der Grenze selbst. Der CVSS-Wert bemisst den
generischen Fall, nicht diesen.

**Empfehlung: `wasmtime` auf ≥ 47.0.3 heben.** Das liegt in `spikes/**` und damit außerhalb der
Dateizone von WP3.5 — hier gemeldet, nicht angefasst. Der neue Dependabot-Eintrag für `cargo` würde
es ohnehin aufgreifen; ein Warten darauf wäre aber die falsche Reihenfolge.

**Keine Ausnahme eingetragen.** Ein Register-Eintrag für einen Fund, der gar nicht blockt, wäre die
falsche Gewöhnung — Ausnahmen sind für Fälle da, die sonst den Release anhalten.

### 3.4 Secrets — 19 Treffer in der Historie, alle synthetisch

gitleaks 8.30.1 über 152 Commits (4,79 MB): **19 Funde**. Jeder einzeln angesehen:

| Datei (Pfad zum Fundzeitpunkt) | Regel(n) | Befund |
|---|---|---|
| `tests/Bifrost.Core.Tests/Configuration/ConfigurationFixtures.cs` | `generic-api-key` ×7 | Kommentar im Quelltext: *„Erfunden, keine echten Zugangsdaten"* |
| `tests/Bifrost.Core.Tests/Diagnostics/DiagnosticRedactionTests.cs` | `slack-bot-token`, `generic-api-key`, `private-key` | `…erfundenerslacktoken`, PEM-Rumpf enthält ausgeschrieben `erfundenerschluessel` |
| `tests/Bifrost.Integration.Tests/Operations/OperationsApiTests.cs` | `generic-api-key` | `0123456789abcdef0123456789abcdef` |
| `tests/McpMcp.Core.Tests/Guardrails/SecretGuardTests.cs` | `gitlab-pat`, `gcp-api-key`, `jwt` | `ghp_abcdefghijklmnopqrstuvwxyz0123456789` u. Ä. |
| `tests/McpMcp.Core.Tests/Webhooks/WebhookSignatureTests.cs` | `generic-api-key` | `whsec_testgeheimnis_1234567890` |
| `tests/McpMcp.Core.Tests/Invocation/TracingTests.cs` | `generic-api-key` | `sk-streng-geheim-4711` |
| `tests/McpMcp.Integration.Tests/Gateway/ContainerIsolationE2ETests.cs` | `generic-api-key` | `s3hr-geheim-xyz` |
| `tests/McpMcp.Integration.Tests/Persistence/PersistenceTestsBase.cs` | `generic-api-key` ×2 | `at_streng_geheim_4711`, `rt_auch_geheim_0815` |

**Kein einziges echtes Zugangsdatum.** Alle stammen aus Testfixtures, die belegen sollen, dass
B.I.F.R.O.S.T Secrets redigiert bzw. erkennt. Die `McpMcp.*`-Pfade sind der Stand vor der
Umbenennung.

Das ist kein Zufall, sondern die Bauart dieses Repositories: Ein Redaktionstest **muss**
secret-förmige Eingaben enthalten — sonst testet er nichts. Ohne Konfiguration wäre dieses Gate
dauerhaft rot, und ein dauerhaft rotes Gate wird abgeschaltet. Wie damit umgegangen wird: §4.

### 3.5 Container und Arbeitsbaum

`trivy fs --scanners vuln,misconfig --severity CRITICAL,HIGH`: **0 Funde, Exit 0.** Erfasst wurden
`Directory.Packages.props`, alle vier `Cargo.lock` und das `Dockerfile`.

Das Image selbst (G4) wurde in diesem Paket **nicht** gescannt — siehe §6.

---

## 4. Der Umgang mit den Testfixtures — und was er kostet

Zwei Mechanismen, bewusst getrennt:

**`.github/gitleaks-baseline.json`** hält die 10 triagierten Altfunde von `main` fest (redigiert erzeugt: das
Feld `Secret` enthält `REDACTED`, es wandern also keine secret-förmigen Zeichenfolgen in eine neue
Datei). Eine Baseline gilt nur für den festgehaltenen Stand; jeder neue Fund kommt durch.

**`.github/gitleaks.toml`** nimmt acht namentlich geprüfte Fixture-Dateien vom Scan aus — für die
Zukunft, damit ein PR, der eine dieser Dateien anfasst, nicht vorhersehbar rot wird.

**Kein `tests/**`-Pauschalausschluss.** Ein echtes Zugangsdatum in einer Testdatei ist genauso ein
Leck wie eines in `src/` — und Testdateien sind der *wahrscheinlichere* Ort, weil dort mit echten
Systemen experimentiert wird.

**Was dieser Zuschnitt nicht leistet — die ehrliche Kehrseite:** Innerhalb der acht ausgenommenen
Dateien würde ein echtes Zugangsdatum **nicht** gefunden. Das ist der Preis dafür, dass das Gate
überhaupt benutzbar ist. Die sauberere Lösung wäre eine Markerkonvention (etwa ein
`// gitleaks:allow`-Kommentar an jeder Fixture-Zeile), die pro Zeile statt pro Datei ausnimmt. Das
verlangt Änderungen in `tests/**` und gehört damit **WP3.6 oder dem Lead**, nicht diesem Paket.

**Ein neuer Fund ist kein Fehlalarm, sondern der vorgesehene Ablauf.** Legt jemand eine neue
Testdatei mit synthetischen Fixtures an, wird das Gate rot; ein Mensch schaut einmal hin und trägt
den Pfad mit Begründung ein. Genau dieses einmalige Hinschauen ist der Wert des Gates.

> **Modushinweis, sonst wundert man sich:** Im Gate-Modus (`gitleaks git`) werden **Commits** geprüft,
> nicht der Endzustand des Baums. Eine Datei, die vor zwanzig Commits eingeführt wurde, taucht nur
> bei jenem Commit auf. Ein voller Baumscan (`gitleaks dir`) zeigt deshalb mehr — das ist kein
> Widerspruch, sondern ein anderer Gegenstand.

---

## 5. Der Ausnahmeweg

Datei: **`.github/security-exceptions.yml`**, Prüfer: `.github/scripts/security_exceptions.py`.

```yaml
exceptions:
  - id: CVE-2026-12345          # genau wie das Werkzeug sie meldet
    tool: trivy                 # dotnet | cargo-audit | trivy | gitleaks | codeql
    reason: >-
      Nicht erreichbar: die betroffene Funktion wird nicht aufgerufen (Issue #123).
      Kein Fix verfügbar, Upstream-Ticket foo/foo#456.
    approved_by: <Product Owner, namentlich>
    approved_at: 2026-07-31
    expires: 2026-10-29         # höchstens 90 Tage nach approved_at
```

Der Validator lässt den Lauf scheitern bei: fehlendem Pflichtfeld, unbekanntem Werkzeug, Doppel­eintrag,
Begründung unter 40 Zeichen, `approved_by` aus `{Team, CI, Bot, Dependabot, -}`, Laufzeit über 90 Tagen,
**abgelaufenem Eintrag** und bei **Drift** — jede ID in `.trivyignore.yaml`, die hier fehlt.

**Warum ein abgelaufener Eintrag den Lauf scheitern lässt, statt bloß wirkungslos zu werden:** Ein
stillschweigend wirkungsloser Eintrag ist Altpapier, das niemand aufräumt. Ein Eintrag, der den Lauf
anhält, ist eine offene Aufgabe. Verlängerung nur mit **neuer** Begründung, nicht durch
Datum-Hochschieben.

**Warum `.trivyignore.yaml` weiter existiert:** Trivy liest dieses Format **nativ**. Ein
selbstgebauter Filter davor wäre eine zweite Stelle, die anders entscheiden kann als das Werkzeug.
Damit daraus kein Schattenregister wird, greift die Drift-Prüfung.

**Fail-closed durchgehend:** Ist das Register unlesbar, brechen die Gates ab, statt „keine
Ausnahmen" oder „alle Ausnahmen" anzunehmen. Fehlt eine erwartete SARIF-Datei, wertet
`sarif_gate.py` das als *Analyse nicht gelaufen* (Exit 2), nicht als *nichts gefunden*.

**Noch offen — technisch nicht erzwungen:** Die PO-Bindung setzt einen **CODEOWNERS**-Eintrag auf
`.github/security-exceptions.yml` und `.trivyignore.yaml` voraus. Eine CODEOWNERS-Datei existiert im
Repository **nicht**; sie liegt außerhalb der Dateizone von WP3.5. Bis der Lead sie anlegt, ist die
Freigabepflicht dokumentiert, aber nicht durchgesetzt.

> **Nachtrag 2026-08-01:** `.github/CODEOWNERS` ist inzwischen angelegt (in M3) und führt die
> Sicherheitsgates, `.trivyignore.yaml`, die Release-Pipeline, `CryptographicNames.cs` und die ADRs.
> Der Absatz darüber beschreibt den Stand vom 2026-07-31 und bleibt als solcher stehen.
>
> **Die Abhängigkeit gilt unverändert: CODEOWNERS allein erzwingt nichts.** In den
> Branch-Protection-Regeln für `main` muss „Require review from Code Owners" eingeschaltet sein,
> sonst ist der Eintrag ein Vorschlag. Das ist eine Repository-Einstellung und lässt sich nicht
> mitliefern. Bis sie gesetzt ist, ist die PO-Freigabepflicht dokumentiert und **nicht
> durchgesetzt** — wer das Ausnahmeregister ändern darf, kann seine eigene Ausnahme freigeben.

---

## 6. Was ungeprüft bleibt, bis ein echter Lauf stattfindet

`release.yml` wurde in M1 gebaut, aber **es hat nie ein Releaselauf stattgefunden**
([product-readiness-status.md](plans/product-readiness-status.md)). Alles, was WP3.5 ergänzt, ist
damit ebenfalls ungelaufen. Diese Trennung ist der Kern der Abgabe:

> **Nachtrag 2026-08-01 — der erste Lauf hat stattgefunden.** `v0.12.0` ist veröffentlicht; drei
> Trockenläufe und drei Tag-Läufe waren nötig, und sie haben **neun Befunde** produziert (Liste in
> [product-readiness-status.md](plans/product-readiness-status.md)). Der Abschnitt unten beschreibt
> den Stand vom 2026-07-31 und bleibt als solcher stehen. Was sich dadurch **ändert**:
>
> - **G4 ist gelaufen** — Trivy meldete auf Image und CLI-Artefakten grün. Ein grüner Lauf belegt
>   aber die Verdrahtung, nicht das Gate: Ein Lauf **dieser** Konfiguration gegen ein Image mit einem
>   echten Critical-Fund gibt es weiterhin nicht. **G4 bleibt das schwächste Glied.**
> - **Der Trockenlaufmodus war selbst nie gelaufen** und war im Trockenlauf *immer* rot (Befund 7).
>   Genau das Muster, das dieses Dokument beschreibt, eine Ebene höher.
> - Was oben unter „steht in der YAML und ist ungeprüft" zu CodeQL, SARIF-Upload und Dependabot
>   steht, ist durch den Lauf **nicht** pauschal erledigt; belegt ist nur, was im Lauf wirklich
>   ausgeführt wurde.

### Lokal/containerisiert wirklich ausgeführt

- `dotnet list package --vulnerable --include-transitive` über die echte Solution (0 Funde) **und**
  gegen ein Fixture mit High-Fund (Gate: Exit 1);
- `cargo audit` über alle vier Lockfiles (2 Funde, beide LOW), inklusive `--ignore`-Pfad;
- `gitleaks` über die vollständige Historie, mit und ohne Config/Baseline, plus Negativprobe;
- `trivy fs` über den Arbeitsbaum (0 Critical/High);
- alle vier Python-Skripte, Positiv- und Negativfall;
- `actionlint` über alle Workflows.

### Steht in der YAML und ist ungeprüft

- **CodeQL läuft ausschließlich auf GitHub.** Ob `build-mode: manual` mit .NET 10 durchläuft, wie
  lange die Analyse braucht und welche Funde sie meldet: **unbekannt.** Dass `sarif_gate.py` eine
  CodeQL-SARIF korrekt bewertet, ist an einer selbst erzeugten SARIF belegt — nicht an einer echten.
- **G4, der Image-Scan**, ist nie gegen ein Release-Image gelaufen (es gibt keins).
- **Der SARIF-Upload** in die Code-Scanning-Ansicht (Berechtigungen, Fork-Verhalten, ob GitHub die
  selbst erzeugte SARIF von `dotnet_vulnerable_gate.py` annimmt): ungeprüft.
- **Dependabot** ist eine Konfigurationsdatei. Ob GitHub sie annimmt, ob `directories` (Plural) für
  `cargo` wie erwartet greift und ob die Gruppen so schneiden wie gedacht, zeigt der erste
  Dependabot-Lauf.
- **`cargo install cargo-audit --locked`** auf dem Runner: Laufzeit geschätzt (~3 min), nicht gemessen.
- **Das Zusammenspiel im Release**: ob `verify` mit den zusätzlichen Schritten im 60-Minuten-Limit
  bleibt, ist Rechnung, kein Messwert.

---

## 7. Pflege der Pins — die bekannte Lücke

Dependabot deckt ab: GitHub Actions (SHA-Pins), NuGet, Cargo (4 Verzeichnisse), Docker (Dockerfile).

**Nicht abgedeckt:** die Digest-Pins von `zricethezav/gitleaks` und `aquasec/trivy`, die in
`security.yml` und `release.yml` als `docker run`-Aufrufe stehen. Das `docker`-Ökosystem von
Dependabot liest Dockerfiles und Compose-Dateien, **keine Workflow-Skripte**.

Das ist genau der Fehler, den dieses Paket beheben sollte — hier bleibt er bestehen, verkleinert auf
zwei Stellen. Die Alternative wäre `aquasecurity/trivy-action` gewesen (Dependabot-gepflegt), aber
genau diese Action hat den Fehler in §8 verursacht. Der Tausch lautet: zwei manuell gepflegte
Digests gegen eine fremde Action weniger.

Nachschau (vierteljährlich, oder wenn ein Advisory die Werkzeuge betrifft):

```bash
docker pull zricethezav/gitleaks:latest
docker image inspect zricethezav/gitleaks:latest --format '{{index .RepoDigests 0}}'
docker pull aquasec/trivy:0.72.0
docker image inspect aquasec/trivy:0.72.0 --format '{{index .RepoDigests 0}}'
```

Aktueller Stand (2026-07-31): gitleaks **v8.30.1**, Trivy **0.72.0**.

---

## 8. Ein Fund in der bestehenden Releasepipeline

Beim Aufsetzen auf `release.yml` fiel ein Fehler auf, der den **allerersten Releaselauf abgebrochen
hätte** — nicht am Ergebnis eines Scans, sondern an einem Pfad.

Der Job `supply-chain` rief `aquasecurity/trivy-action` mit `trivyignores: .trivyignore.yaml` auf.
**Diese Datei existiert im Repository nicht.** Im `entrypoint.sh` des gepinnten SHA `ed142fd0` steht:

```bash
if [ ! -f "$f" ]; then
  echo "ERROR: cannot find ignorefile '${f}'." >&2
  exit 1
fi
```

[supply-chain.md §4.2](security/supply-chain.md) nimmt das Gegenteil an: *„Trivy verträgt einen
fehlenden `trivyignores`-Pfad."* Gegen Trivy 0.72.0 direkt gehalten trifft **auch das nicht zu** —
`--ignorefile` auf eine fehlende Datei endet mit `FATAL … ignore file not found`.

Der Schritt läuft jetzt direkt über das Trivy-Image; die Ausnahmeliste wird nur übergeben, wenn es
sie gibt, und ihr Inhalt wird protokolliert. Nebenbei entfällt eine fremde Action, und der
CLI-SBOM-Scan wurde vom Tag `0.72.0` auf einen Digest umgestellt.

> Der Fund illustriert die Regel dieses Dokuments besser als jede Formulierung: Die Aussage stand
> begründet und plausibel in einem Dokument — und war falsch. Aufgefallen ist sie erst beim
> Ausführen.

---

## 9. Gefunden, nicht angefasst

Außerhalb der Dateizone von WP3.5 (`.github/**`, `SECURITY.md`, dieses Dokument). Gemeldet statt
behoben, wie beauftragt:

1. **`wasmtime` 47.0.2 hat zwei RustSec-Advisories** (§3.3). Betroffen ist die WASI-Sandbox.
   Datei: `spikes/wasi-component-runtime/Cargo.toml` / `Cargo.lock`. Empfehlung: ≥ 47.0.3.
2. **`.trivyignore.yaml` fehlt** (§8). Der Workflow ist jetzt unempfindlich dagegen; wer die Datei
   anlegt, braucht dafür auch den CODEOWNERS-Eintrag.
3. **CODEOWNERS fehlt vollständig.** Ohne sie ist keine der PO-Freigaben technisch erzwungen (§5).
   — *Erledigt in M3: `.github/CODEOWNERS` ist angelegt. Die Freigabe ist damit trotzdem erst
   erzwungen, wenn „Require review from Code Owners" in der Branch-Protection von `main` aktiv ist;
   siehe den Nachtrag in §5.*
4. **DataProtection-Key-Ring im Arbeitsbaum:**
   `src/Bifrost.Server/data/keys/key-bd37746a-c325-441d-81f4-f414c9d4c7ca.xml`.
   **Kein Leck** — die Datei ist über `.gitignore:486` (`src/Bifrost.Server/data/`) ausgeschlossen
   und war **nie** in einem Commit (`git log --all -- 'src/Bifrost.Server/data/**'` ist leer).
   Erwähnt, weil es echtes Schlüsselmaterial ist: derselbe Schlüsseltyp, der laut SECURITY.md jedes
   Upstream-Credential entschlüsselt. Wer dieses Arbeitsverzeichnis weitergibt oder sichert, gibt
   den Schlüssel mit.
5. **Zwei shellcheck-Warnungen in `ci.yml`** (SC2044 Zeile 70, SC2034 Zeile 270). Bestandsstand,
   von WP3.5 **nicht** angefasst — `ci.yml` blieb unverändert. `release.yml` und `security.yml` sind
   actionlint-sauber.

Kein Sicherheitsproblem im Sinne einer ausnutzbaren Schwachstelle in `src/**` oder `tests/**`
gefunden. Die dortigen gitleaks-Treffer sind sämtlich synthetische Fixtures (§3.4).
