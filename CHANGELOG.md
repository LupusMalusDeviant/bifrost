# Changelog

Alle nennenswerten Änderungen an B.I.F.R.O.S.T. Format nach
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung nach
[Semantic Versioning](https://semver.org/lang/de/) — mit der Einschränkung, die eine `0.x`-Linie
ausmacht: **Breaking Changes können in einer Minor-Version auftreten**; sie stehen dann hier unter
„Geändert" mit dem Wort *Breaking* und einem Upgrade-Hinweis.

Die Einträge nennen, was ein Betreiber merkt — nicht jeden Commit. Was ein Release *nicht* belegt,
steht ausdrücklich dabei: Diese Datei ist auch der Ort, an dem offene Nachweise sichtbar bleiben.

## [Unveröffentlicht]

### Hinzugefügt

- **Sicherung und Wiederherstellung** für SQLite-Instanzen: ein ZIP mit vorangestelltem Manifest,
  optional mit Passphrase verschlüsselt, prüfbar ohne Restore. Die Datenbank wird über die
  Online-Backup-API gelesen, nicht kopiert — bei aktivem WAL ist eine Dateikopie eine Sicherung,
  der man nicht ansieht, dass sie älter ist als gedacht. Das Zurückspielen läuft über ein
  Staging-Verzeichnis und sichert vor jedem Überschreiben den Altzustand.
  **Für PostgreSQL gibt es das noch nicht**; der Weg lehnt mit Meldung ab, statt still etwas
  anderes zu tun.
- **Migrationsschutz beim Start:** Genau eine Instanz migriert (Advisory Lock bzw. Dateilock), der
  Zustand steht in einer Journaltabelle statt in einem Log. Ein abgebrochener Lauf wird beim
  nächsten Start erkannt und verweigert den Schreibbetrieb mit Recovery-Hinweis, statt zu
  reparieren. Ein neueres, unbekanntes Schema wird abgelehnt, nicht heruntergestuft.
- **Diagnosedienst** mit 17 stabilen Codes (`BFR-CFG-*`, `BFR-DB-*`, `BFR-KEY-*`, `BFR-NET-*`,
  `BFR-RT-*`, `BFR-UP-*`) — darunter der Fall, der im Betrieb am teuersten war: falsches Volume,
  leere Datenbank, Meldung „bereit". *Noch ohne Befehl:* Die Anbindung an CLI und Oberfläche
  kommt in einem Folgeschritt.
- **Konfigurationsexport und -import**, versioniert. Der Standardexport enthält **keine
  Secretwerte**, sondern Referenzen, die allein aus dem Ort abgeleitet sind — wer „die
  Konfiguration" in ein Git-Repository legt, legt dort keine Zugangsdaten ab. Was nicht
  exportierbar ist, steht als solches im Dokument.
- Rückfrage vor zerstörenden Aktionen in der Oberfläche (Rolle, Identität, API-Key, Upstream,
  UI-Nutzer, Webhook, Profil, Guardrail). Zweistufig in der Zeile des Objekts, ohne Dialogfenster.
- `CHANGELOG.md` (diese Datei) und ein Status-Dokument für die Produktreife unter
  `docs/plans/product-readiness-status.md`.

### Geändert

- Tabellen: Beschreibungsspalten sind auf drei Zeilen begrenzt, die Aktionsspalte bleibt am rechten
  Rand stehen. Vorher schob ein langer Beschreibungstext die Knöpfe „Bearbeiten"/„Versionen" aus
  dem sichtbaren Bereich.
- Die Inhaltsbreite ist nicht mehr auf 1180 px festgenagelt; die Zeilenlänge hält jetzt der Absatz
  selbst (82 Zeichen), Tabellen dürfen die Breite nutzen.

### Behoben

- **Ein Zugangsdatum stand im Klartext in Oberfläche und API.** Die Ausgabemaskierung für
  Upstream-Konfigurationen ließ das Credential eines OpenRPC-Servers unmaskiert durch.
- **Bearbeiten konnte einen Upstream stillschweigend zerlegen.** In die Gegenrichtung fehlte die
  Übernahme bestehender Werte für WASI-Secrets und das OpenRPC-Credential: Wer einen solchen
  Upstream in der Oberfläche speicherte, schrieb die Maske `***` als echten Wert. Der Upstream lief
  bis zum nächsten Neustart weiter und scheiterte dann an einem Passwort aus drei Sternen.
- `cargo fmt --check` war rot: Die Umbenennung auf `bifrost_wasi_component_spike` änderte die
  alphabetische Importreihenfolge in `spikes/wasi-component-runtime/src/main.rs`.
- Die Größenangabe des Images im README (`< 300 MB`) widersprach dem geprüften CI-Gate (350 MB) und
  der gemessenen Größe (315 MB).
- Die Versionstabelle in `SECURITY.md` kannte `v0.11.0` nicht.

## [0.11.0] — 2026-07-31

Erste Fassung unter dem Namen **B.I.F.R.O.S.T** (vorher MCP-MCP), zusammen mit dem Umstieg auf die
MCP-Spec-Revision `2026-07-28`.

### Geändert

- **Breaking (Protokoll):** Der sessionlose Kern ist Vorgabe (SEP-2567). Laufende MCP-Sitzungen
  überstehen das Update **nicht** — ein Client mit `Mcp-Session-Id` wird abgewiesen und muss neu
  verbinden. Der Sitzungsbetrieb bleibt über `BIFROST_MCP_STATELESS=0` erreichbar; dort werden
  allerdings *alle* Clients auf `2025-11-25` zurückgehandelt ([ADR-0023](docs/adr/0023-stateless-kern-und-mrtr.md)).
- **Breaking (Namen):** Projekte, Namespaces, Assemblies, Umgebungsvariablen (`MCPMCP_*` →
  `BIFROST_*`), Datenbankdatei, MCP-Serverkennung, Docker-Image und Repository heißen neu.
- Die Freigabe-Rückfrage läuft über MRTR statt server-initiierter Elicitation.
- Listen tragen Cache-Hinweise (`ttlMs`, `cacheScope: private`) statt `tools/list_changed`.
- Der Herzschlag zu Upstreams nutzt `server/discover` statt `ping` (das es nicht mehr gibt).
- Neues Designsystem der Oberfläche.

### Migration

- Alt benannte Umgebungsvariablen werden übernommen und einmal beim Start gemeldet.
- Eine vorhandene `mcpmcp.db` wird weiterbenutzt; es wird **keine** neue Datei angelegt.
- Der DataProtection-Anwendungsname und die drei Verschlüsselungszwecke behalten bewusst ihre alten
  Werte — ein neuer Name würde jeden gespeicherten Geheimtext unlesbar machen.
- Das UI-Cookie heißt neu: einmal neu anmelden.
- Beim Deployment gilt: **Volume- und Service-Namen nicht mitumbenennen.** Der Volume-Name trägt den
  Compose-Projektnamen als Präfix; ein umbenanntes Volume ist ein leeres Volume.

### Nachweise

- 783 .NET-Tests, 110 Rust-Tests grün.
- NFR-01/02 auf dem neuen Protokoll nachgemessen (Median aus fünf Läufen):
  `tools/call` p95 = 9,3 ms, `tools/list` p95 = 14,2 ms, 0 Fehler.
- **Nicht belegt:** Durchsatz (der Harness misst einen 0,1-Sekunden-Stoß), Verhalten über echtes
  Netz/TLS, sowie sessionlos gegen Sitzungsbetrieb im Vergleich.

## Ältere Versionen

Für `v0.5.0` bis `v0.10.0` existieren Git-Tags, aber keine gepflegten Changelog-Einträge; ein Teil
davon hat auch keinen GitHub-Release-Eintrag. Diese Datei beginnt bewusst bei `0.11.0` und wird
nicht rückwirkend erfunden — was dort passiert ist, steht in der Commit-Historie und in den ADRs.
