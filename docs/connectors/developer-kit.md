# Connector Developer Kit

Ein Connector ist eine Datei: `<name>-<version>.mcpkg`. Sie enthält ein signiertes Manifest, die
Nutzlast und deren Prüfsummen. **Es gibt keine Registry** — wer einen Connector weitergibt, gibt
eine Datei weiter.

Gebaut, signiert und geprüft wird mit `bifrost-connector` **auf dem eigenen Rechner**. Das Gateway
sieht dabei nichts; es bekommt am Ende nur das fertige Paket zu sehen und prüft es mit demselben
Code, mit dem dieses Werkzeug es baut.

```bash
dotnet tool install -g Bifrost.ConnectorKit   # oder: dotnet run --project src/Bifrost.ConnectorKit --
```

## Der ganze Weg in vier Befehlen

```bash
bifrost-connector init mein-connector --id mein-connector
# → legt connector.json, payload/ und einen Herausgeberschlüssel an

cp meine-component.wasm mein-connector/payload/connector.wasm

bifrost-connector build mein-connector --key mein-connector/publisher.key.json
# → out/mein-connector-0.1.0.mcpkg

bifrost-connector test mein-connector/out/mein-connector-0.1.0.mcpkg \
    --publisher mein-connector/publisher.key.json
```

`test` ist die Probe vor der Auslieferung: Signatur, Prüfsummen, Schema **und** Vertragsversion.
Ein gültig signiertes Paket für eine andere Gateway-Generation ist echt und trotzdem unbrauchbar —
das soll man vorher erfahren, nicht beim Betreiber.

## Was jeder Befehl tut

| Befehl | Wirkung |
|---|---|
| `init <verzeichnis>` | Gerüst plus Herausgeberschlüssel (falls keiner mit `--key` übergeben wird) |
| `build <verzeichnis> --key <datei>` | Paket bauen und signieren; erzeugt die abgetrennte Signatur der Component gleich mit |
| `sign <paket> --key <datei> --out <paket>` | Vorhandenes Paket neu signieren — Bauserver baut, Freigabeberechtigter signiert |
| `verify <paket> --publisher <…>` | Signatur und Prüfsummen; sagt, was drin ist |
| `test <paket> --publisher <…>` | wie `verify`, zusätzlich Schema- und Vertragsprüfung |

Exitcodes wie bei den Betriebsbefehlen: `0` ok · `1` Fehler · `2` Bedienfehler · `5` Paket ungültig.

## Was ins Paket kommt

Alles im Projektverzeichnis **außer** `connector.json`, Schlüsseldateien, `out/` und `.mcpkg`-Dateien.
Es gibt keine Auswahlsprache — ein Glob-Dialekt wäre eine zweite Stelle, an der ein Paket
unvollständig werden kann, ohne dass es auffällt.

Die Prüfsummen trägt `build` selbst ein. Eine von Hand gepflegte Prüfsumme ist eine, die irgendwann
nicht mehr stimmt und trotzdem dasteht.

## Der Schlüssel

`init` legt ein Ed25519-Schlüsselpaar an, wenn keines übergeben wird. Der private Teil bleibt in der
Datei und **erscheint in keiner Ausgabe** dieses Werkzeugs — auch in keiner Fehlermeldung. Ein
vorhandener Schlüssel wird nie überschrieben: Jedes damit signierte Paket gehörte sonst zu einem
Herausgeber, den es nicht mehr gibt.

Der **Kennwert** (`KeyId`) ist der SHA-256-Fingerprint des öffentlichen Schlüssels — derselbe Wert,
den ein Betreiber in seinen Trust-Store einträgt:

```bash
bifrost publishers add --key-id <keyid> --public-key <base64> --label "…" --trust ThirdParty
```

Der private Schlüssel gehört nicht ins Versionsverwaltungssystem. `init` schreibt ihn deshalb neben
das Projekt und nicht hinein.

## Das Beispiel im Repository

`samples/counter-connector` ist ein vollständiger Connector: eine WASI-Component, keine Grants, kein
Netz, kein Dateizugriff.

```bash
cp spikes/wasi-component-runtime/fixtures/counter.component.wasm \
   samples/counter-connector/payload/connector.wasm     # liegt dort bereits

bifrost-connector build samples/counter-connector --key /tmp/beispiel.key.json
bifrost-connector test samples/counter-connector/out/counter-connector-0.1.0.mcpkg \
    --publisher /tmp/beispiel.key.json
```

Der Schlüssel wird beim ersten Aufruf über `init --key /tmp/beispiel.key.json` erzeugt und liegt
bewusst außerhalb des Repositories.

**Diese Befehlsfolge ist ein Test** (`ConnectorKitTests`). Wenn die Dokumentation hier nicht mehr
zur Wirklichkeit passt, wird sie rot — nicht der Leser ratlos.

## Grenzen

- **Ein Format, kein zweites.** Gebaut wird mit demselben Bauer und geprüft mit demselben Leser wie
  im Gateway. Was dieses Werkzeug akzeptiert, akzeptiert der Server — und umgekehrt.
- **Keine Registry, kein Katalogdienst.** Ein lokaler Katalog ist ein Verzeichnis mit Dateien.
- **Paketierbar ist bisher nur der WASI-Transport.** Andere Transporte werden konfiguriert, nicht
  paketiert.
