# NFR-01/02 — Referenzmessung

Formale Lastmessung des Gateway-Overheads (WP8.3, der letzte nicht automatisiert belegte Punkt aus der v1.0-Abnahme).

## Aufbau

| | |
|---|---|
| **Harness** | `PerformanceBenchmarkTests.Nfr01_gateway_overhead_and_tools_list_under_load` |
| **Ausführung** | `$env:BIFROST_RUN_BENCHMARK=1; dotnet test --filter FullyQualifiedName~PerformanceBenchmark` |
| **Upstream** | `BulkServer` — stdio-MCP-Server mit **100 Tools** |
| **Last** | 20 gleichzeitige MCP-Sessions, 50 Calls je Session (1000 Calls), maximal **100 gleichzeitig in Flight** |
| **Katalog** | Profil mit allen 100 Tools gepinnt, damit `tools/list` den vollen Katalog liefert |

Der Benchmark läuft bewusst **nicht** in der normalen Suite: Geteilte CI-Runner sind keine
Referenz-Hardware, dort misst man zu einem guten Teil Runner-Auslastung statt Gateway-Verhalten.

> **Korrektur (2026-07-18).** Aus dieser richtigen Beobachtung war die falsche Konsequenz gezogen
> worden: Der Test hing hinter `BIFROST_RUN_BENCHMARK=1`, und diese Variable wurde **nirgends**
> gesetzt — weder lokal in der Suite noch in der CI. Damit war der einzige Test, der die
> NFR-01/02-Schranken überhaupt prüft, dauerhaft übersprungen und die Anforderungen faktisch
> ungeprüft, während die Suite grün meldete. Seit dem Abnahme-Audit gibt es einen eigenen
> CI-Job (`benchmark`, wöchentlich per `schedule` und jederzeit per `workflow_dispatch`).
> Dass CI-Hardware schwächer ist, spricht **für** den Lauf: Wenn die Schranken dort halten,
> halten sie erst recht auf ordentlicher Hardware.

## Messumgebung

| | |
|---|---|
| CPU | AMD Ryzen 7 9800X3D — 8 Kerne / 16 logische Prozessoren |
| RAM | 61,6 GB |
| OS | Windows 11 (10.0.26200) |
| Runtime | .NET 10.0.4 |
| Datum | 2026-07-18 |

> **Einordnung:** NFR-01 definiert als Referenz eine *4-Core-VM*. Diese Maschine ist stärker, die Werte sind daher als **oberes Ende** zu lesen — auf schwächerer Hardware ist mit höheren Latenzen zu rechnen. Der Abstand zu den Schranken (Faktor ~7 bzw. ~3) ist allerdings so groß, dass die Kriterien auch dort tragen sollten.

## Ergebnis

| Metrik | Gemessen | Schranke (NFR-01/02) | Status |
|---|---|---|---|
| `tools/call` p50 | **1,7 ms** | — | |
| `tools/call` **p95** | **7,3 ms** | ≤ 50 ms | ✅ Faktor ~7 Reserve |
| `tools/call` p99 | 14,9 ms | — | |
| `tools/call` max | 16,6 ms | — | |
| `tools/list` (100 Tools) **p95** | **59,1 ms** | ≤ 200 ms | ✅ Faktor ~3 Reserve |
| Durchsatz | ~6400 Calls/s | — | |
| Gleichzeitige Sessions | 20 | ≥ 20 | ✅ |
| Gleichzeitige In-Flight-Calls | 100 | ≥ 100 | ✅ |
| Fehler unter Last | **0** | 0 | ✅ |

**NFR-01 und NFR-02 sind damit auf Referenz-Hardware formal belegt** — der letzte offene Punkt aus [der v1.0-Abnahme](v1.md) ist geschlossen.

## Gegenprobe auf CI-Hardware (2026-07-18)

Derselbe Benchmark auf einem geteilten `ubuntu-latest`-Runner — deutlich schwächer als die
Referenzmaschine und ohne Kontrolle über Nachbarlast. Das ist die härtere Probe:

