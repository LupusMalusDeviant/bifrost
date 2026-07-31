# ADR-0009: HTTP+SSE nur upstream, nicht downstream

- **Status:** Accepted, präzisiert am 2026-07-31 durch [ADR-0023](0023-stateless-kern-und-mrtr.md)
- **Datum:** 2026-07-18
- **Betrifft:** FR-02

> **Nachtrag 2026-07-31 — die Frist ist eine andere geworden.** Die Revision `2026-07-28` ist final,
> und HTTP+SSE bekommt darin die **reguläre Zwölf-Monats-Frist**, nicht die drei Monate aus dem
> Draft-Stand von SEP-2596. Die Entscheidung selbst bleibt damit unverändert richtig — nur der
> Zeitdruck war überschätzt. Der Review-Trigger unten liest sich entsprechend: ab 2026-07-28
> zwölf Monate, nicht drei.
>
> Am Verhalten ändert sich nichts: Der Client fällt weiterhin nur bei ausdrücklichem
> `AllowLegacySse` auf SSE zurück, und die Legacy-Endpunkte des Servers bleiben aus — im
> sessionlosen Betrieb sind sie ohnehin nicht mehr zuschaltbar, weil SSE Sitzungszustand im
> Speicher braucht.

## Kontext

FR-02 verlangt Unterstützung des HTTP+SSE-Transports. Seit MCP-Spec-Revision `2025-03-26` ist
HTTP+SSE durch Streamable HTTP abgelöst und durchgehend als deprecated geführt; offiziell
existieren nur noch stdio und Streamable HTTP. Der Draft nach `2025-11-25` stuft HTTP+SSE per
SEP-2596 formal als *Deprecated* ein — mit der **kürzesten Entfernungsfrist aller deprecateten
Features** (drei Monate nach Final, während andere die reguläre Zwölf-Monats-Frist bekommen).

Große Anbieter haben bereits abgeschaltet (Keboola 2026-04-01, Atlassian Rovo 2026-06-30). Eine
belastbare Zahl, wie viele Server heute noch ausschließlich SSE sprechen, ist nicht öffentlich;
die Einschätzung ist: wenige, aber langlebige Long-Tail-Deployments, die niemand mehr migriert.

Das C#-SDK behandelt beide Richtungen bewusst unterschiedlich:

- **Client:** `HttpClientTransportOptions.TransportMode` kennt `AutoDetect` (Default) — erst
  Streamable HTTP, dann SSE-Rückfall. Kein `[Obsolete]`.
- **Server:** die Legacy-Endpunkte `/sse` und `/message` sind standardmäßig aus, das Opt-in
  `EnableLegacySse` ist `[Obsolete]` (MCP9004), begründet u.a. mit fehlendem HTTP-Backpressure.

## Entscheidung

FR-02 wird **richtungsabhängig** erfüllt:

- **Upstream (Gateway als Client):** HTTP+SSE wird unterstützt. Der Konnektor setzt
  `TransportMode` explizit auf `AutoDetect` — nicht implizit über den SDK-Default, damit ein
  SDK-Upgrade die Fähigkeit nicht stillschweigend entfernt. Pro Server abschaltbar über
  `HttpTransportOptions.AllowLegacySse`.
- **Downstream (Gateway als Server):** nur Streamable HTTP. `EnableLegacySse` bleibt aus.

## Begründung

Transport-Heterogenität wegzukapseln ist der Daseinszweck eines Gateways: Ein einzelner
SSE-only-Upstream soll kein Grund sein, ihn gar nicht anbinden zu können — und die Kosten sind
hier praktisch null, weil das SDK es mitbringt. Ein bewusster Verzicht wäre aktive Mehrarbeit bei
gleichzeitigem Funktionsverlust.

Nach außen gilt das Gegenteil. Ein Gateway bündelt per Definition fremden Traffic; ein Transport
ohne HTTP-Backpressure ist dort Angriffsfläche, nicht Komfort. Clients, die 2026 noch kein
Streamable HTTP sprechen, sind kein Zielpublikum für eine Neuentwicklung, und das SDK würde die
Unterstützung mit einer Obsolete-Warnung quittieren.

## Konsequenzen

- Upstream-Kompatibilität zum Nulltarif; kein eigener SSE-Konnektor-Code, der gepflegt werden muss.
- Die Fähigkeit hängt an einem SDK-Default. Deshalb hält `LegacySseSupportTests` fest, dass
  `HttpTransportMode.Sse`/`AutoDetect` existieren — verschwinden sie, bricht ein Test statt der
  Produktion.
- **Review-Trigger:** Erreicht SEP-2596 den Status *Final*, läuft die Drei-Monats-Frist. Dann ist
  zu entscheiden, ob `AllowLegacySse` per Default auf `false` geht.
- Downstream-SSE-Clients können den Gateway nicht nutzen. Bewusst in Kauf genommen.

## Alternativen

- **SSE auch downstream anbieten:** verworfen — `[Obsolete]` im SDK, fehlendes Backpressure,
  kein belegter Bedarf.
- **SSE ganz weglassen und als Abweichung dokumentieren:** verworfen — hätte upstream Funktion
  gekostet und *zusätzlichen* Code gebraucht, um den SDK-Default abzuschalten.
