# ADR-0010: Sampling und Elicitation werden nicht durchgereicht

- **Status:** Accepted, ergänzt am 2026-07-25 durch [ADR-0019](0019-langlaufende-tasks-und-events.md)
- **Datum:** 2026-07-18
- **Betrifft:** FR-04 (Kann-Teil), ADR-0001 (Proxy-Architektur), ADR-0005 (Supervisor)

> **Ergänzung 2026-07-25 (ADR-0019).** Diese ADR gilt weiter für **synchrone, im Band laufende**
> Sampling- und Elicitation-Anfragen. ADR-0019 öffnet einen anderen Weg für Elicitation: den
> Task-Zustand `input-required`. Der ist nicht server-initiiert im Band, sondern ein persistierter
> Zustand, zu dem der Aufrufer zurückkommt — **die Task-Id ist genau die Korrelation, die das
> Protokoll nicht trägt** (Grund 1 unten), und der Task-Store ersetzt den fehlenden Request-Kontext
> im SDK-Handler (Grund 2). Grund 3 ist zur Hälfte überholt: Die `IdentityId` endet seit
> `b7638f9` nicht mehr im `ToolInvoker`, sondern erreicht über `ICallerAwareUpstreamConnection` den
> Connector; die geteilte Verbindung je `ServerId` bleibt.
>
> **Sampling ist davon unberührt und bleibt verworfen.** Der Kosten-, Modellzugriffs- und
> Prompt-Injection-Einwand unten gilt unverändert — er hängt nicht an der Korrelation.

## Kontext

FR-04 fordert als **Kann**-Teil, dass der Gateway `sampling/createMessage` und
`elicitation/create` durchreicht. Beides sind **server-initiierte** Anfragen: Der Upstream-Server
fragt, der Downstream-Agent antwortet. Der bisherige Datenfluss läuft ausschließlich vorwärts —
Agent → Gateway → Upstream.

Eine Machbarkeitsprüfung gegen SDK `ModelContextProtocol` 1.4.1 und die Spezifikation ergab:

**Beide Enden sind vorhanden.**

- Client-Seite: `McpClientHandlers.SamplingHandler` / `.ElicitationHandler`; das SDK annonciert die
  Capability beim Initialize genau dann, wenn der Handler gesetzt ist.
- Server-Seite: `McpServer.SampleAsync(...)` / `ElicitAsync(...)`, dazu `McpServer.ClientCapabilities`.
- Der Gateway annonciert heute nichts davon (`McpClient.CreateAsync` ohne `McpClientOptions`),
  Upstreams fragen also korrekterweise nie an.

**Die Mitte fehlt — und zwar strukturell.** Fragt ein Upstream nach Sampling, lässt sich nicht
bestimmen, *welcher* Agent gemeint ist:

1. **Das Protokoll trägt keine Korrelation.** `sampling/createMessage` kennt kein Feld, das auf den
   auslösenden Tool-Call zeigt. Die Zuordnung ist aus der Nachricht nicht lesbar.
2. **Der SDK-Handler bekommt keinen Kontext** — nur Params und `CancellationToken`, kein
   `RequestContext`, keine Request-Id. `AsyncLocal` trägt nicht, weil der Handler auf der
   Empfangsschleife des Transports läuft, nicht im Async-Kontext des Aufrufers.
3. **Die Verbindung ist geteilt, die Identität ist vorher weg.** Der Supervisor hält genau eine
   Verbindung je `ServerId`, und `IUpstreamConnection.CallToolAsync(toolName, args, ct)` hat keinen
   Caller-Parameter — die `IdentityId` endet im `ToolInvoker`.

## Entscheidung

Sampling und Elicitation werden **nicht** durchgereicht. Der Gateway annonciert die Capabilities
weder upstream noch downstream. FR-04 gilt im Tool-/Resource-/Prompt-Teil als erfüllt, im
Sampling-/Elicitation-Teil als **bewusst nicht umgesetzt**.

## Begründung

**Aufwand.** Tragfähig wäre nur ein Verbindungs-Pool je `(ServerId, IdentityId)`. Das kippt die
1:1-Annahme, auf der die gesamte Supervisor-Zustandsmaschine aufsetzt — Health-Loop,
Restart-Backoff, Discovery→Katalog-Übergang. Bei stdio-Upstreams entstünden zusätzlich
N Agenten × M Server Kindprozesse, was dem Fan-in-Zweck eines Gateways zuwiderläuft (ADR-0001).
Geschätzt **16–26 PT bei ±40 % Unsicherheit**, der Großteil davon im Supervisor-Umbau.

**Sicherheit — der eigentliche Grund.** Sampling dreht das Vertrauensmodell um: Ein
Upstream-Server löst LLM-Inferenz beim Agenten aus, auf dessen Kosten, mit dessen Modell, mit vom
Upstream kontrolliertem Prompt. Das ist ein Prompt-Injection-Vektor mit Rechnung. Nötig wären
mindestens: RBAC in Gegenrichtung (die `ToolAction`-Achse kennt so etwas nicht), ein Token-Budget
je (Upstream, Identität), Audit inklusive Redaction der Sampling-Payloads, und ein Filter für
`modelPreferences` — sonst fordert ein Upstream einfach das teuerste Modell an.

Entscheidend ist aber die Zusage, die der Gateway **nicht einlösen kann**: Die Spezifikation sagt,
ein Mensch solle eine Sampling-Anfrage immer ablehnen können. Zwischen einem headless Agenten und
einem Upstream-Server sitzend hat der Gateway niemanden, den er fragen könnte. Ein Feature
auszuliefern, dessen Sicherheitsversprechen die Architektur nicht trägt, ist schlechter als es
wegzulassen — der Betreiber würde einen Schutz annehmen, den es nicht gibt.

**Priorität.** FR-04 führt diesen Teil als **Kann**. Es wird also keine Soll-Zusage gebrochen.

## Konsequenzen

- Upstream-Server, die Sampling oder Elicitation benötigen, funktionieren hinter dem Gateway nur
  eingeschränkt — sie sehen einen Client ohne diese Capabilities und müssen ohne auskommen.
  Server, die das zwingend brauchen, sind direkt anzubinden statt über den Gateway.
- Kein Rückkanal heißt auch: kein Rückkanal als Angriffsfläche.
- Die 1:1-Annahme „eine Verbindung je Upstream" bleibt gültig und trägt Supervisor und Katalog.

## Wenn es doch gebaut wird

Dann in dieser Reihenfolge, nicht anders:

1. **Elicitation zuerst, Sampling getrennt entscheiden.** Elicitation kostet kein Geld, gibt keinen
   Modellzugriff frei und ist bei Mehrdeutigkeit gefahrlos ablehnbar.
2. **Hybrider Pool:** geteilte Verbindung als Default, dedizierte Verbindung nur für Upstreams, die
   per Konfiguration ausdrücklich als sampling-/elicitation-fähig markiert sind.
3. **Sampling nie als Default-an-Feature** — Opt-in pro Upstream *und* pro Identität, mit eigener ADR.

## Alternativen

- **„Nur-ein-Call-in-Flight"-Heuristik** (Anfrage dem einzigen laufenden Call zuordnen, sonst
  ablehnen): verworfen. Entweder serialisiert man Calls pro Upstream und verliert den Durchsatz,
  oder man lehnt unter Last sporadisch ab — ein nicht-deterministisches Verhalten, das sich im
  Betrieb nicht erklären lässt.
- **Verbindung je Aufrufer als Default:** verworfen, siehe Aufwand und Prozess-Explosion.