| Metrik | CI-Runner | Referenz-Maschine | Schranke |
|---|---|---|---|
| `tools/call` p50 | 17,1 ms | 1,7 ms | — |
| `tools/call` **p95** | **38,3 ms** | 7,3 ms | ≤ 50 ms ✅ |
| `tools/call` p99 | 52,7 ms | 14,9 ms | — |
| `tools/call` max | 62,7 ms | 16,6 ms | — |
| `tools/list` (100 Tools) **p95** | **118,0 ms** | 59,1 ms | ≤ 200 ms ✅ |
| Durchsatz | ~1024 Calls/s | ~6400 Calls/s | — |
| Fehler unter Last | **0** | 0 | 0 ✅ |

Die Schranken halten auch hier, aber die Reserve schrumpft spürbar: bei `tools/call` von Faktor ~7
auf ~1,3, und p99 liegt mit 52,7 ms bereits über der p95-Schranke. Für Deployments auf kleinen
geteilten VMs ist das die realistischere Erwartung — wer dort Kopffreiheit braucht, sollte die
Tool-Anzahl je Profil im Blick behalten, weil `tools/list` mit dem Katalog wächst.

## Nachmessung nach dem Protokollwechsel (2026-07-31)

Die Messung oben stammt vom 2026-07-18 und damit aus der Zeit **vor** der Spec-Revision
`2026-07-28` ([ADR-0023](../adr/0023-stateless-kern-und-mrtr.md)). Der sessionlose Kern ändert das
Kostenprofil je Anfrage — jede Anfrage trägt jetzt ihre Protokollversion und Fähigkeiten mit —,
also ist die alte Zahl kein Beleg mehr für den heutigen Stand.

Gleiche Maschine, gleicher Harness, **fünf aufeinanderfolgende Läufe** statt eines einzelnen:

| Metrik | Median | Spanne (5 Läufe) | 18.07. | Schranke |
|---|---|---|---|---|
| `tools/call` p50 | **1,5 ms** | 1,4 – 1,6 | 1,7 ms | — |
| `tools/call` **p95** | **9,3 ms** | 9,0 – 11,7 | 7,3 ms | ≤ 50 ms ✅ |
| `tools/call` p99 | 15,7 ms | 15,0 – 17,2 | 14,9 ms | — |
| `tools/list` p50 | 4,3 ms | 4,3 – **29,6** | — | — |
| `tools/list` **p95** | **14,2 ms** | 14,1 – **41,8** | 59,1 ms | ≤ 200 ms ✅ |
| Fehler unter Last | **0** | 0 in allen fünf | 0 | 0 ✅ |

**Beide Schranken halten, mit Faktor ~5 bzw. ~14 Reserve.**

### Warum hier fünf Läufe stehen und nicht einer

Die fetten Ausreißer in der Spanne von `tools/list` gehören **alle zum ersten Lauf** (p50 29,6 ms
statt 4,3 ms, p95 41,8 ms statt 14,1 ms). Das ist der Kaltstart: JIT, erster Prozessstart des
Upstreams, noch leere Caches. Ab dem zweiten Lauf liegen die Werte eng beieinander.

Daraus folgt zweierlei, und das Zweite ist unbequem:

1. **Ein einzelner Lauf ist eine Anekdote.** Der Unterschied zwischen 7,3 ms und 9,3 ms bei
   `tools/call` liegt in derselben Größenordnung wie die Streuung zwischen zwei Läufen — daraus
   eine Verschlechterung durch den Protokollwechsel abzuleiten, wäre nicht gedeckt.
2. **Der Sprung bei `tools/list` von 59,1 ms auf 14,2 ms ist vermutlich kein Fortschritt, sondern
   ein Messfehler von damals.** 59,1 ms passt genau zum Bild eines ersten, kalten Laufs. Der
   Harness hat keine Aufwärmphase; die Zahl vom 18.07. dürfte eine solche sein.

