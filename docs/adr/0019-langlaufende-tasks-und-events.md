# ADR-0019: Persistentes Task- und Event-Modell

- **Status:** Vorgeschlagen
- **Datum:** 2026-07-24
- **Entscheidungsmaterial:** [0019-entscheidungsmaterial.md](0019-entscheidungsmaterial.md) — was
  von diesem Entwurf im Code schon existiert, was er still voraussetzt, und die vier Fragen, an
  denen die Umsetzung hängt (Stand 2026-07-25).

## Kontext

AsyncAPI und A2A benötigen langlebige Zustände, Follow-up-Input, Fortschritt, Events und
Wiederaufnahme. Ein synchroner Tool-Call kann diese Semantik nicht zuverlässig oder auditierbar
abbilden. A2A unterscheidet unter anderem working, completed, failed, canceled und input-required
und besitzt eigene Task-/Context-IDs sowie Polling-, Streaming- und Push-Updates.

Grundlagen:

- [AsyncAPI 3.0.0](https://www.asyncapi.com/docs/reference/specification/v3.0.0)
- [A2A Specification](https://a2a-protocol.org/latest/specification/)

## Entscheidung

### TaskV1

Ein Task persistiert ID, Capability-ID, Connector/Upstream, Eigentümeridentität, delegierte
Identitätskette, Correlation-ID, Zustand, Fortschritt, Eingabe-Fingerprint, Ergebnis/Artifact oder
strukturierten Fehler, Created/Updated/Expires, Cancellation-Status und optional erwartetes
Folge-Input-Schema.

Zustände: `created → working → completed|failed|cancelled|expired`; `working ↔ input-required` ist
zulässig. Terminalzustände sind unveränderlich. Updates verwenden eine monotone Revision für
optimistische Konkurrenzkontrolle. Cancellation ist idempotent und unterscheidet requested von
confirmed.

### EventV1

Ein Event persistiert ID, Topic, Quelle, Typ, Schema-ID, Timestamp, Correlation-/Causation-ID,
Eigentümer, redigierte Payload oder Artifact, Größe und Deduplizierungsschlüssel. Subscriptions
enthalten Identität, Filter, Cursor, TTL und Delivery-Policy.

V1 garantiert intern at-least-once; Consumer müssen per Event-ID deduplizieren. Retries sind
begrenzt und gehen anschließend in eine Dead-Letter-Queue. Backpressure und Größenlimits gelten vor
Persistenz.

## Berechtigungen und Audit

Tasks und Events sind keine global sichtbaren Nebenprodukte. Lesen, Folgen, Canceln und Folge-Input
prüfen Eigentümer, delegierte Grants und Capability-Scope. Jede Zustandsänderung trägt dieselbe
Audit-Correlation wie die ursprüngliche Invocation. Redaction geschieht vor Persistenz.

## Darstellung

REST erhält `/api/v1/tasks` und `/api/v1/events` mit Cursor-Pagination. MCP bleibt kompatibel:
synchrone Capabilities liefern weiterhin direkt Ergebnisse; asynchrone liefern eine Task-Resource
und optionale Notifications. Keine bestehende Toolantwort wird still in Polling umgedeutet.

## Konsequenzen

AsyncAPI folgt erst nach Event-Persistenz, Cursor, Backpressure und DLQ. A2A folgt erst nach
Task-Persistenz, Follow-up-Input, Cancellation, Artifact-Rechten, Delegationsbudgets und
Loop-Erkennung.
