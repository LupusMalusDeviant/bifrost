# ADR-0023: Sessionloser Kern als Vorgabe — und die Rückfrage über MRTR

- **Status:** **Akzeptiert.** Entschieden am 2026-07-31 mit dem Product Owner.
- **Datum:** 2026-07-31
- **Autor:** LupusMalus (Product Owner), ausgearbeitet mit Claude
- **Betrifft:** [ADR-0002](0002-dotnet-mit-offiziellem-csharp-mcp-sdk.md) (SDK-Wahl), FR-07
  (Katalogänderungen), FR-33/FR-39 (Sitzungsanzeige), NFR-09 (Protokollstand)
- **Grundlage:** Ergänzt [ADR-0012](0012-approval-flows-asynchron.md) und
  [ADR-0022](0022-schaerfe-und-durchsetzung-trennen.md) um einen zweiten Weg zum Menschen;
  präzisiert [ADR-0009](0009-sse-legacy-transport.md) und
  [ADR-0010](0010-sampling-elicitation-nicht-durchreichen.md).

## Kontext und Problemstellung

Am 2026-07-28 ist die Spec-Revision `2026-07-28` erschienen — nach eigener Aussage der größte
Eingriff seit Einführung der Autorisierung. Der Kern des Protokolls ist **sessionlos** geworden:
Der `initialize`-Handshake (SEP-2575) und der Kopf `Mcp-Session-Id` (SEP-2567) sind ersatzlos
gestrichen. Jede Anfrage beschreibt sich selbst und trägt Protokollversion, Client-Angaben und
Fähigkeiten in ihrem `_meta`. Dazu kommen MRTR statt server-initiierter Rückfragen (SEP-2322),
Cache-Hinweise auf Listen (SEP-2549), Kopfzeilen fürs Routing (SEP-2243) und eine Reihe
Verschärfungen bei der Autorisierung. Roots, Sampling, Logging und HTTP+SSE sind mit
Zwölf-Monats-Frist abgekündigt.

Das C#-SDK 2.0.0 setzt die Revision um und hält gleichzeitig die Verständigung mit Gegenstellen auf
`2025-11-25` und älter aufrecht. Der Umstieg der Pakete selbst war klein — die gesamte Lösung
übersetzte nach einer einzigen geänderten Zeile. Die Folgen liegen nicht in der API, sondern in
einer Eigenschaft, die das SDK an genau einem Schalter aufhängt:

> `HttpServerTransportOptions.Stateless` … *„Starting with the `2026-07-28` protocol revision,
> Streamable HTTP no longer supports sessions … so over HTTP its requests are only ever served when
> this property is `true`. When it is `false`, such a request is refused with a
> `-32022 UnsupportedProtocolVersion` error so that a dual-path client downgrades."*

Damit steht die Wahl fest verdrahtet: **Es gibt keinen Betrieb, in dem beide Welten alles
bekommen.** Entweder der Gateway spricht die neue Revision — dann verlieren alle Clients, was eine
stehende Sitzung voraussetzt. Oder er behält die Sitzung — dann wird jeder Client, auch der neueste,
auf den alten Stand zurückgehandelt.

Für diese Instanz hängen drei gebaute Mechaniken daran:

1. **Die Freigabe-Rückfrage** (ADR-0012, ADR-0022, fünf Anläufe im Betrieb) lief über
   server-initiierte Elicitation. Das SDK verweigert sie sessionlos ausdrücklich:
   *„Elicitation is not supported in stateless mode."*
2. **`tools/list_changed`** (FR-07) ist eine unaufgeforderte Server-zu-Client-Nachricht. Sessionlos
   gibt es die nicht mehr — in beide Richtungen: Auch Upstreams auf dem neuen Stand melden nichts
   mehr von selbst.
3. **Die Sitzungsanzeige** (FR-33/FR-39) zählt Sitzungen, die es nicht mehr gibt.

**Kernfrage:** Auf welchem Stand arbeitet der Gateway künftig, und wie bleibt der Weg zum Menschen
erhalten, wenn der bisherige Weg dorthin wegfällt?

