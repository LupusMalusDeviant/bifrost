# ADR-0016: Versionierter Connector-/Plugin-Vertrag

- **Status:** Vorgeschlagen; der **Laufzeitvertrag** ist am 2026-07-25 am WASI-Host umgesetzt und
  belegt (Plan 0003, WP6.1). Offen bleibt alles rund um **Pakete**.
- **Datum:** 2026-07-24

> **Umsetzungsstand 2026-07-25.** Der WASI-Host (ADR-0020) ist der erste Connector nach diesem
> Vertrag und erfüllt den Laufzeitteil: versionierter Handshake mit **Capability-Flags**,
> Correlation-Id auf jeder Antwort, normierte Fehlerhülle (`code` + `message`), Discovery mit
> Schema-Normalisierung, Cancellation mit Bestätigung, Readiness getrennt von Liveness und der
> Lifecycle `handshake → load → discover → ready → invoke/cancel → drain → stop`.
>
> **Nicht umgesetzt und weiterhin nur vorgeschlagen:** signiertes Connector-**Manifest**, Packaging
> und Installation, Update/Rollback über Quarantäne, sowie die vier Vertrauensstufen. Der Host wird
> heute mit dem Gateway ausgeliefert (Stufe 1, „Core"); es gibt keinen Weg, ein Fremdpaket zu
> installieren. Solange das so ist, ist der Vertrag belegt, aber nicht erprobt — genau das war der
> Zweck dieser ADR, und deshalb bleibt ihr Status offen.

## Kontext

Die bestehende DI-Auswahl über `IUpstreamConnector.Kind` ist ein guter interner Erweiterungspunkt,
aber kein Drittanbieter-Vertrag. Ihr fehlen Protokollverhandlung, Packaging, Berechtigungen,
Crash-Isolation, Update/Rollback und ein Trust-Modell.

## Entscheidung

Der externe Vertrag wird als `mcpmcp.connector.v1` versioniert. Ein Connector-Paket enthält ein
signiertes Manifest, ein connector-spezifisches JSON-Schema und genau einen isolierten Entry Point.
Das Manifest deklariert:

- Connector-ID, Version, Contract-Version und Herausgeber;
- unterstützte Capability-Arten und Features;
- benötigte Netzwerkziele, Dateisystemwurzeln, Environment- und Secret-Capabilities;
- Discovery-, Health-, Readiness-, Cancellation-, Task-, Event- und Stream-Unterstützung;
- Ressourcenanforderungen und unterstützte Betriebssysteme/Architekturen;
- Paket- und Modulhash.

Der Lifecycle lautet `handshake → validate-config → start → discover → ready → invoke/cancel →
drain → stop`. Jede Antwort trägt Correlation-ID und eine normierte Fehlerhülle. Der Host prüft beim
Handshake Major-Version und Capability-Flags; unbekannte Pflichtfeatures führen zu einem klaren
Kompatibilitätsfehler.

Vertrauensstufen:

1. **Core/in-process:** nur mit dem Produkt ausgelieferter, gleich versionierter Code.
2. **Official/isolated:** signiertes offizielles Paket, bevorzugt WASI Component.
3. **Third-party/isolated:** erlaubter Herausgeber und Hash; niemals direkter Datenbankzugriff.
4. **Community/untrusted:** explizite Admin-Freigabe, deny-by-default Capabilities.

Install, Update und Rollback erfolgen transaktional: Paket prüfen, parallel in Quarantäne
validieren, Health/Discovery testen, atomar aktivieren, vorherige Version bis zum erfolgreichen
Drain behalten. Connector-Konfiguration und Secrets bleiben im Gateway; Connectoren erhalten nur
kurzlebige, auditierte Grants.

## Prozessgrenze

WASI Components sind der bevorzugte Pluginpfad. Native Connectoren laufen in einem gehärteten
Container oder einem dedizierten Worker-Prozess mit lokalem, authentisiertem IPC. Drittanbieter-Code
läuft nicht in der Gateway-AppDomain.

## Governance

Discovery darf Metadaten liefern, aber keine Invocation ausführen. Invocation wird ausschließlich
vom Core nach RBAC, Validierung, Risk Classification, Guardrails, Approval und Limits ausgelöst.
Connectoren sehen weder interne EF-Kontexte noch Approval-/RBAC-Stores. Auditereignisse werden vom
Core aus beobachteten Requests und Results erzeugt, nicht vom Connector als alleiniger Quelle.

## Konsequenzen

Das Interface ist absichtlich schmaler als ein allgemeines Pluginframework. UI-Erweiterungen und
beliebiger Hostcode gehören nicht zu v1. So bleiben Sicherheitsprüfung und langfristige Wartung für
einen Solobetreiber realistisch.
