# ADR-0022: Schärfe und Durchsetzung trennen — zweite Tür `invoke_sensitive_tool`

- **Status:** **Akzeptiert.** Entschieden am 2026-07-30 mit dem Product Owner.
- **Datum:** 2026-07-30
- **Autor:** LupusMalus (Product Owner), ausgearbeitet mit Claude
- **Grundlage:** Ergänzt [ADR-0012](0012-approval-flows-asynchron.md) — die Warteschlange bleibt der
  Vorgabeweg und wird von dieser Entscheidung nicht abgelöst.

## Kontext und Problemstellung

Die Freigabepflicht aus ADR-0012 hat den ersten echten Betrieb nicht überlebt — nicht technisch,
sondern in der Bedienung. Auf der Instanz auf Badwolf sind zwölf Whiskers-Werkzeuge als
freigabepflichtig markiert. Jeder Aufruf legt eine Anfrage in die Warteschlange, und der Mensch muss
in die Weboberfläche oder die CLI wechseln, um sie zu erteilen. Bei einem Deploy, der aus einem
Dutzend Schritten besteht, sind das ein Dutzend Wechsel.

Der naheliegende Ausweg — eine Rückfrage im laufenden Gespräch per MCP-Elicitation — wurde gebaut
(v0.8.0 bis v0.8.2) und funktioniert serverseitig nachweislich. Er trägt trotzdem nicht: Der
eingesetzte Client meldet die Fähigkeit, beantwortet die Rückfrage aber synthetisch mit `cancel`,
ohne je einen Dialog zu zeigen (bekannter Fehler, `anthropics/claude-code#56243`). Auf eine
Client-Eigenschaft, die sich jederzeit ändern kann, lässt sich kein Bedienkonzept gründen.

Damit stand die Frage, die immer am Ende solcher Reibung steht: Schaltet man den Schutz ab? Genau so
sterben Freigabemechanismen üblicherweise — nicht durch eine Entscheidung, sondern durch einen
Schalter, den irgendwann jemand umlegt und niemand dokumentiert.

Bei der Untersuchung fiel eine zweite, ältere Schwäche auf. Ein MCP-Client kann seine eigene
Rückfrage nur je Tool**namen** einstellen. Über das Gateway läuft aber **jeder** Upstream-Aufruf
unter demselben Namen `invoke_tool` — der Client sieht nicht, was dahintersteckt. Wer für
`invoke_tool` nachfragen lässt, wird also auch bei `list_servers` gefragt; wer es abschaltet, bei
`execute_command` nicht mehr. Der Client kann die Grenze zwischen harmlos und gefährlich technisch
nicht ziehen, weil das Gateway sie ihm nicht zeigt.

**Kernfrage:** Wie kann ein Client seine Rückfrage genau auf die gefährlichen Werkzeuge legen, ohne
dass das Gateway sein Wissen darüber verliert, welche das sind?

## Anforderungen

### Funktional

- Ein Client muss gefährliche von harmlosen Gateway-Aufrufen an einer Eigenschaft unterscheiden
  können, die seine Berechtigungsregeln erreichen — praktisch: am Tool-Namen.
- Das Wissen, welches Werkzeug gefährlich ist, muss im Gateway bleiben, auch wenn die Warteschlange
  für dieses Werkzeug nicht mehr benutzt wird.
- Der bisherige Weg (Warteschlange, ADR-0012) muss unverändert weiter funktionieren und
  Vorgabewert bleiben.
- Ein Bestandssystem darf beim Upgrade nicht stillschweigend schwächer werden.

### Nicht-Funktional

- **Kein Weg an der Freigabepflicht vorbei.** Ein neuer Aufrufweg, über den ein Agent die
  Warteschlange umgehen könnte, wäre schlimmer als die Reibung, die er behebt.
- **Keine Selbstfreigabe.** Der Agent darf die Entscheidung des Menschen weder treffen noch
  behaupten.
- **Keine Gewöhnung.** Ein Dialog, der auch bei Belanglosem erscheint, wird reflexhaft bestätigt und
  schützt dann nicht mehr. Die Ermüdung ist hier der eigentliche Angriff.
- Das Protokoll bleibt in jedem Fall vollständig — unabhängig davon, wer aufhält.

## Betrachtete Optionen

### Option 0: Alles lassen, wie es ist

