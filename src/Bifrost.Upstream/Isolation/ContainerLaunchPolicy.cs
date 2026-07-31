using System.Globalization;

using Bifrost.Abstractions;

namespace Bifrost.Upstream.Isolation;

/// <summary>
/// Wie lange der Container lebt. Das ist der <b>einzige</b> Unterschied zwischen den beiden
/// Transporten — und er ist ein Parameter, kein zweiter Startweg.
/// </summary>
internal enum ContainerLifetime
{
    /// <summary>
    /// Ein Job je Aufruf (CLI, ADR-0014/0018). Der Container endet mit dem Kommando; das Aufräumen
    /// hängt nicht daran, dass jemand später etwas stoppt.
    /// </summary>
    PerInvocation = 0,

    /// <summary>
    /// Eine stehende Sitzung (stdio, ADR-0025 E5). Der Vertrag dieses Transports ist eine
    /// langlebige Verbindung über stdin/stdout — deshalb bleibt stdin offen, und das Abräumen ist
    /// eine eigene Handlung.
    /// </summary>
    Session = 1,
}

/// <summary>Was ein Start braucht, unabhängig vom Transport.</summary>
/// <param name="Isolation">Die Container-Optionen aus der Konfiguration.</param>
/// <param name="Identity">Name und Etiketten — die Grundlage des Abräumens.</param>
/// <param name="Lifetime">Job je Aufruf oder stehende Sitzung.</param>
/// <param name="ReadOnlyRoots">Kanonische Lese-Allowlist; wird read-only eingehängt.</param>
/// <param name="WritableRoots">Kanonische Schreib-Allowlist; nur diese wird beschreibbar.</param>
/// <param name="WorkingDirectory">Arbeitsverzeichnis <em>im</em> Container.</param>
/// <param name="EnvironmentNames">
/// Namen der durchzureichenden Variablen — <b>ohne Werte</b>. Die Runtime liest den Wert aus ihrer
/// eigenen Umgebung; mit <c>NAME=wert</c> stünde das Geheimnis in der Kommandozeile.
/// </param>
internal sealed record ContainerLaunchRequest(
    IsolationOptions Isolation,
    ContainerIdentity Identity,
    ContainerLifetime Lifetime,
    IReadOnlyList<string>? ReadOnlyRoots = null,
    IReadOnlyList<string>? WritableRoots = null,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? EnvironmentNames = null);

