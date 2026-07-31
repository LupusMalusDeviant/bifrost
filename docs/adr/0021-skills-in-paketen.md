# ADR-0021: Skills in Paketen — Zustimmung gilt dem Text

- **Status:** **Akzeptiert.** Entschieden am 2026-07-28 mit dem Product Owner, Frage für Frage.
- **Datum:** 2026-07-28
- **Grundlage:** [0021-EM](0021-entscheidungsmaterial-skills-im-paket.md) — Material mit vier
  Optionen und fünf Fragen. Ergänzt [ADR-0016](0016-versionierter-connector-plugin-vertrag.md).

> **Umsetzungsstand 2026-07-28.** Vier der fünf Entscheidungen stehen. **Eine ist offen:**
>
> | Entscheidung | Stand |
> |---|---|
> | F2 Zustimmung gilt dem Text, ohne Stufenrabatt | **steht** (`ConnectorTrustPolicy`, `SkillConsentToken`) |
> | F3 Präfix `<paket-id>/<skill>` | **steht** (`ConnectorPackageInstaller`) |
> | F4 Überschreiben mit Hinweis | **steht** (`SkillPublication.ReplacedLocalEdit`) |
> | F5 Deinstallieren nimmt die Skills mit | **steht** (`IAssetStore.DeleteFromPackageAsync`, Ankündigung über `PreviewRemovalAsync`) |
> | F1 Eigener Pakettyp `bifrost.skills.v1` | **offen** — heute trägt nur ein Connector-Paket Skills |

## Kontext

Ein Connector-Paket trägt seit ADR-0016 einen Konnektor. Was es nicht trägt, ist das Wissen, wie
man ihn benutzt — und genau das ist der Punkt, an dem MCP heute schwach ist: Der Konnektor kommt
an, die Anleitung dazu bleibt beim Autor.

Der Anlass ist konkreter als Bequemlichkeit. Seit `175c41c` deklariert ein Skill seine
`required-tools`, und der Gateway **prüft** sie gegen den Katalog. Prüfen heißt melden, wenn etwas
fehlt. Ein Paket, das Konnektor und Skill zusammen mitbringt, **stellt die Zusage her** — die Tools
kommen mit. Das kann kein Datei-Editor und kein Kopieren von Text.

Dem steht eine Asymmetrie gegenüber, die das ganze ADR bestimmt:

> Ein Konnektor ist eingesperrt — WASI-Grants, deklarierte Zugriffe mit Zustimmung je Eintrag,
> eigener Prozess, Probe vor der Aktivierung. **Ein Skill ist es nicht.** Er ist Text, der
> ungefiltert in die Denkschleife eines Agenten geht, der Tools aufrufen darf.
>
> **Es gibt keine Sandbox für einen Satz.**

## Entscheidung

### 1. Umfang: zwei Pakettypen (F1)

Neben dem Connector-Paket, das Skills mitbringen darf, kommt ein **eigener Pakettyp**
`bifrost.skills.v1` für Skill-Bündel **ohne** Konnektor.

Der Grund für einen zweiten Typ statt eines gelockerten ersten: `Transport` und `EntryPoint` sind
im Connector-Manifest Pflichtfelder, und die **Probe** ist das Tor zur Aktivierung — der Grund,
warum eine kaputte Version nie in Betrieb steht. Ein Connector-Paket ohne Entry Point müsste diese
Sicherung aussetzen und sähe hinterher aus wie eines, das sie bestanden hat. Ein eigener Typ
verspricht von vornherein keinen Konnektor.

Der Preis ist ausgewiesen: zwei Pakettypen, zwei Prüfungen, zwei Oberflächen. Und bei einem reinen
Skill-Bündel ist die Textanzeige aus Punkt 2 nicht eine von mehreren Schutzmaßnahmen, sondern die
**einzige**.

### 2. Zustimmung gilt dem Text — ohne Stufenrabatt (F2)

Jeder mitgelieferte Skill wird beim Installieren **angezeigt** und **einzeln bestätigt**. Der
Zustimmungseintrag lautet `skill:<name>@<sha256>` und bindet an den Inhalt: Ändert ein Update den
Text, verfällt die Zustimmung und ist neu zu geben.

**Auch für `Official` gibt es keine Ausnahme.** Bei einem Zugriff nach außen darf ein offizielles
Paket bekommen, was im Manifest steht, weil eine Laufzeitgrenze ihn durchsetzt. Für Text gibt es
diese Grenze nicht. Eine Stufe „vertrauenswürdig genug, um ungelesen zu bleiben" hätte hier keine
technische Grundlage, sondern wäre eine reine Aussage über den Herausgeber — und ein gepinnter
Schlüssel heißt nach ADR-0016 ausdrücklich „dieser Herausgeber ist echt", nicht „dieser Herausgeber
darf".

Der Preis: Jedes Update mit geändertem Text verlangt erneutes Lesen. Das ist Absicht.

### 3. Namen tragen ihr Paket (F3)

Ein Skill aus einem Paket heißt `<paket-id>/<skill>`. Das Präfix setzt der Gateway; ein `/` im
Manifest-Namen wird abgewiesen, damit ein Paket diese Grenze nicht verwischen kann.