Die Warteschlange bleibt der einzige Weg; die Reibung wird in Kauf genommen.

**Positiv:**
- Stärkster Schutz, clientunabhängig, zentral protokolliert.
- Null Aufwand, null neues Risiko.

**Negativ:**
- Die Reibung ist real und wurde vom Product Owner ausdrücklich als untragbar bezeichnet.
- Genau diese Reibung führt erfahrungsgemäß dazu, dass der Schutz irgendwann ganz abgeschaltet wird
  — dann schützt er gar nicht mehr.

### Option 1: Zeitfenster, sudo-Modell

Eine Freigabe gilt für ein Werkzeug, eine Identität und eine begrenzte Zeit (z. B. 30 Minuten).

**Positiv:**
- Aus einem Dutzend Klicks pro Deploy wird einer pro Arbeitssitzung.
- Der Schutz bleibt im Gateway und gilt für jeden Client.
- Vollständig im Protokoll, jederzeit widerrufbar.

**Negativ:**
- Innerhalb des Fensters ist jeder weitere Aufruf ungeprüft — auch einer, den niemand erwartet hat.
- Der Wechsel in die Oberfläche bleibt, nur seltener.
- Mittlerer Ausbau: Ablaufzeitpunkt, Restlaufzeit-Anzeige, Widerruf.

### Option 2: Regeln nach Argumenten

Harmlose Aufrufe laufen durch, gefährliche fragen — `uptime` ja, alles mit `rm`/`dd`/`>` nein.

**Positiv:**
- Der größte Gewinn im Alltag: Lesende Arbeit würde vollständig reibungsfrei.
- Die Grenze läge dort, wo die Gefahr tatsächlich ist.

**Negativ:**
- Kommandozeilen sicher zu klassifizieren ist ein bekanntes Minenfeld (`uptime; rm -rf /`).
  Eine Erlaubnisliste muss verankert sein und Metazeichen hart ablehnen.
- Ein Fehler in der Erkennung ist ein stiller Durchlass, kein sichtbarer Fehler.

### Option 3: Schutz zum Client verlagern, mit eigener Tür

Das Gateway führt markierte Werkzeuge sofort aus, verlangt aber einen anderen Aufrufweg —
`invoke_sensitive_tool` statt `invoke_tool` —, damit die Berechtigungsregel des Clients genau auf
den gefährlichen Werkzeugen liegen kann.

**Positiv:**
- Der Dialog erscheint dort, wo der Mensch ohnehin arbeitet, ohne Wechsel in eine Oberfläche.
- Die Grenze des Clients fällt mit der Grenze der Gefahr zusammen, statt mit dem Zufall des
  Meta-Tool-Namens.
- Das Gateway behält sein Wissen über die Schärfe und protokolliert unverändert alles.
- Klein zu bauen, ohne neue Zustandshaltung.

**Negativ:**
- **Das Gateway hält nichts mehr auf.** Der Schutz hängt daran, dass der Client tatsächlich fragt;
  ein Client, der nicht fragt, kommt ungebremst durch.
- Die Konfiguration lebt damit auf dem Rechner des Nutzers, nicht zentral — ein zweiter Client oder
  ein Hintergrund-Agent ist ungeschützt.
- Wirksam nur, solange die Markierung gepflegt wird.

## Vorschlag des Autors

Der Vorschlag war Option 1, weil sie den Schutz dort lässt, wo er clientunabhängig wirkt, und die
Reibung trotzdem um mehr als eine Größenordnung senkt.

Der Product Owner hat sich für Option 3 entschieden, mit dem ausdrücklichen Hinweis, dass ein
Wechsel in Oberfläche oder CLI in keiner Häufigkeit akzeptabel ist. Das ist eine legitime Abwägung:
Der eigentliche Angriffsfall auf dieser Instanz ist ein Agent, der sich irrt, und dagegen hilft eine
Rückfrage im Client genauso wie eine im Gateway — solange sie erscheint.

Unabhängig von der Wahl zwischen 1 und 3 ist der Namensvorschlag `invoke_sensitive_tool` die
Behebung eines eigenständigen Entwurfsfehlers und deshalb in jedem Fall richtig: Solange alle
Aufrufe denselben Namen tragen, kann kein Client vernünftig konfiguriert werden.

## Entscheidung