/// <summary>
/// Baut die Argumente, mit denen ein fremdes Programm in einem Container läuft (ADR-0018,
/// ADR-0025 E5).
/// <para>
/// <b>Eine Stelle für stdio und CLI.</b> Die Mindestpolicy steht hier und nicht über den Aufrufpfad
/// verstreut — zwei Launchmodelle wären zwei Wahrheiten, von denen eine veraltet. Was die beiden
/// Transporte unterscheidet, ist die Lebensdauer, und die ist ein Parameter.
/// </para>
/// <para>
/// Jede Zeile hat einen Grund, und die Gründe stehen daneben.
/// </para>
/// </summary>
internal static class ContainerLaunchPolicy
{
    /// <summary>
    /// Argumente für <c>docker run</c>/<c>podman run</c>, ohne das Kommando selbst. Der Aufrufer
    /// hängt Executable und Argumente an — dieselbe literale Übergabe wie im Host-Modus, nie über
    /// eine Shell.
    /// </summary>
    public static IReadOnlyList<string> BuildRunArguments(ContainerLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var isolation = request.Isolation;
        if (string.IsNullOrWhiteSpace(isolation.Image))
        {
            throw new ArgumentException(
                "Container-Modus ohne Image ist nicht ausfuehrbar.", nameof(request));
        }

        if (!ImageReference.SatisfiesPin(isolation.Image, isolation.RequireImageDigest, out var pin))
        {
            throw new ArgumentException(pin, nameof(request));
        }

        var arguments = new List<string>
        {
            "run",
            // Der Container verschwindet mit dem Aufruf. Ohne das sammelten sich beendete
            // Container an, bis jemand aufraeumt — und "jemand" ist im Betrieb niemand.
            "--rm",
            // Ein Name, unter dem der Container wiederfindbar ist. Ohne ihn waere Abraeumen Raten:
            // `docker run` ist ein Client, kein Elternprozess, und stirbt der Client, laeuft der
            // Container weiter.
            "--name", request.Identity.Name,
            // Wurzeldateisystem read-only. Schreibbar ist nur, was ausdruecklich eingehaengt wird.
            "--read-only",
            "--user", isolation.User,
            // Alle Linux-Capabilities weg und keine neuen dazu — ein setuid-Binary im Image kann
            // sich damit nicht hochziehen.
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", isolation.PidLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{isolation.MemoryLimitMb}m",
            // Speicher- UND Swapgrenze auf denselben Wert: Ohne das darf der Container ueber Swap
            // weiterwachsen, und die RAM-Grenze waere eine Empfehlung.
            "--memory-swap", $"{isolation.MemoryLimitMb}m",
            "--cpus", isolation.CpuLimit.ToString(CultureInfo.InvariantCulture),
            // Beschreibbares /tmp, sonst scheitern Programme mit Temporaerdateien am read-only
            // Wurzeldateisystem — mit einer Meldung, die niemand auf diese Ursache zurueckfuehrt.
            "--tmpfs", $"/tmp:rw,noexec,nosuid,size={isolation.TmpfsSizeMb}m",
        };

        arguments.AddRange(request.Identity.LabelArguments());
        arguments.AddRange(BuildLifetimeArguments(request));
        arguments.AddRange(BuildNetworkArguments(isolation));
        arguments.AddRange(ContainerMountPolicy.Build(request.ReadOnlyRoots, request.WritableRoots));

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            arguments.Add("--workdir");
            arguments.Add(ContainerMountPolicy.Canonicalize(
                request.WorkingDirectory, "Arbeitsverzeichnis"));
        }

        // Secrets als NAME ohne Wert: Die Runtime liest den Wert aus ihrer eigenen Umgebung. Mit
        // "NAME=wert" stuende das Geheimnis in der Kommandozeile des Container-Prozesses und waere
        // fuer jeden lesbar, der die Prozessliste sieht.
        foreach (var name in request.EnvironmentNames ?? [])
        {
            arguments.Add("--env");
            arguments.Add(name);
        }

        arguments.Add(isolation.Image);

