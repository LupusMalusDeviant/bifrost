# ADR-0003: Hybride Token-Strategie — Lazy Discovery plus Tool-Profile

- **Status:** Akzeptiert
- **Datum:** 2026-07-17
- **Autor:** Senior-Tech-Specialist (Claude)
- **Konsultiert:** Product Owner

## Kontext und Problemstellung

Jedes exponierte Tool kostet den Agenten Kontext-Tokens (Name, Beschreibung, JSON-Schema — typisch 100–800 Tokens pro Tool). Bei 10 Servern mit 100 Tools sind das leicht 30–60k Tokens *pro Konversation*, bevor der Agent ein Wort gelesen hat. PRD-Ziel Z-2 fordert ≥ 80 % Ersparnis. Gleichzeitig arbeiten Agenten am zuverlässigsten mit Tools, die sie direkt im Kontext sehen — jede Indirektion kostet einen Discovery-Roundtrip und Modell-Disziplin.

**Kernfrage:** Wie reduziert der Gateway die Schema-Token-Last im Agenten-Kontext, ohne die Nutzbarkeit der Tools zu zerstören?

## Anforderungen

### Funktional

- Pro Agent/Rolle steuerbar, welche Tools in `tools/list` erscheinen (FR-11).
- Zugriff auf *alle* erlaubten Tools muss möglich bleiben, auch wenn sie nicht gelistet sind (FR-12).
- Messbarkeit der Ersparnis (FR-15).

### Nicht-Funktional

- Discovery-Aufrufe schnell (< 200 ms), sonst frisst Latenz die Token-Ersparnis auf.
- Muss mit Standard-Clients (Claude Code u. a.) ohne Client-Anpassung funktionieren.

## Betrachtete Optionen

### Option 0: Nur statische Tool-Profile

Pro Agent wird kuratiert, welche Tools voll sichtbar sind; alles andere existiert für ihn nicht.

**Positiv:**
- Einfach, deterministisch, keine Meta-Tool-Indirektion; Agent-Verhalten unverändert.
- Doppelt als RBAC-Sichtbarkeitsmechanismus nutzbar.

**Negativ:**
- Ersparnis hängt allein von Kurationsdisziplin ab; „Agent braucht ausnahmsweise Tool X" heißt Profil ändern.
- Bei breit arbeitenden Agenten (brauchen potenziell vieles) versagt der Ansatz — entweder Bloat oder Handarbeit.

### Option 1: Nur Lazy Discovery (Meta-Tools)

Agent sieht ausschließlich `search_tools`, `describe_tool`, `invoke_tool` (~3 Schemas, < 1k Tokens). Alles Weitere on demand.

**Positiv:**
- Maximale, von der Serverzahl unabhängige Ersparnis (nahe 95 %+).
- Skaliert auf hunderte Tools ohne Kurationsaufwand.

**Negativ:**
- Jede Tool-Nutzung kostet 1–2 zusätzliche Roundtrips (search/describe → invoke).
- Schwächere Modelle übersehen Fähigkeiten, die sie nicht direkt im Kontext sehen; häufig genutzte Tools zahlen die Indirektion bei jedem Call.
- `invoke_tool` mit freiem JSON verliert client-seitige Schema-Validierung.

### Option 2: Hybrid — Pinned-Tools voll sichtbar, Rest lazy

Pro Profil: eine kleine kuratierte Menge „Pinned Tools" erscheint mit vollem Schema in `tools/list`; zusätzlich die drei Meta-Tools für den Long Tail. Beides pro Profil konfigurierbar (auch reine Varianten von Option 0/1 abbildbar).

**Positiv:**
- Häufige Tools bleiben roundtrip-frei und schema-validiert; seltene kosten keinen Dauer-Kontext.
- Deckt beide Extreme als Sonderfall ab — kein Umbau nötig, wenn sich ein Profil-Stil als besser erweist.
- Erfüllt Z-2 auch bei großen Tool-Mengen nachweisbar.

**Negativ:**
- Komplexeste Variante: zwei Sichtbarkeitspfade müssen konsistent gehalten werden (RBAC muss in `tools/list` **und** `search_tools`/`invoke_tool` identisch greifen).
- Tuning-Aufwand: welche Tools gepinnt werden, muss jemand entscheiden (Token-Cockpit FR-15/FR-38 hilft).

## Vorschlag des Autors

Option 2. Die Konsistenzpflicht der zwei Pfade ist real, aber lösbar, indem *eine* Autorisierungs-/Sichtbarkeitskomponente (`IToolCatalog` mit Profil-Filter) beide Pfade speist — dann kann es keine Divergenz geben. Der Restaufwand ist Konfiguration, kein Architekturproblem.

## Entscheidung

**Gewählte Option:** „Hybrid — Pinned plus Lazy"

Nur der Hybrid erfüllt Z-2 für alle Agent-Typen (spezialisierte wie explorative). Die höhere Komplexität wird durch eine einzige gemeinsame Katalog-/Filterkomponente beherrscht.

## Konsequenzen

### Positiv

- ≥ 80 % Token-Ersparnis erreichbar, ohne häufige Workflows zu verlangsamen.
- Profile sind zugleich das RBAC-Sichtbarkeitsinstrument — ein Konzept, zwei Nutzen.

### Negativ

- `search_tools` braucht eine brauchbare Suche (Keyword + Beschreibungs-Matching); schlechte Treffer = Agent findet Tools nicht.
- Meta-Tool-Pfad umgeht client-seitige Schema-Validierung → Gateway muss Argumente serverseitig gegen das Schema validieren, bevor er upstream ruft.

### Folge-Entscheidungen

- Such-Index für `search_tools` (v1: In-Memory-Keyword-Score; v2: optional BM25/Embedding) — Entscheidung bei Bedarf als eigenes ADR.
- Token-Schätzverfahren für das Cockpit (v1: chars/4-Heuristik pro Schema).

### Review

**Reality-Check geplant für:** 2026-09-15

## Weitere Informationen

### Referenzen

- [PRD 0001, FR-11 bis FR-16](../prd/0001-mcp-mcp-meta-gateway.md)
- Vergleichbare Muster: mcpproxy-go (BM25-Filterung), MarimerLLC/mcp-aggregator (lazy discovery), Anthropic „Tool Search Tool"-Pattern
