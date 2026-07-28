# ADR-0018: Native Prozess- und Container-Isolation

> **Entscheidungsmaterial zu stdio (2026-07-28):**
> [0018-entscheidungsmaterial-stdio.md](0018-entscheidungsmaterial-stdio.md) — fünf Optionen mit
> Kosten und Bruchstellen. Noch nicht entschieden.

- **Status:** Vorgeschlagen; der **Container-Modus für CLI-Upstreams** ist am 2026-07-26 umgesetzt
  und an einer laufenden Runtime belegt. Offen bleiben die Netzwerk-Allowlist und der Modus für
  stdio-Upstreams.

> **Umsetzungsstand 2026-07-26.** Der Modus hängt am bestehenden CLI-Transport
> (`Cli.Isolation.Mode = Container`), nicht an einem neuen Transport: Was sich ändert, ist die
> **Ausführung**, nicht der Vertrag — typisierte Manifeste, Argumentbindung, Ausgabegrenzen und
> Prozessbaum-Kill bleiben dieselben. Ohne den Abschnitt verhält sich eine bestehende Konfiguration
> unverändert.
>
> Umgesetzt aus der Mindestpolicy: read-only Wurzeldateisystem, fester Nicht-root-Benutzer, alle
> Capabilities entfernt, `no-new-privileges`, CPU-/RAM-/PID-Grenzen, beschreibbares `/tmp` als
> tmpfs, Netzwerk **aus**, Mounts ausschließlich aus den kanonischen Read-/Write-Allowlisten (den
> gleichen, die der Host-Modus schon durchsetzt), ein Job je Aufruf (`--rm`).
>
> Secrets gehen als `--env NAME` **ohne Wert** mit: Die Runtime liest den Wert aus ihrer eigenen
> Umgebung. Mit `NAME=wert` stünde das Geheimnis in der Kommandozeile des Container-Prozesses und
> wäre für jeden lesbar, der die Prozessliste sieht.
>
> **Kein stiller Rückfall** ist als Verhalten gebaut und getestet: Verlangt eine Konfiguration
> Container und ist keine passende Runtime da, kommt der Upstream nicht hoch — mit einer Meldung,
> die das sagt. Geprüft wird dabei, ob die Runtime die Policy **durchsetzen kann**, nicht nur ob sie
> antwortet: Docker im Windows-Container-Modus antwortet und lehnt danach read-only, cap-drop und
> Nicht-root ab. Genau dieser Fall ist beim ersten CI-Lauf aufgefallen — der erste Probe fragte nur
> die Erreichbarkeit ab und hätte den Upstream dort mit ausgefallener Härtung hochkommen lassen.
>
> **Zwei Punkte ausdrücklich offen**, statt halb gebaut: Die **Netzwerk-Allowlist** wird abgelehnt
> statt als offenes Bridge-Netz durchgereicht — ein offenes Netz mit dem Etikett „Allowlist" wäre
> schlimmer als eine ehrliche Absage. Und **stdio-Upstreams** laufen weiterhin nur im Hostmodus;
> ihr Vertrag ist eine langlebige Verbindung, kein Job je Aufruf, und das ist ein eigener Entwurf.
>
> Belegt an einer laufenden Runtime: 7 Tests (Ausführung im Container, Nicht-root, read-only,
> beschreibbares `/tmp`, kein Netz, Secret-Zustellung ohne Kommandozeile, verweigerter Rückfall)
> plus 8 Tests auf den Aufbau der Argumente, die ohne Runtime laufen.
- **Datum:** 2026-07-24

## Kontext

Nicht jede bestehende CLI kann als WebAssembly Component geliefert werden. Direkte Hostprozesse
haben Zugriff auf Kernel, Benutzerrechte und jede versehentlich geerbte Ressource. Shell-freie
Argumentübergabe verhindert Command Injection, bildet aber keine Sandbox.

## Entscheidung

MCPMCP bietet drei explizite Runtime-Modi:

1. **WASI Component:** Default für neue Plugins.
2. **Native Container:** Default für vorhandene, nicht vertrauenswürdige CLI-/stdio-Programme.
3. **Trusted Host Process:** nur mit absolutem kanonischem Pfad, Root-Allowlist, optionalem Hash-Pin
   und ausdrücklicher Admin-Freigabe; PATH-Auflösung nur Development.

Der Container-Modus verwendet pro Upstream einen langlebigen Worker nur, wenn Startupkosten dies
erzwingen; sonst einen Job pro Invocation. Mindestpolicy:

- read-only Root-Filesystem und eigener nicht-root Benutzer;
- alle Linux Capabilities entfernt, no-new-privileges, seccomp/AppArmor soweit verfügbar;
- CPU-, RAM-, PID-, Prozess-, Output- und Ephemeral-Disk-Limits;
- Netzwerk aus, außer expliziter Ziel-Allowlist;
- Mounts nur aus kanonischen read-only/read-write Allowlists;
- Secrets als kurzlebige In-Memory-/File-Descriptor-Injection, nie Image oder persistentes Volume;
- Prozessbaum-Kill, Container-Stop und nachweisbarer Cleanup bei Timeout, Cancellation und Shutdown.

Ohne Container-Runtime darf eine Konfiguration entweder im Trusted-Modus laufen oder wird mit einer
präzisen Readiness-/Validierungsfehlermeldung abgewiesen. Ein stiller Fallback vom Container auf den
Host ist verboten.

## Bereits umgesetzte Host-Basis

Der direkte CLI-Connector begrenzt Streams während des Lesens, trennt stdout/stderr, leert das
Host-Environment, verlangt standardmäßig absolute Pfade, prüft Roots/Links, unterstützt SHA-256-
Pinning, begrenzt Parallelität und beendet Prozessbäume. Das reduziert Risiko, ersetzt aber weder
Container- noch WASI-Isolation.

## Konsequenzen

Windows- und Linux-Container benötigen getrennte Betriebsnachweise. Kubernetes ist kein Zwang; ein
lokaler OCI-kompatibler Runtimeadapter reicht für v1. Die Runtime-Schnittstelle bleibt vom
Protokoll-Connector getrennt, damit stdio, CLI und zukünftige native Connectoren dieselbe Isolation
nutzen.
