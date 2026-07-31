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

## Wiederholen

```powershell
$env:BIFROST_RUN_BENCHMARK = "1"
dotnet test tests/Bifrost.Integration.Tests --filter "FullyQualifiedName~PerformanceBenchmark" `
  --logger "console;verbosity=detailed"
```

Der Test schlägt fehl, wenn p95 der Calls über 50 ms, p95 von `tools/list` über 200 ms liegt oder
unter Last ein Call scheitert — er ist also zugleich der Regressionsschutz für NFR-01/02.

In GitHub Actions läuft er im Job `benchmark`: automatisch montags um 03:00 UTC und jederzeit über
**Actions → ci → Run workflow**.
