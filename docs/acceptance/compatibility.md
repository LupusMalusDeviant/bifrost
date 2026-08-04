# Kompatibilitätsmatrix

<!-- GENERIERT aus tests/compatibility/matrix.json — nicht von Hand ändern.
     Neu erzeugen: BIFROST_UPDATE_COMPAT_REPORT=1 dotnet test tests/compatibility/Bifrost.Compatibility.Tests -->

**Stand der Matrix:** 2026-08-02 · **Schema:** v1

Jede Zeile verweist auf einen Lauf oder ein handgeführtes Protokoll. `untested` heißt
ungeprüft — nicht „geht nicht" und nicht „geht".

## Werte

| Wert | Bedeutung |
|---|---|
| `pass` | in einem Lauf oder Protokoll durchgehend funktioniert |
| `partial` | funktioniert mit benannter Einschränkung (Spalte unten) |
| `fail` | geprüft und fehlgeschlagen |
| `unsupported` | der Client oder Betriebsmodus kennt die Fähigkeit nicht |
| `untested` | **nicht geprüft** — offene Lücke, keine Aussage |

## Matrix

| Client | Version | OS | Transport | Modus | Protokoll | tools | resources | prompts | elicitation | approval | listChanged | oauth | skills |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `claude-code` | unbekannt | windows | http | stateless | unbekannt | untested | untested | untested | untested | untested | untested | untested | untested |
| `claude-desktop` | unbekannt | windows | http | stateless | unbekannt | untested | untested | untested | untested | untested | untested | untested | untested |
| `codex` | unbekannt | windows | http | stateless | unbekannt | untested | untested | untested | untested | untested | untested | untested | untested |
| `csharp-sdk` | 2.0.0 | windows | http | stateless | 2026-07-28 | pass | pass | pass | untested | untested | unsupported | untested | pass |
| `cursor` | unbekannt | windows | http | stateless | unbekannt | untested | untested | untested | untested | untested | untested | untested | untested |
| `mcp-inspector` | unbekannt | windows | http | stateless | unbekannt | untested | untested | untested | untested | untested | untested | untested | untested |
| `vscode` | unbekannt | windows | http | stateless | unbekannt | untested | untested | untested | untested | untested | untested | untested | untested |

## Belege

| Zeile | Art | Beleg | Datum | Prüfer |
|---|---|---|---|---|
| `claude-code/unbekannt/windows/http/stateless` | — | *nichts geprüft* | — | — |
| `claude-desktop/unbekannt/windows/http/stateless` | — | *nichts geprüft* | — | — |
| `codex/unbekannt/windows/http/stateless` | — | *nichts geprüft* | — | — |
| `csharp-sdk/2.0.0/windows/http/stateless` | automated | `local://windows` | 2026-08-02 | — |
| `cursor/unbekannt/windows/http/stateless` | — | *nichts geprüft* | — | — |
| `mcp-inspector/unbekannt/windows/http/stateless` | — | *nichts geprüft* | — | — |
| `vscode/unbekannt/windows/http/stateless` | — | *nichts geprüft* | — | — |

## Bekannte Einschränkungen (FR-P065)

**`csharp-sdk/2.0.0/windows/http/stateless`**

- In-Process-Host: kein TCP, kein TLS, kein Reverse Proxy — die Netzstrecke belegt WP5.3.

## Offene Lücken

51 von 56 Zellen sind ungeprüft:

- `claude-code/unbekannt/windows/http/stateless`: tools, resources, prompts, elicitation, approval, listChanged, oauth, skills
- `claude-desktop/unbekannt/windows/http/stateless`: tools, resources, prompts, elicitation, approval, listChanged, oauth, skills
- `codex/unbekannt/windows/http/stateless`: tools, resources, prompts, elicitation, approval, listChanged, oauth, skills
- `csharp-sdk/2.0.0/windows/http/stateless`: elicitation, approval, oauth
- `cursor/unbekannt/windows/http/stateless`: tools, resources, prompts, elicitation, approval, listChanged, oauth, skills
- `mcp-inspector/unbekannt/windows/http/stateless`: tools, resources, prompts, elicitation, approval, listChanged, oauth, skills
- `vscode/unbekannt/windows/http/stateless`: tools, resources, prompts, elicitation, approval, listChanged, oauth, skills

