# ADR-0016: Versionierter Connector-/Plugin-Vertrag

- **Status:** **Akzeptiert.** Laufzeitvertrag am 2026-07-25 am WASI-Host umgesetzt (Plan 0003,
  WP6.1), Paketteil am 2026-07-27.
- **Datum:** 2026-07-24

> **Umsetzungsstand 2026-07-27.** Beide Teile stehen.
>
> **Laufzeit** (WASI-Host, ADR-0020, erster Connector nach diesem Vertrag): versionierter Handshake
> mit **Capability-Flags**, Correlation-Id auf jeder Antwort, normierte Fehlerhülle
> (`code` + `message`), Discovery mit Schema-Normalisierung, Cancellation mit Bestätigung,
> Readiness getrennt von Liveness, Lifecycle `handshake → load → discover → ready → invoke/cancel →
> drain → stop`.
>
> **Pakete:** `.mcpkg` als ZIP mit signiertem Manifest, Installation über Quarantäne mit echter
> Probe, atomarer Aktivierung, Update und Rollback, dazu die vier Vertrauensstufen mit Zustimmung
> je Zugriff. Ein Upstream verweist über `Wasi.PackageId` auf ein Paket statt auf Dateipfade — ein
> Update wechselt damit die Dateien, ohne dass jemand die Konfiguration anfasst.
>
> **Bewusst noch nicht:** Nur der WASI-Transport ist paketierbar. Ein Paket mit einem anderen
> Transport wird abgewiesen statt halb unterstützt — für native Connectoren fehlt die
> Prozessgrenze aus dem Abschnitt unten, und ohne sie wäre „installierbar" ein leeres Versprechen.
> Es gibt außerdem keine Bezugsquelle: Pakete werden hochgeladen, nicht aus einem Verzeichnisdienst
> geholt. Ein Registry-Client ohne Betreiber wäre Code ohne Gegenstelle.

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

## Paketformat und Prüfreihenfolge (umgesetzt)

Ein `.mcpkg` ist ein ZIP mit `manifest.json`, der detached Ed25519-Signatur `manifest.sig` und den
Nutzdateien. **Signiert wird das Manifest**, und das Manifest nennt den SHA-256 jeder Nutzdatei.
Damit deckt eine Signatur das ganze Paket ab, ohne dass das Archivformat selbst signiert werden
müsste: Archive sind formbar (Reihenfolge, Kommentare, doppelte Einträge), eine Hash-Liste ist es
nicht. Ein Eintrag, den das Manifest nicht nennt, ist unsigniert und führt zur Ablehnung — sonst
reisten unsignierte Dateien im selben Archiv mit.

Die Reihenfolge ist Teil der Entscheidung: **Archivgrenzen → Signatur → Manifest → Hashes.** Wer das
Manifest vor der Signatur auswertet, trifft Entscheidungen auf Daten, die noch niemand bestätigt hat.

Zwei Signaturen mit zwei Prüfern, und das ist Absicht: Das **Manifest** prüft das Gateway gegen den
Trust-Store; die **Component-Bytes** prüft der WASI-Host unmittelbar vor dem Instanziieren
(ADR-0020). Keine der beiden Prüfungen ersetzt die andere.

Die Zustimmung zu Zugriffen bezieht sich auf genau die Einträge des Manifests. Eine pauschale
„ja zu allem"-Angabe gibt es nicht — sie machte die Liste im Manifest bedeutungslos.

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
