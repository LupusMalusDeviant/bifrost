using System.Globalization;
using McpMcp.Abstractions;

namespace McpMcp.Upstream.Cli;

/// <summary>
/// Baut die Argumente, mit denen ein CLI-Kommando in einem Container läuft (ADR-0018).
/// <para>
/// Ein Job je Aufruf (<c>--rm</c>), kein langlebiger Worker: Der Container endet mit dem Kommando,
/// und das Aufräumen hängt nicht daran, dass jemand später etwas stoppt.
/// </para>
/// <para>
/// Die Mindestpolicy aus ADR-0018 steht hier an <b>einer</b> Stelle, damit sie prüfbar ist statt
/// über den Aufrufpfad verstreut. Jede Zeile hat einen Grund, und die Gründe stehen daneben.
/// </para>
/// </summary>
internal static class ContainerLaunchPolicy
{
    /// <summary>
    /// Argumente für <c>docker run</c>/<c>podman run</c>, ohne das Kommando selbst. Der Aufrufer
    /// hängt Executable und Tool-Argumente an — dieselbe literale Übergabe wie im Host-Modus, nie
    /// über eine Shell.
    /// </summary>
    public static IReadOnlyList<string> BuildRunArguments(
        CliTransportOptions options, CliIsolationOptions isolation)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isolation);
        if (string.IsNullOrWhiteSpace(isolation.Image))
        {
            throw new ArgumentException("Container-Modus ohne Image ist nicht ausführbar.", nameof(isolation));
        }

        var arguments = new List<string>
        {
            "run",
            // Der Container verschwindet mit dem Aufruf. Ohne das sammelten sich beendete
            // Container an, bis jemand aufräumt — und „jemand" ist im Betrieb niemand.
            "--rm",
            // Kein Terminal, kein Stdin: Das Kommando bekommt seine Eingaben über Argumente.
            "--interactive=false",
            // Wurzeldateisystem read-only. Schreibbar ist nur, was ausdrücklich eingehängt wird.
            "--read-only",
            "--user", isolation.User,
            // Alle Linux-Capabilities weg und keine neuen dazu — ein setuid-Binary im Image kann
            // sich damit nicht hochziehen.
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", isolation.PidLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{isolation.MemoryLimitMb}m",
            "--cpus", isolation.CpuLimit.ToString(CultureInfo.InvariantCulture),
            // Beschreibbares /tmp, sonst scheitern Programme mit Temporärdateien am read-only
            // Wurzeldateisystem — mit einer Meldung, die niemand auf diese Ursache zurückführt.
            "--tmpfs", $"/tmp:rw,noexec,nosuid,size={isolation.TmpfsSizeMb}m",
        };

        // Netzwerk aus, wenn nichts erlaubt ist. Das ist die Vorgabe und nicht der Sonderfall:
        // Ein vergessenes Feld darf keinen Netzzugang öffnen.
        if (isolation.NetworkAllow is not { Count: > 0 })
        {
            arguments.Add("--network");
            arguments.Add("none");
        }
        else
        {
            // Ein Bridge-Netz plus Ziel-Allowlist gehört in eine eigene Netzwerkkonfiguration;
            // solange die nicht steht, wäre "--network bridge" ein offenes Netz mit dem Etikett
            // "Allowlist". Deshalb hier ausdrücklich abgelehnt statt stillschweigend geöffnet.
            throw new NotSupportedException(
                "Eine Netzwerk-Allowlist im Container-Modus ist noch nicht umgesetzt. "
                + "Bis dahin läuft der Container ohne Netzwerk — ein offenes Bridge-Netz mit dem "
                + "Etikett 'Allowlist' wäre schlimmer als eine ehrliche Absage.");
        }

        // Mounts ausschließlich aus den kanonischen Allowlisten, die der Host-Modus schon
        // durchsetzt. Lesend zuerst, damit ein Pfad, der in beiden Listen steht, nicht versehentlich
        // nur lesbar endet.
        foreach (var root in options.AllowedReadRoots ?? [])
        {
            arguments.Add("--volume");
            arguments.Add($"{root}:{root}:ro");
        }

        foreach (var root in options.AllowedWriteRoots ?? [])
        {
            arguments.Add("--volume");
            arguments.Add($"{root}:{root}:rw");
        }

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            arguments.Add("--workdir");
            arguments.Add(options.WorkingDirectory);
        }

        // Secrets als NAME ohne Wert: Die Runtime liest den Wert aus ihrer eigenen Umgebung. Mit
        // "NAME=wert" stünde das Geheimnis in der Kommandozeile des Container-Prozesses und wäre
        // für jeden lesbar, der die Prozessliste sieht.
        foreach (var name in (options.EnvironmentVariables ?? new Dictionary<string, string>()).Keys)
        {
            arguments.Add("--env");
            arguments.Add(name);
        }

        arguments.Add(isolation.Image);
        return arguments;
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
    /// <b>Kein stiller Rückfall</b> (ADR-0018): Wer Container verlangt und sie nicht bekommen kann,
    /// bekommt eine Absage — nicht heimlich einen Host-Prozess ohne Isolation.
    /// </para>
    /// </summary>
    public static async Task<string?> ProbeAsync(CliIsolationOptions isolation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(isolation);
        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = isolation.Runtime,
                // Die Plattform des *Servers*, nicht des Clients: Docker Desktop auf Windows kann
                // beides, und nur der Linux-Modus trägt die Policy.
                ArgumentList = { "version", "--format", "{{.Server.Os}}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (probe is null)
            {
                return $"Container-Runtime '{isolation.Runtime}' ließ sich nicht starten.";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var platform = await probe.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await probe.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (probe.ExitCode != 0)
            {
                return $"Container-Runtime '{isolation.Runtime}' antwortet nicht (Exitcode {probe.ExitCode}).";
            }

            platform = platform.Trim();
            return platform.Equals("linux", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Container-Runtime '{isolation.Runtime}' läuft im Modus '{platform}'. "
                    + "Die Mindestpolicy aus ADR-0018 (read-only Wurzeldateisystem, cap-drop, "
                    + "Nicht-root-Benutzer) trägt nur mit Linux-Containern.";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or OperationCanceledException or IOException)
        {
            return $"Container-Runtime '{isolation.Runtime}' ist nicht erreichbar: {exception.Message}";
        }
    }
}
