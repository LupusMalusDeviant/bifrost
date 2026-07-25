# Plan-0003: M5 — WASI-Runtime-Adapter (Out-of-Process) und Connector-SDK

- **Status:** Entwurf
- **Datum:** 2026-07-24
- **Autor:** Senior-Tech-Specialist (Claude)
- **Basis-PRD:** Standalone — gegründet auf [ADR-0020](../adr/0020-wasi-runtime-out-of-process-rust-host.md), Roadmap `docs/prompts/claude-hardening-and-protocol-roadmap.md` (M5)
- **Verantwortlich:** Product Owner

## Kontext / Motivation

M4 hat das WASI-Sicherheitsmodell in einem Rust/Wasmtime-47-Spike belegt (Grant-Modell default-deny, Ed25519-Publisher-Signatur + Pinning, Grant-Audit, echtes deny-before-instantiation). M5 macht daraus einen produktionsfähigen Ausführungspfad. [ADR-0020](../adr/0020-wasi-runtime-out-of-process-rust-host.md) legt die Architektur fest: Da `wasmtime-dotnet` kein Component Model/WASI-P2 kann, läuft die Runtime als **eigenständiger Rust-Host-Prozess**, den das .NET-Gateway über einen **versionierten lokalen IPC-Vertrag** ansteuert — jeder Aufruf bleibt im bestehenden `IToolInvoker`-Governance-Pfad, kein Bypass.

## Ziele

- Ein signiertes WASI-P2-Component erscheint als normaler Upstream im Katalog und ist über MCP **und** REST aufrufbar — durch die volle Governance-Pipeline (RBAC → Guardrail → Approval → Rate-Limit → Audit).
- Feingranulare, default-deny Grants (per-Preopen/-Socket/-Env/-Secret) im Host durchgesetzt.
- Publisher-Signatur gegen einen **persistierten** Trust-Store beim Laden geprüft; Grant-Audit im bestehenden Audit-Pfad.
- Host-Prozess unter Supervisor (Start/Restart/Kill), Win+Linux, non-root; CI grün auf beiden.
- Kein Governance-Bypass; der Host nutzt nie direkt DB/Stores.

## Arbeitspakete (Workstreams)

### WP1: IPC-Vertrag + Rust-Host-Binary

**Zweck:** Der M4-Spike wird ein langlebiger Host-Prozess mit einem versionierten Kommando-Vertrag.
**Rolle:** Rust/Runtime.
**Schätzung:** L

**Schritte:**
1. **WP1.1:** IPC-Form entscheiden (length-prefixed JSON über stdio vs. lokaler Socket) — Mini-Entscheidung, ggf. ADR-0021. Kriterium: robust, killbar, Win+Linux, ein Prozess pro Gateway.
2. **WP1.2:** Handshake mit Versionsfeld (`hello`/`capabilities`) — .NET und Host lehnen inkompatible Versionen ab.
3. **WP1.3:** Kommandos: `load(component-bytes|ref, signature, grants)` → verify + instantiate, `discover` → Tools, `invoke(tool, args)` → result, `health`, `shutdown`. Strukturierte Fehler + Truncation-Metadaten.
4. **WP1.4:** Limits (Fuel/Epoch/Memory/Output) + Prozess-/Aufruf-Timeouts pro `invoke`.
5. **WP1.5:** Property-/Fuzz-Tests auf das Framing (partielle Reads, große/Binär-Payloads); CI Win+Linux.

**Ergebnis:** Ein `mcpmcp-wasi-host`-Binary, das den Vertrag spricht; Vertrag als versioniertes Schema dokumentiert.

### WP2: .NET WasiRuntimeConnector

**Zweck:** Neuer Upstream-Transport `Kind=Wasi`, der den Host startet/überwacht und den Vertrag spricht.
**Rolle:** .NET/Core.
**Schätzung:** L

