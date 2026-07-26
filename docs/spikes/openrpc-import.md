# Spike: OpenRPC-Import

> **Umgesetzt am 2026-07-26.** Der Connector steht (`UpstreamTransportKind.OpenRpc`), das Mapping
> unten ist die Umsetzung, und die sechs Security-Fixtures sind Tests. Zwei Abweichungen vom
> Spike-Text: Der Import folgt **keiner** externen Referenz (statt sie zu validieren, wird sie
> abgewiesen — ein `$ref` nach außen wäre ein zweiter, ungeprüfter Ladevorgang mitten im Schema),
> und die Zielprüfung greift auch am **Aufruf-Endpunkt**, nicht nur an der Dokumentquelle: Sonst
> umginge man sie über ein lokales Dokument, das auf eine interne Adresse zeigt.

## Frage und Grenze

Lässt sich ein statisches OpenRPC-Dokument ohne Netzauflösung in stabile Capabilities überführen?
Der Spike führt keine RPCs aus und folgt keinen externen `$ref`.

OpenRPC beschreibt Methoden und Content Descriptors mit JSON-Schema; `rpc.discover` ist ein
optionaler standardisierter Discovery-Aufruf. V1 behandelt statische Dokumente und
`rpc.discover` gleich, nachdem Antwortgröße, Ziel und Schema validiert wurden.

## Mapping

- Method `name` → nativer technischer Name.
- `paramStructure=by-name` → JSON-Objekt; `by-position` → geordnetes Array mit unveränderlicher
  Descriptor-Reihenfolge.
- Content Descriptor `schema` → Eingabeschema; `required` → Required-Liste.
- Result Descriptor → Ausgabeschema.
- JSON-RPC `error.code/message/data` → strukturierter Connectorfehler; `data` wird begrenzt und
  redigiert.
- Request-ID wird vom Connector erzeugt und mit Audit-Correlation verknüpft.

## Security-Fixtures

1. gültiges by-name/by-position-Dokument;
2. doppelte Methodennamen;
3. lokale zyklische `$ref`;
4. externe HTTP-/file-Referenz;
5. Dokument über Größen-/Tiefenlimit;
6. `rpc.discover` mit Redirect auf private/link-local Adresse.

Go erst bei fail-closed Referenzauflösung. Batch und Notifications sind für v1 ausdrücklich
ausgenommen.

Quelle: [OpenRPC Specification](https://spec.open-rpc.org/)