## Anforderungen

### Funktional

- Ein Client auf `2026-07-28` und einer auf `2025-11-25` müssen **an derselben Adresse** arbeiten
  können. Ein Umstieg, der die eine Hälfte abschaltet, ist keiner.
- Die Freigabe-Rückfrage im laufenden Gespräch muss erhalten bleiben. Sie ist der Grund, warum die
  Freigabepflicht im Betrieb überlebt hat (ADR-0022).
- Eine Katalogänderung muss angeschlossene Agenten weiterhin erreichen — in beiden Richtungen und in
  beiden Betriebsarten.
- Ein Betreiber, dessen Clients alle auf dem alten Stand stehen, muss den bisherigen Betrieb behalten
  können, ohne auf ein SDK von gestern zurückzugehen.

### Nicht-Funktional

- **Keine stille Verhaltensänderung.** Der SDK-Standard für `Stateless` hat sich mit 2.0 von `false`
  auf `true` gedreht. Eine Umstellung dieser Tragweite darf nicht davon abhängen, welche
  Paketversion gerade aufgelöst wurde.
- **Keine erfundenen Zahlen.** Was der Gateway anzeigt, muss das sein, was er misst.
- **Keine Rechteweitergabe durch einen Zwischenspeicher.** Cache-Hinweise auf gefilterten Listen
  sind ein Sicherheitsmerkmal, kein Leistungsdetail.
- **Kein neuer Weg an der Freigabepflicht vorbei.** Ein Zustand, der über den Client zurückläuft,
  darf nicht fälschbar sein.

## Betrachtete Optionen

### Option 0: Auf dem Sitzungsbetrieb bleiben

`Stateless = false`, alles bleibt wie bisher.

**Positiv:**
- Null Aufwand, null Verhaltensänderung. Rückfrage, `tools/list_changed` und Sitzungsanzeige
  funktionieren unverändert für **jeden** Client.

**Negativ:**
- Jeder Client auf `2026-07-28` wird mit `-32022` abgewiesen und handelt den alten Stand aus. Der
  Gateway liefe auf dem neuen SDK und spräche weiter die alte Revision — der Umstieg wäre eine
  Paketaktualisierung ohne Wirkung.
- Die abgekündigten Bestandteile laufen nur noch zwölf Monate. Die Entscheidung wäre vertagt, nicht
  getroffen.

### Option 1: Beide Betriebsarten parallel an zwei Adressen

Ein sessionloser und ein sitzungsbasierter Endpunkt im selben Prozess.

**Positiv:**
- Auf dem Papier die vollständige Antwort: Jeder Client bekommt, was er kann.

**Negativ:**
- **Technisch nicht vorgesehen.** `MapMcp` nimmt nur ein Muster; die Transportoptionen kommen aus
  der Dependency Injection und gelten für den ganzen Prozess. Zwei Betriebsarten hieße zwei Hosts —
  mit zwei Katalogen, zwei Aufsichtsschleifen und zwei Zuständen, oder mit einem gemeinsamen Zustand
  und der ganzen Kopplung, die daraus folgt.
- Zwei Adressen sind für jeden Betreiber eine Fehlerquelle mehr, und die falsche zu erwischen fällt
  erst beim ersten Ausfall auf.

### Option 2: Sessionlos als Vorgabe, Sitzungsbetrieb als Schalter

`Stateless = true` als Vorgabe, umschaltbar über `BIFROST_MCP_STATELESS=0`. Die Rückfrage wandert auf
MRTR, `tools/list_changed` bekommt einen Ersatz.

**Positiv:**
- Der Gateway spricht wirklich den neuen Stand. Alte Clients laufen weiter — das SDK bedient ihren
  Handshake unverändert.
- Die Rückfrage bleibt erhalten, sogar besser: MRTR braucht keine stehende Verbindung, der Aufruf
  wird vom Client wiederholt.
- Ein Betreiber mit ausschließlich alten Clients kann den bisherigen Betrieb behalten.

