# ADR-0015: Protokollneutrales Capability-Modell

- **Status:** Vorgeschlagen; das **Vokabular** ist am 2026-07-26 umgesetzt und angeschlossen, die
  Migration des Invocation-Pfads steht aus.
- **Datum:** 2026-07-24

> **Umsetzungsstand 2026-07-26.** Gebaut sind `CapabilityDescriptorV1`, `CapabilityResultV1` samt
> strukturiertem Fehler, `SchemaRef` mit Dialekt/Herkunft/Hash und der `LegacyCapabilityAdapter`.
> Angeschlossen ist es über `GET /api/v1/capabilities` — additiv neben `/tools`, RBAC-gefiltert.
> Ein Vokabular ohne Abnehmer wäre genau das Muster „Struktur da, aber nie angeschlossen", an dem
> dieses Repo schon 23 Lücken gefunden hat.
>
> **Drei Abweichungen von der Aufzählung oben**, alle in derselben Richtung — die Id soll stabil
> sein, und die ADR sagt das selbst:
>
> 1. Die **Schema-Version** geht *nicht* in die Id ein. Sonst ergäbe jeder zusätzliche Parameter am
>    Upstream eine neue Id, und RBAC-Grants sowie gepinnte Profile brächen bei jeder Schema-Pflege.
>    Die Fassung steht als `SchemaRef.Hash` daneben und bleibt sichtbar.
> 2. Die **Connector-Id** geht *nicht* ein: Die `ServerId` ist schon eindeutig, die Transportart ist
>    eine Eigenschaft dieses Upstreams. Sie mitzuhashen hätte nur bedeutet, dass die Id ohne
>    Konfigurationszugriff nicht mehr ableitbar ist — und die Capability-Sicht hätte Einträge ohne
>    Konfiguration stillschweigend verschluckt.
> 3. **Keine eigene Tabelle für Capability-Ids.** Die ADR verlangt „persistiert"; das Ziel dahinter
>    ist Stabilität, und die liefert die deterministische Ableitung aus (`ServerId`, nativer Name)
>    ohne Speicher. Eine Tabelle, die niemand liest, wäre Struktur ohne Abnehmer.
>
> **Was die Freigabe der Arten betrifft**, hält sich die Umsetzung an die Kompatibilitätsregel: Der
> `Task`-Art ist die Tür seit der Umsetzung von ADR-0019 offen. `Event` und `Subscription` bleiben
> zu — EventV1 mit Zustellzusage ist dort ausdrücklich vertagt, es gibt also keine Persistenz, auf
> die man sie stellen könnte. `AgentDelegation` wartet auf A2A. Die Arten stehen trotzdem im Enum,
> mit einer abrufbaren Begründung, warum sie nicht angeboten werden.

## Kontext

`IUpstreamConnection` trennt den Core bereits vom MCP-SDK, bildet aber Discovery und Invocation
primär als synchrone Tools, Resources und Prompts ab. OpenRPC, gRPC Unary und kuratierte GraphQL-
Operationen passen noch in dieses Modell; Streams, Broker-Events, lang laufende Tasks und
Agentendelegation würden dagegen Semantik verlieren.

## Entscheidung

Ein versioniertes `CapabilityDescriptorV1` wird additiv neben den bestehenden Deskriptoren
eingeführt. Bestehende MCP-, OpenAPI- und CLI-Inventare werden durch einen Adapter darauf
abgebildet; die aktuellen öffentlichen Interfaces bleiben bis zu einer eigenen Migration erhalten.

Eine Capability enthält:

- stabile ID aus Connector-ID, Upstream-ID, nativem Namen und Schema-Version;
- technischen Namen getrennt von Anzeigename und Beschreibung;
- Art: Tool/Command, Query, Mutation, Resource, Prompt, Event, Subscription, Task oder
  AgentDelegation;
- Eingabe- und Ausgabeschema mit Dialekt, Herkunft, Hash und nativer Version;
- Ausführungsart: synchron, asynchron, streaming;
- Seiteneffekt: read, write, destructive, privileged;
- Idempotenz, Cancellation, Retry-Semantik, erwartete Laufzeit und Approval-Default;
- Unterstützung für Binärdaten, Fortschritt, Pagination, Artifacts und Events.

Ein `CapabilityResultV1` ist eine diskriminierte Hülle aus strukturierten Daten, Text, begrenzten
Binärdaten, Artifact-Referenz, Task-ID oder Stream-/Event-Referenz. Fehler tragen stabilen
Gateway-Code, Connector-Code, retryable-Flag und redigierte Details. Truncation ist strukturiert und
nicht nur ein Textsuffix.

Jede Invocation läuft durch denselben Core-Service:

```text
Identity → RBAC → Schema → Risk → Guardrail → Approval → Limits
→ Connector → Output-Limit → Redaction → Audit → Result/Task/Event
```

Connectoren dürfen keine eigene alternative Invocation-Fassade veröffentlichen.

## Kompatibilität

- `ToolDescriptor` wird zunächst verlustfrei zu `CapabilityKind.Tool` adaptiert.
- Resources und Prompts behalten ihre bestehenden Endpunkte.
- Synchrone `IUpstreamConnection.CallToolAsync`-Implementierungen werden von einem
  `LegacyCapabilityAdapter` umschlossen.
- Neue Task-, Event- und Stream-Arten werden erst öffentlich, wenn Persistenz und Berechtigungen
  aus ADR-0019 implementiert sind.
- Capability-IDs werden persistiert; Anzeigenamen und Beschreibungen dürfen sich ändern, IDs nicht.

## Konsequenzen

Der Core erhält ein neutrales Vokabular, ohne jedes Protokoll auf einen synchronen Tool-Call zu
reduzieren. Der Preis ist eine zeitweise doppelte Deskriptorwelt. Diese Übergangsphase ist
absichtlich, weil ein Big-Bang-Ersatz bestehende Daten, MCP-Verträge und Connectoren unnötig
gefährden würde.