**Gewählte Option:** "Schutz zum Client verlagern, mit eigener Tür" — mit einer Trennung, die die
Entscheidung erst tragfähig macht.

Der bisherige An/Aus-Schalter bedeutete zweierlei zugleich: „gefährlich" **und** „über die
Warteschlange". Wer die Warteschlange abschaltete, löschte damit auch das Wissen, welches Werkzeug
überhaupt gefährlich ist — und genau dieses Wissen braucht die neue Tür. Die Markierung („ist
scharf") und der Durchsetzungsweg (`Queue` oder `Client`) sind deshalb ab sofort zwei getrennte
Angaben.

Die Wahl je Werkzeug bleibt beim Menschen; `Queue` ist und bleibt der Vorgabewert.

## Konsequenzen

### Positiv

- Ein Client kann seine Rückfrage auf genau die gefährlichen Werkzeuge legen. Aus „alle oder keins"
  wird eine sinnvolle Grenze.
- Das Gateway behält die Klassifikation, auch wenn es einen Aufruf nicht mehr aufhält — die
  Markierung ist jederzeit auf `Queue` zurückzudrehen, ohne sie neu zu erfassen.
- Beide Türen sind **in beide Richtungen** gesperrt: Scharfes kommt nicht durch die harmlose Tür,
  Harmloses nicht durch die scharfe. Letzteres verhindert, dass ein Agent den Menschen an das
  Wegklicken gewöhnt.
- Bestandssysteme werden beim Upgrade nicht schwächer: Die Migration setzt für jede vorhandene
  Zeile ausdrücklich `Queue`, und ein unbekannter Wert in der Spalte fällt ebenfalls auf `Queue`.

### Negativ

- **Im Modus `Client` hält das Gateway einen Aufruf nicht mehr auf.** Das ist kein Nebeneffekt,
  sondern der Kern der Entscheidung — und der Preis dafür.
- Der Schutz ist dann nur so gut wie die Konfiguration des jeweiligen Clients. Ein zweiter Client,
  ein Hintergrund-Agent oder ein Rechner ohne die passende Regel ist ungebremst.
- Der Agent muss zwei Aufrufwege kennen. Die Fehlermeldung nennt jeweils den richtigen, aber es ist
  ein Umweg mehr als vorher.
- Die Markierung wird zur Sicherheitsgrenze: Ein Werkzeug, das niemand markiert, läuft durch die
  harmlose Tür — und der Client fragt nicht.

### Folge-Entscheidungen

- Ob `Client` auf dieser Instanz für `whiskers__execute_command` und die elf weiteren Werkzeuge
  gesetzt wird, ist eine Betriebsentscheidung und **nicht** Teil dieses ADRs. Sie darf erst fallen,
  nachdem nachgewiesen ist, dass der Client tatsächlich fragt — sonst entsteht zwischen Umschalten
  und Nachweis ein Zeitraum ganz ohne Schutz, den beide Seiten stillschweigend hinnehmen.
- Ob zusätzlich ein Zeitfenster (Option 1) für den `Queue`-Modus gebaut wird, bleibt offen.
- Ob die Markierung aus dem Risikograd eines Werkzeugs vorbelegt wird, statt sie einzeln zu setzen,
  ist ungeklärt.

### Review

**Reality-Check geplant für:** 2026-09-10 — insbesondere die Frage, ob die Markierung im Alltag
gepflegt wird. Eine Sicherheitsgrenze, die von einer Liste abhängt, die niemand aktualisiert, ist
nach einem halben Jahr keine mehr.

## Weitere Informationen

### Scope

Gilt für den MCP-Pfad des Gateways. Die REST-Fassade kennt keine Meta-Tools und ist nicht betroffen;
für sie bleibt die Freigabepflicht unverändert an `RequiresApproval` gebunden.

Schärfe hat zwei Quellen und behält sie: die Politik, die ein Mensch pflegt, und die Selbstauskunft
eines Connector-Pakets (`ToolDescriptor.RequiresApproval`). Beide zählen für die Wahl der Tür.

### Referenzen

- [ADR-0012](0012-approval-flows-asynchron.md) — Freigabe, Fingerprint, einmalige Verwendung.
- `anthropics/claude-code#56243` — Elicitation wird in der Desktop-Oberfläche synthetisch
  abgebrochen, in der CLI nicht.