### Was diese Zahlen NICHT belegen

- **Keinen Durchsatz.** Der Harness meldet „~7800 Calls/s", misst dafür aber ein Fenster von
  **0,1 Sekunden** (1000 Calls im Stoß). Das ist eine Spitzenrate unter idealen Bedingungen, keine
  Dauerlast — die Zahl steht deshalb nicht mehr im README.
- **Kein echtes Netz.** Der Harness fährt gegen einen In-Memory-Host: kein TCP, kein TLS, kein
  Reverse Proxy. Genau diese drei stehen im Produktivaufbau davor.
- **Keine Trennung von Gateway- und Fremdanteil.** Der Testserver antwortet ohne nennenswerte
  Latenz; wie viel vom p95 der Gateway verursacht, ist damit nicht bestimmbar.
- **Keinen Vergleich der Betriebsarten.** Sessionlos gegen Sitzungsbetrieb ist seit ADR-0023 die
  interessanteste Frage — und ungemessen.

### Was den Harness belastbar machen würde

Aufwärmphase vor der Messung; Dauerlast über 30–60 s statt eines Stoßes; Median aus mehreren Läufen
als Berichtswert; ein Lauf über echtes Kestrel/TCP; ein Upstream mit definierter künstlicher
Latenz; und ein A/B zwischen `BIFROST_MCP_STATELESS=1` und `=0`.

## Netzrealistische Messung (WP5.3, 2026-08-02)

Die Abschnitte oben messen gegen einen **In-Memory-Host**. Diese Messung schließt die vier Lücken,
die dort unter „Was diese Zahlen NICHT belegen" stehen: echter Serverprozess, echtes TCP, echte
TLS-Terminierung, Dauerlast statt Stoß, getrennte Spans und ein A/B zwischen sessionlosem und
Sitzungsbetrieb.

| | |
|---|---|
| **Harness** | `NetworkRealisticBenchmarkTests` (`tests/Bifrost.Performance.Tests`) |
| **Gateway** | eigener Prozess, Kestrel auf `127.0.0.1`, Zugang über `--issue-bootstrap-key` |
| **Upstream** | `SlowServer`, angefordert 20 ms — abgezogen wird der **selbst gemessene** Span |
| **Last** | 32 virtuelle Clients, 5 s Aufwärmen, 3 × 30 s Dauerlast je Szenario |
| **Umfang** | ~92 000 Aufrufe je Szenario |
| **Maschine** | Ryzen 7 9800X3D, 16 logische Prozessoren, Windows 11 (10.0.26200), .NET 10.0.4 |
| **Artefakt** | `artifacts/perf/20260802-153519-d838e9.json` (nicht versioniert; CI lädt es als Artefakt hoch) |

### Ergebnis — Median aus drei Läufen

| Szenario | gesamt p50 | gesamt p95 | Upstream p50 | **Gateway p50** | **Gateway p95** | CPU | RSS | Fehler |
|---|---|---|---|---|---|---|---|---|
| sessionlos / TCP | 31,3 ms | 32,4 ms | 29,8 ms | **1,43 ms** | **2,18 ms** | 39 % eines Kerns | 488 MB | 0 |
| Sitzung / TCP | 31,3 ms | 32,4 ms | 30,0 ms | **1,34 ms** | **1,98 ms** | 51 % | 549 MB | 0 |
| sessionlos / TLS | 31,3 ms | 32,4 ms | 29,8 ms | **1,49 ms** | **2,22 ms** | 59 % | 497 MB | 0 |

Die Streuung der drei Läufe je Szenario liegt bei der p95 des Gateway-Anteils zwischen 1,96 und
2,63 ms — der erste Lauf ist jeweils der höchste, danach liegen die Werte eng beieinander.

**NFR-01 (p95 ≤ 50 ms) hält auch über die echte Strecke, mit Faktor ~23 Reserve.**

