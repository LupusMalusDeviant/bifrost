# ADR-0017: WASI Component Runtime als bevorzugter isolierter Pluginpfad

- **Status:** Akzeptiert als Isolations- und Grant-Modell (2026-07-25); der Vorrang für
  *beliebige* neue Tools und Connectoren steht unter Vorbehalt — siehe [Geltungsbereich](#geltungsbereich-2026-07-25).
  Hostauswahl entschieden: Wasmtime 47 out-of-process ([ADR-0020](0020-wasi-runtime-out-of-process-rust-host.md)).
- **Datum:** 2026-07-24, Statuswechsel 2026-07-25

## Kontext

WebAssembly stellt eine Speicher- und Kontrollflussgrenze bereit, ist aber nicht automatisch sicher.
Module können den Host nur über freigegebene APIs erreichen; genau diese Imports, Preopens, Sockets
und Secrets bilden damit die entscheidende Policyfläche. WIT beschreibt typisierte Verträge,
Interfaces und Worlds, aber nicht deren Sicherheitsverhalten.

Grundlagen:

- [WebAssembly Security](https://webassembly.org/docs/security/)
- [WIT Overview](https://component-model.bytecodealliance.org/design/wit.html)

## Entscheidung

MCPMCP bevorzugt WebAssembly Components für neue lokale Tools und externe Connectoren. Ein Component
erhält standardmäßig:

- kein Netzwerk;
- keine Dateisystem-Preopens;
- kein Host-Environment;
- keine Uhr, Zufallsquelle oder Secret-Capability, sofern nicht deklariert und freigegeben;
- begrenzten Speicher, Fuel/Epoch-Deadline, Output und Parallelität.

Der Host liest WIT-Exports zur Discovery und bildet WIT-Typen möglichst verlustarm auf den
Capability-Katalog ab. Imports werden vor Instanziierung gegen den Connector-Grant geprüft.
`list<u8>` wird als begrenzte Binärdaten oder Artifact behandelt; `result<T,E>` bleibt ein
strukturierter Result-/Fehlervertrag. Resources, Futures und Streams werden erst aktiviert, wenn
Ownership, Cancellation und das Task-/Event-Modell implementiert sind.

Jedes Modul wird über SHA-256 identifiziert. Produktion verlangt erlaubten Herausgeber oder
administrativ gepinnten Hash; Cache-Keys enthalten Runtime-, Component- und Grant-Version.
Erteilte Host-Capabilities werden **beim Laden** auditiert: Grants sind an den Ladevorgang
gebunden und können sich zwischen zwei Loads nicht ändern, deshalb genügt ein Datensatz je Load.
Zusammen mit der Audit-Zeile des Tool-Calls ist damit für jeden Aufruf nachvollziehbar, unter
welchen Rechten er lief. (Ursprünglich stand hier „bei Start und Invocation"; der Zusatz je Aufruf
wäre eine Wiederholung unveränderlicher Angaben in jeder Zeile.)

## Begrenzter Spike

Der Spike unter `docs/spikes/wasi-component-discovery.md` prüft nur:

1. Component laden und WIT-World/Exports inventarisieren;
2. primitive Typen, Records, Varianten, Listen, Options und Results mappen;
3. ohne Imports ausführen;
4. verweigerte filesystem-/socket-Imports nachweisen;
5. Fuel, Speicher, Timeout und Output-Cap messen;
6. dasselbe Fixture unter Windows und Linux ausführen.

Er implementiert weder Installation noch Netzwerk noch Secret-Injection. Runtime-Pakete werden erst
nach gemessener API-Stabilität und Wartungslast ausgewählt.

## Spike-Ergebnis 2026-07-24

Das separate Projekt `spikes/wasi-component-runtime` pinnt Wasmtime 47.0.2 und Rust 1.94.0. Es
erzeugt aus dem WIT-Fixture ein binäres Component, reflektiert dessen versionierte Exports und
weist nicht gewährte Imports vor Instanziierung ab. Tests belegen lokal unter Windows Fuel,
Epoch-Timeout, Linear-Memory- und Byte-Output-Limits. CI führt denselben Nachweis auf Windows und
Linux aus.

Ein lokaler Startup-Floor-Vergleich lag für das kurzlebige WASI-Component im Median bei 7,16 ms,
für einen gehärteten Alpine-Containerjob bei 232,40 ms. Diese Zahlen sind keine
Sicherheitsäquivalenz und kein Anwendungsbenchmark.

Der Spike ist ein Go für die nächste Prototypstufe, aber kein Produktions-Go: Publisher-Signatur,
echte WASI-P2-Preopen-/Socket-Grants, Grant-Audit, Cache und Rollback fehlen weiterhin. Der
ADR-Status bleibt daher „Vorgeschlagen“.

### M4-Ausbau 2026-07-24

Der Spike wurde gehärtet (Belege in `docs/spikes/wasi-component-discovery.md`, 19 Tests grün).
Jetzt vorhanden: strukturiertes Grant-Modell (default-deny) für Preopens/Netzwerk/Environment/
Secrets, detached Ed25519-Publisher-Signatur gegen administrativ gepinnte Keys, Grant-Audit-Datensatz
(Modulhash/Publisher/Runtime/erteilte Grants) und eine echte `wasm32-wasip2`-Guest-Component mit
`wasmtime-wasi`-Host, die WASI nur bei Grant linkt (deny-before-instantiation bewiesen); dazu
Traversal-/Symlink-/Socket-/Secret-Negativtests.

Offen für Produktion: externer Linux-CI-Nachweis (bislang nur lokal Windows); per-Interface-Grant-
Gating (der Spike gated world-level); Cache/Rollback; Connector-Handshake; Server-Runtimeadapter.
Der ADR-Status bleibt daher „Vorgeschlagen"; die Hostauswahl (Wasmtime 47) ist durch den Spike aber
bestätigt.

## Geltungsbereich (2026-07-25)

Der Statuswechsel gilt **dem Isolationsmodell**, nicht dem vollen Produktanspruch. Belegt und
angenommen:

- default-deny je WASI-Interface, durchgesetzt **vor** der Instanziierung (nicht gewährte
  Interfaces werden gar nicht erst gelinkt);
- Preopens pro Wurzel (aufgelöst, nur lesend), Netzwerk als aufgelöste `host:port`-Allowlist,
  Environment- und Secret-Injektion je Name;
- Limits pro Aufruf: Fuel, Epoch-Deadline, Linear-Memory, Output;
- Ed25519-Publisher-Signatur gegen einen persistierten Trust-Store, fail-closed, Entzug wirkt
  sofort; Grant-Audit je Load;
- content-adressierter Modul-Cache mit Rollback, Platten-Kompilate über einen host-lokalen
  Schlüssel MAC-gesichert.

**Unter Vorbehalt** steht der Satz „MCPMCP bevorzugt WebAssembly Components für neue lokale Tools
und externe Connectoren" — inzwischen aber deutlich enger als beim Statuswechsel.

*Nachtrag 2026-07-25:* Die zugesagte Behandlung von `list<u8>` als begrenzte Binärdaten und
`result<T,E>` als Fehlervertrag ist umgesetzt. Abgebildet sind alle Skalare, `string`, `char`,
`list<T>`, `option<T>` und `result<T,E>`; Binärdaten gehen als Base64 mit eigener Längengrenze,
64-Bit-Ganzzahlen als Dezimalstring. Seit demselben Tag sind auch `record`, `variant`, `enum`,
`flags` und `tuple` abgebildet.

**Der Vorbehalt bleibt trotzdem**, aus einem anderen Grund als angenommen: Ein aus WIT gebautes
Component legt seine Funktionen in eine **Interface-Instanz**, und der Host ruft bislang nur
Top-Level-Exports auf. Solange das so ist, erreicht die Typabbildung kein reales Component.
Resources, Futures und Streams hängen unverändert am Task-/Event-Modell
([ADR-0019](0019-langlaufende-tasks-und-events.md)).

Der Vorbehalt entfällt, wenn Funktionen in exportierten Interfaces aufrufbar sind. Bis dahin ist WASI der bevorzugte Pfad für **Tools, die in diese Form
passen**, und nicht die Vorgabe für jeden neuen Connector.

Sicherheitsstand und akzeptierte Restrisiken stehen im
[Threat-Model](../security/threat-model.md); der Review dazu in
[wasi-runtime-security-review.md](../security/wasi-runtime-security-review.md).

## Konsequenzen

WASI ist bevorzugte Isolation, kein Ersatz für Governance oder Container. Ein Component mit
freigegebenem Host-Dateisystem oder Netzwerk kann weiterhin erhebliche Seiteneffekte verursachen.
Native Programme bleiben über ADR-0018 unterstützt.
