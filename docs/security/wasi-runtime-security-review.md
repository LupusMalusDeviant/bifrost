# Security-Review WASI-Runtime (Plan 0003, WP7.3)

**Durchgeführt 2026-07-25, gemeinsam mit dem Product Owner. Ergebnis: angenommen mit benannten
Restrisiken.** Der Sicherheitsstand als Ganzes steht im [Threat-Model](threat-model.md); dieses
Dokument ist der Review-Nachweis dazu — was geprüft wurde, was belegt ist, was bewusst offenbleibt.

Die Neubewertung von [ADR-0017](../adr/0017-wasi-component-runtime.md) ist Teil desselben Termins
(WP7.4): Status jetzt **akzeptiert als Isolations- und Grant-Modell**, mit ausdrücklichem Vorbehalt
für den Vorrang bei beliebigen Connectoren — die Aufrufbreite trägt ihn noch nicht.

## Bewertung

Der Governance-Pfad ist dicht: keine Umgehung von Rate-Limit, RBAC, Guardrail, Approval oder Audit,
und der Host greift nie selbst auf DB oder Stores zu. Das Isolationsmodell setzt durch, was ADR-0017
zusagt, und zwar vor der Instanziierung statt als Laufzeitprüfung. Die verbleibenden Punkte sind
Kapazitäts- und Betriebsfragen, keine offenen Löcher — sie stehen als akzeptierte Restrisiken im
Threat-Model.

Kein Befund hat den Schweregrad, der einen Produktivbetrieb blockieren würde. Die schmale
Aufrufbreite begrenzt heute, *wofür* der Pfad taugt, nicht *wie sicher* er ist.

## Vertrauensgrenzen des neuen Pfades

