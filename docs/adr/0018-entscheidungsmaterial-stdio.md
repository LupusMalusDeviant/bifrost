# Entscheidungsmaterial zu ADR-0018 — Isolation von stdio-Upstreams

- **Zweck:** Grundlage für eine Entscheidung, nicht die Entscheidung selbst. Sie gehört dem Product
  Owner; hier steht, was es kostet und was dabei kaputtgeht.
- **Datum:** 2026-07-28
- **Gehört zu:** [ADR-0018](0018-native-prozess-und-container-isolation.md), dort seit dem
  2026-07-24 als offener Punkt benannt.

## 1. Warum das der letzte große Sicherheitsposten ist

ADR-0018 hat den Containerpfad für **CLI**-Upstreams entschieden und umgesetzt; WASI-Components
laufen in einer echten Sandbox (ADR-0017/0020). **stdio ist der einzige Transport ohne
Isolationspfad** — und zugleich der, mit dem praktisch alle real existierenden MCP-Server laufen:
`npx -y @modelcontextprotocol/server-filesystem`, `uvx mcp-server-git`, und so weiter.

Die unangenehme Zusammenfassung: Die aufwendigste Sicherheitsarbeit dieses Projekts greift beim
häufigsten Fall nicht.

Ein stdio-Upstream läuft heute als Kindprozess des Gateways — **gleicher Benutzer, gleicher
Dateizugriff, gleiches Netz**. Er kann das Datenverzeichnis lesen, also die SQLite-Datenbank und
den DataProtection-Key-Ring, mit dem sämtliche Upstream-Credentials entschlüsselt werden. Ein
kompromittierter stdio-Server ist damit nicht ein kompromittierter Upstream, sondern ein
kompromittiertes Gateway.

