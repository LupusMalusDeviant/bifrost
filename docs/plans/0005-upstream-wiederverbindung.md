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
