# ADR-0024: Backup, Restore und Migrationssicherheit

- **Status:** **Akzeptiert.** Entschieden am 2026-07-31 im Rahmen von M2 (Pflichtenheft 0004).
- **Datum:** 2026-07-31
- **Autor:** Lead/Integrator, ausgearbeitet mit Claude
- **Betrifft:** FR-P020 bis FR-P028, NFR-P03; [ADR-0007](0007-ef-core-mit-sqlite-default-postgres-optional.md)
- **Vorbedingung aus dem Pflichtenheft:** WP2.1 verlangt diese Entscheidung **vor** dem Code.

## Kontext und Problemstellung

B.I.F.R.O.S.T hält den vollständigen Zugang zu fremden Systemen: Upstream-Zugangsdaten,
OAuth-Token, Webhook-Secrets, API-Schlüssel, RBAC. Das alles liegt in einer Datenbank und einem
DataProtection-Key-Ring, die sich **nur gemeinsam** benutzen lassen — die Datenbank enthält
Geheimtext, der Key-Ring den Schlüssel dazu.

Heute gibt es dafür keinen Produktpfad. Wer sichern will, kopiert Dateien; wer wiederherstellen
will, hofft. Zwei Beobachtungen aus dem laufenden Betrieb zeigen, warum das nicht reicht:

1. Beim Umstieg auf v0.11.0 hätte ein umbenanntes Docker-Volume genügt, um eine Instanz mit
   **leerer** Datenbank hochzufahren — fehlerfrei, mit der Meldung „bereit".
2. Eine SQLite-Datei im laufenden Betrieb zu kopieren ergibt bei aktivem WAL keine konsistente
   Sicherung, sondern eine, der man das nicht ansieht.

**Kernfrage:** Was genau ist eine Sicherung dieser Instanz, wann darf sie „gültig" heißen, und was
darf ein Restore mit einer bestehenden Installation tun?

## Entscheidungen

### E1 — Ein Archiv, ein Manifest, eine Formatversion

Das Backup ist ein ZIP mit `manifest.json` an erster Stelle. Gelesen wird **immer zuerst das
Manifest**; erst danach wird eine einzige Nutzlast angefasst.

```text
bifrost-backup-v1.zip
├── manifest.json      ← Formatversion, Produktversion, Provider, Migrationsstand, Instanz-Id,
│                        Bereiche, Verschlüsselung, Mindestversion für Restore
├── database/
├── keyring/
├── packages/
├── config/instance.json
└── checksums.json
```

**Warum Manifest zuerst:** Ein Archiv, das man erst auspackt und dann beurteilt, hat bereits
Dateien geschrieben, wenn die Beurteilung „inkompatibel" lautet.

### E2 — Datenbank konsistent sichern, nicht kopieren

- **SQLite:** über die Online-Backup-API (`VACUUM INTO` bzw. `SqliteConnection.BackupDatabase`),
  **nicht** durch Kopieren der Datei. Bei aktivem WAL ist eine Dateikopie ohne die
  `-wal`/`-shm`-Begleiter eine Sicherung, die beim Zurückspielen still älter ist als gedacht.
- **PostgreSQL:** über `pg_dump` in einem dokumentierten Format. Fehlt das Werkzeug, ist das ein
  **Fehler mit Meldung**, kein stiller Rückfall auf einen Zeilenexport.

### E3 — Der Key-Ring gehört dazu, und deshalb ist ein Vollbackup ein Geheimnis

Eine Sicherung ohne Key-Ring ist beim Zurückspielen wertlos: Die Datenbank enthält dann Geheimtext,
den niemand mehr entschlüsseln kann. Also gehört er hinein — und damit ist **jedes vollständige
Backup so schützenswert wie die Instanz selbst**.

Daraus folgt:

- Verschlüsselung ist möglich (Passphrase, moderner KDF + AEAD aus der Standardbibliothek, **keine
  eigene Kryptografie**).
- Das Manifest sagt unverschlüsselt, **ob** verschlüsselt wurde — sonst kann ein Werkzeug nicht
  einmal prüfen, was es vor sich hat.
- Ein unverschlüsseltes Vollbackup wird beim Erzeugen ausdrücklich als solches benannt. Es zu
  verbieten wäre falsch (automatisierte Sicherung auf ein bereits verschlüsseltes Ziel ist ein
  legitimer Fall), es stillschweigend zu erzeugen ebenso.

### E4 — Prüfsummen entstehen vor dem Abschluss, das Archiv wird atomar fertig

