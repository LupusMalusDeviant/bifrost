# WASI-Runtime — Material für den Security-Review (Plan 0003, WP7.3/7.4)

Stand: 2026-07-25 (Platten-Cache nachgetragen). **Dies ist kein Review-Ergebnis und keine Freigabe.** Das Dokument trägt
zusammen, was der WASI-Pfad heute tut, was davon durch benannte Tests belegt ist und welche Punkte
eine Entscheidung brauchen. Bewertung und die Neubewertung von [ADR-0017](../adr/0017-wasi-component-runtime.md)
liegen beim Product Owner.

## Vertrauensgrenzen des neuen Pfades

```
[ Agent ] --API-Key--> [ GATEWAY ] --length-prefixed JSON über stdio--> [ mcpmcp-wasi-host ]
                        │  RBAC, Guardrail, Approval,                    │  Signaturprüfung,
                        │  Audit, Trust-Store                            │  Grants, Limits
                        │                                                └──> [ WASM-Component ]
                        └── hält Component-Bytes, Signatur, Secrets
```

Zwei Grenzen sind neu: die **IPC-Leitung** (Gateway ↔ Host, private Pipes eines Kindprozesses) und
die **Guest-Grenze** (Host ↔ Component, durchgesetzt von Wasmtime plus dem Grant-Modell).

## Belegt durch Tests

| Eigenschaft | Nachweis |
|---|---|
| Kein Governance-Bypass: jeder Aufruf durch RBAC/Guardrail/Approval/Audit | `WasiUpstreamE2ETests` (6 Tests), `WasiRealHostGovernanceTests` (echtes Binary) |
| Nur signierte Components laufen; Signatur gegen persistierte, gepinnte Keys | `WasiRealHostCompatibilityTests`, `PublisherTrustTests`, Rust `load_is_rejected_for_an_unpinned_publisher` |
| Leerer Trust-Store lädt nichts (fail-closed) | `PublisherTrustTests.An_empty_trust_store_lets_no_wasi_upstream_start` |
| Entzug wirkt sofort auf laufende Upstreams | `PublisherTrustTests.Revoking_a_key_stops_the_running_upstream_that_used_it` |
| Grants pro WASI-Interface, default-deny, deny-before-instantiation | Rust `each_category_is_gated_by_its_own_grant`, `only_granted_interfaces_are_linked` |
| Preopens werden aufgelöst; fehlende Wurzel fail-closed | Rust `preopen_roots_are_resolved_and_missing_ones_fail_closed` |
| Netzwerk-Allowlist einmalig aufgelöst, nicht pro Verbindung | Rust `the_network_allowlist_resolves_once_and_rejects_unlistable_targets` |
| Limits pro Aufruf (Fuel, Epoch, Memory, Output) | Rust-Limit-Tests aus M4 |
| Secret-Werte erreichen den Guest, stehen aber in keiner Antwort und keinem Audit | Rust `a_granted_secret_reaches_the_guest`, `WasiRealHostCompatibilityTests` |
| Framing hält partielle Reads, Überlängen und Schrott aus, ohne den Host zu töten | Rust-Framing-Tests (WP1.5) |
| Kaputtes Component fällt beim Laden auf, alter Stand bleibt aktiv | Rust `a_broken_component_rolls_back_to_the_previous_one` |
| Host liegt im Image, spricht dort den Vertrag, Container läuft non-root | CI-Job `docker` (WP7.2) |

## Fläche, die ein Review ansehen sollte

1. **IPC-Leitung.** Kein AuthN, keine Verschlüsselung — bewusst: Es sind die privaten Pipes eines
   Kindprozesses, wer sie erreicht, hat bereits den Gateway-Prozess. Zu prüfen: Gilt das auch für
   die Betriebsart, die der PO vorsieht (etwa Sidecar-Container statt Kindprozess)?
2. **Signaturkette und Platten-Cache.** Die Ed25519-Signatur deckt die `.wasm`-Bytes ab, nicht das
   daraus erzeugte Kompilat. Der Platten-Cache (`Wasi.ModuleCacheDirectory`) schließt diese Lücke
   mit einem HMAC-SHA256 unter einem host-lokalen Schlüssel (`mac.key`, unter Unix `0600`); ein
   Eintrag ohne gültigen MAC wird gelöscht statt geladen, und der eingebettete Cache-Schlüssel wird
   verglichen, damit ein umbenannter Eintrag nicht durchgeht. **Grenze:** Das schützt gegen fremden
   Schreibzugriff und Bitfehler, nicht gegen jemanden, der als derselbe Benutzer läuft. Zu prüfen:
   ob die Betriebsauflage „Verzeichnis gehört dem Host-Benutzer, für andere nicht schreibbar" in
   der vorgesehenen Betriebsart wirklich gilt (Volume-Mounts, `/data`-Rechte).
