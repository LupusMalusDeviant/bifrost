# Plan 0005 — Selbsttätige Wiederverbindung von Upstreams

**Stand:** 2026-11-18 · **Status:** W0 – W9 erledigt (gemergt als `0140a3b`, auf Badwolf
abgenommen); von den Restarbeiten W10 – W13 und W15 – W17 erledigt, W14 und W18 teilweise;
V1 – V5 am 2026-11-18 abgenommen (Vertrag Abschnitt 9), V1 – V3 gebaut
**Vertrag:** [docs/contracts/upstream-wiederverbindung.md](../contracts/upstream-wiederverbindung.md)

Gebaut wird erst, wenn der Vertrag abgenommen ist. Die Reihenfolge ist so gewählt, dass nach jedem
Paket ein lauffähiger, vollständig getesteter Stand steht.

**Beim Bau gefunden** (steht auch im Vertrag bzw. im CHANGELOG):

- Das SDK bricht gleichzeitige Anfragen ab, sobald eine das 404 auf die Sitzung bekommt. Diese
  Aufrufe werden nicht wiederholt und enden mit einer Meldung, die das sagt — Präzisierung in
  Abschnitt 4 des Vertrags, abgenommen am 2026-11-16.
- `ToolInvoker` reicht bei einem gescheiterten Werkzeugaufruf die ungefilterte Meldung des Upstreams
  an den Agenten und ins Audit (`Upstream-Fehler: …`). Außerhalb dieses Vertrags, nicht geändert.

**Review vom 2026-11-16** (unabhängiger Lesedurchgang, kein kritischer Befund). Eingearbeitet auf
Entscheidung des Nutzers:

- **M1** — Der Run-Loop konnte an seiner eigenen Buchführung enden (Audit wirft, Frist außerhalb des
  Timer-Bereichs) und `Failed` ohne weiteren Versuch hinterlassen. Jetzt abgefangen; Z1 hat dafür
  einen Test. `[Claude-prüfbar]`, erledigt.
- **M2** — Bemerkte das Lebenszeichen die verlorene Sitzung, liefen neue Aufrufe in die tote
  Verbindung statt auf den Neuaufbau zu warten (Abweichung von Abschnitt 4). `[Claude-prüfbar]`,
  erledigt.
- **M4** — Die Runtime-Kennung des WASI-Hosts stand noch auf `wasmtime-47.0.2`.
  `[Claude-prüfbar]`, erledigt.

Offen aus demselben Review, **nicht** eingearbeitet:

- **H1** — OAuth-Upstreams fallen bei jedem Ablauf des Access-Tokens für bis zu 15 min aus: Das
  Token wird nur beim Verbinden erneuert, und ein 401 auf der stehenden Verbindung führt in die
  feste Frist aus E2. Vertragsgemäß, braucht eine Vertragsänderung. `[braucht Nutzer]`
- **H2** — Mögliche Doppelausführung, wenn ein Upstream mitten im Aufruf neu startet und der
  SDK-Client die SSE-Verbindung wieder aufnimmt. Nicht belegt; es fehlt der Test, der es zeigt oder
  ausschließt. `[Claude-prüfbar]`
- **M3** — Fremdtext des Upstreams erreicht `LastError`, Audit und Log auch auf anderen Wegen als
  „Response body:"; der Verbindungstest über REST schneidet den Antwortkörper nicht ab.
  `[Claude-prüfbar]`
- **L1 – L7** — u. a. ein kurzes Fenster, in dem „Jetzt verbinden" verpufft; Neuaufbauten der
  Sitzung ohne Backoff; Prüffenster nach einem Neuaufbau; der OAuth-Rückruf stößt keinen
  Verbindungsversuch an; die Audit-Zeile beim Eintritt in `Failed` nennt die Fehlerart nicht.
  `[Claude-prüfbar]`

**Review der Korrekturen vom 2026-11-16** (zweiter Lesedurchgang über M1, M2, M4; kein kritischer
oder hoher Befund). Eingearbeitet auf Entscheidung des Nutzers vom 2026-11-17:

- **R1** (mittel) — Wies das Audit die Zeile zum Eintritt in `Failed` ab, ging sie verloren, und die
  Logzeile dazu fehlte auch. Jetzt werden Zeilen des Loops zurückgestellt und in Reihenfolge
  nachgereicht. `[Claude-prüfbar]`, erledigt.
- **R2** (niedrig) — Der Grenztest bestand auch mit totem Loop; er endet jetzt mit „Jetzt
  verbinden" und genau einem weiteren Versuch. `[Claude-prüfbar]`, erledigt.
- **R3** (niedrig) — Scheiterte die Buchführung, ersetzte der Deckel die schon berechnete Frist
  (nach 401: 5 statt 15 min). `[Claude-prüfbar]`, erledigt.
- **R4** (niedrig) — An der Obergrenze sagte die Logzeile „läuft trotzdem weiter". Eigene Meldung.
  `[Claude-prüfbar]`, erledigt.
- **R5** (niedrig) — Der Z1-Test konnte die Frist mitten in einem Versuch lesen. Er wartet jetzt
  auf die verbuchte Frist. `[Claude-prüfbar]`, erledigt.

Offen aus demselben Durchgang, **nicht** eingearbeitet:

- **R6** — Bemerkt die Katalog-Neuabfrage den Sitzungsverlust, fordert niemand den Neuaufbau an;
  das Lebenszeichen auf dem toten Client endet in `Failed` mit Backoff statt in einem Neuaufbau.
  Älter als diese Änderung. `[Claude-prüfbar]`
- **R7** — Wirft das Audit beim Einschalten oder Umkonfigurieren (Zeile `Starting`), startet der
  Loop nicht: Der Upstream ist eingeschaltet, zeigt `Starting` und hat keinen Loop. Älter als diese
  Änderung; die Verweigerung der Aktion im Compliance-Modus ist gewollt, der halbe Zustand danach
  nicht. `[Claude-prüfbar]`

**Dritter Lesedurchgang vom 2026-11-17** (über R1 – R5; Codex war am Nutzungslimit, geprüft hat
ersatzweise ein unabhängiges Claude-Review; kein kritischer oder hoher Befund). Eingearbeitet auf
Entscheidung des Nutzers vom 2026-11-17:

- **S1** (mittel) — Warf auch die Logzeile im Auffangpfad (etwa eine Log-Ausgabe, die wirft), endete
  der Loop doch. Die Meldungen dort sind jetzt abgefangen; der Zustand wird vorher gesetzt.
  `[Claude-prüfbar]`, erledigt.
- **S2** (mittel) — Eine zurückgestellte Zeile wartete bis zum nächsten echten Wechsel, im Zweifel
  tagelang, und ging beim Entfernen oder Beenden spurlos verloren. Jetzt bei jedem Versuch und jedem
  Lebenszeichen nachgereicht; was am Ende übrig ist, steht als Fehler im Log. `[Claude-prüfbar]`,
  erledigt.
- **S4** (niedrig) — Brach `MarkFailed` nach dem Zustandswechsel, fehlte die Zeile „eingestellt";
  scheiterte schon das Aufräumen, fehlten die Failed-Zeile und die Fehlerart. Jetzt schreibt
  `MarkFailed` alle Audit-Zeilen vor dem Log, und der Auffangpfad holt Eintritt, Grenze und
  Fehlerart nach. `[Claude-prüfbar]`, erledigt.
- **S5** (niedrig) — An der Grenze von 32 fiel die neueste Zeile heraus; jetzt die älteste.
  `[Claude-prüfbar]`, erledigt.

Offen aus demselben Durchgang, **nicht** eingearbeitet:

- **S3** (mittel) — `ChannelAuditSink` zählt jede Abweisung als Verlust. Ein nachgereichter Versuch
  zählt erneut (durch S2 bei vollem Kanal je Versuch und Lebenszeichen), eine an der Grenze
  verworfene oder beim Entfernen verlorene Zeile dagegen nie. Das verfälscht `AuditLossFacts` und
  die Diagnose BFR-DB-0007. Die saubere Lösung ändert `IAuditSink` (etwa ein `TryRecord`, das nicht
  als Verlust zählt) und braucht deshalb zuerst eine Vertragsänderung. `[braucht Nutzer]` für den
  Vertrag, danach `[Claude-prüfbar]`
- **S6** (niedrig) — Die Audit-Zeile zu einer geänderten Tool-Definition geht direkt ans Audit; bei
  vollem Kanal im Compliance-Modus scheitert damit jeder Verbindungsversuch. Ob das als Sicherheits-
  ereignis so gewollt ist, gehört in den Vertrag. `[braucht Nutzer]`

**Vierter Lesedurchgang vom 2026-11-17** (über S1, S2, S4, S5; Codex wieder am Nutzungslimit,
ersatzweise ein unabhängiges Claude-Review; kein kritischer oder hoher Befund). Eingearbeitet auf
Entscheidung des Nutzers vom 2026-11-17:

- **T1** (mittel) — `RecordLoopLifecycle` konnte über seine eigenen Logzeilen doch werfen; bei
  vollem Audit und gestörtem Logger ging die Zeile „eingestellt" verloren. Die Logzeilen dort sind
  jetzt abgefangen. `[Claude-prüfbar]`, erledigt.
- **T3** (mittel) — Ein scheiterndes Schließen loggte das Ausnahmeobjekt, das Text des Upstreams
  tragen kann (P1 – P4). Jetzt nur noch Typnamen. `[Claude-prüfbar]`, erledigt.
- **T5, T6, T7** (niedrig) — Text der Zurückstellen-Meldung; `LastError` übernimmt im Auffangpfad
  den schon bereinigten Grund; die Meldung beim Verwerfen nennt die verworfene Zeile.
  `[Claude-prüfbar]`, erledigt.
- Test für das Nachreichen per Lebenszeichen ergänzt (vorher ungedeckt).

Offen aus demselben Durchgang, **nicht** eingearbeitet:

- **T2** (mittel) — Ist eine ausdrücklich gesetzte Obergrenze erreicht, wartet der Loop nur auf
  „Jetzt verbinden"; zurückgestellte Zeilen kommen bis zu einem Eingriff nicht an (ebenso, schwächer,
  bei sehr großer `restart.maxBackoff`). Abhilfe: in Abschnitten warten und dazwischen nachreichen.
  Betrifft nur nicht voreingestellte Konfigurationen. `[Claude-prüfbar]`
- **T4** (niedrig) — Das Nachreichen je Versuch und Lebenszeichen treibt den Verlustzähler des
  Audits; gehört zu S3. `[braucht Nutzer]` über S3
- **T8** (niedrig) — `DisposeAsync` bricht bei einem werfenden Logger mitten in der Schleife über
  die Upstreams ab. Älter als diese Änderung. `[Claude-prüfbar]`

**Fünfter und sechster Lesedurchgang vom 2026-11-17** (über T1, T3, T5 – T7 und über den ganzen
Branch nach dem Neuaufbau auf `main`; Codex am Nutzungslimit, ersatzweise unabhängige
Claude-Reviews; kein kritischer oder hoher Befund). Eingearbeitet auf Entscheidung des Nutzers vom
2026-11-18:

- **U1, U2** (mittel) — Zeitabhängige Tests: Der Status zeigt die neue Frist bzw. `Healthy`, bevor
  Audit und Log geschrieben sind; Tests, die danach sofort lasen, konnten ohne Korrektur grün oder
  ohne Defekt rot sein. Die Tests warten jetzt, bis der Loop wirklich auf den nächsten Versuch
  wartet (`IsWaitingForNextAttempt`, nur für Tests), bzw. auf die Audit-Zeile selbst.
  `[Claude-prüfbar]`, erledigt.

Offen aus denselben Durchgängen, **nicht** eingearbeitet:

- **U3** (mittel) — Wird ein Upstream bei vollem Audit (Compliance-Modus) abgeschaltet, ist die
  Aktion schon gespeichert und wirksam, wird aber als verweigert gemeldet; die Zeile `Stopped` fehlt
  im Audit, und der Katalog behält die Werkzeuge bis zum nächsten Ereignis. Gegenstück zu R7, älter
  als diese Änderung. `[Claude-prüfbar]`
- **U4** (mittel/niedrig) — Wirft der Logger nach einer gelungenen Verbindung, wird sie wieder
  abgebaut; nach einem Neuaufbau der Sitzung bleibt die alte Verbindung offen. Auch
  `DisposeQuietlyAsync` wirft dann, und ein 401 kann als „nicht erreichbar" gelten.
  `[Claude-prüfbar]`
- **U5** (niedrig) — `RediscoverAsync` kann nach einem Wechsel auf `Failed` oder einem Neuaufbau ein
  veraltetes Inventar schreiben. Älter als diese Änderung. `[Claude-prüfbar]`
- **U6** (niedrig) — `ChangedHandlerThrew` übergibt das Ausnahmeobjekt ans Log (harte Vorgabe: nur
  Typnamen). `[Claude-prüfbar]`
- **U7** (niedrig) — Ein Connector, der seinen Abbruch ignoriert, hinterlässt je Versuch eine späte
  Verbindung; ohne Obergrenze wächst das ohne Ende. `[Claude-prüfbar]`
- **U8** (niedrig) — `AbortPendingRebuild` bricht wartende Aufrufer mit einer unerklärten Abbruch-
  meldung ab statt mit „Verbindung verloren". `[Claude-prüfbar]`
- **U9** (niedrig) — Der Kommentar zu T6 sagt mehr, als der Code tut; die Meldung zum Zurückstellen
  verspricht das Nachreichen auch an der Obergrenze (siehe T2). `[Claude-prüfbar]`

## Arbeitspakete

| # | Inhalt | Marke |
|---|---|---|
| W0 | Vertrag abnehmen, E1 – E6 entscheiden | `[braucht Nutzer]` |
| W1 | Verträge in `Abstractions`: `UpstreamFailureKind`, `UpstreamReconnectInfo`, `UpstreamStatus.Reconnect`, `RestartPolicy.MaxRetries` als `int?` mit neuen Vorgaben, `ConnectNowAsync`, die zwei Ausnahmetypen. Validator und die Testdoubles ziehen mit. | `[Claude-prüfbar]` |
| W2 | Run-Loop: unbegrenzte Wiederholung, Fristen mit Jitter, Fehlerart, feste Frist bei Authentifizierung, Z3 (kein `Starting` bei Wiederholung), Z4 (Frist je Versuch), Z5 (Zähler erst nach gesunder Minute), Wecksignal für „Jetzt verbinden". | `[Claude-prüfbar]` |
| W3 | Bereinigung der Fehlertexte P1 – P4, eine Zeile je Wechsel in Log und Audit. | `[Claude-prüfbar]` |
| W4 | Verlorene Sitzung: Erkennung im SDK-Adapter, ein Neuaufbau für alle wartenden Aufrufe, einmalige Wiederholung nur nach Katalogprüfung. | `[Claude-prüfbar]` |
| W5 | Konfigurationsschlüssel in `Program.cs`, `docs/operations.md`, `.env.example`; REST-Felder und `POST …/connect`. | `[Claude-prüfbar]` |
| W6 | Server-Verwaltung: Frist, Versuchszahl, Knopf; Browsertest über die `data-`Attribute. | `[Claude-prüfbar]`, Blick auf die Seite `[braucht Nutzer]` |
| W7 | CHANGELOG, Blueprint „Upstream-Verwaltung" nachziehen, Lauf über alle Testprojekte wie in der CI. | `[Claude-prüfbar]` |
| W8 | Codex-Review, danach Push und Merge auf dein Go; den CI-Lauf bis zum Ende ansehen. | `[braucht Nutzer]` für Modellwahl und Go |
| W9 | Deploy auf Badwolf und Abnahme am lebenden System. | `[braucht Nutzer]` |

## Restarbeiten (ab 2026-11-18)

W8 und W9 sind erledigt: PR 34 ist gemergt (`main` `0140a3b`), Badwolf läuft auf
`bifrost:0.12.0-0140a3b`; Abnahme am 2026-11-18 mit Neustart (Sitzung in 17 s neu aufgebaut) und
64 s Ausfall (`Failed`, 4 Fehlversuche, 21 s nach Rückkehr von whiskers wieder verbunden).

Die offenen Funde aller Lesedurchgänge, zu Paketen geordnet. Was das zugesagte Verhalten ändert,
bekommt zuerst eine Vertragsänderung (V-Pakete); gebaut wird es erst nach deiner Abnahme.