**Schritte:**
1. **WP2.1:** `UpstreamTransportKind.Wasi` + `WasiTransportOptions` (Host-Pfad, Component-Quelle, Grants, Limits) + Validator-Case.
2. **WP2.2:** `WasiRuntimeConnector : IUpstreamConnector` + `IUpstreamConnection` — Host über den bestehenden Supervisor-/ProcessHygiene-Weg (ADR-0005) starten, IPC-Client, `DiscoverAsync`/`CallToolAsync` auf den Vertrag mappen.
3. **WP2.3:** DI-Registrierung; erscheint im aggregierten Katalog und in der REST-/OpenAPI-Fassade.
4. **WP2.4:** Integrationstest: ein signiertes Component wird über den vollen `IToolInvoker` aufgerufen (RBAC/Guardrail/Approval/Audit nachgewiesen).

**Ergebnis:** WASI-Tools sind über MCP + REST bedienbar, ununterscheidbar von anderen Upstreams.

### WP3: Feingranulares Grant-Mapping im Host

**Zweck:** Grants real per-Ressource an den WASI-Host binden statt world-level.
**Rolle:** Rust/Runtime.
**Schätzung:** M

**Schritte:**
1. **WP3.1:** `CapabilityGrants` → `wasmtime-wasi` per-Interface: Preopens (nur gewährte, kanonische Roots), Netzwerk-Allowlist, Environment-Keys, Secret-Injection.
2. **WP3.2:** Negativtests je Kategorie erweitern (nicht gewährt → deny; Traversal/Symlink am realen Preopen).

**Ergebnis:** Grant-Gating auf Ressourcen-Ebene, default-deny, belegt.

### WP4: Publisher-Trust-Store + Signatur beim Laden

**Zweck:** Administrativ verwaltete, persistierte Publisher-Keys; Signaturprüfung im Ladepfad; Grant-Audit.
**Rolle:** .NET/Persistence + Rust.
**Schätzung:** M

**Schritte:**
1. **WP4.1:** Trust-Store (EF/DataProtection, NFR-04): Publisher-Public-Keys anlegen/pinnen/entziehen; UI/REST-Verwaltung.
2. **WP4.2:** Beim `load` prüft der Host die Signatur gegen die übergebenen gepinnten Keys; Abweisung fail-closed.
3. **WP4.3:** Grant-Audit-Datensatz (Modulhash/Publisher/Runtime/Grants) in den bestehenden Audit-Pfad.

**Ergebnis:** Nur signierte, gepinnte Components laufen; jeder Load ist auditiert.

### WP5: Modul-Cache + Rollback

**Zweck:** Kompilierte Components content-addressed cachen; sicher zurückrollen.
**Rolle:** Rust/Runtime.
**Schätzung:** M

**Schritte:**
1. **WP5.1:** Cache-Key = Hash + Runtime-Version + Grant-Version; Cache-Invalidierung bei Änderung.
2. **WP5.2:** Rollback auf die vorige Version bei fehlgeschlagenem Load/Health; Messung der Startup-/Compile-Kosten.

**Ergebnis:** Warme Starts, deterministische Invalidierung, Rollback bei Fehler.

### WP6: Connector-SDK / Handshake (ADR-0016)

**Zweck:** Den IPC-Vertrag als versionierten Connector-Vertrag formalisieren.
**Rolle:** .NET + Rust.
**Schätzung:** L

**Schritte:**
1. **WP6.1:** Vertrag entlang ADR-0016 schärfen: Discovery, Schema-Normalisierung, Invoke, Cancellation, Health/Readiness, Lifecycle, Capability-Flags, Fehlersemantik, Kompatibilitätsprüfung.
2. **WP6.2:** Kompatibilitätstests (.NET ↔ Host, ältere/neuere Vertragsversion) in CI.

**Ergebnis:** Der WASI-Host ist der erste Connector nach dem stabilen Vertrag — Basis für Drittanbieter-Connectoren.

### WP7: Packaging, CI, Security-Review

**Zweck:** Rust-Host neben dem .NET-Image ausliefern, auf beiden OS, mit Sicherheitsnachweis.
**Rolle:** DevOps/Security.
**Schätzung:** L

