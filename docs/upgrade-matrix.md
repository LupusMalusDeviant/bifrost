# Upgrade-Kompatibilitätsmatrix (WP2.6)

**Stand:** 2026-07-31 (mit Nachträgen vom 2026-08-01) · **Harness:**
`tests/Bifrost.Upgrade.Tests/**` · **Grundlage:**
[ADR-0024](adr/0024-backup-restore-und-migrationssicherheit.md) E6/E7,
[M2-Vertrag](plans/m2-recoverability-contract.md) §7

> **Diese Seite ist die maßgebliche Fassung.** Eine englische Zusammenfassung der betrieblich
> wichtigen Grenzen steht in [`docs/en/operations.md`](en/operations.md#upgrades); sie ist
> **abgeleitet**, und **bei Widerspruch gilt diese Seite**. Sprachregel:
> [`docs/i18n.md`](i18n.md).

Ein Upgrade darf keine Daten verlieren und keine Instanz in einen Zustand bringen, aus dem es keinen
Weg zurück gibt. Dieses Dokument sagt, **was der Harness davon prüft** — und, ausführlicher, **was er
nicht prüft**. Der zweite Teil ist der wichtigere: Eine grüne Suite, deren Grenzen niemand aufschreibt,
liest sich wie eine Zusage, die sie nicht ist.

Ausführen (nur dieses Projekt):

```
dotnet test tests/Bifrost.Upgrade.Tests -c Release
BIFROST_REQUIRE_POSTGRES=1 dotnet test tests/Bifrost.Upgrade.Tests -c Release   # PostgreSQL Pflicht
```

Ohne erreichbaren Docker-Daemon werden die PostgreSQL-Felder **übersprungen und als übersprungen
gemeldet**, nicht als bestanden. Mit `BIFROST_REQUIRE_POSTGRES=1` ist ein nicht startbarer Container
ein Fehlschlag.

---

## 1. Woraus die Fixtures entstehen

Ein Fixturestand wird erzeugt, indem eine leere Datenbank **gezielt bis zu einer bestimmten
EF-Migration** hochgezogen wird (`IMigrator.MigrateAsync(targetMigration)`). Es gibt kein
handgeschriebenes SQL, das einen alten Stand nachahmt — ein nachgeahmtes Schema würde genau die
Abweichung nicht zeigen, wegen der man den Test schreibt.

Die Liste der Stände kommt aus der Migrations-Assembly, nicht aus einer gepflegten Konstante. Eine
neue Migration erweitert die Matrix damit von selbst. Heute sind es **15 veröffentlichte
Migrationsstände** je Provider (`InitialCreate` … `ApprovalDefaultEnforcement`).

Der Bestand, der das Upgrade überleben muss, wird durch die **echten Stores** geschrieben
(`EfUpstreamConfigStore`, `ApiKeyService`, `UiUserService`, `UpstreamOAuthTokenStore`), nicht per
INSERT von Hand. Nur so entsteht Geheimtext mit demselben DataProtection-Purpose, derselben
Serialisierung und demselben Schlüsselring wie im Betrieb — ein von Hand eingefügtes BLOB würde
dieselbe Zeile füllen und trotzdem nichts belegen.

Nach dem Upgrade wird zweierlei geprüft:

- **vollständig** — die Zeilen sind noch da und tragen dieselben Werte (Identität, Rolle, Profil,
  beide Versionen des Config-Verlaufs);
- **lesbar** — der Geheimtext lässt sich entschlüsseln, der API-Key validiert weiterhin, der
  UI-Login funktioniert weiterhin. Ein Upgrade, das Geheimtext unlesbar macht, fällt sonst nicht
  auf: Die Tabelle sieht danach unverändert aus.

---

## 2. Die Matrix

| # | Von-Stand | Nach-Stand | Provider | Ergebnis | Test |
|---|---|---|---|---|---|
| 1 | leere Datenbank | heutiger Stand | SQLite | **grün** | `SqliteUpgradeMatrixTests.Empty_database_reaches_the_current_state` |
| 2 | leere Datenbank | heutiger Stand | PostgreSQL | **grün** | `PostgresUpgradeMatrixTests.Empty_database_reaches_the_current_state` |
| 3 | jeder der 15 veröffentlichten Migrationsstände, **mit Bestand inkl. Geheimtext** | heutiger Stand | SQLite | **grün** (15 Felder) | `SqliteUpgradeMatrixTests.Published_migration_state_upgrades_without_losing_data` |
| 4 | jeder der 15 veröffentlichten Migrationsstände, **mit Bestand inkl. Geheimtext** | heutiger Stand | PostgreSQL | **grün** (15 Felder) | `PostgresUpgradeMatrixTests.Published_migration_state_upgrades_without_losing_data` |
| 5 | v1.0-Schema **ohne** `__EFMigrationsHistory`, mit Geheimtext | heutiger Stand | SQLite | **grün** (Baseline gestempelt) | `SqliteUpgradeMatrixTests.Legacy_schema_without_history_is_baselined_and_keeps_its_ciphertext` |
| 6 | Backup einer **älteren** Produktversion (0.10.0), Schema einen Stand zurück, verschlüsselt | Restore in 0.11.0, danach Migration | SQLite | **grün** (ADR-0024 E6 vorwärts) | `BackupRestoreUpgradeTests.Backup_of_an_older_version_restores_and_migrates_afterwards` |
| 7 | Backup einer **neueren** Version (Mindestversion 99.0.0) | Restore in 0.11.0 | SQLite | **grün — wird abgelehnt**, Zielverzeichnis bleibt leer | `BackupRestoreUpgradeTests.Backup_of_a_newer_version_is_refused_instead_of_attempted` |
| 8 | Archiv mit **neuerem Schema**, aber heutiger Mindestangabe | Restore läuft durch, **Start verweigert** `BFR-DB-0102` | SQLite | **grün — und ein Befund**, siehe §4.1 | `BackupRestoreUpgradeTests.An_archive_with_a_newer_schema_passes_the_version_gate_and_is_stopped_at_the_start` |
| 9 | Backup einer Instanz **mit Bestand inkl. Geheimtext** | Restore auf leeres Ziel und mit `--replace` | PostgreSQL | **prüfbar, Nachweis steht aus** (8 Felder, siehe §2.1) | `PostgresBackupRestoreTests.*` |
| 10 | Upgrade über drei **veröffentlichte Releases** | – | beide | **nicht prüfbar** — es gibt die Artefakte nicht, siehe §4.2 | – |

Gesamt in `tests/Bifrost.Upgrade.Tests`: **50 Testfälle**. Ohne Docker und ohne `pg_dump` laufen
davon **26** (SQLite-Matrix und Archivpfad); die übrigen **24** melden sich als übersprungen
(16 PostgreSQL-Matrix, 8 PostgreSQL-Backup). Mit `BIFROST_REQUIRE_POSTGRES=1` gibt es diese
Unterscheidung nicht — dort ist eine fehlende Voraussetzung ein Fehlschlag.

### 2.1 Feld 9 im Einzelnen (neu, ADR-0024 E2 umgesetzt)

Feld 9 stand bis hierher auf „nicht prüfbar (nicht implementiert)". Mit `pg_dump`/`pg_restore` ist
es herstellbar. Geprüft wird gegen einen **echten** PostgreSQL-Server (Testcontainers) und mit den
**echten** Werkzeugen.

> **Stand: geschrieben, aber noch nicht einmal grün gelaufen.** Der Rechner, auf dem dieses Paket
> entstanden ist, hatte weder einen erreichbaren Docker-Daemon noch ein installiertes `pg_dump`;
> alle acht Felder unten wurden **übersprungen**, nicht bestanden. Das steht hier, weil ein
> Matrixfeld, das „grün" behauptet ohne je gelaufen zu sein, genau der Fehler ist, gegen den dieses
> Dokument geschrieben wurde. Der erste Lauf mit `BIFROST_REQUIRE_POSTGRES=1` (CI, Linux) ist der
> Nachweis — bis dahin gilt Feld 9 als **offen**.

| Was | Test |
|---|---|
| Sicherung und Wiederherstellung, Bestand danach vollständig **und lesbar** (Geheimtext) | `Backup_and_restore_keep_the_stored_ciphertext_readable` |
| Gegenprobe: **ohne** Key-Ring kommen die Zeilen zurück und ihr Inhalt nicht | `Without_the_key_ring_the_restored_rows_are_unreadable` |
| Die Nutzlast ist wirklich ein `pg_dump` im custom-Format (Kennung `PGDMP`) | `The_payload_is_a_pg_dump_in_the_documented_custom_format` |
| Abbruch mitten im Schreiben — nichts bleibt liegen (E4) | `A_cancelled_backup_leaves_no_archive_behind` |
| Verschlüsseltes Archiv, **falsche Passphrase** → Zielinstanz unverändert, keine Vorsicherung | `A_wrong_passphrase_leaves_the_target_untouched` |
| `--replace` über eine bestehende Instanz, Vorsicherung entsteht **und ist ein gültiges Archiv** (E5) | `Replace_overwrites_an_existing_instance_and_keeps_a_valid_pre_backup` |
| Archiv aus einer **neueren** Version → abgelehnt, Zieldatenbank bleibt leer (E6) | `An_archive_from_a_newer_version_is_refused_instead_of_attempted` |
| Archiv mit **unbekanntem Migrationsstand** → abgelehnt (E6, zweite Hälfte) | `An_archive_with_an_unknown_migration_is_refused` |

Dazu, **ohne Container und damit in jedem Lauf**:

| Was | Test |
|---|---|
| Fehlendes `pg_dump` → Meldung mit Ursache und Abhilfe, kein Ersatzweg, kein halbes Archiv | `BackupCreationTests.A_missing_pg_dump_refuses_loudly_instead_of_exporting_rows` |
| Gegenprobe: ohne Datenbankbereich stört ein fehlendes `pg_dump` nicht | `BackupCreationTests.Without_the_database_section_a_missing_pg_dump_is_no_obstacle` |
| E7-Verdrahtung: `Always` genau dann, wenn die Werkzeuge da sind | `PostgresPreMigrationBackupWiringTests.*` (2 Fälle) |
| Ohne `pg_dump` meldet der Vor-Migrationshaken ein **Nein mit Begründung** statt eines Ersatzwegs | `PostgresPreMigrationBackupTests.Without_pg_dump_the_hook_says_no_instead_of_inventing_a_backup` |

Und einer, der wieder Server **und** Werkzeuge braucht:

| Was | Test |
|---|---|
| Vor-Migrationssicherung entsteht auf PostgreSQL und ist ein gültiges Archiv (E7) | `PostgresPreMigrationBackupTests.A_pre_migration_backup_is_created_and_is_a_valid_archive` |

> **Die Serverversion des Testcontainers wird aus dem vorhandenen `pg_dump` abgeleitet** und steht
> nicht fest. Grund: `pg_dump` weigert sich, einen *neueren* Server zu sichern. Ein fest verdrahtetes
> `postgres:17-alpine` wäre auf jedem Rechner mit älterem Client rot — aus einem Grund, der mit dem
> Prüfling nichts zu tun hat.

### Legende

- **grün** — geprüft und bestanden.
- **übersprungen** — die Voraussetzung fehlt (kein Docker-Daemon **oder** kein `pg_dump` auf dem
  Rechner). Wird als übersprungen gemeldet, nie als bestanden. Mit `BIFROST_REQUIRE_POSTGRES=1` ist
  beides ein Fehlschlag.
- **nicht prüfbar** — der Fall lässt sich mit dem vorhandenen Code oder den vorhandenen Artefakten
  nicht herstellen. Kein Test tut so, als ob.

---

## 3. Die Gegenproben

Ein Test, der nichts prüft, ist auch grün. Deshalb enthält die Suite Proben, die zeigen, dass die
Zusagen tragen:

| Gegenprobe | Zeigt |
|---|---|
| `Without_the_upgrade_the_fixture_still_reports_pending_migrations` | Das Fixture steht wirklich auf einem älteren Stand — „nach dem Upgrade steht nichts mehr aus" ist keine Tautologie. |
| `Damaged_ciphertext_makes_the_very_same_check_fail` | Ein einziges gekipptes Bit im Config-Blob macht **dieselbe** Nachprüfung rot, die im Matrixtest grün ist. |
| `A_lost_row_makes_the_very_same_check_fail` | Eine verschwundene Version des Config-Verlaufs macht dieselbe Nachprüfung rot. |
| `Without_the_key_ring_the_restored_rows_are_unreadable` | Ohne Schlüsselring im Archiv kommen die Zeilen zurück und ihr Inhalt nicht — die Lesbarkeitsprüfung hängt wirklich am Schlüsselring (ADR-0024 E3). |
| `The_same_archive_with_a_reachable_minimum_version_is_applied` | **Dasselbe** Archiv mit erreichbarer Mindestversion wird angewendet. Die Ablehnung in Feld 7 kam also aus dem Versionsvergleich und nicht aus irgendetwas anderem am Archiv. |
| `A_used_restore_handle_cannot_be_replayed` | Ein verbrauchtes Plan-Handle lässt sich nicht erneut anwenden. |

Zusätzlich wurde einmalig, außerhalb des Repositorys, mutiert: Wird die Nachprüfung gegen einen
**fremden Schlüsselring** gefahren — das Bild eines Upgrades, das den DataProtection-Purpose
verändert —, schlagen **alle 15** SQLite-Matrixfelder mit
`CryptographicException: The key … was not found in the key ring` fehl. Damit ist belegt, dass die
Lesbarkeitszusage nicht nebenbei mitläuft.

---

## 4. Was NICHT geprüft wird — und warum

### 4.1 Das Rückwärts-Tor aus E6 schützt nicht vor einem neueren *Schema*

**Befund, nicht Wunsch.** `RestoreService` vergleicht die **selbst behauptete**
`minimumRestoreVersion` des Manifests mit der eigenen Produktversion.
`BackupOptions.MinimumRestoreVersion` ist heute die Konstante `0.11.0`
(`BackupLayout.DefaultMinimumRestoreVersion`), die keine Version anhebt. Ein Archiv aus einer
späteren Version trägt darum **dieselbe** Mindestangabe und läuft an dem Tor vorbei, obwohl sein
Schema neuer ist.

Aufgehalten wird es erst eine Stufe später: Der Start erkennt Migrationen, die er nicht kennt, und
verweigert mit `BFR-DB-0102` (Feld 8). Der Schaden ist damit begrenzt — aber nicht dort, wo
ADR-0024 E6 ihn begrenzt sehen wollte: Das Archiv ist zu diesem Zeitpunkt bereits **eingespielt**,
die Zielinstanz steht mit einer fremden Datenbank da und kommt nicht mehr hoch.

Die Behebung liegt in `src/**` (Mindestversion beim Sichern aus dem tatsächlichen Migrationsstand
ableiten, oder den Migrationsstand im Manifest gegen die bekannten Migrationen prüfen, bevor
entpackt wird) und ist deshalb **nicht** Teil dieses Pakets. Der Test hält den heutigen Zustand
fest, statt ihn zu verschweigen.

### 4.2 Ein Upgrade über drei veröffentlichte Releases

**Nicht prüfbar, und zwar grundsätzlich mit diesem Harness.** Es gibt die Artefakte nicht: M1 ist
nicht abgenommen, ein Releaselauf hat nicht stattgefunden. Es existiert kein Binary einer früheren
Version, das man installieren, befüllen und dann ablösen könnte.

> **Nachtrag 2026-08-01:** M1 ist abgenommen, `v0.12.0` ist veröffentlicht — es gibt jetzt **ein**
> Releaseartefakt. Für dieses Feld ändert das noch nichts: Ein Upgrade *über* Releases braucht
> mindestens zwei, und der Absatz darunter gilt unverändert weiter — ein Migrationsstand ist nicht
> dasselbe wie ein Release. Ab dem nächsten Release ist das Feld erstmals herstellbar.

Wichtiger als die fehlenden Artefakte ist der Unterschied dahinter: **Ein Migrationsstand ist nicht
dasselbe wie ein Release.** Ein Release ist Binary *plus* Migrationen *plus* Konfigurationsformat
*plus* Key-Ring-Format *plus* Paketlayout *plus* Manifestformat. Der Harness variiert allein die
Migrationen; alles andere ist immer der heutige Stand. Selbst wenn man drei Fixturestände
hintereinanderschaltete, wäre das kein Release-Upgrade, sondern dreimal derselbe Code auf drei
Schemaständen.

### 4.3 Die alten Daten wurden vom heutigen Code geschrieben

**Die schwerwiegendste bekannte Lücke des Harness.** Das Schema der Fixtures ist echt alt; der Code,
der sie befüllt, ist der heutige. Eine Regression, die im **Serialisierungsformat** eines früheren
Builds liegt — eine geänderte JSON-Form von `UpstreamServerConfig`, ein anderes Hashformat, ein
umbenanntes Feld im geschützten Blob —, wird hier **nicht** gefunden: Der Test schreibt sie nie in
der alten Form.

Diese Lücke schließt nur ein Fixture, das ein früherer Build erzeugt hat, also ein
Release-Artefakt — siehe §4.2. Bis dahin ist die Zusage dieses Harness enger, als sie klingt: Er
prüft, dass **Migrationen** den Bestand nicht beschädigen, nicht dass **Formatänderungen** es nicht
tun.

Zwei bekannte Stellen, an denen genau das kritisch wäre, sind heute nur durch **Kommentare**
gesichert, nicht durch Tests:

- `EfUpstreamConfigStore.ProtectionPurpose = "McpMcp.UpstreamConfig.v1"` und
  `UpstreamOAuthTokenStore.Purpose = "McpMcp.UpstreamOAuthToken.v1"`;
- `Program.cs`, `SetApplicationName("MCPMCP")`.

Alle drei gehen in die Schlüsselableitung ein. Eine Umbenennung wäre stiller Totalverlust jedes
gespeicherten Geheimtextes. **Kein Test im Repository nagelt diese drei Zeichenketten fest** —
geprüft wurde das für dieses Dokument. Ein solcher Test gehört in ein Paket, das `src/**` und die
bestehenden Testprojekte anfassen darf.

> **Nachtrag 2026-08-01:** Erledigt. Die Bezeichner stehen jetzt als Konstanten in
> `src/Bifrost.Persistence/CryptographicNames.cs` — inzwischen **vier**, mit
> `McpMcp.Webhook.Secret.v1` — und `CryptographicNamesTests` prüft sie gegen ihre Werte;
> `.github/CODEOWNERS` führt die Datei zusätzlich. **Die Lücke des Harness bleibt davon unberührt:**
> Der Test nagelt die Namen fest, er schreibt keine Daten in der alten Form. Der Absatz oben zur
> Formatregression gilt unverändert.

### 4.4 `AuditEvents` und `Assets` sind nicht Teil des Bestands

Beide Tabellen haben nach `InitialCreate` Spalten bekommen (`CorrelationId`, `CallerRoles`,
`References`, `RequiredTools`, `WhenToUse`, `SourcePackageId`, `SourcePackageVersion`). Das heutige
EF-Modell kann sie deshalb nicht gegen ein altes Schema schreiben — ein `INSERT` nennt Spalten, die
den alten Stand nicht kennen.

Folge: **Ein Datenverlust in genau diesen beiden Tabellen würde die Matrix nicht rot machen.** Der
Weg dahin wäre handgeschriebenes SQL je Fixturestand; das wurde bewusst nicht getan, weil es die
Zusage aus §1 („Fixtures aus echten Migrationen") an der Datenseite wieder aufweichte. Die Lücke
steht lieber hier.

Der Bestand deckt ab: `Identities`, `Roles`, `Profiles`, `ConfigVersions` (verschlüsselt),
`ApiKeys`, `UiUsers` — und `UpstreamOAuthTokens` (verschlüsselt), sobald der Fixturestand die
Tabelle kennt.

### 4.5 Backup und Restore für PostgreSQL — was Feld 9 *nicht* abdeckt

**Implementiert** (ADR-0024 E2, `pg_dump --format=custom` / `pg_restore --single-transaction`).
Die Lücke aus der ersten Fassung dieses Dokuments ist geschlossen; §2.1 zählt auf, was jetzt geprüft
wird. Was weiterhin **nicht** geprüft ist:

- **„Backup einer älteren Version → Restore in den heutigen Stand" auf PostgreSQL.** Feld 9 sichert
  und stellt auf dem *heutigen* Schemastand wieder her. Das SQLite-Gegenstück (Feld 6) fährt ein
  Archiv von einem älteren Stand ein und migriert danach; auf PostgreSQL fehlt dieses Feld noch. Der
  Weg dahin ist derselbe Harness — nur mit einem Fixturestand statt einer vollmigrierten Datenbank.
- **Ein `pg_restore`, der mittendrin abbricht.** `--single-transaction` sagt zu, dass die Datenbank
  dann unberührt bleibt; das ist eine Zusage von PostgreSQL, keine dieser Suite. Herbeigeführt wird
  der Abbruch nicht.
- **Ein Wechsel der PostgreSQL-Hauptversion zwischen Sicherung und Wiederherstellung.** Der Test
  fährt beides gegen denselben Container.
- **Ein Restore, dessen `pg_dump` älter ist als der Zielserver.** Die Werkzeugversion wird im Test
  bewusst an den Server angeglichen (§2.1); im Betrieb ist die Diskrepanz ein Fehlerfall, den die
  Meldung des Werkzeugs trägt und den kein Test hier nachstellt.

Ohne installiertes Clientpaket bleibt es auf einer Instanz beim alten Zustand: Der Aufruf lehnt mit
einer Meldung ab, und Sichern ist Betriebspflicht. Der Unterschied zu vorher ist, dass das jetzt
behebbar ist, statt auf eine Ausbaustufe zu warten.

### 4.6 Restore auf einem anderen Rechner oder unter einem anderen Benutzer

Der Schlüsselring liegt per `PersistKeysToFileSystem` auf der Platte. Ohne
`BIFROST_KEYRING_CERT_PATH` schützt ASP.NET Core ihn **plattformabhängig**: unter Windows per DPAPI
gebunden an den ausführenden Benutzer, unter Linux gar nicht (Klartext, mit Warnung).

Der Harness fährt Sicherung und Wiederherstellung im selben Prozess, auf demselben Rechner, unter
demselben Benutzer. **Nicht geprüft ist damit der eigentliche Umzugsfall:** ein Vollbackup, das auf
einem *anderen* Windows-Rechner oder unter einem *anderen* Konto zurückgespielt wird. Dort wäre der
mitgereiste Schlüsselring nicht entschlüsselbar, und das Ergebnis sähe aus wie Feld „ohne
Schlüsselring": Zeilen ja, Inhalt nein. Im Container-Betrieb (Linux, Klartext-Ring) tritt das nicht
auf.

### 4.7 Mehrknotenbetrieb, Abbruch, volle Platte

Der M2-Vertrag §7 nennt diese Grenze bereits, und sie gilt hier unverändert:

- **Echter Mehrknotenbetrieb** — zwei Instanzen auf getrennten Rechnern gegen dieselbe
  PostgreSQL-Datenbank — ist lokal nicht herstellbar. Zwei parallele Starts *im selben Prozess*
  prüft WP2.3 (`MigrationSafetyTests`, `MigrationSafetyPostgresTests`); die Kombination aus
  parallelem Start **und** Datenbestand prüft niemand.
- **Ein realer Abbruch mitten in der Migration** (Stromausfall, OOM-Kill) wird durch einen Failpoint
  *nachgestellt*, nicht herbeigeführt. Was ein halb geschriebener Plattenblock anrichtet, weiß diese
  Suite nicht.
- **Volle Platte** wird in `RestoreService` vorab geprüft, aber nicht im Ernstfall provoziert.

### 4.8 Der Rückweg: kein Downgrade, und auf PostgreSQL nur mit den Werkzeugen

`Down`-Migrationen werden weder gefahren noch geprüft. Der Rückweg aus einem missglückten Upgrade
ist das **Vor-Migrationsbackup** aus ADR-0024 E7, nicht ein Downgrade.

Verdrahtet ist es in `OperationsRegistration.AddBifrostOperations` (aufgerufen in `Program.cs`).
`PreMigrationBackupRequirement.Always` gilt

- bei **SQLite**, sobald eine Datei hinter der Verbindung steht;
- bei **PostgreSQL**, sobald `pg_dump` und `pg_restore` erreichbar sind — geprüft **einmal beim
  Zusammenbau**, mit Dateisystemzugriffen und ohne Prozessstart.

Fehlen die Werkzeuge, bleibt es bei `WhenAvailable`: Der Start warnt und migriert. `Always` wäre
dort kein Schutz, sondern ein **Startverbot** — der Server käme nach einem Upgrade nicht mehr hoch,
aus einem Grund, der mit seinen Daten nichts zu tun hat.

Damit gilt für die Matrix: Auf einer PostgreSQL-Instanz **mit** Clientpaket hat ein Upgrade den
Rückweg, den ADR-0024 E7 zusagt; **ohne** läuft es weiterhin ohne. Die Unterscheidung ist geprüft
(`PostgresPreMigrationBackupWiringTests`), und dass die Sicherung dann wirklich entsteht und ein
gültiges Archiv ist, ebenfalls (`PostgresPreMigrationBackupTests`).

> **Nachtrag:** Die frühere Feststellung „kein Test prüft die Verdrahtung selbst" ist damit für die
> DI-Zusammensetzung erledigt. **Nicht** erledigt ist der zweite Teil: dass ein *hochfahrender
> Server* den Backuphaken wirklich gerufen bekommt. Dass der Haken gerufen *wird*, wenn er da ist,
> prüft WP2.3 (`MigrationSafetyTests.Pre_migration_backup_hook_is_called_before_the_migration`); der
> Weg über den echten Serverstart fehlt weiterhin.

### 4.9 Datenmenge und Laufzeit

Die Fixtures tragen eine einstellige Zahl von Zeilen. Ein Upgrade, das erst bei zehn Millionen
Auditzeilen in ein Sperren- oder Zeitproblem läuft, fällt hier nicht auf. Die Matrix sagt etwas über
**Korrektheit**, nichts über **Dauer**.

---

## 5. Was sich ändern muss, damit Felder dazukommen

| Feld | Voraussetzung |
|---|---|
| Upgrade über veröffentlichte Releases | Abgenommenes M1 und ein Releaselauf; danach Fixtures aus echten Artefakten (§4.2, §4.3) |
| ~~Backup/Restore auf PostgreSQL~~ | **erledigt** — `pg_dump`/`pg_restore` in `PostgresBackup` (ADR-0024 E2), siehe §2.1 |
| „Backup einer älteren Version" auf PostgreSQL | Ein Fixturestand statt einer vollmigrierten Datenbank in `PostgresBackupRestoreTests` (§4.5) |
| Rückwärts-Tor greift am Schema | Mindestversion beim Sichern aus dem Migrationsstand ableiten (§4.1) |
| `AuditEvents`/`Assets` im Bestand | Ein Schreibpfad, der gegen ein historisches Schema arbeitet — oder Release-Fixtures (§4.4) |
| Umzug auf einen anderen Rechner | Ein Zertifikat für den Schlüsselring (`BIFROST_KEYRING_CERT_PATH`) und ein Test, der ihn wechselt (§4.6) |
| DataProtection-Bezeichner festgenagelt | Ein Test in einem Paket, das `src/**` und die bestehenden Testprojekte anfassen darf (§4.3) |
| ~~Vor-Migrationsbackup auf PostgreSQL~~ | **erledigt** — mit erreichbarem `pg_dump` gilt dort `Always` (§4.8) |
| `postgresql-client` im mitgelieferten Image | Eine Entscheidung zum Image-Gewicht gegen das 350-MB-Gate; bis dahin installiert der Betreiber es selbst (§4.8) |
| Backuphaken im Serverstart belegt | Ein Test über den echten Serverstart — gehört in `Bifrost.Integration.Tests` (§4.8) |