| # | Funde | Inhalt | Marke |
|---|---|---|---|
| W10 | L1, L6, T2, U8, U9; Test-L2 | Zustandspflege im Loop: Wecksignal schon beim Verbuchen setzen und bei „Jetzt verbinden" sofort lösen (kein verpuffter Klick); Fehlerart in der Audit-Zeile zum Eintritt in `Failed` (Abschnitt 6); an der Obergrenze in Abschnitten warten und dazwischen nachreichen; wartende Aufrufer beim Stoppen mit „Verbindung verloren" statt einem unerklärten Abbruch; Kommentar und Logtext richtig. | `[Claude-prüfbar]`, erledigt (`c44fc2d`) |
| W11 | U4, T8, U6 | Werfende Log-Ausgabe: Meldungen in `Publish`, im Neuaufbau und in `DisposeQuietlyAsync` abfangen, die alte Verbindung im `finally` schließen; `DisposeAsync` je Upstream abgesichert; `ChangedHandlerThrew` nur mit Typnamen. | `[Claude-prüfbar]`, erledigt (`d88501d`) |
| W12 | L3, R6, U5 | Inventar: `RediscoverAsync` schreibt nur für die Verbindung, auf der es lief, und ein 404 dort fordert den Neuaufbau an; ein Aufruf an ein Werkzeug, das die Prüfung gerade zurückhält, wird auch beim ersten Versuch abgewiesen. | `[Claude-prüfbar]`, erledigt |
| W13 | L4, R7, U3 | Verwaltungsaktionen: Bei vollem Audit (Compliance) wird die Aktion verweigert, **bevor** sie gespeichert oder wirksam ist; ein abgeschalteter Upstream wird während des Drains nicht neu aufgebaut. | `[Claude-prüfbar]`, erledigt |
| W14 | M3 | Fremdtext: Für unbekannte Ausnahmetypen formuliert das Gateway den Grund selbst; der Verbindungstest über REST läuft durch dieselbe Bereinigung; die Maskierliste kennt auch OAuth-Token, OAuth-Client-Secret und Stdio-Argumente. | `[Claude-prüfbar]`, teilweise: Verbindungstest und Maskierliste (OAuth-Client-Secret, geheimnisartige Stdio-Schalter) erledigt; Tokens stehen nicht in der Konfiguration und sind dort nicht maskierbar; „unbekannte Typen nur mit Gateway-Text" ist eine Vertragsfrage, siehe V5 |
| W15 | H2 | SSE-Wiederaufnahme: Test, der „Aufruf läuft" mit „Sitzung fort" verbindet und genau eine Ausführung verlangt; Wiederaufnahme im SDK-Client für Upstreams abschalten, falls der Test sie zeigt. | `[Claude-prüfbar]`, erledigt: Der Test zeigt genau eine Ausführung; die Wiederaufnahme muss nicht abgeschaltet werden |
| W16 | L5 | OAuth: Der Rückruf nach dem Autorisieren stößt sofort einen Verbindungsversuch an; ein 5xx des Token-Endpunkts zählt als „nicht erreichbar", nicht als abgelehnte Anmeldung. | `[Claude-prüfbar]`, erledigt; der Anstoß im Rückruf hat keinen eigenen Test (braucht einen echten Authorization Server, den es nur im Keycloak-Lauf gibt) |
| W17 | U7 | Späte Verbindungen eines Connectors, der seinen Abbruch ignoriert: je Upstream zählen, ab einer Schwelle einmal melden. | `[Claude-prüfbar]`, erledigt |
| W18 | L7 | Testlücken: freier Port, den ein paralleler Test belegen kann; WASI-Proben gegen den Stand auf `main` prüfen. | `[Claude-prüfbar]`, Port erledigt (gebundener Socket ohne `Listen`); die WASI-Proben sind offen |
| V1 | H1 | OAuth-Token läuft auf stehender Verbindung ab → heute bis zu 15 min Ausfall. Vorschlag: ein 401 auf stehender Verbindung erneuert das Token und verbindet sofort einmal neu, erst ein zweites 401 gilt als abgelehnt (E2). | Vertrag abgenommen am 2026-11-18; `[Claude-prüfbar]`, erledigt |
| V2 | L2 | Neuaufbauten der Sitzung ohne Bremse (Lastverteiler ohne feste Zuordnung). Vorschlag: mehr als drei Neuaufbauten in fünf Minuten gelten als Ausfall mit Backoff. | Vertrag abgenommen am 2026-11-18; `[Claude-prüfbar]`, erledigt |
| V3 | S3, T4 | Verlustzählung des Audits: `IAuditSink` bekommt einen Weg, eine Zeile anzubieten, ohne dass eine Abweisung als Verlust zählt; verworfene Zeilen zählen dafür. | Vertrag abgenommen am 2026-11-18; `[Claude-prüfbar]`, erledigt |
| V4 | S6 | Audit-Zeile der Rug-Pull-Prüfung bei vollem Audit: heute scheitert jeder Verbindungsversuch. Entscheidung: so lassen (fail-closed) oder zurückstellen wie die Zustandszeilen. | entschieden am 2026-11-18: so lassen (fail-closed), im Vertrag festgeschrieben |
| V5 | M3 | Fremdtext unbekannter Ausnahmetypen: so lassen (P3 bereinigt, rund 140 Gateway-Stellen werfen Standardtypen mit nützlichem Text) oder nur noch Gateway-Text. | entschieden am 2026-11-18: so lassen, P3 verschärft (W14) |
| — | außerhalb | Der lastempfindliche p95-Test steht in der funktionalen Suite. Eigene Aufgabe, nicht Teil dieses Vertrags. (Der Fremdtext im `ToolInvoker` stand hier auch; er ist mit C7 und D1 in diesen Plan gewandert.) | `[braucht Nutzer]` |

### Codex-Review vom 2026-11-18 (`0140a3b..0a4543e`)

Codex (gpt-6.2-sol) hat 15 Befunde gemeldet; alle sind am Code nachgeprüft, 13 echt, zwei teilweise
(C1, C2). Die Einstufung ist die nachgeprüfte, nicht die von Codex. „Neu" heißt: aus diesen Commits.
Auf deinen Wunsch werden alle 15 sofort behoben. Stand: alle 15 gebaut und getestet; wo eine
Zusage präzisiert wird, steht es im Vertrag Abschnitt 10 (N1 – N8) zur Abnahme. Gegenprobe gegen
`0a4543e`: 22 von 25 neuen Tests rot; grün bleiben die Kontrolle „neue Autorisierung erledigt die
Anforderung" und die beiden C13-Tests (Testlücke, kein Produktfehler — der alte Code schickte den
Aufruf nie doppelt).

| # | Codex | Inhalt | Herkunft | Marke |
|---|---|---|---|---|
| C1 | H1 | Rückruf zeigt Antworttext des Token-Endpunkts und `error_description` ungefiltert (nur Admin, nicht in Log/Audit). Fehlertext nur aus Status und bekanntem OAuth-Fehlercode. | alt | `[Claude-prüfbar]`, erledigt |
| C2 | H2 | Neuabfrage: Die Audit-Zeile einer geänderten Definition kann (Compliance, voller Kanal) werfen, bevor das geprüfte Inventar steht — das Werkzeug bleibt unter altem Namen aufrufbar. | alt | `[Claude-prüfbar]`, erledigt |
| C3 | H3 | Die Erneuerungsanforderung (V1) geht bei einem gestörten Token-Endpunkt verloren; der nächste Versuch legt das abgewiesene Token wieder vor. | neu | `[Claude-prüfbar]`, erledigt |
| C4 | H4 | Abgebrochenes Abschalten während des Drains lässt den Upstream laufen; Umkonfigurieren stoppt vor dem Speichern. | alt | `[Claude-prüfbar]`, erledigt |
| C5 | H5 | Eine hinter dem Löschen wartende Admin-Anfrage holt den Upstream zurück (unsichtbare Schleife, nach Neustart wieder da). | alt | `[Claude-prüfbar]`, erledigt |
| C6 | H6 | Die Frist (Z4) begrenzt nur `ConnectAsync`, nicht Abfrage und Pin-Prüfung; ein spätes Ergebnis wird noch veröffentlicht. | alt | `[Claude-prüfbar]`, erledigt |
| C7 | H7 | `ToolInvoker` gibt den Fremdtext eines gescheiterten Aufrufs an Client, Task-Ablage und Trace. | alt | `[Claude-prüfbar]`, erledigt |
| C8 | M1 | V1 erneuert auch bei 403, und die gesunde Minute läuft ab der Erneuerung statt ab der gesunden neuen Verbindung. | neu | `[Claude-prüfbar]`, erledigt |
| C9 | M2 | Der Anstoß im OAuth-Rückruf verpufft, wenn gerade ein Versuch läuft. | neu | `[Claude-prüfbar]`, erledigt |
| C10 | M3 | Eine späte Token-Erneuerung überschreibt eine neue Autorisierung oder macht „Trennen" rückgängig. | alt | `[Claude-prüfbar]`, erledigt |
| C11 | M4 | Eine frisch veröffentlichte Verbindung schließt sich dem Neuaufbau der alten an. | alt | `[Claude-prüfbar]`, erledigt |
| C12 | M5 | Die V2-Bremse leert beim Auslösen ihr eigenes Fenster. | neu | `[Claude-prüfbar]`, erledigt |
| C13 | M6 | Der W15-Test kann die doppelte Ausführung nicht entdecken (kein Event-Store, Sitzungen nach Neustart frisch). | neu | `[Claude-prüfbar]`, erledigt |
| C14 | M7 | Beim Herunterfahren schließt das Audit vor dem letzten Nachreichen des Supervisors. | alt | `[Claude-prüfbar]`, erledigt |
| C15 | L1 | Die volle Warteschlange zurückgestellter Zeilen verwirft die älteste, bevor sie nachreicht. | alt | `[Claude-prüfbar]`, erledigt |

### Zweites Codex-Review vom 2026-11-18 (`385c3e4`)