**Schritte:**
1. **WP7.1:** Cross-Platform-Build des Hosts (statisch wo möglich); ein Container mit .NET + Host **oder** getrennte Artefakte entscheiden.
2. **WP7.2:** CI-Matrix Win+Linux für Host + Integrationstests; non-root im Image.
3. **WP7.3:** Security-Review des neuen Pfades (IPC-Fläche, Signaturkette, Grant-Durchsetzung); DoD-Abgleich.
4. **WP7.4:** ADR-0017 auf Basis der Belege neu bewerten (Vorgeschlagen → Akzeptiert oder begründet weiter offen).

**Ergebnis:** Deploybarer WASI-Pluginpfad, extern belegt; ADR-0017 entschieden.

## Abhängigkeiten

| Von | Nach | Typ |
|-----|------|-----|
| WP1 fertig | WP2 Start | intern, blocking |
| WP1 + WP2 fertig | WP3 Start | intern |
| WP2 fertig | WP4, WP5 Start | intern |
| WP1 + WP2 fertig | WP6 Start | intern |
| WP1–WP6 fertig | WP7 Start | intern, blocking |

```mermaid
graph LR
    WP1 --> WP2
    WP2 --> WP3
    WP2 --> WP4
    WP2 --> WP5
    WP2 --> WP6
    WP3 --> WP7
    WP4 --> WP7
    WP5 --> WP7
    WP6 --> WP7
```

WP3/WP4/WP5 laufen nach WP2 weitgehend parallel — der kritische Pfad ist WP1 → WP2 → WP6 → WP7.

## Risiken & Mitigationen

| # | Risiko | Wahrscheinlichkeit | Impact | Mitigation |
|---|--------|---------------------|--------|------------|
| R1 | Cross-Platform-Rust-Build/Packaging (Win+Linux, statisch vs. dynamisch, Container) | mittel | hoch | Schon in WP1.5 einen minimalen Cross-Build + CI-Matrix aufsetzen; Host statisch linken, wo möglich; früh im Ziel-Container testen |
| R2 | IPC-Robustheit (Framing, Deadlocks, partielle Reads, große/Binär-Payloads) | mittel | hoch | Length-prefixed Framing + Property-/Fuzz-Tests (WP1.5); Timeouts/Cancel; Binärdaten als begrenzte Blobs/Artifact-Ref |
| R3 | Host-Prozess-Lifecycle (Start/Restart/Orphan/Shutdown) | mittel | mittel | Bestehende Supervisor-/ProcessHygiene-Muster (ADR-0005) wiederverwenden; `health` im Vertrag; Shutdown-Test |
| R4 | Startup-/Compile-Latenz pro Aufruf | mittel | mittel | Langlebiger Host + Modul-Cache (WP5); Latenz früh messen (M1) |
| R5 | Vertrags-Versionen (.NET ↔ Rust) laufen auseinander | mittel | mittel | Expliziter Versions-Handshake (WP1.2/WP6); Kompatibilitätstests in CI |
| R6 | Governance-Bypass-Regression (neuer Pfad umgeht Guardrail/Approval) | niedrig | hoch | Connector geht durch denselben `IToolInvoker`; Integrationstest, der Guardrail/Approval auf einem WASI-Tool nachweist (WP2.4) |
| R7 | Solo-Kapazität — M5 ist groß, XL-Gefahr | hoch | mittel | Strikte WP-Reihenfolge; jedes WP einzeln releasefähig; keine Produktionsreife-Behauptung vor Belegen; Scope pro WP hart halten |

## Meilensteine

