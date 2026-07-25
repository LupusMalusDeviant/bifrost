# Entscheidungsmaterial zu ADR-0019 (Tasks und Events)

**Stand 2026-07-25. Dies ist Material, keine Entscheidung.** [ADR-0019](0019-langlaufende-tasks-und-events.md)
steht auf *Vorgeschlagen* und beschreibt bereits eine ziemlich vollständige Lösung — TaskV1, EventV1,
Zustände, at-least-once, REST-Endpunkte. Dieses Dokument prüft diesen Entwurf gegen den Code, der
heute wirklich da ist: Was davon existiert schon unter anderem Namen, was setzt der Entwurf still
voraus, und welche Fragen bestimmen tatsächlich die Umsetzung. Die Entscheidung trifft der Product
Owner.

Anlass ist eine konkrete Blockade: `stream<T>` und `future<T>` im WASI-Adapter (Plan 0003) hängen an
diesem Modell. Abschnitt 4 sagt, welche der Fragen dafür beantwortet sein müssen — und welche nicht.

## 1. Was heute existiert

| Baustein | Stand | Ort | Was er von TaskV1/EventV1 schon abdeckt |
|---|---|---|---|
| Freigabe-Queue (ADR-0012) | fertig, persistiert, REST + UI | `Approvals.cs`, `ApprovalRequestRow`, Migration `AddApprovals` | Id, Eigentümer, Tool, Argument-Fingerprint, **redigierte** Argumente, Zustandsautomat (`Pending → Approved → Consumed` / `Denied`), Ablaufzeit, idempotentes Einreihen. Das ist der Großteil von TaskV1 — für genau einen Anwendungsfall |
| Audit | fertig, persistiert, Hintergrund-Writer + Retention | `Audit.cs`, `AuditWriterService`, `AuditRetentionService` | Zeitstempel, Aufrufer, Herkunft, Status, redigierte Argumente/Antwort, Filter, Paginierung |
| MCP-Session-Verzeichnis | fertig | `McpSessionRegistry` | **Push zum Agenten ist vorhanden**: pro Session ein `McpServer`, die Identität dazu bekannt, `SendNotificationAsync` läuft heute für `tools/list_changed` |
| Hintergrunddienste | etabliertes Muster | `HostedServices.cs` | Kanal-Enqueue, Batch-Writer, periodisches Aufräumen — das Gerüst für Zustell- und Verfallsläufe |
| Webhooks (ADR-0013) | fertig | `Webhooks.cs`, `WebhookStore` | **Nur eingehend**: ein signierter Aufruf löst ein Tool aus. Ausgehende Zustellung gibt es nicht |
| Upstream-Notifications | Leitung vorhanden, **niemand hört zu** | `IUpstreamConnection.NotificationReceived` | Jeder Connector und der `GuardedUpstreamConnection` reichen das Ereignis durch. Im ganzen Repo gibt es **keinen einzigen Abonnenten** — Ereignisse aus Upstreams werden heute eingesammelt und fallengelassen |
| Aufrufer-Identität bis zum Connector | seit `b7638f9` | `ICallerAwareUpstreamConnection` | Wer aufruft, ist jetzt bis in den Connector bekannt. Vorher endete die `IdentityId` im `ToolInvoker` — siehe Frage 4 |

Der letzte Punkt der Tabelle ist der auffälligste Befund: Das Ereignis-Rohr ist quer durch alle
Connectoren verlegt und mündet ins Nichts. Wer EventV1 baut, baut nicht bei null an — aber er baut
auch nicht auf etwas Erprobtem, denn diese Leitung hat noch nie etwas transportiert.

## 2. Was der Entwurf voraussetzt, das es nicht gibt

Vier Zusagen aus ADR-0019 haben heute keine Grundlage im Code. Das macht sie nicht falsch, aber sie
sind Aufwand, den der Entwurf nicht ausweist:

1. **„Jede Zustandsänderung trägt dieselbe Audit-Correlation wie die ursprüngliche Invocation."**
   `AuditEvent` hat kein Correlation-Feld, und im gesamten `src/` gibt es weder `Correlation` noch
   `Activity`/`TraceId`. Die Korrelation muss erst eingeführt werden — im Audit-Datensatz, in der
   Migration, im Invoker.
