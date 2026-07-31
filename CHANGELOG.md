# Changelog

Alle nennenswerten Änderungen an B.I.F.R.O.S.T. Format nach
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung nach
[Semantic Versioning](https://semver.org/lang/de/) — mit der Einschränkung, die eine `0.x`-Linie
ausmacht: **Breaking Changes können in einer Minor-Version auftreten**; sie stehen dann hier unter
„Geändert" mit dem Wort *Breaking* und einem Upgrade-Hinweis.

Die Einträge nennen, was ein Betreiber merkt — nicht jeden Commit. Was ein Release *nicht* belegt,
steht ausdrücklich dabei: Diese Datei ist auch der Ort, an dem offene Nachweise sichtbar bleiben.

## [Unveröffentlicht]

## [0.12.0] — 2026-08-01

Zwei Meilensteine: **Wiederherstellbarkeit** (sichern, zurückspielen, migrieren, diagnostizieren)
und **sichere Vorgaben** (Isolation, Key-Ring-Schutz, Erstzugang ohne Log-Credentials).

**Was diese Version nicht belegt:** Es ist der **erste Lauf dieser Releasepipeline überhaupt**. Sie
wurde in M1 gebaut und war seitdem nie gelaufen — in dieser Zeit sind drei Fehler darin gefunden
worden, die nur ein echter Lauf gezeigt hätte (ein umbenanntes Volume, ein Verweis auf eine
nicht existierende Ignoredatei, und ein Schwachstellen-Gate, das mit Exit 0 endete). Alle drei sind
behoben; ob weitere darin stecken, sagt erst dieser Lauf.


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
- **`bifrost doctor`** mit stabilen, maschinenlesbaren Codes (`BFR-CFG-*`, `BFR-DB-*`, `BFR-KEY-*`,
  `BFR-NET-*`, `BFR-RT-*`, `BFR-UP-*`) — darunter der Fall, der im Betrieb am teuersten war:
  falsches Volume, leere Datenbank, Meldung „bereit". Ein Check, der nicht laufen kann, meldet das
  mit Begründung und besteht nicht still.
- **Befehle für den Betrieb:** `backup create` / `backup verify`, `restore`, `doctor`,
  `config export` / `config import`, `db unblock` — alle mit einheitlichen Exit-Codes
  (0 Erfolg · 2 Bedienfehler · 3 Warnung · 4 Fehler · 5 Archiv ungültig · 6 Ziel nicht leer), dazu
  eine Betriebsseite in der Oberfläche und Endpunkte unter `/api/v1/operations/`.
  Eine Passphrase wird **nicht** als Argument angenommen — sie stünde sonst in der Prozessliste und
  in der Shell-Historie.
- **`bifrost-server --db-unblock`** löst den Riegel aus `BFR-DB-0101` auch dann, wenn der Gateway
  wegen ebendieses Riegels gar nicht erst startet. Vorher hätte ein Betreiber eine Datenbankzeile
  von Hand löschen müssen.
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

- **Native Programme laufen isoliert.** Neu angelegte stdio- und CLI-Upstreams führen ihr Programm
  in einem Container aus: non-root, read-only Wurzeldateisystem, keine Capabilities, kein Netzwerk
  ohne ausdrückliche Zielliste, Grenzen für Speicher, CPU, Prozesse, Ausgabe und Zeit. Bestehende
  Upstreams ändern ihr Verhalten **nicht**.
  Fehlt die Container-Runtime, kommt der Upstream nicht hoch — es gibt keinen Rückfall auf den
  Host. Ein Ausweichen wäre eine stille Herabstufung genau der Eigenschaft, wegen der jemand den
  Container gewählt hat.
- **Erstzugang über ein kurzlebiges Setup-Token statt über das Log.** Der erste Start schreibt kein
  Adminpasswort und keinen API-Key mehr ins Anwendungslog, sondern stellt ein einmalig
  einlösbares Token in eine Datei aus, die nur das Dienstkonto lesen darf. Benutzername und
  Passwort wählt der Betreiber selbst unter `/setup`.
  **Breaking für die Einrichtung:** `docker compose logs bifrost` zeigt keine Zugangsdaten mehr —
  der Weg steht im README. Bestehende Installationen sind nicht betroffen und behalten ihre Admins.
- **Der Key-Ring lässt sich schützen, und sein Verlust wird erkannt.** Zertifikat oder Datei-Secret
  einrichtbar, Passwort über `…_FILE` zuführbar (nicht mehr nur über die Umgebung). Fehlt
  Schlüsselmaterial, das vorhanden sein müsste, bricht der Start ab, statt einen leeren Ring
  anzulegen und sich als bereit zu melden.
- **Hostausführung ist eine Entscheidung, kein Vorgabezustand.** Neue Instanzen verbieten sie;
  bestehende laufen weiter, aber die Übernahme wird geschrieben, auditiert und im Diagnosebericht
  namentlich ausgewiesen (ADR-0025).

### Behoben

- **MCP über HTTP war der einzige Transport ohne Zielprüfung.** Der Endpunkt ging ungeprüft in den
  Transport, während OpenAPI, OpenRPC und der OAuth-Issuer private Adressen abweisen. Ebenso sondete
  der OAuth-Verbindungsendpunkt gegen die genannte Adresse, **bevor** irgendetwas sie geprüft hatte.
  Neu angelegte Konfigurationen weisen private Ziele jetzt ab; bestehende bleiben unverändert, bis
  jemand den Schalter setzt.
- **Ein Archiv aus einer neueren Version wurde eingespielt, statt abgelehnt zu werden.** Das
  Rückwärts-Tor verglich eine Versionsangabe, die das Archiv über sich selbst macht und die für
  jedes Archiv gleich war. Geprüft wird jetzt der Migrationsstand: Kennt diese Installation ihn
  nicht, stammt das Archiv aus einer neueren Version. Gefunden hat das die Upgrade-Matrix, indem
  sie gegen die Zusage getestet hat statt für sie.
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