Codex (gpt-6.2-sol) hat 15 weitere Befunde gemeldet, die meisten tiefer an denselben Stellen. Auf
deinen Wunsch sind alle behoben (Präzisierungen im Vertrag, Abschnitt 10, N9 – N19). „Alt" heißt:
schon vor `0140a3b` so. Beim Test zu D13 fiel ein weiterer Fehler auf: Nach einer mit 404
abgewiesenen Wiederaufnahme lief jeder weitere Aufruf in den beendeten SDK-Client und wurde als
„nicht wiederholt" abgewiesen, bis das Lebenszeichen den Verlust bemerkte. Behoben unter D13 (N18).

| # | Codex | Inhalt | Herkunft | Marke |
|---|---|---|---|---|
| D1 | H1 | Aufruffehler: Das vorgelegte OAuth-Token steht nicht in der Konfiguration und wird nicht maskiert; der Timeout-Zweig gibt `ex.Message` weiter; der äußere Fang loggt das Ausnahmeobjekt. | C7 | `[Claude-prüfbar]`, erledigt |
| D2 | H2 | Rückruf: falscher `iss` steht wörtlich in der Antwort; das Request-Log von ASP.NET Core schreibt die Query samt Autorisierungscode. | C1 | `[Claude-prüfbar]`, erledigt |
| D3 | H3 | Ein geändertes Werkzeug wird erst nach der Prüfung aller anderen zurückgezogen — hängt oder wirft eine davon, nie. | C2 | `[Claude-prüfbar]`, erledigt |
| D4 | H4 | Das Stoppen wartet nach der Gnadenfrist unbegrenzt auf einen Loop, der seinen Abbruch ignoriert; eine werfende Drain-Meldung verhindert den Abbruch; späte Arbeit kann nach dem Stopp noch veröffentlichen. | alt | `[Claude-prüfbar]`, erledigt |
| D5 | M1 | Rückzug geänderter Werkzeuge ohne Prüfung der Verbindung: Eine alte Neuabfrage oder ein verspäteter Aufbau trifft das Inventar einer neueren Verbindung; Aufräumen, wenn die Frist das Ergebnis knapp verliert. | C2, C6 | `[Claude-prüfbar]`, erledigt |
| D6 | M2 | Die Erneuerungsanforderung markiert das nächste gelesene Token als abgewiesen, nicht das der abgewiesenen Verbindung. | C3 | `[Claude-prüfbar]`, erledigt |
| D7 | M3 | `ReplaceAsync` erkennt das Token nur am Zeitpunkt des Erhalts; zwei Tokens mit gleichem Zeitpunkt überschreiben sich; die Standardumsetzung ist nicht atomar. | C10 | `[Claude-prüfbar]`, erledigt |
| D8 | M4 | Die gesunde Minute aus V1 läuft über gewöhnliche Wiederholungen und Degraded hinweg nicht als ununterbrochene Gesundheit. | C8 | `[Claude-prüfbar]`, erledigt |
| D9 | M5 | Eine Token-Erneuerung ohne neues Refresh-Token löscht das alte (RFC 6749 §6 erlaubt das Weglassen). | alt | `[Claude-prüfbar]`, erledigt |
| D10 | M6 | Ein 401/403 bei der Katalogabfrage lässt den Upstream auf `Healthy`. | alt | `[Claude-prüfbar]`, erledigt |
| D11 | M7 | N4 widerspricht Abschnitt 5.1 (`false` heißt „nichts getan"); der Rückruf während `Starting` verpufft weiter. Eigenes Signal für neue Zugangsdaten statt `ConnectNowAsync`. | C9 | `[Claude-prüfbar]`, erledigt |
| D12 | M8 | Entfernen löscht erst im Speicher, dann in der Ablage (scheitert das, kehrt der Upstream nach dem Neustart zurück); beim Herunterfahren kann eine wartende Anfrage noch einen Loop starten. | C5 | `[Claude-prüfbar]`, erledigt |
| D13 | M9 | Der SSE-Test verlangt keine Wiederaufnahme und keine ungültige Sitzung. | C13 | `[Claude-prüfbar]`, erledigt |
| D14 | M10 | Bricht das Herunterfahren eine Audit-Schreibung ab, kann der Drain dieselben Zeilen an späterer Kettenstelle schreiben (Lücke ohne gezählten Verlust). | alt | `[Claude-prüfbar]`, erledigt |
| D15 | L1 | Plan: C7 stand noch als „außerhalb". | Plan | `[Claude-prüfbar]`, erledigt |

### Drittes Codex-Review vom 2026-11-18 (`49e1792`)

Codex (gpt-6.2-sol) meldet 17 Befunde (6 hoch, 9 mittel, 2 niedrig, kein kritischer). Jeder ist
gegen den Code geprüft. „Neu" heißt: mit `49e1792` entstanden; „alt": schon in `385c3e4`.
„Schwere" ist die geprüfte Einschätzung, nicht die von Codex.

| # | Codex | Inhalt | Urteil | Herkunft | Schwere | Marke |
|---|---|---|---|---|---|---|
| E1 | H2 | Nach dem 10-s-Join (D4) läuft der alte Loop auf demselben Eintrag weiter; ein später Fehlschlag setzt `Degraded`/`Failed`, schließt die **neue** Verbindung und lässt ihren Neuaufbau scheitern. Nur `Publish` ist gezäunt. | echt | neu (D4) | hoch | `[Claude-prüfbar]` behoben, Abnahme offen |
| E2 | H3 | OAuth nach Umkonfigurieren: Der Rückruf schickt das **aktuelle** Client-Secret an den Token-Endpunkt aus dem **alten** Vorgang; auch die Erneuerung geht mit dem neuen Secret an den alten Aussteller, und ein gespeichertes Token geht an einen neuen Endpunkt. Token und Vorgang sind nur an die ServerId gebunden. | echt, breiter als gemeldet | alt | hoch | `[Claude-prüfbar]` behoben, Abnahme offen |
| E3 | H4 | Fehler aus Lebenszeichen und Katalogabfrage werden nur mit den Geheimwerten der Konfiguration gesäubert, nicht mit dem vorgelegten Token (Status, Log, Audit). `isError`-Inhalte sind Nutzlast, kein Verstoß gegen P1–P3. | teilweise | alt | mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| E4 | M3 | Stoppen: Abbruch und Dispose sind unbegrenzt; während des Drains gibt `GetConnection` die Verbindung weiter heraus; Ressourcen- und Prompt-Aufrufe zählen nicht als laufend. | teilweise, im Kern echt | alt | mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| E5 | M5 | Ein 401/403 bei der **manuellen** Neuabfrage lässt den Upstream `Healthy` (Abschnitt 2 gilt für jede Katalogabfrage). Nach „Annahme" fehlt dann die Audit-Zeile, und der Client bekommt 500, obwohl die Annahme wirkt. | echt | alt | mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| E6 | M2 | D8 unvollständig: Zeit in `Degraded` zählt weiter als gesund (`healthySince` bleibt stehen). | echt | alt | mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| E7 | M4 | Eine veraltete Neuabfrage schreibt Pin-Einträge (Pending löschen/überschreiben). `AcceptAsync` nimmt keinen Hash — angenommen wird, was beim Klick ansteht, nicht unbedingt das Geprüfte. | echt | alt | mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| E8 | M6 | D12: Hinzufügen wird gespeichert und danach abgewiesen, wenn das Herunterfahren dazwischen beginnt (kehrt nach dem Neustart zurück). Test erwartet die falsche Ausnahme und prüft keinen Wettlauf. | teilweise | neu (D12) | niedrig – mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| E9 | M9 | D14: Geht nur die Bestätigung eines Commits verloren (v. a. Postgres), schreibt der Drain dieselben Kettenstellen doppelt; der Index auf (ChainId, ChainIndex) ist nicht eindeutig → falscher Manipulationsalarm. | echt, schmal | gemischt | niedrig – mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| E10 | M8 | Mein Zusatzfix zu D13 markiert bei **jedem** internen Abbruch die Sitzung als verloren (auch stdio, sessionloses HTTP) und verbraucht das V2-Budget. Die SDK-Prüfung allein deckt den Fall ab. | teilweise | neu (D13) | niedrig – mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| E11 | M1 | D6: Ein später, verlassener Handshake überschreibt das gemerkte vorgelegte Token; die Markierung wird unbedingt entfernt. | echt, eng | neu (D6) | niedrig – mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| E12 | H1 | Jedes 404 bei bestehender Sitzung gilt als „nicht ausgeführt" und wird wiederholt — auch eines von einem Zwischenglied nach der Ausführung. Setzt Abschnitt 4 wörtlich um. | teilweise, vertragskonform | alt | niedrig – mittel | `[braucht Nutzer]` (Vertrag 4) entschieden: nur MCP-404; behoben, Abnahme offen |
| E13 | M7 | Das Signal für neue Zugangsdaten weckt auch einen Upstream mit erschöpfter Grenze (ein Versuch je Rückruf). 3.3 nennt als Ausnahme nur „Jetzt verbinden". Verhalten bestand schon über `ConnectNowAsync`. | teilweise | alt | niedrig | `[braucht Nutzer]` (N12) entschieden: bleibt, als Ausnahme in 3.3 eingetragen |
| E14 | H5 | Die Zulassungsprüfung liest das Inventar des Eintrags, nicht das der ausführenden Verbindung. Das Rennen Prüfung/Versand ist praktisch folgenlos. | teilweise | alt | niedrig | `[Claude-prüfbar]` behoben, Abnahme offen |
| E15 | H6 | D7: Zwei **gleichzeitige** Speicherungen können denselben Zeitpunkt berechnen (nur bei gleichem Tick oder rückläufiger Uhr). | teilweise | Fix neu, Rest alt | niedrig | `[Claude-prüfbar]` behoben, Abnahme offen |
| E16 | L1 | Der D11-Test für den laufenden Wiederholungsversuch besteht auch mit altem Verhalten. | echt | neu | niedrig | `[Claude-prüfbar]` behoben, Abnahme offen |
| E17 | L2 | Vertrag: N3 („mit Standardumsetzung") widerspricht N14. | echt | neu | niedrig | `[Claude-prüfbar]` behoben, Abnahme offen |

D8 ist mit E6 vervollständigt.

**Stand nach „Alle Claude-prüfbaren":** Die fünfzehn prüfbaren Befunde sind gebaut; ihre Präzisierungen
stehen im Vertrag, Abschnitt 10, N20 – N30. Gegenprobe: Jeder neue Test eines Fehlerbefunds scheitert
gegen `49e1792` (E16 und E17 sind Test- bzw. Vertragsarbeit und haben keinen).

E12 und E13 am 2026-11-18 entschieden: Wiederholt wird nur ein 404 mit JSON-RPC-Fehlerkörper
(Vertrag Abschnitt 4); das Signal für neue Zugangsdaten weckt auch einen erschöpften Upstream und steht
als zweite Ausnahme in 3.3. Offen für den Nutzer: die Abnahme von N20 – N30 und der Präzisierung in
Abschnitt 4.

### Viertes Codex-Review vom 2026-11-18 (`60d8bf9`)

Codex (gpt-6.2-sol) meldet 15 Befunde (8 hoch, 6 mittel, 1 niedrig, kein kritischer). Jeder ist
gegen den Code geprüft (nur gelesen). „Schwere" ist die geprüfte Einschätzung. Alle sind behoben
(Vertrag N31 – N41); jeder neue Test scheitert gegen `60d8bf9`. Offen: Abnahme N31 – N41; das
Muster „erst Wirkung, dann Audit" in den übrigen Verwaltungsaktionen.

| # | Codex | Inhalt | Urteil | Herkunft | Schwere | Marke |
|---|---|---|---|---|---|---|
| F1 | H3 | Fehler aus `initialize`/`tools/list` beim Aufbau und Neuaufbau werden nur mit den Geheimwerten der Konfiguration gesäubert. Eine JSON-RPC-Fehlermeldung, die das Token zitiert, erreicht Status, Audit, Log und über die Neuaufbau-Ausnahme den Agenten. | echt | alt, von E3 nicht abgedeckt | mittel – hoch | `[Claude-prüfbar]` behoben, Abnahme offen |
| F2 | H4 | Nach dem Schließen der Verbindung im Fehlerpfad (ohne Frist) bucht ein abgelöster Loop noch `MarkFailed` auf den Nachfolger (falscher Status, falsches Audit, falsches „wieder verbunden") und setzt im `finally` des Wartens `Wake` des Nachfolgers zurück. Ein hängendes Schließen blockiert ohne Eingriff jede weitere Wiederholung. | echt | alt, E1 unvollständig | mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| F3 | M2 | OAuth-Rückruf: Audit nach dem Speichern; weist ein voller Compliance-Kanal ab, ist das Token gespeichert ohne Audit, und `CredentialsChanged` entfällt (erschöpfter Upstream bleibt liegen). | echt | alt, durch E13 verschärft | mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| F4 | H8 | Pin-Annahme (REST und UI): erst festgeschrieben, dann auditiert. Im Compliance-Modus bei vollem Kanal ist der Hash vertraut ohne Audit-Zeile. Dasselbe Muster „erst ändern, dann auditieren" steht systemweit in den Verwaltungs-Endpunkten. Der V4-Teil (Prüfung beim Verbinden) ist vertragsgemäß. | echt für Annahme | alt, systemweit | mittel | `[braucht Nutzer]` entschieden „alle beheben": nur die Annahme; behoben, Abnahme offen |
| F5 | H1 | Restfenster der OAuth-Bindung: Ein Rückruf kann zwischen Speichern und Nachprüfung das Token der alten Ressource an den neuen Loop geben (Millisekunden). Die Nachprüfung löscht ohne Bedingung und kann im Extremfall ein neueres Token löschen. | teilweise | E2 hat verengt, nicht geschlossen | mittel / niedrig | `[Claude-prüfbar]` (Bindung beim Token speichern) behoben, Abnahme offen |
| F6 | H5 | Ein laufender Neuaufbau kann während des Drains veröffentlichen; die neue Verbindung nimmt Anfragen an und wird nach der Gnadenfrist mitten in Aufrufen geschlossen. `Publish` beachtet `Stopping` nicht. | echt, enges Fenster | neu (E4 unvollständig) | niedrig – mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| F7 | H7 | Veraltete Neuabfrage schreibt weiter Pins, wenn sie erst in `VerifyAsync` hängt (Prüfung nur davor). Folge nur Anzeige/Bedienung, kein Umgehen der Sperre. Der E7-Test hält zu früh an. | teilweise; Testlücke echt | alt, E7 verengt | niedrig – mittel | `[Claude-prüfbar]` behoben, Abnahme offen |
| F8 | H2 | Die 404-Erkennung nimmt jedes Objekt als `error` an (auch `{}`). Praktisch nur durch ein MCP-fähiges Zwischenglied erreichbar. Vertrag Abschnitt 4 ist ebenso weit gefasst. | teilweise | neu (E12) | niedrig | `[Claude-prüfbar]` (Vertrag 4 präzisieren) behoben, Abnahme offen |
| F9 | H6 | Eine ersetzte Hülle läuft nach einem Umkonfigurieren über die neue Verbindung (gleicher Werkzeugname, identische Definition, sonst zurückgehalten). Vertragsgemäß nach N29, aber die Bindung an die Konfigurationsfassung fehlt. Bestand vorher schon für HTTP mit Sitzung; 60d8bf9 macht es für alle Transporte deterministisch. | teilweise | gemischt | niedrig | `[braucht Nutzer]` entschieden: nur dieselbe Konfiguration; behoben, Abnahme offen |
| F10 | M1 | Ein Hinzufügen, das die Zulassungsfrist überdauert, registriert nach dem Herunterfahren noch einen Eintrag ohne Loop. Konfiguration ist gespeichert und kommt nach dem Neustart zurück. Weicht von N26 ab. | teilweise | neu (E8) | niedrig | `[Claude-prüfbar]` (N26 ergänzen) behoben, Abnahme offen |
| F11 | M5 | Scheitert der letzte Best-Effort-Versuch nach dem Festschreiben, wird der ganze Batch als Verlust gezählt (zu hoch, nichts verloren). | echt | alt | niedrig | `[Claude-prüfbar]` behoben, Abnahme offen |
| F12 | M6 | 401/403 bei der Metadaten-Discovery der Erneuerung gilt als „nicht erreichbar" (exponentieller Backoff statt 15 min). | teilweise | alt | niedrig | `[Claude-prüfbar]` behoben, Abnahme offen |
| F13 | M3 | Ein veralteter Aufbau kann im `else`-Zweig einen neueren Erneuerungsvermerk löschen. Ohne `await` dazwischen praktisch nicht erreichbar. | teilweise | alt | niedrig | `[Claude-prüfbar]` behoben, Abnahme offen |
| F14 | M4 | Nach Trennen und Neuanlage kann dieselbe Kennung (`ObtainedAt`) wieder entstehen; nur bei Uhr auf den Tick gleich. | teilweise, theoretisch | alt | niedrig | `[Claude-prüfbar]` (Versionsspalte, Migration) behoben, Abnahme offen |
| F15 | L1 | Der Test mit zehn Aufrufern baut keinen schon laufenden Aufruf auf; eine Regression, die abgebrochene Aufrufe wiederholt, bestünde ihn. | echt (Testlücke) | alt | niedrig – mittel | `[Claude-prüfbar]` behoben, Abnahme offen |

Gemeinsames Muster von F2, F5, F6, F7, F9, F14: Es fehlt eine Generation je Loop bzw.
Konfigurationsfassung bzw. Token-Revision, gegen die spät eintreffende Schritte geprüft werden.

### Fünftes Codex-Review vom 2026-11-18 (`60d8bf9..fb43db0`)

Codex (gpt-6.2-sol) meldet 13 Befunde (3 hoch, 8 mittel, 2 niedrig, kein kritischer). Jeder ist
gegen den Code geprüft (nur gelesen). „Schwere" ist die geprüfte Einschätzung. Auf den Auftrag „Die
sieben echten" sind **G1 – G7 behoben, Abnahme offen** (Vertrag N42 – N48; jeder neue Test gegen
`fb43db0` rot belegt). G8 – G13 sind nicht behoben. Gegen die abgenommenen Abschnitte 1 – 9 fand Codex keinen Widerspruch; N38 muss N26
ausdrücklich ersetzen.

| # | Codex | Inhalt | Urteil | Herkunft | Schwere | Marke |
|---|---|---|---|---|---|---|
| G1 | H1 | Zitiert der Upstream das Token in einem JSON-RPC-Fehler auf `initialize` (HTTP 200), wird es nirgends maskiert: Der Connector wirft vor dem Entstehen der Verbindung, die Bereinigung aus F1 setzt erst bei der Katalogabfrage an. Erreicht Status, Audit, Log und über die Neuaufbau-Ausnahme den Agenten. | echt | alt, F1 unvollständig | mittel – hoch | `[Claude-prüfbar]` |
| G2 | M2 | `CredentialsChanged` verpufft in `Degraded`/`Healthy`, ohne das Merkzeichen zu setzen. Scheitert ein laufender Neuaufbau danach mit dem alten Token, ist der Upstream erschöpft bzw. sitzt die 15 min ab, obwohl ein gültiges Token vorliegt. Widerspricht 3.3 und N12. | echt, breiter als gemeldet | alt | mittel | `[Claude-prüfbar]` |
| G3 | M6 | Ein DNS-Fehler beim Metadatenabruf (Standard ohne private Ziele) wird zur abgelehnten Anmeldung (15 min, falscher Hinweis „neu autorisieren"). Außerdem: Unsinn auf dem ersten Well-known-Pfad (Array, `issuer` kein Text) überspringt die übrigen Pfade und wird kurz wiederholt. | echt | DNS: neu (F12); Rest alt | mittel / niedrig | `[Claude-prüfbar]` |
| G4 | M3 | Das Lebenszeichen läuft während des Drains weiter; überschreitet es die Toleranz, schließt der Loop die Verbindung, auf deren Aufrufe der Drain wartet, und bucht `Failed` mitten im Abschalten. | echt | alt | mittel – niedrig | `[Claude-prüfbar]` |
| G5 | H2 | Zwei Aufrufe über dieselbe alte Hülle: Wechselt der zweite die Ersatzverbindung, bevor der erste seinen Fehler formatiert, wird mit dem falschen Token bereinigt. Braucht einen zitierenden Upstream und eine Ersetzung genau im Fenster. | echt, enges Rennen | alt | niedrig – mittel | `[Claude-prüfbar]` |
| G6 | M8 | Pin-Annahme: Geht die Berichtigungszeile (`TryRecord`) bei vollem Kanal verloren, wird kein Verlust gezählt; im Audit steht eine Annahme, die nicht stattfand. Die Oberfläche meldet den Konflikt gar nicht. | echt, selten | neu (F4) | niedrig – mittel | `[Claude-prüfbar]` |
| G7 | M5 | Eine laufende Neuabfrage kann nach dem Entfernen des Upstreams noch einen Pin einfügen (Zeile und Anzeige bis zum Neustart). `RemoveAsync` nimmt die Prüf-Sperre nicht. | echt, enges Fenster | gemischt | niedrig | `[Claude-prüfbar]` |
| G8 | M4 | Ressourcen und Prompts haben die Prüfung auf eine veraltete Hülle nicht: Nach Aus-/Einschalten scheitert eine Ressource mit „wird gestoppt", wo ein Werkzeug übergeben wird (Ungleichbehandlung gegen Abschnitt 4). Senden über einen alten Transport ist nur theoretisch. | teilweise | alt | niedrig | `[Claude-prüfbar]` |
| G9 | M1 | Ein später OAuth-Rückruf kann ein neueres Token überschreiben; das Umkonfigurieren löscht ohne Bedingung. Braucht einen hängenden Token-Endpunkt und einen zweiten, vollständigen Browser-Durchlauf in Sekunden. | teilweise | alt, durch F5 entschärft | niedrig | `[Claude-prüfbar]` |
| G10 | H3 | Ein abgelöster Loop könnte die Live-Verbindung seines Nachfolgers abhängen. Zwischen Abbruchprüfung und Abhängen liegt nur synchroner Code; erreichbar nur, wenn ein Thread länger als 10 s steht. | teilweise, theoretisch | alt | niedrig | `[Claude-prüfbar]` |
| G11 | L1 | Registrieren gegen Herunterfahren: Restfenster nur bei einem Thread, der zwischen zwei synchronen Anweisungen 10 s steht. | teilweise, theoretisch | gemischt | niedrig | `[Claude-prüfbar]` |
| G12 | L2 | Die Audit-Nachzählung ist nur über das Abbruchsignal befristet; im schlimmsten Fall blockiert sie bis zum Standard-Timeout der Datenbank (30 s je aufgegebenem Batch). | teilweise | neu (F11) | niedrig | `[Claude-prüfbar]` |
| G13 | M7 | Die volle Endpunkt-URL (mit Query) steht in einer OAuth-Fehlermeldung an den Browser. Kein Leck: nur Admins, die die URL ohnehin sehen; nicht in Audit oder Log. Hygiene. | nicht als Leck | alt | niedrig | `[Claude-prüfbar]` |

Neu durch die vierte Runde sind G3 (DNS), G6 und G12; der Rest war schon vorher im Code.

**Stand der Behebung (G1 – G7):**

| # | Fix | Test (rot gegen `fb43db0`) |
|---|---|---|
| G1 | `StreamableHttpUpstreamConnector` bereinigt den Handshake-Fehler (`HandshakeFailure`, `CredentialRedaction`). Dabei gefunden (schon vorher im Code): Teilten zwei Geheimwerte ihre ersten sechs Zeichen, blieb der Rest des zweiten stehen; jetzt werden alle Treffer am unveränderten Text gesucht | `HandshakeCredentialLeakTests` (4); `RedactionCorpusTests.The_upstream_failure_path_masks_presented_credentials_whole_and_cut` |
| G2 | `Entry.AttemptRunning` statt Anzeigezustand in `CredentialsChanged` | `SupervisorReviewG2G4G7Tests` (2) |
| G3 | DNS-Fehler mit innerer `SocketException`, auch am Token-Endpunkt; Formprüfung der Well-known-Dokumente | `OAuthDiscoveryClassificationTests` (6) |
| G4 | Im Drain: Ping-Fehler ignoriert, keine Erneuerung, keine Katalogabfrage, Loop schließt nichts | `SupervisorReviewG2G4G7Tests` (1) |
| G5 | `CountedAsync` bereinigt auf der ausführenden Verbindung | `ReplacementCredentialLeakTests` (1) |
| G6 | `ReportDiscarded(1)` bei verworfener Berichtigung; Meldung `pin-message` in der Oberfläche | `ToolDefinitionAcceptLossTests` (1) |
| G7 | `Entry.PinsRetired`; späte Prüfung räumt selbst nach | `SupervisorReviewG2G4G7Tests` (1) |

Offen dazu: Die Konfliktmeldung der Oberfläche (G6) hat keinen automatischen Test (kein bUnit im
Repo, die Playwright-Tests laufen nicht in der CI). Bei G7 steht ein spät geschriebener Pin bis zum
Nachräumen kurz in der Anzeige; eine harte Bedingung im `ToolDefinitionPinStore` wäre die
gründlichere Lösung. Nebenbefund, nicht behoben: `StartLoop` setzt `CredentialRenewalRequested` nicht
zurück.

### Sechstes Codex-Review vom 2026-11-19 (`fb43db0..079bb33`)

Codex (gpt-6.2-sol) meldet 9 Befunde (2 hoch, 5 mittel, 2 niedrig, kein kritischer) zur Behebung
von G1 – G7. Jeder ist gegen den Code geprüft (nur gelesen, nichts behoben). „Schwere" ist die
geprüfte Einschätzung; die Spalte „Codex" nennt Codex' Stufe. Gegen die abgenommenen Abschnitte
1 – 9 fand Codex keinen Widerspruch; der Widerspruch in N47 besteht zu N40, nicht zu §§ 2/5.2.

| # | Codex | Inhalt | Urteil | Herkunft | Schwere | Marke |
|---|---|---|---|---|---|---|
| H1 | hoch | Ein `CallToolResult` mit `isError: true` geht ohne jede Bereinigung an den Agenten (`SdkUpstreamConnection.cs:300` → `ToolInvoker.cs:267` → `GatewayMcpHandlers.cs:427`). Zitiert der Upstream das Token, steht es beim Agenten; der Secret-Guard fängt nur bekannte Muster (JWT nur `Observe`). Schärfer als gemeldet: Der eigene OpenAPI-Connector legt bei 401/403 den Antwortkörper als `isError`-Text ab (`OpenApiUpstreamConnector.cs:159`), OpenRPC übernimmt Fehlermeldung und `data`. E3 hatte `isError`-Inhalte als Nutzlast gewertet; N8 nennt „gescheiterte Werkzeugaufrufe an den Agenten", lässt aber offen, ob ein `isError`-Ergebnis dazuzählt. Kein Test. | echt, Vertragslücke | alt | mittel – hoch | `[braucht Nutzer]` (N49: gelten P1 – P3 für `isError`-Ergebnisse?) |
| H2 | hoch | Im Modus BestEffort zählt `Record` bei vollem Kanal nur den Verlust; Pin-Annahme, OAuth-Rückruf und Trennen wirken trotzdem. Vertragsgemäß: F4/N33 verlangen das Abweisen nur, wo das Audit abweist (Compliance); BestEffort ist nach ADR-0007 gezählter Verlust (`LostAtIntake`, BFR-DB-0007). Codex' Vorschlag (`TryRecord`, 503) wäre eine neue Entscheidung: diese drei Aktionen in jedem Modus audit-pflichtig. | vertragskonform | alt (ea783a8) | — | `[braucht Nutzer]` (Auslegung von F4) |
| H3 | mittel | Im Drain geht das Lebenszeichen weiter (`WatchAsync` prüft `Stopping` erst im `catch`, Z. 1299); `PingAsync` und `DiscoverAsync` laufen an der Zulassung vorbei. Die Katalogabfrage prüft `Stopping` vor dem Warten auf `RediscoveryLock`, nicht danach; `RediscoverAsync` prüft gar nicht. Ein 404 beendet nach D13 den SDK-Client samt laufendem Aufruf. Der Supervisor selbst schließt im Drain nichts; derselbe 404 träfe außerhalb des Drains genauso. Der G4-Test spritzt eine `IOException` ein und sieht das nicht. | teilweise | G4 unvollständig | niedrig | `[Claude-prüfbar]` |
| H4 | mittel | Fenster zwischen Verbindungsverlust und `Failed`: `RunLoopAsync` zählt den Versuch (Z. 687) und wartet dann auf `CleanupConnectionAsync` (bis 10 s). Währenddessen `Wake == null`, `AttemptRunning == false`, Zustand `Healthy`/`Degraded` → `CredentialsChanged` setzt nichts. Mit `maxRetries = 0` bleibt der Upstream erschöpft (verletzt E13), sonst sitzt er nach einem Anmeldefehler die 15 min ab. | echt | G2 unvollständig | mittel | `[Claude-prüfbar]` |
| H5 | mittel | (a) Eine Prüfung, die `ThrowIfPinsRetired` schon passiert hat, schreibt nach dem Entfernen; scheitert das Nachräumen, bleibt der Pin. Das ist das in N46 und oben genannte Restrisiko. (b) Neu: `AcceptAsync` liest die Zeile, `ForgetServerAsync` löscht Zeilen und Cache, `AcceptAsync` schreibt die alte Zeile in den Cache zurück (`ToolDefinitionPinStore.cs:123 – 128`). Nur Anzeige bis zum Neustart; `VerifyAsync` liest die Datenbank. Ein verwaister Pin wirkt nach einem Konfigurations-Import mit derselben Id als Basis, ohne Rechteausweitung. | (a) bekanntes Restrisiko, (b) echt | G7 unvollständig | niedrig | `[Claude-prüfbar]` |
| H6 | mittel | (a) Ganze Werte unter 4 Zeichen werden nicht maskiert: bewusst (Kommentar, `ConnectionTesterScrubTests`, Umgebungswerte wie „1"), kein Befund. (b) Die Schema-Heuristik nimmt jedes Erstwort bis 10 Zeichen im Bereich `A` – `z` als Schema. Für ein Geheimnis `abc defghijk1234` bleibt `abc defgh` in einer Meldung unmaskiert. Nur bei Geheimnissen mit Leerzeichen (Passphrasen). P3 nennt nur „Bearer"/„Basic". | (a) nicht echt, (b) teilweise | alt | niedrig – mittel | `[Claude-prüfbar]` |
| H7 | mittel | Token-Antwort mit HTTP 200 und falscher Form (Array, `access_token` als Zahl, `expires_in` als Text, wie manche Anbieter es senden) wirft `InvalidOperationException` (`OAuthFlow.cs:254 – 272`). Der Connector fängt nur `OAuthDiscoveryException` → Fehlerart „nicht erreichbar" statt Anmeldung; beim Rückruf 500 statt 400. Gleiche Art wie G3, dort nur die Discovery behoben. | echt | alt | mittel | `[Claude-prüfbar]` |
| H8 | niedrig | N47 („liefert keiner ein brauchbares Dokument, ist das ein dauerhafter Fehler") widerspricht wörtlich N40: Ist ein anderer Pfad gestört (5xx), entscheidet der Code richtig auf Unerreichbarkeit. Nur Wortlaut. | echt (Dokument) | neu (N47) | niedrig | `[Claude-prüfbar]` |
| H9 | niedrig | `HandshakeFailure` baut höchstens 8 Glieder nach; ein 401/403 tiefer in der Kette ginge verloren, die Fehlerart kippte auf „nicht erreichbar". SDK und `HttpClient` liefern 1 – 3 Glieder. Widerspricht in diesem Fall N42 („Fehlerart bleibt"). | teilweise, theoretisch | neu (G1) | niedrig | `[Claude-prüfbar]` |

Neu durch die fünfte Runde sind H3, H4, H5 (b), H8 und H9 (Lücken in G1, G2, G4, G7 und N47); H1,
H6 und H7 waren schon vorher im Code, H2 ist eine Auslegungsfrage.

**Stand der Behebung (H3 – H9, Auftrag „H3 bis H9 zuerst beheben"; Abnahme offen):** H1 und H2
warten auf deine Entscheidung.

| # | Fix | Vertrag | Test (rot gegen `079bb33`) |
|---|---|---|---|
| H3 | Kein Ping im Drain; Katalogabfrage prüft das Stoppen nach dem Warten auf `RediscoveryLock`; `RediscoverAsync` im Drain ohne Wirkung | N49 | `SupervisorReviewH3H4Tests` (3); G4-Test angepasst (verlangte laufende Pings) |
| H4 | `AttemptRunning` ab dem Zählen des Fehlschlags; ein abgelöster Loop zählt keinen Versuch mehr | N50 | `SupervisorReviewH3H4Tests` (1) |
| H5 | (b) Sperre je Upstream um Datenbankschritt und Cache in `ToolDefinitionPinStore` (Prüfen, Annehmen, Vergessen); (a) bleibt Restrisiko nach N46 | N51 | `ToolDefinitionPinForgetRaceTests` (2, echte SQLite) |
| H6 | Schema nur aus fester Liste (`Bearer`, `Basic`, …) | N52 | `ConnectionTesterScrubTests` (1 rot, 4 Fälle Schutz des Schema-Worts) |
| H7 | Formprüfung der Token-Antwort; `expires_in` als Text/Kommazahl, begrenzt | N53 | `OAuthTokenEndpointFailureTests` (9 Fälle) |
| H8 | Wortlaut N47 berichtigt | N47 | `OAuthDiscoveryClassificationTests.Nonsense_beside_a_disturbed_path_arrives_as_unreachable` (hält das schon richtige Verhalten fest, grün) |
| H9 | Ablehnung hinter der Tiefengrenze wird als innerstes Glied angehängt | N54 | `HandshakeFailureDepthTests` (2 rot, 1 Schutz der Grenze) |

### Siebtes Codex-Review vom 2026-11-20 (`079bb33..74c5129`)

Codex (gpt-6.2-sol) meldet 8 Befunde (1 hoch, 5 mittel, 2 niedrig, kein kritischer) zur Behebung
von H3 – H9. Jeder ist gegen den Code geprüft (nur gelesen, nichts behoben); I5 zusätzlich mit
einem Probelauf gegen .NET 10. Spalten wie in der sechsten Runde.

| # | Codex | Inhalt | Urteil | Herkunft | Schwere | Marke |
|---|---|---|---|---|---|---|
| I1 | hoch | Eine ausdrückliche Neuabfrage des alten Eintrags steckt beim Entfernen im Schreiben; ihr spätes Nachräumen (`ForgetRetiredPinsAsync`, `UpstreamSupervisor.cs:2236`) vergisst alle Pins der `ServerId`. Wird dieselbe Id inzwischen per Konfigurations-Import wiederhergestellt (`ServerConfigurationPorts.cs:129`, Id aus dem Export) und hat ihre erste Prüfung schon Pins geschrieben, löscht das späte Nachräumen sie. Die nächste Abfrage nimmt dann eine geänderte Definition als Erstsichtung an (Z6). Das Nachräumen kennt nur die Id, nicht die Registrierung. Das Fenster ist eine Datenbankoperation lang, das Wiederherstellen ein Verwaltungsschritt. | echt, sehr schmal | G7 (nicht durch H5) | niedrig – mittel | `[Claude-prüfbar]` |
| I2 | mittel | `ForgetServerAsync` wartet auf die Sperre je Upstream, die `VerifyAsync`/`AcceptAsync` für ihren Datenbankschritt halten (`ToolDefinitionPinStore.cs:186`); das Entfernen hält dabei die Verwaltungssperre. N46 sagt „Das Entfernen wartet auf keine Prüfung“. Es wartet nur auf einen laufenden Datenbankschritt, nicht auf die Katalogabfrage; hängt die Datenbank, hinge das Vergessen selbst ebenso. Die Tests geben die blockierte Operation nach einer Frist frei und sähen ein echtes Warten nicht. | teilweise (Wortlaut N46/N51) | neu (H5) | niedrig | `[Claude-prüfbar]` |
| I3 | mittel | Läuft `tools/list` schon, wenn der Drain beginnt, schickt `SdkUpstreamConnection.ListAsync` danach noch `resources/list` und `prompts/list` (`SdkUpstreamConnection.cs:257 – 265`) ohne neue Prüfung. Ein 404 darauf könnte nach D13 einen laufenden Aufruf beenden. N49 nennt das Restfenster zwischen Prüfung und Senden, aber nur für eine Anfrage, nicht für die Folgeanfragen. | echt | H3 unvollständig | niedrig | `[Claude-prüfbar]` |
| I4 | mittel | Der Fehlerpfad des Loops prüft `IsStopping` außerhalb von `entry.Gate` (`UpstreamSupervisor.cs:665`); unter der Sperre prüft er nur `ct`, und den bricht der Stopp erst nach dem Drain ab. Beginnt der Stopp dazwischen, schließt `CleanupConnectionAsync` die Verbindung, während der Drain noch auf laufende Aufrufe wartet (verletzt G4/N45). Auslöser ist ein gescheitertes Lebenszeichen oder ein Sitzungsverlust; die Verbindung ist dann meist ohnehin gestört. | echt | G4 (alt) | niedrig – mittel | `[Claude-prüfbar]` |
| I5 | mittel | Ein Token-Feld mit einzelnem Surrogat-Escape (`"\uD800"`) hat `ValueKind == String`, `GetString()` wirft `InvalidOperationException` (Probelauf .NET 10 bestätigt). Der Rückruf fängt nur `OAuthDiscoveryException` (`UpstreamOAuthEndpoints.cs:130`) → 500 statt 400; die Erneuerung wird „nicht erreichbar“. | echt | H7 unvollständig | mittel | `[Claude-prüfbar]` |
| I6 | mittel | Die Schema-Erkennung gilt für jeden Geheimwert, nicht nur für Kopfzeilen: Ein Umgebungswert, der mit `Token ` beginnt, wird ab dem Teil nach dem Leerzeichen gemessen. Unmaskiert bleiben dann das Wort und bis zu 5 Zeichen danach. Das ist dieselbe Restmenge (unter 6 Zeichen), die P3 für jeden Wert hinnimmt; das Schema-Wort selbst ist nicht geheim. Wortlaut: P3 nennt nur „Bearer“ und „Basic“, N52 erweitert die Liste. | teilweise (Wortlaut P3/N52) | neu (H6) | niedrig | entschieden 2026-11-20: Liste aus N52 abgenommen, keine Codeänderung |
| I7 | niedrig | Zwei Autorisierungen während eines laufenden Versuchs setzen dasselbe `CredentialsChanged` und bringen einen Versuch. E13 sagt „ein Versuch je Autorisierung“. Der zweite Versuch nähme dasselbe (neueste) Token; das Zusammenlegen ist sinnvoll und galt schon vor H4 für jeden laufenden Versuch. | Wortlaut E13/N50 | alt | — | entschieden 2026-11-20: Zusammenlegen bleibt, N50 präzisiert |
| I8 | niedrig | (a) `expires_in` als Ziffern-Text über `long.MaxValue` wird abgelehnt, als JSON-Zahl begrenzt. (b) Ein ausdrückliches `null` in `refresh_token`, `scope`, `expires_in` gilt als fehlend; N53 sagt „vorhanden, aber kein Text“. (c) Der Laufzeit-Test prüft nur „länger als 59 min“ und sähe weder Aufrunden noch eine fehlende Zehn-Jahres-Grenze. | (a), (c) echt, (b) Wortlaut | neu (H7) | niedrig | `[Claude-prüfbar]` |

Echt und zu beheben: I1, I3, I4, I5, I8 (a, c). Nur der Vertragswortlaut ist falsch: I2 (N46/N51), I8 (b)
(N53: `null` gilt als fehlend). I6 und I7 sind am 2026-11-20 entschieden (siehe Marke).

**Stand der Behebung (Auftrag „Echte beheben"; Abnahme offen):** I6 und I7 sind ohne
Codeänderung entschieden (Vertrag N52, N50).

| # | Fix | Vertrag | Test |
|---|---|---|---|
| I1 | Entfernen und spätes Nachräumen vergessen Pins nur, solange unter der Id kein Nachfolger steht; gefragt unter der Pin-Sperre | N55 | `SupervisorReviewG2G4G7Tests.I1_…` (rot ohne Fix); `ToolDefinitionPinForgetRaceTests.A_conditional_forget_asks_its_condition_under_the_lock` (echte SQLite; ohne die neue Überladung nicht übersetzbar, kein eigener Rot-Lauf) |
| I2 | Nur Wortlaut: N51 sagt, worauf das Entfernen wartet | N51 | — |
| I3 | Die SDK-Verbindung fragt vor `resources/list` und `prompts/list`, ob sie weitermachen darf; im Drain endet die Abfrage ohne Fehler | N56 | `SupervisorReviewH3H4Tests.I3_…` (rot ohne Fix). Die SDK-Verbindung selbst ist nur über den Supervisor-Test und das Fake abgedeckt |
| I4 | Stopp-Prüfung und Lösen der Verbindung im Fehlerpfad unter `entry.Gate` | N57 | Kein eigener Test: Der Wettlauf braucht einen Haltepunkt zwischen zwei Anweisungen, den es nicht gibt. Die G4-/H3-Tests decken den Drain weiter ab |
| I5 | Ungültiger Text in Token-Feldern wird zum Formfehler | N58 | `OAuthTokenEndpointFailureTests` (3 neue Fälle, rot ohne Fix) |
| I8 | (a) Ziffern-Text über `long` begrenzt; Unendlich begrenzt vor der Umwandlung; (b) Wortlaut N53; (c) Laufzeit-Test prüft genaue Werte | N53, N58 | `OAuthTokenEndpointFailureTests.The_lifetime_…` (7 Fälle, 1 rot ohne Fix) |

## Prüfliste für den Nutzer

1. ~~Vertrag lesen und abnehmen; die sechs Fragen in Abschnitt 8 beantworten.~~ Erledigt am
   2026-11-16, samt der Präzisierung „Gleichzeitige Aufrufe — gemessen" in Abschnitt 4.
2. Vor dem Deploy: In der Versionshistorie von „whiskers" nachsehen, ob die Konfiguration ein Feld
   `restart` trägt. Fehlt es (erwartet), gilt nach dem Update die unbegrenzte Wiederholung von selbst.
3. Deploy auf Badwolf. **Achtung:** Der Neustart von `mcpmcp` kappt alle MCP-Verbindungen über
   `:8443` — auch die der gerade arbeitenden Claude-Sitzungen. Zeitpunkt wählen.
4. Abnahme: den whiskers-Container neu starten und in der Server-Verwaltung zusehen. Erwartet:
   `Failed` mit Fehlerart „nicht erreichbar", sichtbare Frist, dann ohne Eingriff `Healthy` mit der
   alten Werkzeugzahl. Bei einem Ausfall von 30 s liegt das rechnerisch unter einer Minute: bis zu
   20 s bis zur Erkennung (zwei Lebenszeichen im Abstand von 10 s), dann Versuche nach weiteren 5,
   10 und 20 s. Spricht Whiskers die sessionlose Fassung und ist schneller zurück als ein
   Lebenszeichen-Abstand, wird der Neustart gar nicht als Ausfall sichtbar.
5. Gegenprobe Authentifizierung: den Schlüssel von „whiskers" in der Gateway-Konfiguration
   absichtlich falsch setzen. Erwartet: `Failed` mit „Authentifizierung abgelehnt", nächster Versuch
   in rund 15 min, im Log von Whiskers **kein** Dauerfeuer. Danach zurücksetzen.
6. Im Log und im Audit des Gateways nach dem Anfang des Whiskers-Schlüssels suchen — erwartet: kein Treffer.
7. Ein Blick auf die Seite Server-Verwaltung: Text und Knopf an der richtigen Stelle, auch schmal.