2. **Cursor-Pagination.** Die bestehende Oberfläche ist Offset-basiert (`PagedResult` mit
   `Page`/`PageSize`, so in Audit und REST). Entweder kommen zwei Paginierungsstile ins Haus, oder
   der bestehende ändert sich.
3. **Zustellung mit Retry, Dead-Letter-Queue und Backpressure.** Dafür existiert nichts. Der
   Webhook-Pfad ist die Gegenrichtung.
4. **Das Vokabular für „synchrone vs. asynchrone Capability"** kommt aus
   [ADR-0015](0015-protokollneutrales-capability-modell.md) — das ebenfalls auf *Vorgeschlagen*
   steht und ausdrücklich sagt, Task-/Event-/Stream-Arten würden erst öffentlich, „wenn Persistenz
   und Berechtigungen aus ADR-0019 implementiert sind". Beide ADRs zeigen aufeinander. **Wer zuerst
   entschieden wird, ist selbst eine Frage** — ADR-0019 lässt sich ohne 0015 entscheiden, wenn Tasks
   zunächst nur eine eigene Ressource sind und keine Capability-Art.

## 3. Die vier Fragen, an denen die Umsetzung hängt

Zu jeder: was daran hängt, welche Wege es gibt, und wie die Belege liegen. Das letzte ist meine
Lesart, nicht die Entscheidung.

### Frage 1 — Ist ein Task die Verallgemeinerung der Freigabe, oder etwas daneben?

Die Freigabe-Anfrage hat heute schon Id, Aufrufer, Tool, Fingerprint, redigierte Argumente, einen
Zustandsautomaten und eine Ablaufzeit. TaskV1 ist dasselbe plus Fortschritt, Ergebnis, Revision und
Abbruch. Und der A2A-Zustand `input-required`, den der Entwurf nennt, ist inhaltlich genau das, was
`Pending` heute bedeutet: Es fehlt eine menschliche Eingabe.

- **A: Task verallgemeinert die Freigabe.** Eine Tabelle, eine API, eine Liste in der UI. Ein
  Betreiber sieht einen Vorgang an einer Stelle. Preis: Datenmigration der persistierten Freigaben,
  und `TryConsumeApprovalAsync` liegt im **heißen Pfad jedes Aufrufs** — dieser Weg darf nicht
  langsamer oder unschärfer werden, nur weil er jetzt durch ein allgemeineres Modell läuft.
- **B: Task neben der Freigabe.** Heute billiger, dauerhaft zwei Warteschlangen, zwei
  REST-Flächen, zwei UI-Listen. Die Frage „warum steht mein Aufruf in zwei Listen und in welcher
  zuerst" wird nie wieder verschwinden.

**Wie die Belege liegen:** Die Überschneidung ist groß genug, dass B eine Doppelung wäre, die man
später nicht mehr los wird. Gegen A spricht ernsthaft nur der heiße Pfad — und der lässt sich mit
einem schmalen Index auf (Aufrufer, Tool, Fingerprint, Zustand) genauso schnell halten wie heute.

### Frage 2 — Werden Ergebnisse geholt oder geschickt?

Das ist die Frage, die Streams freigibt, und die teuerste.

- **Holen (Pull).** Der Aufrufer fragt `/api/v1/tasks/{id}` oder eine Task-Resource über MCP.
  Kein Rückkanal, keine Subscriptions, keine Zustellgarantie, kein DLQ, kein Backpressure — der
  Zustand steht in der Datenbank, und wer ihn will, holt ihn. Preis: Latenz ist das Abfrageintervall,
  und viele Agenten erzeugen viele Leerabfragen.
- **Schicken (Push).** Das Substrat ist da — `McpSessionRegistry` kennt Session und Identität und
  sendet heute schon Notifications. Aber sobald Zustellung eine *Zusage* ist, kommt der ganze
  Apparat aus dem Entwurf mit: Subscriptions, Cursor, TTL, Delivery-Policy, at-least-once, Dedup per
  Event-Id, begrenzte Retries, Dead-Letter-Queue.
