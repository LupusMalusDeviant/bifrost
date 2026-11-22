# Offizielle Gateway-CLI

`faolan` ist ein separater HTTP-Client. Er verwendet ausschließlich `/healthz`, `/readyz` und die
öffentlichen `/api/v1`-Verträge; er liest weder Datenbank noch interne Stores.

## Konfiguration und Identität

```json
{
  "endpoint": "https://gateway.example/",
  "tokenFile": "C:/secure/faolan.token",
  "identity": "production-operator"
}
```

Die Config wird mit `--config PATH` oder `FAOLAN_CONFIG` gewählt.
`FAOLAN_ENDPOINT` überschreibt den Endpoint. Die wirksame Gateway-Identität stammt immer aus dem
API-Token und damit aus der serverseitigen RBAC-Zuordnung; `identity`/`FAOLAN_IDENTITY` ist nur ein
lokales Profil-Label.

Tokenquellen:

1. `--token-stdin`;
2. `FAOLAN_TOKEN`;
3. `tokenFile` aus der Config.

Ein `--token`-Argument existiert absichtlich nicht, damit Tokens nicht in Prozesslisten oder
Shell-History landen. TLS-Zertifikatsfehler werden nicht ignoriert.

## Befehle

```text
faolan status
faolan tools search <query>
faolan tools describe <tool>
faolan tools invoke <tool> --json '{...}'
faolan tools invoke <tool> --file args.json
faolan tools invoke <tool> --file -
faolan servers list
faolan servers add --file server.json
faolan servers enable <id>
faolan servers disable <id>
faolan servers remove <id>
faolan approvals list
faolan approvals approve <id>
faolan approvals deny <id>
faolan audit tail
```

Globale Optionen stehen vor dem Befehl:

```text
faolan --json --config gateway.json tools search git
```

`--json` gibt genau ein kompaktes JSON-Dokument auf stdout aus. Menschliche Hinweise und Fehler
gehen nach stderr. Feldnamen und Exitcodes bleiben innerhalb einer Minor-Version kompatibel;
additive JSON-Felder sind erlaubt, Entfernen oder Umdeuten erst mit Major-Version.

## Exitcodes

| Code | Bedeutung |
|---:|---|
| 0 | Erfolg |
| 2 | Syntax, lokale Datei oder ungültiges JSON |
| 3 | nicht authentifiziert oder nicht berechtigt |
| 4 | Objekt/Tool nicht gefunden |
| 5 | Gateway-/Upstream-Fehler |
| 6 | menschliche Freigabe erforderlich |
| 10 | Netzwerk, TLS, I/O oder Abbruch |