**Negativ:**
- Alte Clients verlieren im sessionlosen Betrieb die Rückfrage — für sie bleibt die Warteschlange.
- `tools/list_changed` entfällt und muss durch Cache-Fristen und turnusmäßige Abfragen ersetzt
  werden. Beides ist träger als eine Benachrichtigung.
- Ein Client, der MRTR spricht, aber kein Formular anzeigen kann, bekommt eine Ausnahme statt der
  Warteschlangen-Meldung (siehe Konsequenzen).

## Entscheidung

**Gewählte Option:** Option 2 — sessionlos als Vorgabe, Sitzungsbetrieb als ausdrücklicher Schalter.

Die Wahl fiel gegen Option 0, weil die Alternative nicht „neu gegen alt" heißt, sondern „jetzt
umsteigen oder in zwölf Monaten unter Zeitdruck". Und gegen Option 1, weil zwei Hosts zwei Systeme
sind, die auseinanderlaufen.

Der Schalter ist kein Kompromiss aus Unentschlossenheit, sondern die Antwort auf eine konkrete Lage:
Wer heute ausschließlich Clients auf `2025-11-25` betreibt, verliert durch die Umstellung die
Rückfrage — und für den ist der Sitzungsbetrieb objektiv besser. Der Test
`A_current_client_is_downgraded_to_the_previous_revision` hält fest, was er dafür bezahlt.

### Was daraus folgt

| | Sessionlos (Vorgabe) | Sitzungsbetrieb (`BIFROST_MCP_STATELESS=0`) |
|---|---|---|
| Client auf `2026-07-28` | voll bedient, Rückfrage über MRTR | abgewiesen (`-32022`), handelt `2025-11-25` aus |
| Client auf `2025-11-25` | arbeitet; Rückfrage nur über die Warteschlange | arbeitet; Rückfrage im laufenden Aufruf |
| `tools/list_changed` | entfällt; Ersatz ist die Cache-Frist auf den Listen | wie bisher |
| Sitzungsanzeige | „Aktive Agenten (letzte 5 Minuten)" | „Aktive Sessions" |

## Konsequenzen

### Positiv

- **Der Gateway spricht `2026-07-28`.** Festgehalten in `GatewayE2ETests` als Untergrenze und in
  `StatelessProtocolTests` als ausgehandelter Stand.
- **Die Rückfrage ist robuster als vorher.** Sie hängt nicht mehr an einer offenen Verbindung: Der
  Aufruf endet mit `input_required`, der Client wiederholt ihn mit der Antwort. Der Zustand
  zwischen beiden Runden liegt geschützt beim Client — der Gateway muss zwischen zwei Runden nichts
  festhalten.
- **Der Cache-Hinweis ist `private`.** Unsere Listen sind je Identität gefiltert; fehlt das Feld,
  gilt laut Spec `public`, und ein gemeinsamer Zwischenspeicher dürfte die Werkzeugliste der einen
  Identität an die nächste ausliefern. Diese Zeile ist eine Sicherheitsaussage, keine
  Leistungsoptimierung.
- **Der alte Stand bleibt unter Test.** Weil „kann nicht gefragt werden" nur noch dort existiert,
  laufen mehrere Freigabe-Tests jetzt ausdrücklich gegen einen Client auf `2025-11-25` — die alte
  Revision ist damit besser abgedeckt als vor der Umstellung.

### Negativ, und was daran gemessen wurde

- **Elicitation ist als Fähigkeit verschwunden.** Auf `2026-07-28` meldet sie kein Client mehr.
  Nachgemessen in dieser Reihenfolge: Testclient mit Handler → `Elicitation: False`; derselbe mit
  ausdrücklich gesetzter Capability → weiterhin `False`; roher JSON-RPC-Aufruf mit
  `"clientCapabilities":{"elicitation":{}}` am Draht → ebenfalls `False`. Der Gateway fragt deshalb
  jeden, der MRTR spricht. **Der Preis:** Ein Client, der MRTR beherrscht, aber kein Formular
  anzeigen kann, läuft in ein `no ElicitationHandler is registered` — eine Ausnahme statt der
  Warteschlangen-Meldung. Der Vorgang selbst geht dabei nicht verloren; er steht in der
  Warteschlange und ist in der Oberfläche entscheidbar. Festgehalten in
  `A_client_that_cannot_show_a_form_still_leaves_the_request_in_the_queue`.