- **Beides, mit klarer Rangordnung.** Holen ist der Vertrag, Schicken ist nur eine Beschleunigung
  („da hat sich was geändert, schau nach"). Dann ist ein verlorenes Ereignis kein Korrektheitsfehler,
  sondern zusätzliche Latenz — und at-least-once, Dedup und DLQ sind für V1 schlicht nicht nötig.

**Wie die Belege liegen:** Der Entwurf verspricht mit at-least-once und DLQ die teuerste Variante,
ohne dass ein Anwendungsfall im Repo sie fordert. Die Rangordnung „Holen ist der Vertrag" liefert
denselben Nutzen für Streams und Tasks und verschiebt den Zustell-Apparat auf den Tag, an dem
jemand ihn wirklich braucht.

### Frage 3 — Wer bricht ab, und was heißt Abbruch für den Upstream?

Heute ist Abbruch ein `CancellationToken`, das mit dem Request stirbt, plus der Per-Call-Timeout
(FR-09) und Fuel/Epoch im WASI-Host. Ein Task überlebt den Request — das Token kann das Mittel also
nicht mehr sein. Nötig wäre ein persistiertes Abbruch-Kennzeichen, das der Ausführende abfragt.

Und „abgebrochen" heißt je Transport etwas anderes:

| Transport | Was heute möglich ist |
|---|---|
| MCP-Upstream (stdio/SSE) | `notifications/cancelled` existiert im Protokoll |
| CLI (ADR-0014) | Prozessbaum-Kill ist gebaut |
| WASI (ADR-0020) | **nichts** — der IPC-Vertrag ist ein Frame rein, ein Frame raus; es gibt kein Cancel-Frame. Nur Fuel und Epoche begrenzen, und die sind keine Abbruchsemantik, sondern eine Reißleine |
| OpenAPI/HTTP | Verbindungsabbruch, ohne Zusage über die Gegenseite |

**Wie die Belege liegen:** Die Unterscheidung `requested` vs. `confirmed` aus dem Entwurf ist genau
richtig — aber nur, wenn `confirmed` je Transport auch wirklich belegbar ist. Sonst ist es ein Feld,
das Sicherheit vortäuscht. Für WASI wäre `confirmed` heute nicht erreichbar, ohne den IPC-Vertrag
umzustellen.

### Frage 4 — Öffnet ADR-0019 die Tür, die ADR-0010 geschlossen hat?

[ADR-0010](0010-sampling-elicitation-nicht-durchreichen.md) (*Accepted*) hat Sampling und
Elicitation verworfen, mit drei strukturellen Gründen und einem Sicherheitsgrund. **Einer dieser
Gründe stimmt seit `b7638f9` nicht mehr:** Der ADR schreibt, `CallToolAsync` habe keinen
Caller-Parameter, „die `IdentityId` endet im `ToolInvoker`". Über `ICallerAwareUpstreamConnection`
tut sie das nicht mehr.

Die anderen Gründe stehen — für *synchrone, im Band laufende* Elicitation: Das Protokoll trägt keine
Korrelation, der SDK-Handler bekommt keinen Request-Kontext, und die Verbindung ist je `ServerId`
geteilt.

Der Punkt ist: TaskV1s `input-required` samt „erwartetem Folge-Input-Schema" **ist** Elicitation in
anderer Kleidung — nur nicht server-initiiert im Band, sondern als persistierter Zustand, zu dem der
Aufrufer zurückkommt. Damit fallen die Gründe 1 und 2 weg, denn **die Task-Id ist genau die
Korrelation, die das Protokoll nicht trägt**. ADR-0010 selbst sagt für den Fall, dass es doch gebaut
wird: „Elicitation zuerst, Sampling getrennt entscheiden."

Auch das Sicherheitsargument verschiebt sich: ADR-0010 argumentiert, der Gateway habe „niemanden,
den er fragen könnte". Seit ADR-0012 gibt es eine Freigabe-UI mit einem Menschen davor.

**Wie die Belege liegen:** ADR-0019 berührt ADR-0010 unvermeidlich. Besser man sagt es ausdrücklich
— „`input-required` ist Elicitation über Tasks, Sampling bleibt draußen" — als dass es sich über
ein Zustandsfeld einschleicht. Sampling ist davon unberührt: Der Kosten- und
Prompt-Injection-Einwand aus ADR-0010 gilt unverändert.

## 4. Was Streams davon brauchen

Der Auslöser dieses Dokuments. Drei Schichten blockieren `stream<T>` im WASI-Adapter:

1. **wasmtime**: `stream`/`future` gibt es nur hinter `component-model-async`, und das Feature zieht
   `async` nach sich — der Store wird asynchron, `Func::call` wird `call_async`, der synchrone
   stdio-Loop des Hosts wird umgebaut, samt Fuel-Nachfüllung, Epochen-Wachhund und der persistenten
   Instanz aus `b7638f9`.
2. **Der IPC-Vertrag**: strikt ein Frame rein, ein Frame raus. Ein Stream braucht Korrelations-Ids,
   Chunk-Frames und ein Cancel-Frame.
3. **Die Nordseite**: MCP `tools/call` liefert ein Ergebnis, nicht viele.

Davon hängt an dieser Entscheidung:

- **Frage 2 blockiert Streams.** Ob Chunks geholt oder geschickt werden, legt die Frame-Formen in
  Schicht 2 und die Darstellung in Schicht 3 fest.
- **Frage 3 blockiert Streams.** Ein Stream ohne Abbruch ist ein Leck.
- **Frage 1 und 4 blockieren Streams nicht.** Sie betreffen Tasks, nicht die Übertragung.

**Schicht 1 löst diese Entscheidung nicht.** Der asynchrone Umbau des WASI-Hosts ist eigener
Aufwand in Plan 0003 und fällt auch dann an, wenn ADR-0019 morgen entschieden wird. Das ist der
einzige Punkt, an dem „ADR-0019 entscheiden" nicht genügt.

## 5. Grobe Kosten

Bewusst grob und an das gebunden, was es schon gibt:

| Ausbaustufe | Was dazukommt | Größenordnung |
|---|---|---|
| **Tasks, nur Holen** | Task-Tabelle + Migrationen (SQLite/Postgres), Zustandsautomat mit Revision, Correlation im Audit, `/api/v1/tasks` mit Paginierung, Verfallslauf als `BackgroundService`, UI-Liste, persistiertes Abbruch-Kennzeichen | überschaubar; jedes Teil hat ein Vorbild im Haus (Approvals, AuditRetentionService) |
| **+ Freigaben aufgehen lassen** (Frage 1/A) | Datenmigration, heißer Pfad neu belegt | klein, aber mit Migrationsrisiko auf bestehenden Installationen |
| **+ Events mit Zusage** | Subscriptions, Cursor, TTL, Delivery-Policy, Retry, DLQ, Backpressure, ausgehende Zustellung | der mit Abstand größte Brocken — und der einzige ohne Vorbild im Repo |
| **+ Streams über WASI** | asynchroner Host, IPC-Vertrag v4 (Korrelation, Chunks, Cancel) | eigener Aufwand in Plan 0003, unabhängig von dieser Entscheidung |

Die Stufen bauen aufeinander auf und lassen sich einzeln abbrechen. Der Sprung von Stufe 1 auf Stufe
3 ist der teure; Frage 2 entscheidet, ob er überhaupt nötig ist.

## 6. Was hier nicht entschieden ist

- Keine der vier Fragen. Abschnitt 3 nennt jeweils, wie die Belege liegen — das ist eine Lesart,
  keine Festlegung.
- Nicht die Reihenfolge zu ADR-0015 (Abschnitt 2, Punkt 4).
- Nicht, ob ADR-0010 angefasst wird. Falls ja, wäre das eine eigene Änderung an ADR-0010, kein
  Nebeneffekt von ADR-0019.
- Nicht der Zeitpunkt für Streams. Der hängt zusätzlich am asynchronen Host-Umbau.