3. **Secrets über Environment.** Ein Secret-Grant zieht `wasi:cli/environment` nach sich; das
   Component kann damit **alle** gesetzten Variablen auflisten, nicht nur die eigenen. Bewusste
   Folge der festgelegten Entscheidung 3.
4. **Grant-Granularität.** Filesystem und Netzwerk sind pro Ressource begrenzt (Preopen-Wurzeln,
   `host:port`), die übrigen Kategorien nur an/aus. Preopens sind **nur lesend**.
5. **Trust-Store-Schreibzugriff.** Wer `/api/v1/publishers` bedienen darf (Global-Grant-Admin),
   entscheidet, welcher fremde Code laufen darf. Der Store liegt unverschlüsselt in der DB —
   Public Keys sind kein Geheimnis, aber ihre **Integrität** hängt am DB-Schreibzugriff.
6. **Ressourcenverbrauch.** Ein Host-Prozess pro Upstream, Modul-Cache im Prozess. Beide
   Cache-Ebenen sind jetzt begrenzt (8 Kompilate im Speicher, 256 MiB auf Platte, Verdrängung nach
   Nutzung). Die Grenze im Speicher zählt **Einträge, nicht Byte** — Wasmtime gibt den
   Speicherbedarf eines fertigen `Component` nicht her. Bei sehr großen Modulen bleibt der
   Verbrauch damit nach oben offen; zu prüfen, ob das für die vorgesehene Betriebsgröße reicht.
7. **Prozess-Lifecycle.** Start/Kill über die bestehende ProcessHygiene (ADR-0005). Ein hängender
   Host wird beim Dispose hart beendet; ein Zombie-Kindprozess des Hosts selbst ist nicht getestet.

## Entscheidungen, die anstehen

- **Schreibende Preopens.** Heute nicht ausdrückbar. Ob und wie das Grant-Modell sie bekommt, ist
  eine Produktentscheidung.
- **Angemessenheit der Cache-Grenzen.** Umgesetzt sind 8 Einträge im Speicher und 256 MiB auf
  Platte. Ob die Vorgaben für die vorgesehene Betriebsgröße passen — und ob die Speichergrenze in
  Einträgen statt Byte ausreicht — ist eine Betriebsentscheidung.

## Für die Neubewertung von ADR-0017

Der ADR steht auf „Vorgeschlagen; Hostauswahl nach Spike". Was seither belegt ist:

| Zusage im ADR | Stand |
|---|---|
| Default-deny für Netzwerk, Preopens, Environment, Uhr, Zufall, Secrets | belegt, pro Interface durchgesetzt (WP3) |
| Limits: Speicher, Fuel/Epoch, Output | belegt (M4/WP1.4) |
| Imports vor Instanziierung gegen den Grant geprüft | belegt — nicht gewährte Interfaces werden gar nicht erst gelinkt |
| Discovery liest WIT-Exports und bildet Typen ab | belegt für Kommando-Exports und `(s32) -> s32`; **alles andere meldet die Discovery als nicht unterstützt** |
| `list<u8>` als begrenzte Binärdaten, `result<T,E>` als Fehlervertrag | **offen** — nicht implementiert |
| Resources, Futures, Streams erst mit Task-/Event-Modell | unverändert offen (ADR-0019) |
| Modul über SHA-256 identifiziert, Publisher gepinnt, Cache-Key mit Runtime-/Grant-Version | belegt (WP4, WP5) |
| Erteilte Capabilities bei Start und Invocation auditiert | Start belegt (Grant-Audit je Load); **pro Invocation nicht** — dort steht der Tool-Call im Audit, nicht die Capability-Liste |
| Installation, Netzwerk, Secret-Injection im Spike nicht enthalten | Secret-Injektion inzwischen umgesetzt; ein Installationsfluss fehlt weiterhin |

Die Aufrufbreite ist die auffälligste Lücke zwischen ADR-Anspruch und Code: Ein Component kann
heute genau einen Kommando-Einstiegspunkt oder eine `(s32) -> s32`-Funktion anbieten. Für „neue
lokale Tools und externe Connectoren" reicht das nicht — das gehört in die Bewertung, ob der Status
auf „Akzeptiert" wandert oder mit benannter Restarbeit offen bleibt.
