# ADR-0019: Persistentes Task- und Event-Modell

- **Status:** Akzeptiert
- **Datum:** 2026-07-24, entschieden 2026-07-25 mit dem Product Owner
- **Betrifft:** ADR-0012 (Approval-Flows), ADR-0010 (Sampling/Elicitation), ADR-0015
  (Capability-Modell), Plan 0003 (WASI-Streams)
- **Entscheidungsmaterial:** [0019-entscheidungsmaterial.md](0019-entscheidungsmaterial.md) — was
  von diesem Entwurf im Code schon existierte, was er still voraussetzte, und die vier Fragen, an
  denen die Umsetzung hing.

## Kontext

AsyncAPI und A2A benötigen langlebige Zustände, Follow-up-Input, Fortschritt, Events und
Wiederaufnahme. Ein synchroner Tool-Call kann diese Semantik nicht zuverlässig oder auditierbar
abbilden. A2A unterscheidet unter anderem working, completed, failed, canceled und input-required
und besitzt eigene Task-/Context-IDs sowie Polling-, Streaming- und Push-Updates.

Grundlagen:

- [AsyncAPI 3.0.0](https://www.asyncapi.com/docs/reference/specification/v3.0.0)
- [A2A Specification](https://a2a-protocol.org/latest/specification/)

Der ursprüngliche Entwurf dieser ADR (2026-07-24) beschrieb bereits eine vollständige Lösung. Die
Prüfung gegen den Code ergab, dass er an vier Stellen mehr zusagte, als der Anlass trägt, und an
einer Stelle eine bestehende Entscheidung berührt, ohne es zu sagen. Die folgenden Abschnitte sind
das Ergebnis dieser Prüfung; die abgelösten Zusagen stehen in [Verworfen](#verworfen-gegenüber-dem-entwurf).

## Entscheidung

### 1. TaskV1 verallgemeinert die Freigabe

Ein Task ist der eine persistierte Vorgang, der einen Request überlebt — die Freigabe-Anfrage aus
[ADR-0012](0012-approval-flows-asynchron.md) geht darin auf und wird der Task-Zustand
`input-required`. **Eine Tabelle, eine API, eine Liste in der UI.** Ein Betreiber soll einen Vorgang
an genau einer Stelle sehen.

Persistiert werden ID, Capability-ID, Connector/Upstream, Eigentümeridentität, delegierte
Identitätskette, Correlation-ID, Zustand, Fortschritt, Eingabe-Fingerprint, redigierte Eingabe,
Ergebnis/Artifact oder strukturierter Fehler, Created/Updated/Expires, Cancellation-Status und
optional erwartetes Folge-Input-Schema.

Zustände: `created → working → completed|failed|cancelled|expired`; `working ↔ input-required` ist
zulässig. Terminalzustände sind unveränderlich. Updates verwenden eine monotone Revision für
optimistische Konkurrenzkontrolle.

Zwei Auflagen, die aus der Verallgemeinerung folgen:

- **Der heiße Pfad bleibt heiß.** `TryConsumeApprovalAsync` läuft heute vor *jedem* Tool-Call. Der
  Ersatz braucht einen Index auf (Eigentümer, Tool, Eingabe-Fingerprint, Zustand); ohne ihn zahlt
  jeder Aufruf für ein Modell, das die wenigsten Aufrufe brauchen.
- **Bestehende Freigaben werden migriert**, nicht verworfen. Eine Installation mit wartenden
  Freigaben darf durch das Update keine verlieren.

**Umsetzungsnachtrag 2026-07-26.** Die Zustandsliste oben deckt einen Fall nicht ab, der beim Bauen
auffiel: Die Freigabe kannte `Approved` **und** `Consumed`, der Task-Automat hat für „freigegeben,
aber noch nicht eingelöst" keinen eigenen Zustand — beides ist `working`. Ohne Unterscheidung liefe
ein zweiter identischer Call erneut durch, und eine erteilte Zustimmung wäre der
Dauerfreifahrtschein, den ADR-0012 ausschließt. Gelöst über einen **Claim-Zeitpunkt** am Vorgang,
nicht über einen zusätzlichen Zustand: Das lässt diese Liste unverändert und macht die Einmaligkeit
nachprüfbar. Der Hot-Path-Index war übrigens kein Neubau — `(Aufrufer, Tool, Fingerprint, Zustand)`
gab es bei den Freigaben schon und ist mitgewandert.

### 2. Geholt wird, geschickt wird nur zur Beschleunigung

**Der Vertrag ist Polling.** Der Zustand steht in der Datenbank; wer ihn will, holt ihn — über
`/api/v1/tasks/{id}` oder als Task-Resource über MCP. Eine Notification sagt ausschließlich „da hat
sich etwas geändert, schau nach" und trägt keine Nutzlast, auf die sich jemand verlassen darf.

Damit ist ein verlorenes Ereignis **Latenz, kein Korrektheitsfehler**. Das ist der ganze Punkt: Es
gibt in V1 keine Zustellgarantie, weil keine gebraucht wird.

Das Push-Substrat existiert bereits (`McpSessionRegistry` kennt Session und Identität und sendet
heute `tools/list_changed`) und wird dafür mitbenutzt. Was **nicht** gebaut wird, steht unter
[Verworfen](#verworfen-gegenüber-dem-entwurf).

### 3. Abbruch ist persistiert, und `confirmed` nur, wo es belegbar ist

Ein Task überlebt den Request — ein `CancellationToken` kann das Mittel deshalb nicht mehr sein. Der
Abbruch ist ein **persistiertes Kennzeichen**, das der Ausführende abfragt. Er ist idempotent und
unterscheidet `requested` von `confirmed`.

`confirmed` wird nur gesetzt, wo der Transport es wirklich hergibt:

| Transport | Bestätigung |
|---|---|
| MCP-Upstream (stdio/SSE) | ja — `notifications/cancelled` |
| CLI ([ADR-0014](0014-cli-programme-als-upstream-transport.md)) | ja — Prozessbaum-Kill ist gebaut |
| OpenAPI/HTTP | nein — Verbindungsabbruch ohne Zusage über die Gegenseite |
| WASI ([ADR-0020](0020-wasi-runtime-out-of-process-rust-host.md)) | ja, seit IPC-Vertrag v4 (2026-07-25) — `cancel` trappt den Guest über die Epoche, und `confirmed` wird erst gemeldet, wenn der Aufruf wirklich geendet hat. Bleibt das binnen fünf Sekunden aus, steht `confirmed: false`, und es greifen wieder nur Fuel und Frist |

> **Nachtrag 2026-07-27.** Diese Tabelle beschreibt Vorgänge, bei denen etwas *läuft*. Für einen
> Vorgang, bei dem **nichts** läuft — eine wartende Freigabe oder eine erteilte, noch nicht
> eingelöste — ist der Abbruch sofort belegbar und wird deshalb unmittelbar `confirmed`, Zustand
> `cancelled`. Der Grund ist derselbe wie für die Tabelle: Bestätigt wird, was sich bestätigen
> lässt. Bis dahin war der Abbruch dort ein Vermerk **ohne Wirkung** — eine widerrufene Freigabe
> blieb einlösbar, weil niemand das Feld auswertete. Das war kein Teil dieser Entscheidung, sondern
> eine Lücke in ihrer Umsetzung.

Ein Transport ohne Bestätigung bleibt bei `requested` stehen. Das ist ausdrücklich so gewollt: Ein
`confirmed`, das niemand einlöst, wäre ein Feld, das Sicherheit vortäuscht — und im Audit ist
„wurde der Upstream wirklich gestoppt" genau die Frage, die gestellt wird.

### 4. `input-required` ist Elicitation über Tasks

[ADR-0010](0010-sampling-elicitation-nicht-durchreichen.md) hat Elicitation verworfen, weil das
Protokoll keine Korrelation trägt, der SDK-Handler keinen Request-Kontext bekommt und die
Verbindung je `ServerId` geteilt ist. Diese Gründe gelten unverändert für **synchrone, im Band
laufende** Elicitation.

`input-required` ist ein anderer Weg: nicht server-initiiert im Band, sondern ein persistierter
Zustand, zu dem der Aufrufer zurückkommt. **Die Task-Id ist genau die Korrelation, die das Protokoll
nicht trägt.** Damit fallen die Gründe 1 und 2 weg, und ADR-0010s eigene Reihenfolge für den Fall,
dass es doch gebaut wird, lautet „Elicitation zuerst, Sampling getrennt entscheiden".

Das wird hier **ausdrücklich** entschieden und nicht über ein Zustandsfeld eingeschlichen:

- Elicitation über Tasks ist zulässig. Ein Upstream, der Folge-Input braucht, setzt den Task auf
  `input-required` und hinterlegt das erwartete Schema.
- **Sampling bleibt draußen.** Der Kosten- und Prompt-Injection-Einwand aus ADR-0010 ist von dieser
  Entscheidung unberührt und gilt weiter.

## Berechtigungen und Audit

Tasks sind keine global sichtbaren Nebenprodukte. Lesen, Folgen, Canceln und Folge-Input prüfen
Eigentümer, delegierte Grants und Capability-Scope. Redaction geschieht **vor** der Persistenz — wie
schon bei den Freigaben, die nie die rohen Argumente halten.

Jede Zustandsänderung trägt dieselbe Audit-Correlation wie die ursprüngliche Invocation. **Diese
Correlation existiert heute nicht**: `AuditEvent` hat kein solches Feld, und im gesamten `src/` gibt
es weder `Correlation` noch `Activity`/`TraceId`. Sie einzuführen — im Audit-Datensatz, in der
Migration, im Invoker — ist Teil dieser Umsetzung und nicht vorausgesetzt.

## Darstellung

REST erhält `/api/v1/tasks`. **Paginierung bleibt vorerst beim bestehenden Offset-Stil**
(`PagedResult` mit `Page`/`PageSize`, wie in Audit und REST): Cursor-Pagination löst ein Problem,
das ohne Event-Strom nicht auftritt, und zwei Paginierungsstile im Haus sind teurer als der
spätere Wechsel an einer Stelle.

MCP bleibt kompatibel: synchrone Capabilities liefern weiterhin direkt Ergebnisse; asynchrone
liefern eine Task-Resource und optionale Notifications. Keine bestehende Toolantwort wird still in
Polling umgedeutet.

## Verworfen gegenüber dem Entwurf

Der Entwurf vom 2026-07-24 sagte mehr zu, als der Anlass trägt. Ausdrücklich **nicht** Teil von V1:

- **EventV1 als eigenständiges Modell** mit Topics, Subscriptions, Cursor, TTL und Delivery-Policy.
  Ereignisse sind in V1 nur Hinweise auf einen Task-Zustand.
- **at-least-once mit Dedup per Event-Id, begrenzten Retries und Dead-Letter-Queue.** Das ergibt nur
  Sinn, wenn Zustellung der Vertrag ist — sie ist es nicht (Entscheidung 2). Es ist der mit Abstand
  größte Brocken und der einzige ohne Vorbild im Repo.
- **Backpressure und Größenlimits vor der Persistenz** als eigener Mechanismus. Die bestehenden
  Output-Limits und die Redaction greifen bereits vor dem Schreiben.
- **Cursor-Pagination** (siehe Darstellung).

Diese Punkte sind nicht falsch, sondern vertagt. Sie kommen zurück, sobald ein Anwendungsfall
Zustellung als Zusage braucht — dann als eigene ADR, weil sie ein eigenes Betriebsversprechen sind.

## Konsequenzen

- **ADR-0012 geht in dieser ADR auf.** Die Freigabe bleibt als Begriff und als UI-Ansicht, ist
  technisch aber ein Task-Zustand. ADR-0012s Entscheidung „sofort ablehnen statt blockieren" bleibt
  unberührt gültig.
- **ADR-0010 ist ergänzt, nicht ersetzt.** Elicitation über Tasks ist zulässig; Sampling und
  synchrone, im Band laufende Elicitation bleiben verworfen.
- **AsyncAPI** folgt weiterhin erst nach Event-Persistenz mit Zusage — also nach der vertagten
  Stufe, nicht nach dieser ADR.
- **A2A** folgt erst nach Follow-up-Input, Cancellation, Artifact-Rechten, Delegationsbudgets und
  Loop-Erkennung. Diese ADR liefert die ersten beiden.
- **ADR-0015** bleibt vorgeschlagen. Task- und Event-Capability-Arten werden dort öffentlich, wenn
  Persistenz und Berechtigungen aus dieser ADR **implementiert** sind — die Entscheidung hier
  genügt dafür nicht.
- **WASI-Streams** (Plan 0003) sind damit auf einer Seite entschieden: Chunks werden geholt, Abbruch
  ist persistiert. **Nachtrag 2026-07-25:** Der IPC-Vertrag v4 ist gebaut — Korrelations-Ids,
  nebenläufige Aufrufe und ein `cancel`, das seine Wirkung belegt. Damit ist WASI in der Tabelle
  oben von „nein" auf „ja" gewechselt. **Die Streams selbst sind am selben Tag zurückgestellt
  worden:** Ein dynamischer Host kann sie nur für fest einkompilierte Payload-Typen lesen
  (`StreamReader<T>` verlangt ein statisches `T`, `Val` erfüllt das nicht), am Ende des
  asynchronen Umbaus stünde also `stream<u8>` und nicht „Streams". Die Polling-Entscheidung dieser
  ADR bleibt davon unberührt — sie gilt für Tasks, und die kommen ohne Streams aus.
