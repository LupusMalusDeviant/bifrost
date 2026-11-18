# Plan 0005 — Selbsttätige Wiederverbindung von Upstreams

**Stand:** 2026-11-16 · **Status:** W0 – W7 erledigt (Vertrag abgenommen am 2026-11-16, gebaut und
getestet; die Präzisierung in Abschnitt 4 des Vertrags ist ebenfalls abgenommen); offen sind W8
und W9
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