        // Ab hier weiss der Aufraeumlauf, dass diese Runtime in Gebrauch ist. Ohne die Notiz muesste
        // er beim Herunterfahren jede denkbare Runtime befragen — oder gar keine.
        ContainerLifecycle.NoteLaunch(isolation.Runtime);
        return arguments;
    }

    private static IReadOnlyList<string> BuildLifetimeArguments(ContainerLaunchRequest request)
        => request.Lifetime switch
        {
            // Kein Terminal, kein Stdin: Das Kommando bekommt seine Eingaben ueber Argumente.
            ContainerLifetime.PerInvocation => ["--interactive=false"],

            ContainerLifetime.Session =>
            [
                // stdin bleibt offen — darueber laeuft das MCP-Protokoll. Ohne `--interactive`
                // bekaeme der Server sofort EOF und beendete sich, bevor die erste Nachricht
                // ankommt.
                "--interactive=true",
                // Kein Pseudo-Terminal: Ein TTY wuerde Zeilen umbrechen und CR einfuegen, und
                // JSON-RPC ueber stdio vertraegt beides nicht.
                "--tty=false",
                "--stop-timeout",
                request.Isolation.StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ],

            _ => throw new ArgumentOutOfRangeException(
                nameof(request), request.Lifetime, "Unbekannte Lebensdauer."),
        };

    /// <summary>
    /// Netzwerk: <b>default-deny</b>. Eine leere Allowlist heißt „kein Netz" und ist die Vorgabe —
    /// ein vergessenes Feld darf keinen Netzzugang öffnen.
    /// <para>
    /// Eine <em>nicht</em> leere Allowlist wird abgewiesen, statt sie zu einem offenen Bridge-Netz
    /// zu machen. Der Grund steht in ADR-0018 und gilt unverändert: Durchsetzen ließe sie sich nur
    /// mit einer eigenen Netzwerkkonfiguration (interner Netzbereich plus filternder Vermittler);
    /// ohne die wäre <c>--network bridge</c> ein offenes Netz mit dem Etikett „Allowlist" — und
    /// damit schlechter als eine ehrliche Absage, weil es nach Schutz aussieht.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> BuildNetworkArguments(IsolationOptions isolation)
    {
        if (isolation.NetworkAllow is not { Count: > 0 })
        {
            return ["--network", "none"];
        }

        throw new NotSupportedException(
            "Eine Netzwerk-Allowlist im Container-Modus ist noch nicht durchsetzbar. Erlaubt ist "
            + "derzeit ausschliesslich die geschlossene Vorgabe (kein Netzwerk); die genannten "
            + $"Ziele ({string.Join(", ", isolation.NetworkAllow)}) waeren ungefiltert erreichbar. "
            + "Ein offenes Bridge-Netz mit dem Etikett 'Allowlist' waere schlimmer als eine "
            + "ehrliche Absage.");
    }

    /// <summary>
    /// Prüft, ob die Container-Runtime die Policy <b>durchsetzen kann</b> — nicht bloß, ob sie
    /// antwortet.
    /// <para>
    /// Der Unterschied ist keine Feinheit: Eine Docker-Installation im Windows-Container-Modus
    /// antwortet bereitwillig und lehnt dann <c>--read-only</c>, <c>--cap-drop</c> und
    /// <c>--user</c> ab. Ein Probe, der nur die Erreichbarkeit prüft, ließe den Upstream dort
    /// hochkommen und die zugesagte Härtung stillschweigend ausfallen. Deshalb wird die
    /// Server-Plattform abgefragt.
    /// </para>
    /// <para>
    /// <b>Kein stiller Rückfall</b> (ADR-0018, ADR-0025 E6): Wer Container verlangt und sie nicht
    /// bekommen kann, bekommt eine Absage — nicht heimlich einen Host-Prozess ohne Isolation.
    /// </para>
    /// </summary>
    public static async Task<string?> ProbeAsync(IsolationOptions isolation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(isolation);
        try
        {
            using var probe = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = isolation.Runtime,
                    // Die Plattform des *Servers*, nicht des Clients: Docker Desktop auf Windows
                    // kann beides, und nur der Linux-Modus traegt die Policy.
                    ArgumentList = { "version", "--format", "{{.Server.Os}}" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            if (probe is null)
            {
                return $"Container-Runtime '{isolation.Runtime}' liess sich nicht starten.";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var platform = await probe.StandardOutput.ReadToEndAsync(timeout.Token)
                .ConfigureAwait(false);
            await probe.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (probe.ExitCode != 0)
            {
                return $"Container-Runtime '{isolation.Runtime}' antwortet nicht "
                    + $"(Exitcode {probe.ExitCode}).";
            }

            platform = platform.Trim();
            return platform.Equals("linux", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Container-Runtime '{isolation.Runtime}' laeuft im Modus '{platform}'. "
                    + "Die Mindestpolicy aus ADR-0018 (read-only Wurzeldateisystem, cap-drop, "
                    + "Nicht-root-Benutzer) traegt nur mit Linux-Containern.";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or OperationCanceledException or IOException)
        {
            return $"Container-Runtime '{isolation.Runtime}' ist nicht erreichbar: "
                + exception.Message;
        }
    }

    /// <summary>
    /// Die Absage, wenn Container verlangt sind und die Runtime nicht trägt. Eine Stelle, damit
    /// stdio und CLI denselben Satz sagen — und ein Test ihn an beiden Wegen findet.
    /// </summary>
    public static string RefusalMessage(string slug, string problem)
        => $"Upstream '{slug}' verlangt Container-Isolation, aber {problem} "
            + "Ein Rückfall auf den Host findet nicht statt (ADR-0018, ADR-0025 E6).";
}