Damit kann ein Paket per Konstruktion keinen handgeschriebenen Skill überschatten, und man sieht
jedem Namen an, woher er kommt. Der Preis sind lange Namen, die ein Agent so aufrufen muss.

Unabhängig davon sind Skill-Namen seit `54aeeb0` **eindeutig** — vorher waren zwei gleichen Namens
möglich, und ausgeliefert wurde der erstbeste.

### 4. Ein Update löst eine angepasste Fassung ab — und sagt es (F4)

Hat jemand den Text nach dem Installieren bearbeitet, wird ein Paket-Update trotzdem angehängt und
ist ab sofort das, was ein Agent bekommt. Die eigene Fassung bleibt in der Historie, Zurückschalten
existiert, und die Installation **meldet**, dass sie eine angepasste Fassung abgelöst hat.

Erkennbar ist der Fall daran, dass die Herkunft an der **Version** hängt: Wer von Hand
veröffentlicht, erzeugt eine Version ohne Paketherkunft.

Bewusst verworfen: die lokale Fassung aktiv zu lassen. Ein Herausgeber ändert den Text meist, weil
sich sein Konnektor geändert hat — eine eingefrorene Anleitung beschriebe dann Tools, die es so
nicht mehr gibt, und das fiele erst auf, wenn ein Agent scheitert. Ebenso verworfen: stillschweigend
überschreiben; das wäre der Vertrauensbruch, den die Versionierung verhindern soll.

### 5. Deinstallieren nimmt die Skills mit (F5)

Wird ein Paket entfernt, verschwinden auch die Skills, die es mitgebracht hat.

Das verlangt eine **Lösch-Operation** in `IAssetStore`, die es bisher bewusst nicht gibt, und sie
nimmt die Historie mit — einschließlich einer Fassung, die jemand selbst geschrieben hat. Diese
Konsequenz ist gesehen und in Kauf genommen: Ein verwaister Skill wäre über `list_skills` für jeden
Agenten weiter sichtbar, während die Kennzeichnung „verwaist" nur ein Mensch in der Oberfläche
sieht. Eine Anleitung für Tools, die es nicht mehr gibt, ist schlimmer als keine.

**Auflage:** Das Entfernen muss vorher sagen, welche Skills es mitnimmt, und eine lokal angepasste
Fassung muss dabei besonders genannt werden. Ein Löschvorgang, der ungefragt fremde Arbeit
mitnimmt, wäre genau die Art Schritt, die dieses Projekt sonst vermeidet.

## Begründung

Die vier Entscheidungen hängen an einem Satz: **Es gibt keine Sandbox für einen Satz.** Alles, was
für Konnektoren gilt — Stufen, Grants, Probe — beschreibt Zugriffe, die eine Laufzeit durchsetzen
kann. Ein Skill hat davon nichts. Also verlagert sich der Schutz an die einzige Stelle, an der er
noch wirken kann: auf den Menschen, der installiert, und zwar mit dem Text vor Augen (2), mit
Namen, die keine fremde Arbeit überschatten (3), mit einer Meldung, wenn eigene Arbeit abgelöst
wird (4), und ohne Rückstände, wenn das Paket geht (5).

Punkt 1 folgt daraus, dass eine Sicherung, die man aussetzt, schlechter ist als eine, die es an
dieser Stelle gar nicht erst gibt: Ein Skill-Bündel verspricht keine Probe, ein Connector-Paket
ohne Entry Point hätte sie nur vorgetäuscht.

## Konsequenzen

- **Ein Paket kann eine Zusage herstellen, die vorher nur geprüft werden konnte.** `required-tools`
  eines mitgelieferten Skills stimmen per Konstruktion.
- **Reibung bei Updates ist gewollt.** Geänderter Text heißt erneutes Lesen und Bestätigen.
- **Zwei Pakettypen** sind zu pflegen, zu prüfen und zu erklären.
- **`IAssetStore` bekommt eine Lösch-Operation.** Damit ist „die Historie bleibt" nicht mehr
  ausnahmslos wahr; die Ausnahme ist auf das Deinstallieren eines Pakets begrenzt und muss dort
  angekündigt werden.
- **Das Paketformat blieb unverändert**, soweit es Connector-Pakete betrifft: `ConnectorPayload` ist
  Pfad plus Hash, die Signatur deckt den Skill-Text schon ab.

## Verworfen

- **Skills nur zusammen mit einem Konnektor** (Option B allein). Das Teilen von Skill-Sammlungen —
  vermutlich der häufigste Wunsch — bliebe unmöglich.
- **Connector-Paket mit optionalem Entry Point** (Option D). Es müsste die Probe aussetzen, und
  hinterher wäre nicht mehr unterscheidbar, welches Paket sie bestanden hat.
- **Kein Zustimmungszwang bei `Official`.** Siehe Punkt 2: Die Analogie zu Zugriffen trägt nicht,
  weil ihr die Laufzeitgrenze fehlt.
- **Anzeigen ohne Bestätigen.** Eine Anzeige, die nichts blockiert, wird nach dem dritten Mal nicht
  mehr gelesen — Struktur ohne Wirkung.
- **Verwaiste Skills stehen lassen.** Sie blieben für Agenten sichtbar, während die Warnung nur
  Menschen erreicht.