```
[ Agent ] --API-Key--> [ GATEWAY ] --length-prefixed JSON über stdio--> [ bifrost-wasi-host ]
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

## Geprüfte Fläche

1. **IPC-Leitung.** Kein AuthN, keine Verschlüsselung — bewusst: Es sind die privaten Pipes eines
   Kindprozesses, wer sie erreicht, hat bereits den Gateway-Prozess. **Bewertet:** für die aktuelle
   Betriebsart (Kindprozess unter ProcessHygiene) angemessen. Wird der Host je zum Sidecar über
   einen Socket, ist das neu zu bewerten.
2. **Signaturkette und Platten-Cache.** Die Ed25519-Signatur deckt die `.wasm`-Bytes ab, nicht das
   daraus erzeugte Kompilat. Der Platten-Cache (`Wasi.ModuleCacheDirectory`) schließt diese Lücke
   mit einem HMAC-SHA256 unter einem host-lokalen Schlüssel (`mac.key`, unter Unix `0600`); ein
   Eintrag ohne gültigen MAC wird gelöscht statt geladen, und der eingebettete Cache-Schlüssel wird
   verglichen, damit ein umbenannter Eintrag nicht durchgeht. **Grenze:** Das schützt gegen fremden
   Schreibzugriff und Bitfehler, nicht gegen jemanden, der als derselbe Benutzer läuft.
   **Bewertet:** angenommen unter der Betriebsauflage „Verzeichnis gehört dem Host-Benutzer, für
   andere nicht schreibbar" — im Image erfüllt `/data` das (`chown app:app`, `0770`). Bei
   gemounteten Volumes liegt die Auflage beim Betreiber; steht in operations.md.
3. **Secrets über Environment.** Ein Secret-Grant zieht `wasi:cli/environment` nach sich; das
   Component kann damit **alle** gesetzten Variablen auflisten, nicht nur die eigenen. Bewusste
   Folge der gewählten Injektionsform. **Bewertet:** angenommen — Werte stehen in keiner Antwort
   und keinem Audit, und der Host setzt nur das, was gewährt wurde. Eine eigene
   WASI-Secret-Schnittstelle wäre enger und bleibt der Weg, falls das nicht mehr reicht.
4. **Grant-Granularität.** Filesystem und Netzwerk sind pro Ressource begrenzt (Preopen-Wurzeln,
   `host:port`), die übrigen Kategorien nur an/aus. Preopens sind **nur lesend**.
   **Bewertet:** ausreichend. Uhr und Zufall sind an/aus, tragen aber auch nichts nach außen;
   schreibende Preopens fehlen als Funktion, nicht als Absicherung.
5. **Trust-Store-Schreibzugriff.** Wer `/api/v1/publishers` bedienen darf (Global-Grant-Admin),
   entscheidet, welcher fremde Code laufen darf. Der Store liegt unverschlüsselt in der DB —
   Public Keys sind kein Geheimnis, aber ihre **Integrität** hängt am DB-Schreibzugriff.
   **Bewertet:** unverändertes Vertrauensniveau gegenüber vorher, aber zentralisiert und
   auditiert — eine Verbesserung, kein neues Risiko.
6. **Ressourcenverbrauch.** Ein Host-Prozess pro Upstream, Modul-Cache im Prozess. Beide
   Cache-Ebenen sind jetzt begrenzt (8 Kompilate im Speicher, 256 MiB auf Platte, Verdrängung nach
   Nutzung). Die Grenze im Speicher zählt **Einträge, nicht Byte** — Wasmtime gibt den
   Speicherbedarf eines fertigen `Component` nicht her. Bei sehr großen Modulen bleibt der
   Verbrauch damit nach oben offen. **Bewertet:** angenommen — `health` liefert die Zahlen, um das
   im Betrieb zu sehen, statt es zu schätzen.
7. **Prozess-Lifecycle.** Start/Kill über die bestehende ProcessHygiene (ADR-0005): erst
   `shutdown` über den Vertrag, dann `WaitForExit(2000)`, dann `Kill(entireProcessTree)`.
   **Bewertet:** angemessen. Der Host startet selbst keine Kindprozesse, das Job-Object fängt
   Verwaisung ab. Ein Zombie-Szenario ist nicht eigens getestet — Restrisiko niedrig.
8. **Nebenläufigkeit.** Ein Aufruf pro Upstream gleichzeitig (Semaphore in der Verbindung).
   **Bewertet:** Kapazitäts-, keine Sicherheitsgrenze; als akzeptiertes Restrisiko dokumentiert.

## Im Review entschieden

| Punkt | Entscheidung |
|---|---|
| ADR-0017-Status | Akzeptiert als Isolations- und Grant-Modell; Vorrang für beliebige Connectoren unter Vorbehalt bis zur breiteren Aufrufunterstützung |
| Capabilities pro Aufruf auditieren | Nein — Grants sind an den Load gebunden und ändern sich zwischen zwei Loads nicht. Der ADR-Text wurde entsprechend präzisiert statt das Audit aufzublähen |
| Serialisierung je WASI-Upstream | Bleibt. Als Kapazitätsgrenze in operations.md und im Threat-Model dokumentiert; Nebenläufigkeit wäre eine Vertragsänderung (Korrelations-Ids) |
| Ablage des Sicherheitsstands | Threat-Model ist der eine Ort; dieses Dokument ist der Review-Nachweis |

## Weiterhin offen (keine Sicherheitsbefunde)

- **Schreibende Preopens.** Im Grant-Modell nicht ausdrückbar; Preopens sind nur lesend. Eine
  Produktentscheidung, kein Mangel.
- **Angemessenheit der Cache-Grenzen.** 8 Einträge im Speicher, 256 MiB auf Platte. Ob das zur
  Betriebsgröße passt, zeigt der Betrieb — `health` liefert die Zahlen dafür.
- **Aufrufbreite.** `list<u8>`, `result<T,E>`, Resources/Futures/Streams. Blockiert den Vorbehalt
  in ADR-0017.

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
lokale Tools und externe Connectoren" reicht das nicht. **Konsequenz im Review:** Der Status wandert
auf „Akzeptiert", aber ausdrücklich nur für das Isolations- und Grant-Modell; der Produktanspruch
bleibt unter Vorbehalt, bis die Aufrufbreite trägt.
