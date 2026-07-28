# Betrieb — MCP-MCP Gateway

Praxisleitfaden zum Deployment und Betrieb. Zielgruppe: Self-hosted Single-Operator (ADR-0001).

## Schnellstart (Docker)

```bash
docker compose up -d          # SQLite-Default, ein Volume
docker compose logs mcpmcp    # Bootstrap-Zugangsdaten NUR beim Erststart ablesen
```

Beim **Erststart** legt der Gateway zwei Zugänge an und loggt sie **genau einmal** (Henne-Ei — danach nie wieder):

```
ERSTSTART: Bootstrap-Admin (Agent) angelegt. API-Key (wird NIE wieder angezeigt): mcpk_...
ERSTSTART: UI-Admin 'admin' angelegt. Passwort (wird NIE wieder angezeigt): ...
```

- **API-Key** → für Agenten (Claude Code, MCP Inspector) und die REST-Fassade.
- **UI-Passwort** → Login der Web-UI (`http://localhost:8080`, Benutzer `admin`).

Beide Werte sofort sichern. Verloren? Siehe [Zugang zurücksetzen](#zugang-zurücksetzen).

## Konfiguration (Env-Vars)

| Variable | Default | Zweck |
|---|---|---|
| `MCPMCP_DATA_DIR` | `data` (bzw. `/data` im Container) | Verzeichnis für SQLite-DB **und** DataProtection-Key-Ring |
| `MCPMCP_DB_PROVIDER` | `sqlite` | `sqlite` oder `postgres` |
| `MCPMCP_DB_CONNECTION` | `Data Source=<datadir>/mcpmcp.db` | Connection-String (bei Postgres Pflicht) |
| `MCPMCP_AUDIT_MODE` | `best-effort` | `best-effort` verwirft bei Überlast gezählt; `compliance` meldet Überlast explizit und retryt DB-Fehler mit Backpressure |
| `ASPNETCORE_URLS` | `http://+:8080` (Container) | Bind-Adresse/Port |
| `MCPMCP_KEYRING_CERT_PATH` | *(nicht gesetzt)* | PFX-Zertifikat zum Verschlüsseln des Key-Rings (siehe [Key-Ring schützen](#key-ring-schützen)) |
| `MCPMCP_KEYRING_CERT_PASSWORD` | *(nicht gesetzt)* | Passwort des PFX |
| `MCPMCP_OAUTH_ISSUER` | *(nicht gesetzt)* | Authorization Server, dem für **eingehende** Agenten-Token vertraut wird. Gesetzt = der Gateway ist zusätzlich OAuth-Resource-Server (siehe [Agenten über OAuth](#agenten-über-oauth)) |
| `MCPMCP_OAUTH_AUDIENCE` | `MCPMCP_PUBLIC_BASE_URL` | Kanonische Adresse dieses Gateways; ein Token muss darauf lauten |
| `MCPMCP_WASI_HOST` | *(nicht gesetzt)* | Pfad zum WASI-Host-Binary. **Pflicht für die Installation von Connector-Paketen** — ohne ihn lässt sich ein Paket nicht proben, und ungeprobt wird nichts aktiv |
| `MCPMCP_PUBLIC_BASE_URL` | *(nicht gesetzt)* | Öffentliche Adresse des Gateways; nötig für die Redirect-URI der Upstream-Autorisierung (siehe [OAuth gegen Upstreams](#oauth-gegen-upstreams)) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(nicht gesetzt)* | Ziel für Metriken **und** Traces (siehe [Metriken und Traces](#metriken-und-traces)) |
| `MCPMCP_AUDIT_DEBUG_PAYLOADS` | *(aus)* | `1`/`true` schaltet den Debug-Modus des Audits ein (siehe [Audit-Debug-Modus](#audit-debug-modus)) |
| `MCPMCP_AUDIT_RETENTION_DAYS` | `30` | Aufbewahrung der Audit-Ereignisse in Tagen; ältere werden täglich gelöscht (FR-25) |
| `MCPMCP_MAX_RESULT_CHARS` | *(aus)* | Kürzt Tool-Ergebnisse oberhalb dieser Zeichenzahl (FR-16, siehe [Ergebnis-Kompression](#ergebnis-kompression)) |
| `MCPMCP_GUARD_ENABLED` | `1` | `0`/`false` schaltet die Secret-Guardrail global ab (Not-Aus) |
| `MCPMCP_GUARD_MAX_SCAN_CHARS` | `262144` | Nutzlasten darüber werden nicht geprüft und **abgewiesen** |
| `MCPMCP_GUARD_ALLOW_CUSTOM_PATTERNS` | *(aus)* | Erlaubt Admins eigene Regex in der UI (siehe [Guardrails](#guardrails)) |

## Guardrails

Der Gateway prüft Tool-**Argumente** und Tool-**Ergebnisse** auf Zugangsdaten
([ADR-0011](adr/0011-secret-erkennung-als-guardrail.md)). Verwaltung unter **Guardrails** in der
Web-UI: Regeln lassen sich pro Stück ein- und ausschalten, zwischen *Blockieren* und *Beobachten*
umstellen und ergänzen — alles zur Laufzeit, ohne Neustart.

Die wichtigere Richtung ist **Ergebnis → Agent**: Ein Tool, das eine `.env`, ein Kubernetes-Secret
oder eine Datenbankzeile liefert, schiebt den Wert sonst ins Kontextfenster des Modells — und von
dort in dessen Logs und Folgeantworten.

### Was beim Blockieren passiert

| Richtung | Verhalten |
|---|---|
| Argumente | Der Aufruf wird **vor** dem Upstream abgebrochen. Kein Seiteneffekt. |
| Ergebnis | Der Aufruf **ist bereits gelaufen**; nur das Ergebnis wird zurückgehalten. |

Der zweite Fall ist der wichtige: Bei einem schreibenden Tool ist die Aktion eingetreten. Die
Fehlermeldung sagt das ausdrücklich und weist darauf hin, den Aufruf **nicht** zu wiederholen —
sonst legt ein Agent dasselbe Issue ein zweites Mal an. Im Audit trägt der Vorgang den eigenen
Status `GuardBlocked` und ist damit von einem RBAC-`Denied` unterscheidbar.

### Grenzen — bitte lesen, bevor man sich darauf verlässt

Erkannt wird, was ein **Muster** hat: `AKIA…`, `ghp_…`, `sk-ant-…`, PEM-Blöcke, Slack-Webhooks.
**Nicht** erkannt wird, was keins hat — ein 32-stelliges Zufallspasswort ist von einer Datei-Id
nicht zu unterscheiden. Entropie-Heuristik ist bewusst nicht eingebaut: Sie schlägt auf
Git-Commit-SHAs und UUIDs praktisch zu 100 % an, und unter „blockieren" wäre jeder Fehlalarm ein
abgebrochener Arbeitsschritt statt einer Logzeile.

Die Guardrail ist damit eine **zusätzliche Schicht**, kein Ersatz dafür, Zugangsdaten aus
Tool-Ergebnissen herauszuhalten.

Zwei weitere Punkte:

- **Befunde enthalten nie den gefundenen Wert.** Protokolliert werden Regel-Id, Richtung, Modus und
  Fingerabdruck (Hash). Position und Länge stehen im Befund selbst, gehen aber nicht ins Log —
  beides zusammen grenzt eine Zeichenkette weiter ein, als für das Wiedererkennen nötig ist. Eine Secret-Erkennung, die ihre Funde im Klartext loggt, kopiert
  Secrets in ein zweites und meist schwächer geschütztes System.
- **Über der Prüfgrenze wird abgewiesen**, nicht durchgelassen — sonst wäre die Grenze genau der
  blinde Fleck, den man ansteuert. Wer große Ergebnisse erwartet, kombiniert das mit
  `MCPMCP_MAX_RESULT_CHARS`: Die Kürzung greift vorher, und das gekürzte Ergebnis läuft durch.

### Eigene Regeln

Der **geführte Editor** ist der Normalfall: Präfix, Zeichenart und Längenbereich als Felder,
daraus wird das Muster erzeugt. Das deckt praktisch alle Token-Formate ab.

Freitext-Regex ist standardmäßig **aus** und über `MCPMCP_GUARD_ALLOW_CUSTOM_PATTERNS=1`
einschaltbar. Das ist eine bewusste Vertrauensentscheidung: .NET bietet laut Microsoft keine
Sicherheitsgrenze gegen bösartige Muster — auch die hier verwendete backtracking-freie Engine
schützt gegen teure *Eingaben*, nicht gegen bösartige *Muster*. Wer den Schalter setzt, erlaubt
Admins, Rechenzeit im Gateway-Prozess zu verbrauchen.

Neue eigene Regeln starten immer im Modus **Beobachten**. Erst nach Sichtung der Treffer auf
*Blockieren* stellen — eine Regel scharfzuschalten, die man nie hat feuern sehen, bricht im
Zweifel produktive Arbeit ab.

## Freigabe-Flows (Approval)

Einzelne Tools lassen sich freigabepflichtig machen (FR-32,
[ADR-0012](adr/0012-approval-flows-asynchron.md)): Ein solcher Aufruf wird **nicht** ausgeführt,
sondern **sofort abgewiesen** (`ApprovalRequired`), und eine Anfrage landet in der Queue unter
**Freigaben** in der Web-UI. Ein Mensch (Operator/Admin) sieht dort die konkreten — maskierten —
Argumente und entscheidet.

Nach der Freigabe setzt der Agent **denselben** Aufruf erneut ab; er läuft dann **einmalig** durch.
Die Freigabe bindet an `(Identität, Tool, Argument-Fingerprint)` und verfällt nach einer Stunde:

- Kein hängender Agent — der Timeout aus FR-09 bleibt unberührt, es wird nichts blockierend gewartet.
- Eine Freigabe für `delete_file{path:/tmp/x}` deckt **nicht** `delete_file{path:/etc/passwd}` ab.
- Einmalig: Eine Wiederholung erfordert erneute Freigabe. So wird eine erteilte Zustimmung nicht
  zum Dauerfreifahrtschein.

Welche Tools freigabepflichtig sind, ist unter **Freigaben** (Admin-Bereich) zur Laufzeit
schaltbar, ohne Neustart.

### Unterbau seit 2026-07-26: Freigaben sind Vorgänge

Am Verhalten ändert sich nichts — an der Speicherung schon. Freigaben liegen seit
[ADR-0019](adr/0019-langlaufende-tasks-und-events.md) in der **Vorgangs-Tabelle** (`Tasks`): Eine
wartende Anfrage ist ein Vorgang im Zustand `input-required`, eine erteilte Freigabe einer im
Zustand `working`, eine abgelehnte ein gescheiterter mit dem Code `approval-denied`. Statt zweier
Warteschlangen gibt es eine.

Beim **ersten Start nach dem Update** werden vorhandene Freigaben einmalig übernommen, bevor
irgendwem eine Liste angezeigt wird — sonst sähe ein Operator eine leere Queue und hielte offene
Anfragen für erledigt. Die Übernahme ist idempotent (die Freigabe-Id wird die Vorgangs-Id), ein
zweiter Start kopiert also nichts doppelt. Eine bereits **verbrauchte** Freigabe kommt als
eingelöst herüber und ist nicht erneut einlösbar.

Die alte Tabelle `ApprovalRequests` bleibt mit ihren Zeilen stehen. Sie zu leeren wäre unumkehrbar,
und der Gewinn wären ein paar Kilobyte.

### Vorgänge ansehen und abbrechen

Die Web-UI zeigt sie unter **Vorgänge** (Operator/Admin). Über REST:

```bash
curl -H "Authorization: Bearer $API_KEY" http://localhost:8080/api/v1/tasks
```

- `GET /api/v1/tasks` — Liste mit Offset-Paginierung (`page`, `pageSize`, Filter `state` und `tool`).
  **Sichtbarkeit folgt der Eigentümerschaft:** Wer keinen Global-Grant hat, sieht ausschließlich
  seine eigenen Vorgänge. Ein fremder Vorgang ist `404`, nicht `403` — sonst liesse sich über den
  Statuscode abfragen, welche Ids existieren.
- `GET /api/v1/tasks/{id}` — einzelner Vorgang.
- `POST /api/v1/tasks/{id}/cancel` — Abbruch. Solange **nichts läuft** (der Vorgang wartet oder ist
  freigegeben, aber noch nicht eingelöst), ist er **endgültig**: Antwort `200`, Zustand `Cancelled`,
  `Cancellation: Confirmed`. Es gibt hier keinen Ausführenden, der noch etwas bestätigen müsste.
  **Eine so abgebrochene Freigabe ist nicht mehr einlösbar** — das ist der eigentliche Zweck.
  Ein bereits **eingelöster** Vorgang antwortet `409`: Der Aufruf ist gelaufen, da ist nichts mehr zu
  stoppen. Ein abgeschlossener Vorgang ebenfalls `409`.

  Für künftige, wirklich laufende Vorgänge bleibt die Unterscheidung aus ADR-0019 bestehen —
  `Requested` bis der Ausführende bestätigt. Einen solchen Ausführenden gibt es heute noch nicht.

Der Zustand wird **geholt, nicht zugestellt** (ADR-0019). Es gibt kein Abo und keine Zusage, dass
eine Benachrichtigung ankommt; wer den Stand braucht, fragt danach.

**Verfall:** Ein Hintergrunddienst setzt überfällige Vorgänge alle fünf Minuten auf `expired`. Das
ist Sichtbarkeit, keine Durchsetzung — eine verstrichene Frist wirkt schon vorher, weil der
Einlöse-Pfad sie selbst prüft. Abgelaufene Vorgänge werden nicht gelöscht: Sie bleiben als
Terminalzustand auditierbar stehen.

## OpenRPC-Dienste als Upstream

Ein JSON-RPC-Dienst mit OpenRPC-Beschreibung wird ein normaler Upstream. Die Beschreibung kommt
entweder aus einem Dokument oder über den Discovery-Aufruf des Dienstes:

```json
"OpenRpc": {
  "Endpoint": "https://dienst.example/rpc",
  "SpecLocation": "https://dienst.example/openrpc.json",
  "AuthKind": "Bearer",
  "Credential": "…"
}
```

Ohne `SpecLocation` versucht der Gateway `rpc.discover` am Endpunkt. Beide Wege laufen durch
dieselbe Prüfung von Ziel, Größe und Schema — Discovery bekommt keinen Vertrauensvorschuss.

**Was beim Import abgewiesen wird**, statt es zu laden oder zu raten:

- **Externe `$ref`.** Ein Verweis nach außen wäre ein zweiter, ungeprüfter Ladevorgang mitten in der
  Schemaverarbeitung. Lokale Verweise werden aufgelöst, mit Tiefen- und Zyklenprüfung.
- **Doppelte Methodennamen.** Beim Aufruf wäre nicht bestimmbar, welche Signatur gilt.
- **Dokumente über 10 MB.**
- **Ziele in privaten, Loopback- oder Link-Local-Netzen** — einschließlich der Adressen hinter einer
  Weiterleitung, die einzeln geprüft werden. Sonst wäre der Gateway ein Werkzeug, um interne Dienste
  zu erreichen (etwa den Cloud-Metadatendienst). Für einen Dienst im eigenen Netz setzt man
  `AllowPrivateTargets: true` — ausdrücklich und pro Upstream.

**Nicht unterstützt in v1:** Batch-Requests und Notifications. Ein Batch bündelt mehrere Aufrufe in
einer Nachricht; jeder müsste einzeln durch RBAC, Guardrail, Approval und Audit, sonst entstünde ein
Weg, an der Governance vorbei mehrere Dinge zu tun. Eine Notification hat definitionsgemäß keine
Antwort und passt nicht auf einen Tool-Call.

Zu den Parametern: `paramStructure: by-name` schickt ein Objekt, `by-position` ein **geordnetes
Array** in der Reihenfolge aus dem Dokument. Der Aufrufer nennt in beiden Fällen die Namen; die
Reihenfolge kommt aus der Beschreibung, nicht aus der Reihenfolge im Aufruf.

## Skills: was der Mensch sieht und was der Agent selbst holt

Zentrale Skills (Assets) gehen auf **zwei** Wegen an die Agenten, und der Unterschied ist wichtig:

| Weg | Wer löst aus | Wo er auftaucht |
|---|---|---|
| MCP-**Prompt** `assets__<name>` und Resource `mcpmcp://assets/<name>` | **der Mensch** | Slash-Menü bzw. Anhang-Menü des Clients |
| Meta-Tools `list_skills` / `read_skill` | **das Modell** | im Tool-Katalog, wie jedes andere Tool |

Der Prompt-Weg allein reicht nicht, wenn ein Agent *selbst* merken soll, dass es für seine Aufgabe
eine hinterlegte Anleitung gibt: Prompts sind in den meisten Clients nutzerinitiiert — die Liste
sieht der Mensch, nicht das Modell. Tools ruft das Modell dagegen von sich aus auf.

**Und die Token-Rechnung, weil sie die naheliegende Sorge ist:** `list_skills` liefert **nur** Namen
und Kurzbeschreibungen, `read_skill` holt den Text. Dasselbe Muster wie `search_tools`/
`describe_tool`, aus demselben Grund. Die beiden Schemas heben die Schätzung des Lazy-Pfads von 700
auf 950 Tokens — einmal je Sitzung, gegenüber den Tausenden, die das Anpinnen aller Tools kostete.
Ein Blick in den Skill-Bestand kostet danach so viel wie eine kurze Liste, nicht wie ein Dokument.

### Struktur, Referenzen und Versionen

Ein Skill ist Text plus ein paar **deklarierte** Angaben:

| Feld | Wofür |
|---|---|
| Beschreibung | eine Zeile; sie entscheidet, ob ein Agent zugreift |
| Wann anwenden | geht mit in `list_skills` — der eigentliche Auslöser |
| Referenzierte Skills | andere Skills, die dieser voraussetzt oder ergänzt |
| Benötigte Tools | namespaced Tool-Namen, die der Skill voraussetzt |

**Warum überhaupt Struktur, wo ein Skill doch Text ist:** Nur was deklariert ist, lässt sich
prüfen. Ein Verweis in der Prosa („Details siehe `codebase-mapper/references/x`") hängt still ins
Leere, sobald jemand umbenennt. Deklariert man ihn, sagt der Gateway, dass er nicht aufgeht — und
bei den vorausgesetzten Tools kann er es gegen den **Katalog** prüfen. Das kann kein Datei-Editor,
weil nur der Gateway weiß, welche Tools angeschlossen sind.

Die Befunde sind **Warnungen, keine Fehler**. Wer Skill A schreibt, der B referenziert, legt B
vielleicht erst danach an; ein hartes Nein erzwänge eine Reihenfolge, die niemand einhält — und die
naheliegende Reaktion wäre, das Feld leer zu lassen. Ein leeres Feld prüft nichts.

**Mehrteilige Skills** bildest du über den Namen ab: `codebase-mapper/SKILL`,
`codebase-mapper/references/format`. Der Einstieg deklariert die Teile als Referenzen, ein Agent
liest zuerst den Einstieg und zieht mit `read_skill` nur das nach, was er braucht. Das ist dieselbe
schrittweise Offenlegung wie bei `search_tools`/`describe_tool`. Für den Slash-Command-Weg sind
flache Namen die sicherere Wahl — wie ein Client mit einem Schrägstrich im Prompt-Namen umgeht, ist
nicht garantiert.

**Versionen** sind append-only. Die Historie zeigt jede Fassung samt ihrer Angaben; Zurückschalten
hängt die alte Fassung als **neue** Version an, statt Geschichte zu überschreiben — dieselbe Regel
wie bei der Server-Konfiguration.

### Skills aus einem Paket

Ein Connector-Paket kann die Skills mitbringen, die erklären, wie man seinen Konnektor benutzt
([ADR-0021](adr/0021-skills-in-paketen.md)). Ein Pakettyp für Skill-Bündel **ohne** Konnektor ist
entschieden, aber noch nicht gebaut. Der Grund ist
nicht Bequemlichkeit: *Benötigte Tools* konnte der Gateway bisher nur **prüfen** und melden, wenn
etwas fehlt. Ein Paket **stellt die Zusage her** — die Tools kommen mit.

Beim Installieren zeigt die Paket-Seite jeden mitgelieferten Text an, und er ist einzeln zu
bestätigen — **auch bei einem offiziellen Herausgeber**. Für einen Zugriff nach außen gibt es eine
Laufzeitgrenze, die ihn durchsetzt; für einen Skill nicht. Er ist Text, der ungefiltert in die
Denkschleife eines Agenten geht, der Tools aufrufen darf. Es gibt keine Sandbox für einen Satz.

Die Zustimmung lautet `skill:<name>@<hash>` und bindet an den **Inhalt**. Ändert ein Update den
Text, verfällt sie und ist neu zu geben — sonst wäre sie eine Zustimmung zu einem Namen, und unter
demselben Namen stünde beim nächsten Mal etwas anderes.

| Was | Verhalten |
|---|---|
| Name | `<paket-id>/<skill>` — ein Paket kann keinen handgeschriebenen Skill überschatten |
| Herkunft | steht an der Version; die Skill-Liste zeigt sie an |
| Update | hängt eine Version an; die vorherige bleibt |
| Eigene Änderung | Wer den Text bearbeitet, verliert die Herkunft. Das nächste Paket-Update **meldet**, dass es eine angepasste Fassung ablöst — sie bleibt in der Historie und lässt sich zurückschalten |
| Paket entfernt | Die mitgelieferten Skills gehen mit — **samt Historie**, und das ist die einzige Stelle, an der Historie verloren geht ([ADR-0021](adr/0021-skills-in-paketen.md), F5). Vorher wird angekündigt, was mitgeht; eine von dir angepasste Fassung wird dabei besonders genannt. Eine einzelne alte Paketversion zu entfernen lässt die Skills stehen — sie gehören zum Paket, nicht zu einer Version |
| Probe gescheitert | Kein Skill wird eingespielt — eine Anweisung aus einem Konnektor, der nie gelaufen ist, wäre trotzdem in Umlauf |

Skill-Namen sind seitdem **eindeutig**. Vorher waren zwei gleichen Namens möglich; ausgeliefert
wurde dann der erstbeste. Beim Aktualisieren einer bestehenden Datenbank scheitert die Migration,
wenn dort schon Dopplungen stehen — dann vorher umbenennen.

**Größe:** Ein Skill darf höchstens 256 KB groß sein — dieselbe Grenze für einen von Hand
angelegten wie für einen aus einem Paket. Der Grund ist die Auslieferung: `read_skill` schickt den
Text vollständig in den Kontext eines Agenten, und ein unbegrenzter Skill hebelt genau das Argument
aus, für das die Meta-Tools existieren. Was länger ist, gehört auf mehrere Skills verteilt, die
sich gegenseitig referenzieren. Die Grenze wirkt beim **Speichern**; ein bereits gespeicherter,
größerer Skill wird weiter vollständig geliefert — ihn stillschweigend abzuschneiden hieße, einem
Agenten eine halbe Anweisung zu geben.

**Frontmatter-Import:** Eine bestehende `SKILL.md` mit YAML-Kopf lässt sich einfügen; der Knopf
*Frontmatter aus Inhalt übernehmen* liest `name`, `description`, `when-to-use`, `references` und
`required-tools` in die Felder und lässt den Rest als Inhalt stehen. Das ist bewusst **kein
YAML-Parser**, sondern ein Leser für genau diese flache Form.

Jeder angeschlossene Agent erfährt beim Verbinden über die **Server-Instruktion**, dass es diesen
Bestand gibt — drei Sätze, ebenfalls einmal je Sitzung. Länger macht sie nicht wirksamer.

> Skills sind für **jede** authentifizierte Identität lesbar (FR-40) — auch über `list_skills`.
> Keine Zugangsdaten in Skills ablegen.

## Agenten über OAuth

Neben API-Keys kann sich ein Agent mit einem Zugriffstoken eines vorhandenen Identitätsanbieters
anmelden. Der Gateway ist dann **Resource Server** im Sinne der MCP-Autorisierung — er stellt selbst
keine Token aus, das bleibt Sache des Authorization Servers.

```bash
MCPMCP_OAUTH_ISSUER=https://login.example.com/realms/mcpmcp
MCPMCP_PUBLIC_BASE_URL=https://gateway.example.com
```

Damit passieren drei Dinge:

- `/.well-known/oauth-protected-resource` liefert die Protected Resource Metadata (RFC 9728) —
  **anonym**, denn sie ist der Weg, auf dem ein Client überhaupt erst erfährt, wo er ein Token
  bekommt.
- Eine `401`-Antwort trägt `WWW-Authenticate: Bearer resource_metadata="…"` und verweist dorthin.
- Ein mitgeschicktes Token wird gegen den JWKS des Issuers geprüft: Signatur, Issuer, Ablauf — und
  **die Audience**. Ein Token, das für einen anderen Dienst ausgestellt wurde, bewirkt hier nichts.
  Ohne diese Prüfung wäre der Gateway die Stelle, an der fremde Token eingelöst werden.

**API-Keys bleiben bestehen.** Sie werden zuerst geprüft, das Token danach; ein Agent, der heute
läuft, läuft ohne Umstellung weiter. Ohne `MCPMCP_OAUTH_ISSUER` ändert sich gar nichts — der
Standard nennt Autorisierung ausdrücklich optional.

> **Wie eine neue Identität entsteht:** Beim ersten gültigen Token eines unbekannten Subjects legt
> der Gateway eine Identität an — **ohne jede Rolle**. Sie kann damit nichts, bis ein Administrator
> ihr eine gibt (Default-Deny). Das ist Absicht: Die Alternative wäre, unbekannte Subjects
> abzuweisen, und dann sähe nie jemand, wer angeklopft hat. Der Name trägt den Issuer mit
> (`oauth:<issuer>#<sub>`), damit zwei Authorization Server mit gleichem `sub` nicht auf dieselbe
> Identität fallen.

## OAuth gegen Upstreams

Ein HTTP-Upstream, der OAuth verlangt, war bisher nicht anbindbar — statische Header reichen dafür
nicht. Mit `Http.OAuth` holt sich der Gateway ein Token beim Authorization Server des Upstreams:

```json
"Http": {
  "Endpoint": "https://upstream.example.com/mcp",
  "OAuth": { "ClientId": "mcpmcp-gateway", "ClientSecret": "…" }
}
```

**Voraussetzung:** Der Gateway ist als Client beim Authorization Server registriert, mit der
Redirect-URI `<MCPMCP_PUBLIC_BASE_URL>/oauth/upstream/callback`. Dynamic Client Registration ist im
Standard abgelöst, und Client-ID-Metadata-Documents verlangen ein öffentlich abrufbares Dokument —
ein selbst gehosteter Gateway steht oft nicht im Netz. Vorregistrierung ist deshalb der Weg, der
ohne öffentliche Erreichbarkeit funktioniert.

**Verbinden** ist ein einmaliger Schritt eines Administrators: In der Server-Verwaltung *Verbinden*
klicken, beim Authorization Server zustimmen, fertig. Danach erneuert der Gateway das Token selbst,
mit zwei Minuten Sicherheitsabstand vor dem Ablauf.

Was dabei passiert, in der Reihenfolge des Standards: unautorisiert anfragen → `WWW-Authenticate`
lesen → Protected Resource Metadata (RFC 9728) holen → Authorization Server bestimmen → dessen
Metadaten holen → Authorization Code mit **PKCE S256** und dem `resource`-Parameter (RFC 8707), der
das Token an genau diesen Upstream bindet.

**Wo abgebrochen wird, statt es irgendwie hinzubiegen:**

- Der Authorization Server weist **S256 nicht aus** → keine Autorisierung. Ohne PKCE ist der Code
  abfangbar, und ein stiller Verzicht fiele niemandem auf.
- Die Metadaten nennen einen **anderen Issuer** als den angefragten → Abbruch. Sonst liefe die
  spätere Prüfung gegen einen Wert, den die Gegenseite vorgegeben hat.
- Der `iss`-Parameter der Antwort **passt nicht** zum notierten Issuer (RFC 9207) → Abbruch, auch
  bei Fehlerantworten. Das ist der Schutz gegen untergeschobene Antworten eines fremden Servers.
- Discovery- oder Token-Endpunkt zeigen **ins interne Netz** → Abbruch. Diese Adressen kommen vom
  Upstream; es ist derselbe SSRF-Weg wie bei importierten Schemabeschreibungen, und es greift
  dieselbe Prüfung. `AllowPrivateTargets` ist die ausdrückliche Ausnahme.
- **Kein HTTPS** → Abbruch. Ein Token über Klartext ist keins.
- Die **Erneuerung scheitert** → der Upstream kommt nicht hoch, statt es mit dem alten Token zu
  versuchen. Ein 401 im Betrieb wäre schwerer zu deuten als eine klare Meldung beim Start.

Token liegen DataProtection-verschlüsselt in einer eigenen Tabelle, getrennt von der
Konfigurationshistorie — ein Token erneuert sich laufend, und jede Erneuerung als
Konfigurationsversion zu führen wäre Unsinn.

> **Was das nicht ist:** Alle Agenten teilen sich weiterhin die Identität, unter der der Gateway
> beim Upstream angemeldet ist. Ein Token **je Nutzer** würde jedem Agenten einen zugeordneten
> Menschen abverlangen, der selbst zustimmt — das ändert das Modell und ist bewusst nicht Teil
> dieses Schritts.

## Geänderte Tool-Definitionen (Rug-Pull-Schutz)

Ein Upstream, dem einmal vertraut wurde, kann später still die **Beschreibung** eines Tools ändern.
Die Beschreibung landet unverändert im Kontext des Modells — sie ist damit der bequemste Weg,
Anweisungen einzuschleusen, ohne die Konfiguration anzufassen. Kein MCP-Standard verlangt Integrität
von Tool-Definitionen; das OWASP-Cheat-Sheet fordert sie ausdrücklich, und CVE-2025-54136 zeigt den
Fall in freier Wildbahn.

Der Gateway hält deshalb bei jeder Discovery einen Fingerabdruck über **Name, Beschreibung und
Eingabeschema** fest und vergleicht ihn:

| Fall | Verhalten |
|---|---|
| Erstsichtung | Wird übernommen (Trust-on-first-use) und ist ab dann der Bezugspunkt. |
| Unverändert | Nichts passiert. |
| **Abweichend** | Das Tool wird **zurückgehalten** — nicht sichtbar, nicht aufrufbar — und die Abweichung landet im Audit. |
| Zurück zum angenommenen Stand | Die Abweichung gilt als erledigt. |

**Zurückgehalten wird nur das geänderte Tool, nicht der ganze Server.** Ein Rug Pull zielt auf ein
Tool; den Upstream komplett abzuschalten wäre bei jedem normalen Update Kollateralschaden — und ein
Schutz, der bei jedem Update den Betrieb anhält, wird abgeschaltet.

Die neue Fassung nimmt ein Administrator an, in der UI unter *Server* (das zurückgehaltene Tool
steht dort mit Knopf) oder über die API:

```bash
curl -X POST http://localhost:8080/api/v1/tool-definitions/<serverId>/<tool>/accept \
  -H "Authorization: Bearer $KEY"
```

Danach wird der Katalog des Upstreams neu abgefragt, und das Tool ist mit der neuen Fassung wieder
da — ohne Neustart.

> **Was das nicht leistet:** Trust-on-first-use schützt gegen Änderungen **nach** der Aufnahme, nicht
> gegen einen von Anfang an bösartigen Upstream. Dafür sind Herausgeber-Signaturen zuständig (siehe
> Connector-Pakete unten) und die Entscheidung, wen man überhaupt anschließt.

## Connector-Pakete installieren

Ein Connector kann als signiertes Paket kommen statt als Pfad in der Konfiguration
([ADR-0016](adr/0016-versionierter-connector-plugin-vertrag.md)). Ein `.mcpkg` ist ein ZIP mit
`manifest.json`, dessen Ed25519-Signatur `manifest.sig` und den Nutzdateien; das Manifest nennt den
SHA-256 jeder Datei.

**Voraussetzung ist ein gepinnter Herausgeber.** Ohne ihn wird nichts installiert — ein leerer
Trust-Store heißt fail-closed, nicht „keine Einschränkung":

```bash
curl -X POST http://localhost:8080/api/v1/publishers -H "Authorization: Bearer $KEY" \
  -H 'Content-Type: application/json' \
  -d '{"publicKey":"<base64-32-byte>","label":"Beispiel GmbH"}'
```

Die **Vertrauensstufe** entscheidet, wie viel ein Paket dieses Herausgebers ohne Rückfrage bekommt.
Vorgabe ist `ThirdParty`; ein gepinnter Schlüssel heißt „dieser Herausgeber ist echt", nicht
„dieser Herausgeber darf ins Dateisystem":

| Stufe | Was ohne Rückfrage gilt |
|---|---|
| `Core` | Mit dem Produkt ausgeliefert. **Nicht installierbar** — ein Paket kann das nicht sein. |
| `Official` | Die Zugriffe aus dem signierten Manifest werden erteilt. |
| `ThirdParty` | Jeder Zugriff nach außen ist beim Installieren einzeln zu bestätigen. |
| `Community` | Wie `ThirdParty`, zusätzlich muss das Paket selbst freigegeben werden. |

```bash
curl -X PUT http://localhost:8080/api/v1/publishers/<keyId>/trust-level \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' -d '{"level":"Official"}'
```

Installiert wird über die UI (*Connector-Pakete*) oder die API:

```bash
curl -X POST http://localhost:8080/api/v1/packages?grant=env:TOKEN \
  -H "Authorization: Bearer $KEY" --data-binary @connector.mcpkg
```

**Was beim Installieren passiert**, in dieser Reihenfolge: Signatur gegen die gepinnten Herausgeber
prüfen → Manifest lesen → Hash jeder Datei vergleichen → in **Quarantäne** auspacken → den Connector
dort **wirklich starten** und seinen Katalog abfragen → erst dann atomar aktivieren. Ein Paket, das
die Probe nicht besteht, hat nie in Betrieb gestanden; der Fehlschlag bleibt mit Begründung sichtbar.

Die vorherige Version bleibt liegen. Ein Rollback ist deshalb ein Schalter und kein neuer Download:

```bash
curl -X POST http://localhost:8080/api/v1/packages/com.example.connector/rollback \
  -H "Authorization: Bearer $KEY"
```

Ein Upstream verweist über `Wasi.PackageId` auf das Paket statt auf Dateipfade — ein Update
wechselt damit die Dateien, ohne dass jemand die Konfiguration anfasst. Gibt es keine aktive
Version, kommt der Upstream **nicht** hoch; ein Rückfall auf alte Pfade wäre eine stille Abweichung
von dem, was konfiguriert ist.

> **Grenzen heute:** Nur WASI-Connectoren sind paketierbar, und Pakete werden hochgeladen, nicht
> aus einem Verzeichnisdienst geholt.

## Ziele im internen Netz (OpenAPI und OpenRPC)

Beide Konnektoren rufen Adressen ab, die ein Administrator konfiguriert hat. Ohne Prüfung wäre der
Gateway damit ein Werkzeug, um **interne** Dienste zu erreichen — den Cloud-Metadatendienst auf
`169.254.169.254`, einen Admin-Port auf `127.0.0.1`, einen Nachbarn im Firmennetz. Geprüft wird
deshalb beides: die **Quelle der Beschreibung** und die **Ziel-API**. Der Hostname wird aufgelöst
und *alle* seine Adressen geprüft; Weiterleitungen beim Laden werden einzeln erneut geprüft.

Im **Aufrufpfad** folgt kein Konnektor einer Weiterleitung. Ein `302` der Gegenstelle zeigte sonst
auf eine Adresse, die nie geprüft wurde; stattdessen kommt der Aufruf mit einem Fehler zurück, der
das Ziel nennt. Ist die Weiterleitung beabsichtigt, gehört die Zieladresse in die Konfiguration.

Für einen Dienst im eigenen Netz — in Entwicklungsaufbauten der Normalfall — setzt man den Schalter
ausdrücklich und pro Upstream:

```json
"OpenApi": {
  "SpecLocation": "http://localhost:8080/openapi.json",
  "AllowPrivateTargets": true
}
```

In der UI ist es das Häkchen „Ziele im internen Netz erlauben" im OpenAPI-Formular.

> **Umstellung:** Vorgabe ist `false`. Bestehende OpenAPI-Upstreams, die auf `localhost` oder ein
> privates Netz zeigen, kommen nach dem Update nicht mehr hoch, bis der Schalter gesetzt ist. Die
> Fehlermeldung nennt die Adresse und den Schalter beim Namen — der Upstream steht auf `Failed`,
> nichts läuft still weiter.

## CLI-Programme im Container ausführen

Ein CLI-Upstream läuft standardmäßig als Host-Prozess: gehärtet (absolute Pfade, Root-Allowlist,
minimale Umgebung, Prozessbaum-Kill), aber **keine Sandbox**. Für Programme, denen man nicht
vertraut, gibt es seit [ADR-0018](adr/0018-native-prozess-und-container-isolation.md) den
Container-Modus:

```json
"Cli": {
  "Executable": "/usr/bin/werkzeug",
  "Isolation": { "Mode": "Container", "Image": "meine-registry/werkzeug@sha256:…" },
  "AllowedReadRoots": ["/daten/ein"],
  "AllowedWriteRoots": ["/daten/aus"]
}
```

Was der Container mitbringt, ohne dass man es einstellen muss: read-only Wurzeldateisystem, fester
Nicht-root-Benutzer, alle Linux-Capabilities entfernt, `no-new-privileges`, CPU-/RAM-/PID-Grenzen,
ein beschreibbares `/tmp` als tmpfs, **kein Netzwerk**, und ein Container je Aufruf, der danach
verschwindet.

Wichtig für die Konfiguration:

- **`Executable` liegt im Image**, nicht auf dem Host. Ein `ExecutableSha256` wird abgelehnt — er
  prüfte eine Datei auf diesem Rechner. Der passende Pin ist ein **Image-Digest** im Image-Namen
  (`@sha256:…`), so wie oben.
- **Mounts kommen aus `AllowedReadRoots`/`AllowedWriteRoots`** — denselben Listen, die der
  Host-Modus schon durchsetzt. Was nicht darin steht, sieht das Programm nicht.
- **Secrets** aus `EnvironmentVariables` erreichen das Programm über die Umgebung. Sie stehen nie in
  der Kommandozeile des Container-Prozesses, wo jeder sie über die Prozessliste läse.
- **Netzwerk ist aus und bleibt es vorerst.** Eine `NetworkAllow`-Liste wird abgelehnt statt als
  offenes Bridge-Netz durchgereicht — ein offenes Netz mit dem Etikett „Allowlist" wäre schlimmer
  als eine ehrliche Absage.

**Kein stiller Rückfall.** Verlangt eine Konfiguration Container und ist keine passende Runtime da,
kommt der Upstream **nicht** hoch, mit genau dieser Meldung. Ein Ausweichen auf den Host würde die
Isolation abschalten, ohne dass es jemand merkt.

Geprüft wird dabei nicht nur, *ob* die Runtime antwortet, sondern ob sie die Policy durchsetzen
kann: **Docker im Windows-Container-Modus** antwortet bereitwillig und lehnt dann `--read-only`,
`--cap-drop` und `--user` ab. Auf einem Windows-Host braucht der Container-Modus deshalb Docker im
**Linux-Modus** (WSL2-Backend); sonst wird er abgelehnt statt ungeschützt ausgeführt.

Nicht abgedeckt: **stdio-Upstreams**. Deren Vertrag ist eine langlebige Verbindung, kein Job je
Aufruf — dafür braucht es einen eigenen Entwurf.

## WASI-Components als Upstream

Ein signiertes WebAssembly-Component läuft in einem eigenen Rust-Host-Prozess
([ADR-0020](adr/0020-wasi-runtime-out-of-process-rust-host.md)). Der Host **liegt im Image** unter
`/usr/local/bin/mcpmcp-wasi-host` — genau dieser Pfad gehört in `Wasi.HostExecutable` eines
WASI-Upstreams.

Vertrauen kommt **ausschließlich** aus dem Publisher-Trust-Store, nicht aus der Upstream-Config:

```bash
curl -X POST http://localhost:8080/api/v1/publishers \
  -H "Authorization: Bearer $ADMIN_KEY" -H 'Content-Type: application/json' \
  -d '{"publicKey":"<Ed25519-Public-Key, Base64, 32 Byte>","label":"acme"}'
```

- Ohne passenden gepinnten Schlüssel lädt der Host nichts — ein Upstream kommt dann gar nicht erst
  hoch (fail-closed). Beim ersten Start nach dem Update werden Schlüssel aus vorhandenen
  `Wasi.PinnedPublishers` einmalig übernommen und danach ignoriert.
- `POST /api/v1/publishers/{keyId}/revoke` wirkt **sofort**: Laufende Upstreams mit Components
  dieses Publishers werden gestoppt und der Vorgang auditiert.
- Grants sind default-deny und werden pro WASI-Interface durchgesetzt. Dateisystem-Preopens sind
  absolute Pfade und werden **nur lesend** eingehängt, Netzwerkziele sind `host:port`.
- Secrets: `Grants.Secrets` nennt die Namen, `Wasi.Secrets` liefert die Werte (verschlüsselt wie
  alle Upstream-Credentials, in Ausgaben maskiert). Der Host injiziert sie als
  Environment-Einträge — wer Secrets gewährt, gewährt damit die Environment-Schnittstelle, das
  Component kann also alle gesetzten Variablen lesen.
- Jeder Load steht im Audit: Modulhash, Publisher, Runtime und die tatsächlich erteilten Grants.

### Resources: `Wasi.PersistentInstance`

Manche Components geben `resource`-Handles aus — ein Handle ist ein Index in die Guest-Instanz, die
es ausgegeben hat, und überlebt den Aufruf. Damit das funktioniert, muss die Instanz leben bleiben:
`Wasi.PersistentInstance: true`. Ohne das Flag bekommt jeder Aufruf eine frische Instanz, und der
Host weist Resource-Aufrufe mit genau dieser Begründung ab.

Über die Leitung ist ein Handle ein undurchsichtiges Objekt (`{"handle": "res-1"}`); der Wert
bleibt im Host. Jedes Handle gehört dem Aufrufer, für den es entstanden ist — ein anderer bekommt
„Handle ist unbekannt", ob es den Namen nun gibt oder nicht.

**Die Auflage dazu:** Eine persistente Instanz teilt ihren **internen** Zustand (Globals, linearer
Speicher) zwischen allen Aufrufern desselben Upstreams. Die Handle-Trennung ändert daran nichts —
sie verhindert nur, dass ein Aufrufer ein fremdes Handle benennt. Das Flag gehört deshalb nur an
Upstreams, die Resources wirklich brauchen, und nur an Components, denen man das Zusammenlegen
zutraut. Wer strikte Trennung braucht, legt pro Mandant einen eigenen Upstream an.

Betriebliches: `health` meldet die offenen Handles der Instanz; über 256 lehnt der Host neue ab.
Ein Trap verwirft die Instanz samt Handles, der nächste Aufruf startet frisch. Ein Reload (neues
Component, Hot-Swap) beendet die Instanz ebenfalls — Handles von davor sind danach ungültig.

### Kapazität: bis zu 16 Aufrufe gleichzeitig

Seit Vertrag v4 trägt jede Anfrage eine Korrelations-Id, und Antworten dürfen in anderer
Reihenfolge zurückkommen. Ein langsames Component blockiert damit **nicht mehr** die übrigen
Aufrufe desselben Upstreams; jeder bekommt seine eigene Instanz mit eigenem Store.

Die Grenze liegt bei **16 gleichzeitigen Aufrufen je Host-Prozess**. Darüber antwortet der Host mit
`too-many-calls`, statt weiter Speicher zu binden: `MaxMemoryBytes` gilt pro Aufruf, ohne Grenze
wäre der Speicherbedarf das Produkt aus Limit und Anzahl der Anfragen. Wer dauerhaft mehr braucht,
legt den Upstream mehrfach an — jeder bekommt seinen eigenen Host-Prozess.

**Eine Ausnahme:** Mit `Wasi.PersistentInstance` gibt es genau eine Guest-Instanz, und die kann
nicht zweimal gleichzeitig rechnen. Aufrufe darauf laufen weiterhin nacheinander. Sie sind
trotzdem abbrechbar (siehe unten) — sie blockieren nur einander, nicht den Host.

### Abbruch: ein Aufruf lässt sich stoppen

Bricht der Aufrufer ab (Per-Call-Timeout FR-09, abgebrochener Request), schickt das Gateway ein
`cancel` an den Host. Der trappt den laufenden Guest über die Epoche und antwortet erst, wenn der
Aufruf **wirklich** beendet ist — `confirmed: true` heißt beendet, nicht „Abbruch abgeschickt".
Bleibt die Bestätigung binnen fünf Sekunden aus, meldet der Host `confirmed: false`; dann läuft der
Guest noch, und es greift weiterhin nur sein Fuel- oder Zeitlimit.

Der Unterschied ist im Ergebnis sichtbar: Ein abgebrochener Aufruf endet mit dem Code `cancelled`,
eine abgelaufene Frist mit dem gewohnten Timeout. Wer im Audit nachsieht, muss nicht raten, welches
von beidem passiert ist.

### Bereitschaft, Phasen und geordnetes Beenden

`health` unterscheidet **Leben** von **Bereitschaft**. `status: "ok"` heißt nur, dass der Host
antwortet; `ready: true` heißt, dass er Aufrufe annimmt. Dazwischen liegen echte Zustände, die das
Feld `phase` nennt:

| Phase | Bedeutung |
|---|---|
| `handshake` | Prozess läuft, Version noch nicht verhandelt |
| `negotiated` | verhandelt, aber kein Component geladen — Aufrufe gingen ins Leere |
| `ready` | geladen und annahmebereit |
| `draining` | nimmt keine neuen Aufrufe mehr an; die laufenden dürfen zu Ende kommen |

Dazu meldet `health` die Zahl der gerade laufenden Aufrufe (`inFlight`) und die offenen
Resource-Handles.

**Beim Beenden erst drainieren.** Das Gateway schickt `drain` (Vorgabe 5 s Frist) und danach
`shutdown`. Ein `shutdown` ohne Drain bricht laufende Aufrufe ab — seit Vertrag v4 können mehrere
gleichzeitig unterwegs sein, und der Host beendet sie, statt beliebig lange auf sie zu warten. Die
Antwort auf `drain` sagt, ob es sauber war: `idle: true` heißt, es lief nichts mehr. Ein Aufruf, der
nach dem Drain noch eintrifft, bekommt den Code `draining` — nicht einen allgemeinen Fehler.

**Capability-Flags beim Handshake.** Der Host nennt in der `hello`-Antwort, was er kann
(`cancellation`, `concurrency`, `drain`, `readiness`, `persistentInstances`, `resources`, `secrets`,
`diskCache`) und was nicht (`streams: false`). Fehlt dem Gateway ein Pflichtfeature, kommt der
Upstream gar nicht hoch — mit Nennung des fehlenden Namens, statt beim ersten Aufruf zu scheitern.

### Platten-Cache für Kompilate

Ohne `Wasi.ModuleCacheDirectory` kompiliert **jeder Host-Start** neu — gemessen rund 2,3 ms je KiB,
bei einem Component von 1–3 MB also 3–7 Sekunden pro Gateway-Neustart oder Hot-Swap. Mit
Verzeichnis (im Image z. B. `/data/wasi-cache`) fällt das auf unter 3 ms:

| | Ladezeit | Kompilierung |
|---|---|---|
| erster Host-Start | 107 ms | 90 ms |
| jeder weitere Start | 0,9–2,6 ms | keine |

Ein Kompilat ist **ausführbarer Maschinencode**, den die Publisher-Signatur nicht abdeckt. Der Host
legt deshalb im Cache-Verzeichnis einen eigenen Schlüssel (`mac.key`, unter Unix `0600`) an und
versieht jeden Eintrag mit einem HMAC darüber; ein Eintrag ohne gültigen MAC wird gelöscht statt
geladen. Daraus folgen zwei Betriebsauflagen:

- Das Verzeichnis **muss** dem Host-Benutzer gehören und darf für andere nicht schreibbar sein.
  Kein geteiltes Volume, kein weltschreibbares Temp-Verzeichnis.
- Der Schlüssel schützt gegen fremden Schreibzugriff und Bitfehler, **nicht** gegen jemanden, der
  als derselbe Benutzer läuft — der liest den Schlüssel und könnte ohnehin das Host-Binary
  austauschen.

**Obergrenzen** halten beides endlich: Im Speicher hält der Host höchstens 8 Kompilate und
verdrängt das am längsten nicht genutzte; auf Platte gilt ein Budget von 256 MiB, über
`Wasi.ModuleCacheMaxBytes` einstellbar (`0` = ausdrücklich unbegrenzt). Verdrängt wird nach
Nutzung, nicht nach Schreibalter — ein Treffer stempelt seinen Eintrag frisch. Ein verdrängtes
Kompilat kostet nur eine erneute Kompilierung. `mac.key` wird beim Aufräumen nie angefasst.

`mac.key` löschen macht alle Einträge ungültig (sie werden verworfen und neu erzeugt) — der Weg,
einen Cache-Verdacht auszuräumen. Im `health`-Signal des Hosts stehen `diskHits` und `diskErrors`;
bleibt `diskHits` bei 0 und `diskErrors` steigt, sind meist die Verzeichnisrechte falsch.

## Webhook-Trigger

Ein eingehender Webhook löst genau **einen** Tool-Aufruf im Namen einer festen Identität aus
(FR-20, [ADR-0013](adr/0013-webhook-trigger.md)). Anlegen unter **Webhooks** (Admin): Name,
Identität und Tool wählen — das Signatur-Secret wird **einmalig** angezeigt.

Der Trigger-Endpunkt ist `POST /webhooks/{id}/trigger` und ist der **einzige unauthentifizierte
Pfad**. Absicherung über HMAC-SHA256:

- Der Absender signiert `{timestamp}.{body}` mit dem Secret und schickt zwei Header:
  `X-McpMcp-Signature: sha256=<hmac>` und `X-McpMcp-Timestamp: <unix-sekunden>`.
- Anfragen älter als **5 Minuten** werden abgewiesen (Replay-Schutz).
- Fehlende, falsche oder abgelaufene Signatur → **401**. Eine unbekannte Webhook-Id liefert
  ebenfalls 401, damit sich keine gültigen Ids durchprobieren lassen.

Der ausgelöste Aufruf durchläuft die **volle Pipeline** — RBAC der gebundenen Identität, Guardrail,
Rate-Limit — und erscheint im Audit mit Herkunft **`Webhook`**. Ein Webhook kann damit nie mehr,
als seine Identität ohnehin darf.

**Grenze:** Ein Webhook löst genau ein Tool aus, keine Kette. Mehrstufige Abläufe sind v2.

## Ergebnis-Kompression

Ein einzelnes umfangreiches Tool-Ergebnis kann die Token-Ersparnis der Profile wieder auffressen.
`MCPMCP_MAX_RESULT_CHARS` begrenzt das:

```
MCPMCP_MAX_RESULT_CHARS=20000
```

Standardmäßig **aus** — Kürzen ist verlustbehaftet, das soll niemand unbemerkt bekommen. Wenn es
greift, bleibt das Ergebnis gültiges JSON und trägt das Feld `_mcpmcp_truncated: true` samt Hinweis,
wie viel fehlt. Bei Listen bleiben die vorderen Einträge erhalten und `totalItems` nennt die
Gesamtzahl; bei einzelnen großen Objekten ist der Ausschnitt ausdrücklich als nicht parsbar
gekennzeichnet. Das Audit hält weiterhin die **ungekürzte** Größe fest, damit man die Kürzung
nachträglich einordnen kann.

## Agenten anbinden (Config-Snippets)

Beim Ausstellen eines API-Keys zeigt die Web-UI unter **RBAC → Keys** fertige
Konfigurations-Snippets für Claude Code und für JSON-basierte MCP-Clients (FR-41) — inklusive
Endpunkt und Authorization-Header. Sie enthalten den Key im Klartext und erscheinen nur einmal,
zusammen mit dem Key selbst.

Läuft die UI hinter einem Reverse-Proxy, prüfe die Adresse im Snippet: sie stammt aus dem
Browser-Aufruf und ist nicht zwingend die, unter der Agenten den Gateway erreichen.

Für Upstream-Server, die noch kein Streamable HTTP sprechen, fällt der Gateway automatisch auf
den abgelösten HTTP+SSE-Transport zurück; abschaltbar je Server über den Schalter im Anlege-Formular
([ADR-0009](adr/0009-sse-legacy-transport.md)). Als **Server** spricht der Gateway ausschließlich
Streamable HTTP — Agenten, die nur SSE können, lassen sich nicht anbinden.

Logs werden außerhalb von `Development` als **JSON** auf stdout geschrieben (NFR-07), damit
Container-Logs ohne Zusatzkonfiguration von einem Aggregator geparst werden können. Lokal bleibt
es beim lesbaren Textformat.

## Audit-Debug-Modus

Standardmäßig schreibt das Audit **keine** Ergebnis-Payloads mit — nur deren Größe in Bytes.
Zur Fehlersuche lässt sich das umschalten:

```
MCPMCP_AUDIT_DEBUG_PAYLOADS=1
```

Dann landet der vollständige Antwort-Payload im Audit-Log, **maskiert** durch dieselbe Redaction
wie die Argumente. Zwei Dinge dazu:

- Der Schalter ist als Debug-Hilfe gedacht, nicht für Dauerbetrieb: Antworten können groß sein und
  die Audit-Tabelle schnell aufblähen. Die Retention greift zwar, aber der Plattenbedarf steigt spürbar.
- Redaction maskiert bekannte Secret-Feldnamen. Trägt ein Upstream Geheimnisse in *unbenannten*
  Strukturen (Freitext, Base64-Blobs), hilft das nicht — in dem Fall den Schalter aus lassen.

Zusätzliche Muster pro Tool lassen sich in der Web-UI unter **Tools → \[Tool wählen\]** als
Admin pflegen; sie gelten zusätzlich zu den globalen Mustern (`password`, `token`, `secret`, `key` …).

## Agent anbinden

```bash
claude mcp add --transport http mcpmcp http://localhost:8080/mcp \
  --header "Authorization: Bearer <API-KEY>"
```

Der Agent sieht dann die Meta-Tools `search_tools` / `describe_tool` / `invoke_tool` (Lazy-Default) plus die im Profil gepinnten Tools. Upstream-Server, Rollen und Profile werden über die Web-UI oder die REST-API verwaltet.

## PostgreSQL statt SQLite

Für größere Setups (viel Audit-Volumen, mehrere Instanzen an einer DB):

```bash
docker compose --profile postgres up -d
# in docker-compose.yml MCPMCP_DB_PROVIDER + MCPMCP_DB_CONNECTION einkommentieren,
# und das Passwort (CHANGE_ME) ersetzen.
```

Das Schema wird beim Start automatisch über EF-Migrationen angelegt (siehe [Schema & Upgrades](#schema--upgrades)).

## Schema & Upgrades

Ab **v1.1** verwaltet der Gateway sein Datenbankschema über EF-Core-Migrationen. Beim Start passiert automatisch genau eine von drei Sachen — das Ergebnis steht im Log (`Datenbank initialisiert (…)`):

| Vorgefunden | Aktion | Log-Ausgabe |
|---|---|---|
| Leere/neue DB | Schema aus Migrationen anlegen | `CreatedFromMigrations` |
| **v1.0-DB** (per `EnsureCreated` erzeugt, ohne Migrationshistorie) | Initial-Migration als Baseline stempeln (**kein DDL, keine Datenänderung**), dann migrieren | `BaselinedLegacySchema` |
| Bereits migrationsverwaltet | ausstehende Migrationen anwenden | `Migrated` |

### Upgrade von v1.0 auf v1.1

Es ist **kein manueller Schritt nötig** — der Gateway erkennt das Alt-Schema selbst und stempelt die Baseline. Trotzdem gilt die übliche Sorgfalt:

1. Dienst stoppen.
2. **Datenverzeichnis sichern** (`mcpmcp.db` **und** `keys/`, siehe [Backup](#backup)).
3. Neue Version starten und im Log `BaselinedLegacySchema` bestätigen.

Beim Rollback auf v1.0 ist die zusätzliche Tabelle `__EFMigrationsHistory` unschädlich — v1.0 ignoriert sie.

Jeder Provider hat eine eigene Migrations-Assembly (`McpMcp.Persistence.Migrations.Sqlite` bzw. `.Postgres`), weil SQLite und PostgreSQL unterschiedliches DDL brauchen. Beide sind im Image enthalten; die Auswahl erfolgt automatisch über `MCPMCP_DB_PROVIDER`.

## TLS / Reverse-Proxy

Der Gateway terminiert selbst kein TLS. **Immer hinter einen Reverse-Proxy** (Caddy, nginx, Traefik) mit TLS setzen — der Gateway hält Upstream-Credentials und API-Keys, ein Klartext-Transport ist inakzeptabel (NFR-04). Beispiel Caddy:

```
gateway.example.com {
    reverse_proxy localhost:8080
}
```

Der Proxy sollte `X-Forwarded-*`-Header setzen; das UI-Cookie ist `SameSite=Strict` + `HttpOnly`.

## Backup

Alles Persistente liegt im Datenverzeichnis (`MCPMCP_DATA_DIR`):

- `mcpmcp.db` — Konfiguration, RBAC, API-Key-Hashes, Audit-Log (bei SQLite).
- `keys/` — **DataProtection-Key-Ring**. Ohne ihn sind die verschlüsselten Upstream-Credentials unbrauchbar.

Beide **zusammen** sichern (Volume-Snapshot bei gestopptem Container oder DB-Dump + `keys/`-Kopie). Bei PostgreSQL: DB separat dumpen, `keys/` weiterhin aus dem Datenvolume sichern.

## Audit-Retention

Das Audit-Log wächst mit jedem Call. Default-Aufbewahrung: 30 Tage, stündlicher Bereinigungs-Job (FR-25). Bei SQLite ist Retention **Betriebspflicht** (ADR-0007) — sehr große Logs (> ~10 GB) sind ein Grund, auf PostgreSQL zu wechseln.

## Metriken und Traces

Der Gateway misst jeden Tool-Call (FR-26) unter dem Meter `McpMcp.Gateway`:

| Instrument | Bedeutung | Dimensionen |
|---|---|---|
| `mcpmcp.tool_calls` | Zähler aller Calls — daraus ergeben sich Calls/s und Fehlerquote | `server`, `tool`, `status`, `origin` |
| `mcpmcp.tool_call_duration` | Latenz-Histogramm (ms) — daraus Perzentile | `server`, `tool`, `status` |

Der Export ist **aus**, solange kein Ziel konfiguriert ist (sonst würde der Exporter dauerhaft ins Leere laufen):

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://collector:4317
```

Exportiert wird per **OTLP** — der OpenTelemetry-Standard. Für **Prometheus** einen OTel-Collector davorschalten, der OTLP annimmt und einen Scrape-Endpoint anbietet; ein direkter Prometheus-Exporter ist im .NET-Ökosystem noch nicht stabil veröffentlicht, deshalb bewusst dieser Weg.

### Traces

Derselbe Schalter aktiviert **Traces** aus der Quelle `McpMcp.Gateway`. Metriken beantworten „wie
viele und wie schnell im Mittel", Traces beantworten „wo ist die Zeit *dieses einen* Aufrufs
geblieben":

| Span | Bedeutung | Tags |
|---|---|---|
| `mcpmcp.tool_call` | Der gesamte Aufruf durch die Pipeline | `mcpmcp.tool`, `mcpmcp.server`, `mcpmcp.status`, `mcpmcp.origin`, `mcpmcp.caller` |
| `mcpmcp.upstream_call` | Nur der Fremdanteil, als Kind-Span | `mcpmcp.server`, `mcpmcp.upstream_tool` |

Die Differenz zwischen beiden ist der **Gateway-Overhead** — genau die Frage, die NFR-01 stellt.
Ohne die Trennung sieht man in einer langsamen Antwort nicht, wer sie verursacht hat.

Ein Aufruf, der nicht mit `Success` endet, wird als Fehler-Span markiert. Ein Deny oder ein
Guardrail-Treffer ist kein Serverfehler, aber auch kein gelungener Aufruf — in einer Fehlersuche
will man ihn sehen.

> **Spans tragen keine Argumente und keine Ergebnisse.** Das Audit-Log ist redigiert, ein
> Telemetrie-Backend ist es nicht — ein Payload im Span wäre der bequemste Weg, die Redaction zu
> umgehen, und zwar an eine Stelle, die oft weniger geschützt ist als die Datenbank. Ein Test hält
> das fest.

`/healthz` und `/readyz` sind vom Tracing ausgenommen; im Sekundentakt laufende Probes würden den
Trace-Strom fluten, ohne etwas über einen Tool-Aufruf zu sagen.

## Health / Readiness

- `GET /healthz` — Prozess lebt (anonym).
- `GET /readyz` — DB erreichbar + Upstream-Zustände (anonym).

Der Container-Healthcheck nutzt `dotnet McpMcp.Server.dll --healthcheck` (self-ping, da das schlanke Runtime-Image kein `curl` enthält). Der Container läuft als non-root `app`-User.

## Key-Ring schützen

Der DataProtection-Key-Ring unter `<datadir>/keys/` entschlüsselt die at-rest verschlüsselten Upstream-Credentials. Ohne Zusatzschutz liegt er im Klartext neben der Datenbank — der Gateway warnt beim Start entsprechend.

Ab v1.1 lässt er sich mit einem X509-Zertifikat verschlüsseln (bewusst zertifikatsbasiert statt Cloud-KMS, damit es self-hosted funktioniert):

```bash
# Zertifikat einmalig erzeugen (Beispiel, OpenSSL):
openssl req -x509 -newkey rsa:2048 -keyout k.pem -out c.pem -days 3650 -nodes -subj "/CN=mcpmcp-keyring"
openssl pkcs12 -export -out keyring.pfx -inkey k.pem -in c.pem -password pass:GEHEIM

# Gateway damit starten:
MCPMCP_KEYRING_CERT_PATH=/secrets/keyring.pfx
MCPMCP_KEYRING_CERT_PASSWORD=GEHEIM
```

Danach enthalten die XML-Dateien im Key-Ring nur noch verschlüsseltes Material. **Das Zertifikat wird zum Entschlüsseln gebraucht** — geht es verloren, sind die gespeicherten Upstream-Credentials unbrauchbar (die Server müssen dann neu konfiguriert werden). Zertifikat also getrennt vom Datenverzeichnis sichern. Beim Zertifikatswechsel bleibt Altmaterial lesbar, solange das alte Zertifikat weiterhin angegeben wird.

## Zugang zurücksetzen

Bootstrap-Zugänge werden nur bei **leerer** DB erzeugt. Für verlorene Zugänge gibt es ab v1.1 zwei Kommandos, die gegen die konfigurierte Datenbank laufen, den Zugang **einmalig** ausgeben und sich beenden, ohne den Gateway zu starten:

```bash
# UI-Passwort zurücksetzen (Default-Benutzer "admin"; Rolle bleibt unverändert,
# ein fehlender Nutzer wird als Admin angelegt):
docker compose run --rm mcpmcp dotnet McpMcp.Server.dll --reset-ui-admin
docker compose run --rm mcpmcp dotnet McpMcp.Server.dll --reset-ui-admin betreiber

# Notfall-API-Key: legt eine NEUE Agenten-Identität mit Global-Grant an
# (bestehende bleiben unangetastet):
docker compose run --rm mcpmcp dotnet McpMcp.Server.dll --issue-bootstrap-key
```

Ohne Container analog mit `dotnet run --project src/McpMcp.Server -- --reset-ui-admin`. Den Notfall-Zugang nach Gebrauch wieder entfernen, falls er nur der Wiederherstellung diente.

- **UI-Passwort vergessen, aber anderer Admin existiert** → einfacher über die UI (Seite „UI-Nutzer") neu setzen.

## Audit-Betriebsmodi

`best-effort` hält Tool-Calls bei Audit-Überlast nicht auf. Drops werden gezählt, in `/readyz`
ausgegeben und beim Shutdown geloggt. Fehlgeschlagene DB-Batches werden als Fehler geloggt und
verworfen.

`compliance` wirft bei vollem Channel einen expliziten `AuditUnavailableException`, markiert
Readiness als nicht bereit und verwirft fehlgeschlagene DB-Batches nicht; der Writer retryt sie.
Das kann Shutdown oder Verarbeitung bei längerem DB-Ausfall bewusst blockieren. Für HA ist vor
Produktionsfreigabe zusätzlich ein durables externes Spool/Queue-Backend erforderlich.

`/readyz` liefert `auditMode`, `auditHealthy` und `auditDropped`. Alarmieren auf
`auditHealthy=false`, `auditDropped>0` und HTTP 503.

## Sicherheit

Vor dem Produktivbetrieb unbedingt [SECURITY.md](../SECURITY.md) und das [Threat-Model](security/threat-model.md) lesen — insbesondere: **nur vertrauenswürdige stdio-Server anschließen** (v1 ohne Sandbox, ADR-0005), Gateway als non-root betreiben (das Container-Image tut das bereits), Netzexposition minimieren.