- **`ping` gibt es nicht mehr.** Der Herzschlag der Aufsicht lief darüber und riss beim Umstieg
  jede Upstream-Verbindung mit — drei Integrationstests fielen mit scheinbar unzusammenhängenden
  Fehlern aus (Zeitüberschreitung, Neustartschleife, abgebrochener Aufruf). Ersatz ist
  `server/discover`, gewählt nach ausgehandelter Revision der jeweiligen Gegenstelle, nicht nach
  Konfiguration.
- **Upstreams melden nichts mehr von selbst.** Ohne Ersatz bliebe ein dort neu hinzugekommenes
  Werkzeug unsichtbar, bis jemand „Neu einlesen" drückt — ein Ausfall, bei dem nichts kaputt
  aussieht. Der Supervisor fragt solche Upstreams deshalb turnusmäßig
  (`SupervisorOptions.CatalogPollInterval`, Vorgabe eine Minute) und meldet **nur echte
  Änderungen** weiter; eine Abfrage ohne Fund bleibt still, sonst liefe jede Minute ein
  Katalogereignis durch das ganze System.
- **Die Sitzungsanzeige misst etwas anderes.** Sessionlos gibt es keine offenen Sitzungen; gezählt
  wird, welche Identitäten in den letzten fünf Minuten Anfragen gestellt haben.
  `IActiveSessionSource.CountsOpenSessions` sagt, welche der beiden Bedeutungen gilt, und die
  Oberfläche beschriftet danach.
- **Der `requestState` läuft über den Client.** Er trägt die Freigabe-Id und ist deshalb mit dem
  DataProtection-Key-Ring geschützt und an Identität und Werkzeug gebunden; beim Zurückkommen wird
  beides erneut gegen den laufenden Aufruf geprüft. Ohne diesen Schutz könnte ein Aufrufer die
  Antwort auf eine fremde Frage als Zustimmung für seinen eigenen Aufruf ausgeben. Die Spec verlangt
  eine solche Sicherung ausdrücklich, wenn der Zustand etwas Schützenswertes trägt.
- **Höchstens eine Rückfrage je Aufruf.** Kommt die Wiederholung ohne Zustimmung zurück, wird nicht
  erneut gefragt — sonst entstünde eine Endlosfrage an einen Menschen, der gerade abgelehnt hat.

### Unverändert

- **Die Auswertung der Antwort.** Nur ein ausdrückliches `accept` mit gesetztem Häkchen ist eine
  Zustimmung; alles andere führt zurück in die Warteschlange. Diese Regel hat drei Fehlschläge im
  Betrieb gekostet und gilt für beide Wege gleichermaßen — MRTR ändert den Transport, nicht das
  Vertrauensmodell.
- **ADR-0010 gilt weiter.** Rückfragen eines **Upstreams** werden nicht durchgereicht; der Gateway
  könnte nicht sagen, an welchen der vielen Aufrufer sie gehen sollen. Neu ist nur, dass ein
  Upstream sie jetzt als `input_required` stellen kann — der Aufruf scheitert dann mit einer
  Aussage statt mit einer SDK-Meldung über einen fehlenden Handler.
- **Der eigene Task-Store.** Die SDK-Tasks sind mit 2.0 in ein eigenes Paket gewandert und zu denen
  aus 1.4.x nicht drahtkompatibel. Uns betrifft das nicht: Unsere Vorgänge laufen über den eigenen
  `ITaskStore` und die REST-Fassade (ADR-0019, Polling ist der Vertrag).
  `ModelContextProtocol.Extensions.Tasks` bleibt bewusst außen vor.