### Drei Ergebnisse, die vorher nicht zu haben waren

1. **Der Gateway-Anteil ist klein und stabil.** 1,4 ms Median, 2,2 ms p95 — über TCP, über TLS und
   in beiden Betriebsarten praktisch gleich. Was ein Aufruf kostet, bestimmt die Gegenstelle.
2. **TLS kostet CPU, keine Latenz.** Gleiche Perzentile, aber ~50 % mehr CPU-Zeit (39 % → 59 % eines
   Kerns) und deutlich mehr Handles. Für eine kleine VM ist das die relevantere Zahl.
3. **Sessionlos und Sitzungsbetrieb nehmen sich in der Latenz nichts.** Der Sitzungsbetrieb liegt
   sogar minimal darunter, kostet dafür mehr CPU und Arbeitsspeicher. Die Frage aus
   [ADR-0023](../adr/0023-stateless-kern-und-mrtr.md) ist damit gemessen statt vermutet.

### Der Fehler im ersten Lauf dieses Harness

Der erste Durchgang meldete **11 ms Gateway-Overhead** — das Achtfache des heutigen Werts. Ursache
war nicht das Produkt, sondern der Messstand: Er zog die **angeforderten** 20 ms Upstream-Zeit ab,
während `Task.Delay(20)` unter Windows regelmäßig gut 31 ms dauert. Die Auflösung des Systemtimers
erschien damit als Gateway-Anteil.

Deshalb misst der Upstream seinen Span jetzt selbst (`sleep_measured`) und der Harness zieht die
**gemessene** Zeit ab, je Aufruf. Die Lehre ist unangenehm allgemein: Ein abgezogener Fremdanteil,
den niemand gemessen hat, ist eine Annahme — und sie fällt auf die Seite, auf der das eigene Produkt
schlechter aussieht, oder eben besser.

### Was auch diese Messung NICHT belegt

- ~~**Keinen Reverse-Proxy-Sprung.**~~ **Nachgemessen am 2026-08-04** — siehe den Abschnitt unten.
- **Keine Kapazitätsgrenze.** Der Lastlauf ist ein geschlossener Regelkreis mit fester
  Nebenläufigkeit; der Durchsatz (~1020 Aufrufe/s) ergibt sich aus 32 Clients ÷ 31 ms Antwortzeit.
  Was der Gateway trägt, sagen der Overhead je Aufruf und die CPU-Zeit.
- **Keine kleine VM.** Gemessen wurde auf einer 16-Kern-Maschine. Die Referenz aus NFR-01 ist eine
  4-Kern-VM; der limitierte Container aus dem Pflichtenheft steht aus.
- **Keine GC-Kennzahlen.** Von außen sind CPU, Arbeitsspeicher, Threads und Handles zu haben —
  Sammlungen je Generation nicht.
- **Kein Linux.** Der Lauf ist Windows; der CI-Job `benchmark` fährt ihn auf `ubuntu-latest`.

## Der Reverse-Proxy-Sprung (WP5.3, 2026-08-04)

Die Messung oben terminiert TLS im Gateway selbst. Im Produktivaufbau steht davor ein nginx oder
Traefik — eine zweite Verbindung, ein zweiter Prozess, ein zweites Mal Serialisieren. Was das
kostet, stand bisher unter „Was auch diese Messung NICHT belegt". Jetzt steht es hier.

| | |
|---|---|
| **Harness** | `ReverseProxyHopTests` (`tests/Bifrost.Performance.Tests`) |
| **Proxy** | `nginx:1.27-alpine` als **Container**, mit der Konfiguration aus `docs/operations.md`: `Host` durchgereicht, `X-Forwarded-Proto: https`, `X-Forwarded-For`, `proxy_buffering off` |
| **Gateway** | eigener Prozess, HTTP auf `127.0.0.1`, `BIFROST_TRUSTED_PROXIES=any` |
| **Upstream** | `SlowServer`, angefordert 20 ms — abgezogen wird der selbst gemessene Span |
| **Last** | 16 virtuelle Clients, 3 s Aufwärmen, 15 s Dauerlast je Weg, ~7 600 Aufrufe je Weg |
| **Maschine** | Ryzen 7 9800X3D, Windows 11 (10.0.26200), Docker Desktop |