`checksums.json` wird berechnet, **bevor** das Archiv geschlossen wird. Geschrieben wird in eine
temporäre Datei **im Zielverzeichnis** und erst danach atomar umbenannt.

**Warum im Zielverzeichnis:** Ein `Move` über Dateisystemgrenzen ist kein atomarer Vorgang, sondern
Kopieren und Löschen. Ein abgebrochenes Backup, das dabei als vollständig erscheint, ist der
schlimmste Fehler, den diese Funktion machen kann.

Ein Teilarchiv wird nie als gültig gemeldet. `bifrost backup verify` prüft Manifest, Prüfsummen und
Vollständigkeit **ohne** Restore.

### E5 — Restore ist standardmäßig ein Vorgang auf einer leeren Instanz

- Vorgabe: Wiederherstellung auf eine **leere** Zielinstallation.
- Auf eine bestehende Instanz nur mit ausdrücklichem `--replace` bzw. Bestätigung in der
  Oberfläche.
- Vor einem Replace wird der vorhandene Zustand gesichert. Ohne Ausweg kein Überschreiben.
- Der Restore läuft in ein **Staging-Verzeichnis** und schaltet erst nach vollständiger Prüfung um.
- Gegen Zip-Slip, Symlinks, Dekompressionsbomben und unbekannte Formate wird geprüft, **bevor**
  entpackt wird — Pfade werden kanonisiert und gegen das Zielverzeichnis verankert.

### E6 — Vorwärts ja, rückwärts nein

Ein Backup einer **älteren** Produktversion darf in eine neuere zurückgespielt werden; die
Migration läuft danach. Der umgekehrte Weg wird **abgelehnt**, nicht versucht: Das Manifest trägt
eine Mindestversion, und eine Instanz, die sie unterschreitet, bricht mit klarer Meldung ab.

**Warum so hart:** Ein Downgrade vorzutäuschen heißt, ein neueres Schema mit alten Regeln zu
bedienen. Der Schaden fällt dann später und woanders auf.

### E7 — Genau eine Instanz migriert

- **PostgreSQL:** Advisory Lock.
- **SQLite:** Dateilock plus Transaktion.
- Der Migrationszustand wird **außerhalb flüchtiger Logs** vermerkt.
- Ein unbekannter oder halber Zustand beim Start führt zur **Verweigerung des Schreibbetriebs** mit
  Recovery-Hinweis, nicht zu einem Reparaturversuch.
- Vor schemaändernden Migrationen entsteht bei SQLite automatisch ein Backup; bei PostgreSQL wird
  ein vorhandenes verlangt oder ausdrücklich abgewählt.

### E8 — Konfigurationsexport ist nicht Backup

Zwei getrennte Dinge mit getrennten Zwecken:

| | Backup | Konfigurationsexport |
|---|---|---|
| Zweck | dieselbe Instanz wiederherstellen | eine gleichartige Instanz aufbauen |
| Enthält | alles, inkl. Key-Ring | Server, Rollen, Profile, Regeln, Skills |
| Secrets | ja (deshalb schützenswert) | **nein** — Referenzen oder Masken |
| Format | ZIP mit Manifest | JSON, versioniert |

Ein vollständiger Export **mit** Geheimnissen ist möglich, aber verschlüsselt und ausdrücklich als
Credential-Export benannt.

## Konsequenzen

**Positiv:** Ein Betreiber kann sichern, prüfen, wiederherstellen und umziehen, ohne Dateien zu
raten. Upgrades bekommen einen Rückweg. Die Trennung aus E8 verhindert, dass jemand versehentlich
seine Zugangsdaten in ein Git-Repository legt, weil er „die Konfiguration" exportieren wollte.

**Negativ und bewusst in Kauf genommen:**

- Ein Vollbackup ist ein Geheimnis. Das ist keine Schwäche des Verfahrens, sondern die Folge davon,
  dass die Instanz selbst eines hält — es muss nur gesagt werden.
- `pg_dump` wird zur Voraussetzung für PostgreSQL-Backups. Der Alternativweg (eigener Zeilenexport)
  wäre eine zweite, schlechter geprüfte Implementierung derselben Aufgabe.
- Der Restore braucht einen Wartungsmoment. Eine Wiederherstellung im laufenden Schreibbetrieb
  ließe sich nicht atomar halten.

**Offen und ausdrücklich nicht entschieden:** Inkrementelle Sicherungen, automatische Zeitpläne und
das Schreiben auf entfernte Ziele. Dafür gibt es bewährte Werkzeuge; B.I.F.R.O.S.T liefert das
Archiv, nicht die Aufbewahrung.
