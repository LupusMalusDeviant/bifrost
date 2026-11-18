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
| — | außerhalb | `ToolInvoker` reicht Fremdtext eines gescheiterten Aufrufs an Agent und Audit (jetzt C7); der lastempfindliche p95-Test steht in der funktionalen Suite. Eigene Aufgabe, nicht Teil dieses Vertrags. | `[braucht Nutzer]` |

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