Das steht so im Threat-Model als akzeptiertes Restrisiko („nur vertrauenswürdige Server
anschließen"). Die Frage ist, ob das die Antwort bleiben soll.

## 2. Was stdio von CLI unterscheidet — und warum der vorhandene Code nicht reicht

Der Containerpfad für CLI ist **ein Job je Aufruf**: `docker run --rm`, Argumente rein, Ausgabe
raus, Container weg. Das passt zu einem Programm, das einmal läuft und antwortet.

stdio ist etwas anderes:

| | CLI | stdio |
|---|---|---|
| Lebensdauer | ein Aufruf | so lange der Upstream steht |
| Kommunikation | Argumente und Ausgabe | JSON-RPC über stdin/stdout, dauerhaft offen |
| Zustand | keiner | Sitzung, Initialisierung, ggf. Abos |
| Neustart | irrelevant | Verbindung bricht, Katalog muss neu |

`ContainerLaunchPolicy.BuildRunArguments` liefert die Härtungsflags (read-only Rootfs, `--cap-drop
ALL`, Nicht-root, PID-/RAM-/CPU-Grenzen, kein Netz, tmpfs) und ist wiederverwendbar. **Der
Lebenszyklus ist es nicht:** `--rm` je Aufruf müsste einem langlebigen Container mit angehängten
Pipes weichen, und der Supervisor müsste ihn wie einen Prozess überwachen und aufräumen.

Das ist die eigentliche Arbeit — nicht die Flags.

## 3. Die Optionen

### A — Container je Upstream, Pipes durchgereicht

Statt `npx …` startet der Gateway `docker run -i --rm <flags> <image> npx …` und spricht mit dessen
stdin/stdout. Der MCP-Client merkt keinen Unterschied: Er redet mit einem Kindprozess, und der
Kindprozess ist eben `docker`.

**Dafür:** echte Kernel-Isolation. Genau das, was der einzige Anbieter tut, der das Problem gelöst
hat (Docker MCP Gateway). Die Härtungspolicy liegt bereits vor und ist an einer laufenden Runtime
belegt.

**Dagegen — und das ist kein Detail:** Es braucht **ein Image je Server**. Heute schreibt man
`npx -y @modelcontextprotocol/server-filesystem /daten`; danach braucht es ein Image, in dem dieses
Paket liegt. Für jeden bestehenden stdio-Upstream. Das ist ein Bruch, kein Zusatz.

**Und der Dateisystem-Fall bleibt heikel:** Ein Filesystem-Server ist dazu da, auf Host-Pfade
zuzugreifen. Die Mounts müssen also explizit sein — die Allowlisten (`AllowedReadRoots`,
`AllowedWriteRoots`) gibt es schon, sie wären hier Pflicht statt Kür.

### B — Ein mitgeliefertes Basis-Image, Kommando unverändert hineingereicht

Der Gateway bringt ein Standard-Image (etwa `node:22-alpine`) mit und führt das konfigurierte
Kommando darin aus. Bestehende Konfigurationen blieben äußerlich gleich.

**Dagegen, und daran scheitert es:** `npx -y` **lädt das Paket beim Start herunter**. Das braucht
Netz im Container — und widerspricht direkt dem Default-Deny, das die Isolation ausmacht. Man
könnte einen npm-Cache als Volume mitgeben; dann lädt der erste Start trotzdem, und man hat ein
beschreibbares Volume, in dem ausführbarer Code landet.

Diese Option sieht bequem aus und ist es nicht: Sie tauscht „keine Isolation" gegen „Isolation mit
Netzzugang und beschreibbarem Code-Volume". Das wäre eine Verbesserung mit Etikettenschwindel.

### C — Betriebssystem-Sandbox ohne Container

seccomp/AppArmor/Landlock unter Linux, Job Objects/AppContainer unter Windows.

**Dagegen:** .NET hat dafür keine portable Schnittstelle. Wir schrieben sicherheitskritischen
Plattformcode selbst — dasselbe Argument, mit dem in dieser Codebasis schon eine handgeschriebene
Ed25519-Implementierung wieder verworfen wurde. Der Windows-Pfad wäre zudem deutlich schwächer als
der Linux-Pfad, und das Ergebnis ließe sich schlecht prüfen.

Unverhältnismäßig für den Nutzen.

### D — Eigener Benutzer statt Sandbox (kleine, sofortige Maßnahme)

Der Kindprozess läuft unter einem **eigenen, weniger berechtigten Konto**, mit eigenem
Arbeitsverzeichnis und minimaler Umgebung — letzteres gibt es für CLI schon.

**Dafür:** Ein kompromittierter Server kommt nicht mehr an Datenverzeichnis und Key-Ring. Das ist
der Schaden, der heute am meisten weh tut, und die Maßnahme ist klein.

**Dagegen:** Keine Sandbox. Netzzugang bleibt, Zugriff auf alles Weltlesbare bleibt. Unter Windows
ist „als anderer Benutzer starten" aus einem Dienst heraus umständlich und braucht Zugangsdaten.

**Wichtig:** D schließt A nicht aus. D ist die Maßnahme für heute, A die für die Zielarchitektur.

### E — Bewusst so lassen, aber datiert entscheiden

Status quo, jedoch als ausdrückliche, begründete Entscheidung mit den Mitigationen, die es gibt:
nur vertrauenswürdige Server anschließen, Konfiguration nur für Admins, Guardrail auf Argumenten
und Ergebnissen, vollständiges Audit.

**Dafür:** ehrlich, kostenlos, und für einen Einzelbetreiber mit selbst ausgewählten Servern
vertretbar — ADR-0001 nennt genau diese Zielgruppe.

**Dagegen:** Die Angriffsfläche bleibt die größte im System, und die Werbung „Isolation" im README
gilt weiterhin nur für zwei von vier Transporten.

## 4. Was das jeweils kostet

| Option | Aufwand | Bricht Bestehendes | Deckt den Hauptfall |
|---|---|---|---|
| A — Container je Upstream | groß (Lebenszyklus, Supervisor, Mounts, UI) | **ja, jeden stdio-Upstream** | ja |
| B — Basis-Image | mittel | nein | nur scheinbar |
| C — OS-Sandbox | sehr groß | nein | teilweise, plattformabhängig |
| D — eigener Benutzer | klein | nein (Linux); Windows offen | nein, aber deutlich weniger Schaden |
| E — dokumentiert lassen | keiner | nein | nein |

## 5. Meine Einschätzung

**A ist die einzige echte Isolation**, und sie ist der Weg, den das Feld geht. **B würde ich
ausschließen** — sie erzeugt ein Sicherheitsversprechen, das der Netzzugang gleich wieder aufhebt.
**C ist unverhältnismäßig.**

Die praktikable Reihenfolge wäre **D jetzt, A als Ziel**: Der eigene Benutzer nimmt dem
schlimmsten Fall die Spitze, ohne irgendetwas zu brechen, und kauft die Zeit für den Umbau, der
Konfigurationen bricht.

**E ist vertretbar**, wenn die Antwort lautet: Dieses Gateway ist persönliche Infrastruktur, die
Server wählt der Betreiber selbst aus, und der Aufwand geht woanders hin. Dann sollte es aber
**datiert und begründet** in ADR-0018 stehen statt als offener Punkt weiterzulaufen — ein offener
Punkt liest sich wie „kommt noch", eine Entscheidung wie „so ist es gemeint".

## 6. Was hier nicht entschieden ist

- Ob es überhaupt eine Änderung geben soll (E ist eine gültige Antwort).
- Ob ein Bruch bestehender Konfigurationen akzeptabel ist — das hängt daran, wie viele
  stdio-Upstreams real konfiguriert sind, und diese Zahl kenne ich nicht.
- Ob Windows denselben Isolationsgrad erreichen muss wie Linux, oder ob dort ein dokumentierter
  Rückstand hinnehmbar ist.