- **M1 (~2026-08-08):** WP1 fertig — der Rust-Host spricht den Vertrag; load/verify/instantiate/invoke/discover lokal grün, Framing-Tests in CI (Win+Linux). Startup-Latenz gemessen.
- **M2 (~2026-08-22):** WP2 fertig — ein signiertes Component erscheint als Upstream und wird durch die **volle** Governance-Pipeline aufgerufen (Integrationstest R6).
- **M3 (~2026-09-12):** WP3 + WP4 + WP5 fertig — feingranulare Grants, persistierter Trust-Store + Load-Signaturprüfung, Modul-Cache/Rollback.
- **M4 (~2026-09-30):** WP6 + WP7 fertig — Connector-Vertrag formalisiert, Packaging + CI + Security-Review; ADR-0017 neu bewertet.

## Stand (2026-07-24)

Belegt, jeweils an einen benannten Test gebunden — nicht an ein Plan-Häkchen:

| Punkt | Nachweis |
|-------|----------|
| WP1 (IPC-Vertrag, Rust-Host) | `spikes/wasi-component-runtime/src/host.rs` — Framing, partielle Reads, Frame-Limits; insgesamt 51 Rust-Tests |
| WP2 (.NET-Connector) | `WasiRuntimeConnectorTests` gegen den Stub-Host; `UpstreamConfigValidatorTests` für die fail-closed Validierung |
| WP3 (feingranulares Grant-Mapping) | Der Host linkt **nur die gewährten** WASI-Interfaces (`add_granted_wasi_to_linker`); Preopens werden aufgelöst und lesend eingehängt, die Netzwerk-Allowlist einmalig zu Socket-Adressen aufgelöst (`apply_grants_to_context`). Tests: Kategorie-Gating end-to-end an zwei WAT-Fixtures, Linker-Politik über alle Kategorien, Preopen-Auflösung/fail-closed, Allowlist-Auflösung; `UpstreamConfigValidatorTests` weist nicht durchsetzbare Grants schon in der Konfiguration ab |
| WP5 (Modul-Cache + Rollback) | `ModuleCache` — Schlüssel aus Modulhash, Runtime-Version, Engine-Profil und Grant-Fingerabdruck; `load` kompiliert als Gesundheitstest, ein Fehlschlag lässt den bisherigen Stand aktiv (`load-rolled-back`). `health` meldet aktives Modul und Cache-Kennzahlen. **Gemessen** am echten Host (Release, Fixture-Guest): kalter Aufruf ~75 ms, warmer ~0,4 ms — vorher zahlte *jeder* Aufruf den kalten Weg. Tests: 3 Rust-Tests zum Cache, 3 zu Rollback/Wiederverwendung, 2 `WasiRealHostCompatibilityTests` über die Leitung |
| WP4 (Trust-Store, Load-Prüfung, Grant-Audit, Secret-Injektion) | `PublisherTrustStore` persistiert (EF-Migrationen SQLite + Postgres), Fingerprint-Id passend zum Host-Audit; der Connector zieht die Schlüssel von dort statt aus der Config und schreibt jeden Load in den Audit-Pfad. Entzug wirkt sofort auf laufende Upstreams. REST unter `/api/v1/publishers`, admin-only. Secrets kommen verschlüsselt aus der Upstream-Konfiguration und werden vom Host als Environment-Einträge injiziert; Werte stehen in keiner Antwort und keinem Audit. Tests: 7 `PublisherTrustTests`, 2 Rust-Tests zur Injektion, 1 Real-Host-Test, Validator-Tests für Namen/Werte |
| WP6.1 (Namens- und Schema-Normalisierung) | Vertrag **v2**: `discover` liefert typisierte Beschreibungen (`describe_component_tools`), der Kommando-Einstiegspunkt ist genau ein Tool, nicht aufrufbare Signaturen sind als solche markiert. Gateway-seitig `WasiToolNormalizer` — katalog- und URL-taugliche Namen plus ein Schema **pro** Tool. Tests: 3 Rust-Tests, `WasiRuntimeConnectorTests` (Normalisierung, Kollisionen, Schema, gefilterte Exports) |
| WP6.2 (Vertragskompatibilität) | `WasiRealHostCompatibilityTests` — .NET gegen das **echte** Binary: Handshake, signierter Load, Discovery, Invoke, Default-Deny, Versionsverhandlung mit „1" (der echte Bruch) und „3" |
| WP7.1/7.2 (Packaging, CI) | Der Host wird **mit** dem Gateway ausgeliefert (ein Image = ein Vertragsstand): eigene Rust-Stage im Dockerfile, die von der Bau-Architektur aus kreuzkompiliert statt unter QEMU zu emulieren. Gemessen: Image 121 MB (Grenze 300 MB), Host-Binary 30 MB unter `/usr/local/bin/mcpmcp-wasi-host`. CI prüft, dass der Host im Image den Handshake beantwortet und der Container non-root läuft (UID 1654) |
| **M2** (signiertes Component durch die volle Pipeline) | `WasiRealHostGovernanceTests` (echter Host, signiertes Fixture) + `WasiUpstreamE2ETests` (RBAC, Guardrail, Approval, Audit, MCP + REST) |

