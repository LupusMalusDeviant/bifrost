# CLI installieren

Diese Seite beschreibt, wie man `bifrost` ohne .NET-Installation in Betrieb nimmt. Was die CLI
kann, steht in [gateway-cli.md](gateway-cli.md); wie die Artefakte heißen und wo sie herkommen,
regelt [plans/m1-distribution-contract.md](plans/m1-distribution-contract.md).

## Was man herunterlädt

Jedes GitHub-Release trägt ein Archiv je Plattform und eine gemeinsame `checksums.txt`.

| Plattform | Archiv |
|---|---|
| Windows x64 | `bifrost-cli-<version>-win-x64.zip` |
| Linux x64 | `bifrost-cli-<version>-linux-x64.tar.gz` |
| Linux arm64 | `bifrost-cli-<version>-linux-arm64.tar.gz` |
| macOS Intel | `bifrost-cli-<version>-osx-x64.tar.gz` |
| macOS Apple Silicon | `bifrost-cli-<version>-osx-arm64.tar.gz` |

Im Archiv liegen genau zwei Dateien: das Programm (`bifrost` bzw. `bifrost.exe`) und `LICENSE`.
Das Programm bringt die .NET-Laufzeit mit. Es ist deshalb rund 73 MB groß und im Archiv rund
31 MB — dafür braucht der Zielrechner weder SDK noch Runtime, und es gibt nichts zu installieren
außer der Datei selbst.

## Prüfsumme verifizieren

Die Prüfsumme wird **vor** dem Auspacken geprüft, sonst prüft man das Ergebnis eines Vorgangs, dem
man noch nicht getraut hat.

**Linux/macOS**

```bash
VERSION=0.11.0
curl -LO "https://github.com/LupusMalusDeviant/bifrost/releases/download/v$VERSION/bifrost-cli-$VERSION-linux-x64.tar.gz"
curl -LO "https://github.com/LupusMalusDeviant/bifrost/releases/download/v$VERSION/checksums.txt"

# prüft nur die vorhandenen Zeilen und meldet die übrigen als "FEHLT"
sha256sum --ignore-missing --check checksums.txt
# bifrost-cli-0.11.0-linux-x64.tar.gz: OK
```

**Windows (PowerShell)**

```powershell
$Version = '0.11.0'
$Archive = "bifrost-cli-$Version-win-x64.zip"
$Erwartet = (Select-String -Path checksums.txt -Pattern ([regex]::Escape($Archive))).Line.Split(' ')[0]
$Tatsaechlich = (Get-FileHash $Archive -Algorithm SHA256).Hash.ToLower()
if ($Erwartet -ne $Tatsaechlich) { throw "Prüfsumme weicht ab — Archiv nicht verwenden." }
'Prüfsumme stimmt.'
```

Weicht die Summe ab, wird das Archiv gelöscht und nicht ausgepackt. `checksums.txt` selbst wird
über die Signatur- und Provenance-Artefakte des Releases abgesichert; die Anleitung dazu steht in
`docs/security/`.

## Auspacken und in den Pfad legen

**Linux/macOS**

```bash
tar -xzf "bifrost-cli-$VERSION-linux-x64.tar.gz"
chmod +x bifrost
sudo install -m 0755 bifrost /usr/local/bin/bifrost
bifrost --version
```

Ohne Root-Rechte tut es auch `~/.local/bin`, sofern das Verzeichnis in `PATH` liegt.

**Windows (PowerShell)**

```powershell
Expand-Archive "bifrost-cli-$Version-win-x64.zip" -DestinationPath "$env:LOCALAPPDATA\Programs\bifrost" -Force
# einmalig, wirkt in neuen Konsolen:
[Environment]::SetEnvironmentVariable(
  'Path',
  [Environment]::GetEnvironmentVariable('Path', 'User') + ";$env:LOCALAPPDATA\Programs\bifrost",
  'User')
bifrost --version
```

## Erste Prüfung

```text
$ bifrost --version
bifrost 0.11.0
Commit:   679c1a2fe730c84d33f69650adea5b6f9cc8aa06
Laufzeit: .NET 10.0.4, win-x64
```

Der Commit ist der Punkt: Er sagt, aus welchem Stand genau diese Datei gebaut wurde, und er gehört
in jede Fehlermeldung. Für Skripte gibt es dieselbe Auskunft maschinenlesbar:

```bash
bifrost --json --version
# {"version":"0.11.0","commit":"679c1a2f...","runtime":".NET 10.0.4","rid":"linux-x64"}
```

Steht dort `unbekannt` statt eines SHA, stammt die Datei nicht aus einem Releasebau — dann besser
neu herunterladen.

Danach gegen das eigene Gateway:

```bash
export BIFROST_ENDPOINT=https://gateway.example
bifrost status
# Gateway ist bereit.
```

`bifrost --help` listet alle Befehle. Beide Befehle laufen ohne Konfiguration und ohne erreichbares
Gateway — man kann die Version also auch dann abfragen, wenn sonst nichts geht.

## Aktualisieren und entfernen

Aktualisieren heißt: neues Archiv herunterladen, Prüfsumme verifizieren, Datei ersetzen. Es gibt
keinen Zustand neben der Programmdatei. Entfernen heißt: Datei löschen (und den `PATH`-Eintrag
zurücknehmen). Konfiguration und Token liegen dort, wohin man sie gelegt hat, und werden nicht
angefasst.

## Bekannte Stolperstellen

- **Windows SmartScreen** meldet einen unbekannten Herausgeber. Die Artefakte tragen eine
  Supply-Chain-Signatur (Sigstore/keyless), aber kein Authenticode-Zertifikat; SmartScreen kennt
  nur letzteres. Vor dem Wegklicken die Prüfsumme vergleichen — das ist die belastbarere Aussage.
- **macOS Gatekeeper** blockt die Datei, weil sie nicht bei Apple notarisiert ist:
  `xattr -d com.apple.quarantine ./bifrost`. Auch hier gilt: erst Prüfsumme, dann Freigabe.
- **Alpine und andere musl-Distributionen** funktionieren mit `linux-x64` nicht; das Programm ist
  gegen glibc gelinkt. Ein `linux-musl-x64`-Artefakt ist derzeit nicht Teil des Releases. Wer
  Alpine braucht, meldet es — das ist eine Erweiterung des Distributionsvertrags, keine
  Einstellungssache.
- **Schreibgeschützte Verzeichnisse** sind kein Problem: Das Programm entpackt sich beim Start
  nicht in ein temporäres Verzeichnis, sondern lädt alles aus der eigenen Datei.

## Für Entwickler: aus dem Quellbaum

Wer ohnehin ein .NET-SDK hat, braucht kein Archiv:

```bash
dotnet run --project src/Bifrost.Cli -- status
```

Ein eigenes Artefakt entsteht mit derselben Konfiguration, die auch der Release verwendet:

```bash
dotnet publish src/Bifrost.Cli -c Release -r linux-x64
```

Runtime-ID ersetzen nach Bedarf (`win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`).
Eigenständigkeit, Einzeldatei und eingebettete Symbole sind in der Projektdatei hinterlegt und
müssen nicht auf der Kommandozeile wiederholt werden; wer sie ausdrücklich anders will, übergibt
`-p:SelfContained=false` oder `-p:PublishSingleFile=false`.
