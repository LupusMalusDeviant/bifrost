# ADR-0012: Approval-Flows asynchron statt blockierend

- **Status:** Akzeptiert; technisch abgelöst am 2026-07-25 durch
  [ADR-0019](0019-langlaufende-tasks-und-events.md); Weg zum Menschen ergänzt am 2026-07-31 durch
  [ADR-0023](0023-stateless-kern-und-mrtr.md)
- **Datum:** 2026-07-20
- **Betrifft:** FR-32, FR-09 (Call-Timeout), ADR-0008 (Invocation-Kern)

> **Ergänzung 2026-07-25 (ADR-0019).** Die Entscheidung dieser ADR — sofort ablehnen statt
> blockieren, Freigabe pro Call — bleibt unverändert gültig. Was sich ändert, ist der Unterbau: Die
> Freigabe-Anfrage geht im Task-Modell auf und wird der Task-Zustand `input-required`. Eine Tabelle,
> eine API, eine Liste in der UI, statt zweier Warteschlangen nebeneinander. Bestehende wartende
> Freigaben werden migriert, nicht verworfen; der heiße Pfad (`TryConsumeApprovalAsync` vor jedem
> Call) braucht dafür einen Index auf (Eigentümer, Tool, Eingabe-Fingerprint, Zustand).
>
> **Vollzogen am 2026-07-26.** `IApprovalStore` bleibt der Vertrag; der Unterbau ist die
> Vorgangs-Tabelle. Der Index existierte bereits und ist mitgewandert. Die Einmaligkeit dieser ADR
> hängt jetzt an einem Claim-Zeitpunkt, weil der Task-Zustandsautomat "freigegeben" und "eingelöst"
> nicht unterscheidet.

> **Ergänzung 2026-07-31 (ADR-0023).** Der Weg zum Menschen hat eine zweite Bauform bekommen: Die
> Spec-Revision `2026-07-28` ersetzt die server-initiierte Rückfrage durch **MRTR** — der Aufruf
> endet mit `input_required`, der Client zeigt das Formular und wiederholt den Aufruf mit der
> Antwort. Für einen Client auf diesem Stand ist das der einzige Weg; die alte Rückfrage verweigert
> das SDK ohne Sitzung ausdrücklich.
>
> **Die Regeln dieser ADR gelten unverändert für beide Bauformen:** sofort ablehnen statt
> blockieren, Freigabe für genau einen Call, und nur ein ausdrückliches `accept` mit gesetztem
> Häkchen ist eine Zustimmung. Neu ist einzig, dass der Zustand zwischen Frage und Antwort über den
> Client läuft — er ist deshalb mit dem DataProtection-Key-Ring geschützt und an Identität und
> Werkzeug gebunden.

## Kontext

FR-32 verlangt, dass bestimmte Tools **pro Call eine menschliche Freigabe** erfordern, mit einer
Queue in der Web-UI. Der offensichtliche Weg — den `tools/call` blockieren, bis ein Mensch
freigibt — kollidiert direkt mit FR-09: Jeder Call hat einen Timeout (Standard 60 s), und ein
Gateway, das Calls minutenlang offen hält, widerspricht seinem eigenen Fault-Isolation-Versprechen.
Ein blockierender Call bindet außerdem eine In-Flight-Reservierung und einen Agenten, der headless
wartet.

## Entscheidung

**Asynchrones Modell: sofort ablehnen, Freigabe läuft nebenher.**

1. Trifft ein Call auf ein freigabepflichtiges Tool und liegt **keine** gültige Freigabe vor, wird
   er **nicht** ausgeführt. Der Invoker legt eine **Approval-Anfrage** in eine persistente Queue
   und kehrt sofort mit dem neuen Status **`ApprovalRequired`** zurück — mit einer Meldung, die
   sagt: „Freigabe angefordert, später erneut versuchen."
2. Ein Mensch entscheidet in der UI-Queue über **Freigeben** oder **Ablehnen**. Er sieht dabei die
   **konkreten Argumente** des Calls.
3. Beim **erneuten** Aufruf desselben Tools mit denselben Argumenten durch dieselbe Identität greift
   die Freigabe: Der Call läuft **einmalig** durch. Danach ist die Freigabe verbraucht.

Die Bindung ist `(Identität, Tool, Argument-Fingerprint)`. Der Fingerprint ist ein Hash der
**redigierten** Argumente — dieselbe Redaction wie im Audit, damit die Queue keine Secrets im
Klartext hält.

## Begründung

- **Kein hängender Agent, kein blockierter Slot.** Das Verhalten fügt sich in das bestehende
  Request/Response-Modell ein, statt ihm zu widersprechen.
- **Der Mensch gibt genau diesen Call frei, nicht „das Tool für 5 Minuten".** Der Argument-Fingerprint
  macht die Freigabe präzise: Wer `delete_file{path:/tmp/x}` freigibt, gibt nicht
  `delete_file{path:/etc/passwd}` frei.
- **Einmalige Freigabe** verhindert, dass eine einmal erteilte Zustimmung zum Dauerfreifahrtschein
  wird. Wiederholung erfordert erneute Freigabe — das ist bei freigabepflichtigen (also heiklen)
  Tools die sichere Vorgabe.

## Konsequenzen

- Der Agent muss den Retry selbst fahren. Das ist zumutbar: Der `ApprovalRequired`-Status und die
  Meldung sagen ausdrücklich, dass und warum. Kein Meta-Tool und kein Polling-Protokoll nötig.
- Eine Freigabe hat ein **Verfallsfenster** (Vorschlag: 1 h). Eine Zustimmung, die niemand einlöst,
  soll nicht ewig scharf bleiben.
- Neuer Status `InvocationStatus.ApprovalRequired` (Wert am Ende — persistierte Zahlen). Er ist von
  `Denied` unterscheidbar: `Denied` heißt „darf nie", `ApprovalRequired` heißt „darf nach Freigabe".
- Die Queue ist persistent — ein Gateway-Neustart darf offene Anfragen nicht verlieren. Sie
  speichert den Argument-**Fingerprint** und die redigierten Argumente zur Anzeige, nie die rohen.
- Ab wann ein Tool freigabepflichtig ist, wird pro Tool konfiguriert (wie Description-Overrides und
  Guard-Regeln), zur Laufzeit über die UI, ohne Neustart.

## Verworfen

- **Blockierend warten** (Call bleibt offen bis Freigabe/Timeout): widerspricht FR-09, bindet Slot
  und Agent, und ein Approval-Timeout von Minuten ist genau die Latenz, die das Gateway sonst
  vermeidet.
- **Freigabe pro `(Identität, Tool)` ohne Argumentbindung:** zu grob — gäbe eine ganze Tool-Klasse
  frei statt des konkreten, geprüften Calls.
- **Meta-Tool `check_approval`:** eigener Protokoll-Umweg, der den Agenten zu einem Polling-Client
  macht; der schlichte Retry desselben Calls ist einfacher und braucht keine neue Tool-Oberfläche.