Offen und ausdrücklich **nicht** behauptet:

- **WP6.1, Rest** — der Vertrag deckt Namen, Schemata, Discovery, Invoke, Health und
  Fehlersemantik ab; **Cancellation, Readiness, Lifecycle-Phasen und Capability-Flags** nach
  ADR-0016 fehlen weiterhin. Ebenso die Aufrufbreite: Der Host führt heute nur
  `(s32) -> s32`-Funktionen und den Kommando-Einstiegspunkt aus — alles andere meldet die
  Discovery ehrlich als nicht unterstützt, statt es im Katalog zu zeigen.
- **WP5, Rest — braucht eine Entscheidung (Messung liegt vor).** Die Voraussetzung von
  Entscheidung 4 ist **nicht** eingetreten: Gemessen (Release, `compilation_cost_by_module_size`,
  reproduzierbar über `cargo test --release --lib -- --ignored --nocapture compilation_cost`)
  kostet die Kompilierung rund **2,3 ms je KiB** — 100 KiB ≈ 0,25 s, 434 KiB ≈ 1,0 s,
  1,4 MiB ≈ 3,2 s. Ein realistisch großes Plugin-Component (1–3 MB) zahlt also **3–7 Sekunden pro
  Host-Start**, nicht die angenommenen ≤ 500 ms. Damit lohnt sich ein Platten-Cache — nur ist die
  offene Frage jetzt eine sicherheitsrelevante: Ein cwasm-Artefakt ist ausführbarer Code, den die
  Signaturkette **nicht** abdeckt, und der Trust-Store hält nur öffentliche Schlüssel, also kein
  Material für einen MAC. Wer den Cache baut, muss vorher entscheiden, wie das Artefakt geschützt
  wird (host-lokaler Schlüssel? Ableitung über DataProtection? Nur Verzeichnisrechte?). Diese
  Entscheidung gehört zum Product Owner und wurde hier bewusst nicht getroffen.
- **WP3, Rest** — Preopens bleiben **nur lesend**; Schreibrechte sind im Grant-Modell nach wie vor
  nicht ausdrückbar.
- **WP7.3/7.4 — liegen beim Product Owner.** Das Material steht in
  [`docs/security/wasi-runtime-review-material.md`](../security/wasi-runtime-review-material.md):
  Vertrauensgrenzen, was durch benannte Tests belegt ist, die Prüffläche und der Abgleich der
  ADR-0017-Zusagen mit dem Code. Bewertung und Statuswechsel des ADR wurden bewusst **nicht**
  vorweggenommen. Auffälligste Lücke zwischen ADR-Anspruch und Code: die Aufrufbreite — heute nur
  Kommando-Exports und `(s32) -> s32`.

## Festgelegte Entscheidungen für WP4 (2026-07-24, Product Owner)

Diese vier Punkte sind entschieden und nicht mehr offen — die Umsetzung folgt ihnen, statt sie
neu abzuwägen:

1. **Quelle der gepinnten Publisher-Keys:** Der Trust-Store ist die einzige maßgebliche Quelle.
   Vorhandene `Wasi.PinnedPublishers` aus der Upstream-Konfiguration werden beim Start **einmalig**
   in den Store übernommen (mit Audit-Eintrag) und danach ignoriert. Bestehende Konfigurationen
   laufen weiter; die Governance hat trotzdem genau eine Quelle.
2. **Key-Entzug:** wirkt **sofort**. Der Supervisor beendet jeden Upstream, dessen geladenes
   Component von diesem Publisher signiert war, mit Audit-Eintrag. Ein Entzug, der erst beim
   nächsten Neustart greift, ist kein Entzug.
3. **Secret-Injektion** (offener Rest aus WP3): über den bestehenden verschlüsselten Secret-Store.
   Der Grant nennt Namen, das Gateway löst sie auf und schickt die Werte beim `load` mit, der Host
   injiziert sie als Environment-Einträge. **Werte gehen nie ins Audit**, nur die Namen. Keine
   eigene WASI-Secret-Schnittstelle.
4. **Platten-Cache** (offener Rest aus WP5): erst messen. Kompiliert ein realistisch großes
   Component unter ~500 ms, wird der Punkt bewusst geschlossen statt gebaut — die Signaturkette
   deckt cwasm-Artefakte nicht ab, und der Gewinn wäre eine Kompilierung pro Upstream-Start.

## Erfolgskriterien

- Ein signiertes WASI-P2-Component ist über MCP **und** REST aufrufbar, durch RBAC/Guardrail/Approval/Audit — im Integrationstest belegt.
- Grants sind feingranular (per-Preopen/-Socket/-Env/-Secret), default-deny; nicht gewährte Zugriffe werden fail-closed abgewiesen (Negativtests).
- Publisher-Signatur wird beim Laden gegen den persistierten Trust-Store geprüft; jeder Load steht im Audit-Log.
- Host läuft unter Supervisor (Start/Restart/Kill), Win+Linux, non-root; CI grün auf beiden.
- Kein Governance-Bypass; der Host nutzt nie direkt DB/Stores.
- ADR-0017 ist auf Basis dieser Belege entschieden.

## Zeitschätzung (Gesamt)

- **Summe Arbeitspakete:** ~78 Tage (L≈15, M≈7; WP1 L, WP2 L, WP3 M, WP4 M, WP5 M, WP6 L, WP7 L).
- **Puffer (25 % + je 1–2 Tage pro Hoch-Risiko R1/R2/R7):** ~24 Tage.
- **Gesamt:** ~100 Tage ≈ 14 Kalender-Wochen bei 1 FTE (~3,5 Monate solo). Der Wert ist bewusst ehrlich; jedes WP ist einzeln releasefähig, sodass Teilnutzen früh entsteht.

## Offene Punkte

- **IPC-Form** (WP1.1): length-prefixed JSON über stdio vs. lokaler Socket (Named Pipe/UDS) — braucht evtl. ein Mini-ADR-0021.
- **Packaging** (WP7.1): ein Container mit .NET + Rust-Host vs. getrennte Artefakte — Ops-Auswirkung.
- **Binärdaten/Streaming** über den Vertrag: zunächst begrenzte Blobs; echte Streams erst mit dem Task-/Event-Modell (ADR-0019).

## Referenzen

- [ADR-0020](../adr/0020-wasi-runtime-out-of-process-rust-host.md) — die Architektur-Grenze, die dieser Plan umsetzt.
- [ADR-0016](../adr/0016-versionierter-connector-plugin-vertrag.md), [ADR-0017](../adr/0017-wasi-component-runtime.md), [ADR-0018](../adr/0018-native-prozess-und-container-isolation.md), [ADR-0005](../adr/0005-hot-swap-upstreams-als-verwaltete-kindprozesse.md).
- Roadmap: `docs/prompts/claude-hardening-and-protocol-roadmap.md` (M5). Spike: `spikes/wasi-component-runtime`, `docs/spikes/wasi-component-discovery.md`.