### Ergebnis

| Weg | gesamt p50 | gesamt p95 | Upstream p50 | **Gateway + Netz p50** | **p95** | Fehler |
|---|---|---|---|---|---|---|
| direkt | 31,5 ms | 32,2 ms | 30,3 ms | **1,12 ms** | **1,62 ms** | 0 |
| über nginx | 31,3 ms | 32,5 ms | 28,6 ms | **2,70 ms** | **3,54 ms** | 0 |

**Der Sprung kostet rund 1,6 ms an der p95** (zweiter Lauf: 1,69 ms; erster: 1,92 ms). Damit
verdoppelt der Proxy den Anteil, den Gateway und Netz zusammen ausmachen — und dieser Anteil bleibt
trotzdem klein gegenüber der Antwortzeit der Gegenstelle. **NFR-01 hält auch über die Strecke mit
Proxy.**

### Warum die Differenz gemessen wird und nicht der absolute Wert

Derselbe Lastlauf geht einmal direkt und einmal durch den Proxy; die Differenz ist der Anteil des
Proxy. Ein einzelner Proxy-Lauf für sich sagte nichts — die Fremdlatenz des Upstreams steckt in
beiden Zahlen und ist um eine Größenordnung größer als das, was hier gesucht wird.

Aus demselben Grund prüft der Test **keine feste Millisekundenschranke** für den Proxyanteil: Auf
geteilter Hardware wäre das eine Wette. Geprüft wird die Größenordnung — der Proxy darf den
Gateway-Anteil nicht um ein Vielfaches überschreiten — und dass kein einziger Aufruf scheitert.

### Was auch dieser Lauf nicht belegt

- **Kein TLS am Proxy.** Der Sprung läuft über HTTP; gemessen ist die zweite Verbindung, nicht die
  TLS-Terminierung davor. Was TLS kostet, steht in der Messung darüber: CPU, keine Latenz.
- **Kein Traefik, kein Caddy, kein Cloud-Loadbalancer.** Gemessen ist nginx mit der empfohlenen
  Konfiguration. Ein Proxy, der puffert, verhält sich bei Server-Sent Events anders — deshalb steht
  `proxy_buffering off` in der Empfehlung und in dieser Messung.
- **Keine Kette.** `ForwardLimit = 1`: Der Gateway glaubt genau einem Absender. Ein Aufbau mit
  zwei Proxies hintereinander ist weder gemessen noch vorgesehen.

## Wiederholen

```powershell
$env:BIFROST_RUN_BENCHMARK = "1"
dotnet test tests/Bifrost.Performance.Tests --logger "console;verbosity=detailed"
```

Kürzer für einen Rauchtest — das Ergebnis wird dann als `"complete": false` markiert und taugt
**nicht** für eine Abnahmeaussage:

```powershell
$env:BIFROST_PERF_DURATION_SECONDS = "8"; $env:BIFROST_PERF_RUNS = "1"
```

Die ältere In-Process-Messung liegt im selben Projekt:

```powershell
$env:BIFROST_RUN_BENCHMARK = "1"
dotnet test tests/Bifrost.Performance.Tests --filter "FullyQualifiedName~PerformanceBenchmark" `
  --logger "console;verbosity=detailed"
```

Der Test schlägt fehl, wenn p95 der Calls über 50 ms, p95 von `tools/list` über 200 ms liegt oder
unter Last ein Call scheitert — er ist also zugleich der Regressionsschutz für NFR-01/02.

In GitHub Actions läuft er im Job `benchmark`: automatisch montags um 03:00 UTC und jederzeit über
**Actions → ci → Run workflow**.
