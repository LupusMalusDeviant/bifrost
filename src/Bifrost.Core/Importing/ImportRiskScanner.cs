using System.Globalization;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Importing;

/// <summary>Was die Risikoprüfung an einem Server gefunden hat.</summary>
public sealed record ImportRiskReport(
    IReadOnlyList<ImportFinding> Findings,
    IReadOnlyList<ImportSecret> Secrets);

/// <summary>
/// Die Risikoindikatoren eines importierten Servers (Pflichtenheft WP4.1).
/// <para>
/// <b>Nichts hiervon verbietet etwas.</b> Ein <c>npx -y</c>-Server ist eine legitime Konfiguration
/// und zugleich eine, die beim Start beliebigen Code aus dem Netz nachlädt; ein Ziel im eigenen Netz
/// ist der Regelfall und zugleich das, womit ein Gateway zum Werkzeug für interne Dienste wird. Der
/// Unterschied gehört sichtbar gemacht, nicht wegentschieden — deshalb tragen diese Befunde
/// <see cref="ImportSeverity.Risk"/> und blockieren den Plan nicht.
/// </para>
/// <para>
/// <b>Genau eine Ausnahme:</b> Sagt die Ausführungs-Policy dieser Instanz nein, ist das ein
/// <see cref="ImportSeverity.Error"/>. Nicht weil diese Klasse das entschieden hätte, sondern weil
/// derselbe Torposten den späteren Anlegeversuch abweisen würde — ein Plan, der als „anwendbar"
/// gilt und beim Anwenden scheitert, ist eine Falschaussage.
/// </para>
/// </summary>
public static class ImportRiskScanner
{
    /// <summary>Programme, die beim Start Code aus dem Netz nachladen.</summary>
    private static readonly HashSet<string> FetchingLaunchers = new(StringComparer.Ordinal)
    {
        "npx", "uvx", "pnpx", "bunx", "dlx", "pipx",
    };

    /// <summary>
    /// Paketwerkzeuge, die erst mit einem bestimmten Unterbefehl nachladen — <c>npm exec</c>,
    /// <c>pnpm dlx</c>, <c>yarn dlx</c>, <c>bun x</c>, <c>uv tool run</c>.
    /// </summary>
    private static readonly Dictionary<string, string[]> FetchingSubcommands = new(StringComparer.Ordinal)
    {
        ["npm"] = ["exec", "x"],
        ["pnpm"] = ["dlx", "exec"],
        ["yarn"] = ["dlx"],
        ["bun"] = ["x"],
        ["uv"] = ["tool", "run"],
        ["pip"] = ["install"],
        ["pipx"] = ["run"],
    };

    /// <summary>Container-Runtimes, deren Kommandozeile ein Image trägt.</summary>
    private static readonly HashSet<string> ContainerRuntimes = new(StringComparer.Ordinal)
    {
        "docker", "podman", "nerdctl", "finch",
    };

    /// <summary>
    /// Optionen einer Container-Runtime, die einen eigenen Wert nachziehen. Ohne diese Liste hielte
    /// der Bildsucher den Wert von <c>-e</c> für den Imagenamen.
    /// </summary>
    private static readonly HashSet<string> ValueTakingContainerFlags = new(StringComparer.Ordinal)
    {
        "-e", "--env", "--env-file", "-v", "--volume", "--mount", "-p", "--publish", "-w",
        "--workdir", "-u", "--user", "--name", "--network", "--net", "--entrypoint", "-l",
        "--label", "--add-host", "--device", "--platform", "--pull", "--memory", "-m", "--cpus",
        "--tmpfs", "--hostname", "-h", "--restart", "--log-driver",
    };

    /// <summary>
    /// Prüft einen normalisierten Server auf die Risikoindikatoren und markiert Zugangsdaten.
    /// </summary>
    /// <param name="path">
    /// Der Ort im Quelldokument (etwa <c>mcpServers/github</c>). Ohne Ort ist ein Befund über eine
    /// Datei mit dreißig Servern eine Suchaufgabe.
    /// </param>
    /// <param name="hostExecution">
    /// Die Policy dieser Instanz (ADR-0025 E4). <c>null</c> heißt nicht „egal", sondern
    /// <see cref="HostExecutionReason.Undetermined"/> — und damit nein.
    /// </param>
    [HostExecutionChecked(Note = "Fragt HostExecutionGuard und uebernimmt dessen Urteil unveraendert.")]
    public static ImportRiskReport Scan(string path, UpstreamServerConfig config, IHostExecutionPolicy? hostExecution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(config);

        var findings = new List<ImportFinding>();
        var secrets = new List<ImportSecret>();

        ScanHostExecution(path, config, hostExecution, findings);
        ScanProgram(path, config, findings);
        ScanImages(path, config, findings);
        ScanEnvironment(path, config, findings, secrets);
        ScanArguments(path, config, findings, secrets);
        ScanHeaders(path, config, findings, secrets);
        ScanTargets(path, config, findings);

        return new ImportRiskReport(findings, secrets);
    }

    /// <summary>
    /// <b>BFR-IMP-0100.</b> Die Frage „läuft das nativ und darf es das?" wird hier nicht beantwortet,
    /// sondern gestellt. Es gibt dafür bereits <c>NativeExecution</c> und <c>IHostExecutionPolicy</c>;
    /// eine zweite Beurteilung wäre eine zweite Wahrheit, von der eine veraltet (ADR-0025 E1).
    /// </summary>
    [HostExecutionChecked]
    private static void ScanHostExecution(
        string path, UpstreamServerConfig config, IHostExecutionPolicy? hostExecution, List<ImportFinding> findings)
    {
        var decision = HostExecutionGuard.Evaluate(hostExecution, config);

        if (string.Equals(decision.ReasonCode, HostExecutionReason.NotNative, StringComparison.Ordinal))
        {
            return;
        }

        findings.Add(new ImportFinding(
            ImportReason.HostExecution,
            decision.Allowed ? ImportSeverity.Risk : ImportSeverity.Error,
            $"[{decision.ReasonCode}] {decision.Summary}",
            path,
            decision.Remediation
                ?? "Den Server vor dem Anlegen auf Container-Isolation umstellen, wenn das Programm "
                + "nicht ausdruecklich vertrauenswuerdig ist."));
    }

    /// <summary>
    /// <b>BFR-IMP-0101/0102/0103.</b> Was startet — und entscheidet das die Konfiguration oder die
    /// Umgebung des Dienstes?
    /// </summary>
    private static void ScanProgram(string path, UpstreamServerConfig config, List<ImportFinding> findings)
    {
        var (command, arguments, workingDirectory, field) = config.Kind switch
        {
            UpstreamTransportKind.Stdio when config.Stdio is { } stdio =>
                (stdio.Command, stdio.Arguments, stdio.WorkingDirectory, "Stdio"),
            UpstreamTransportKind.Cli when config.Cli is { } cli =>
                (cli.Executable, (IReadOnlyList<string>)[], cli.WorkingDirectory, "Cli"),
            _ => (null, [], null, string.Empty),
        };

        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var commandPath = $"{path}/{(field == "Cli" ? "executable" : "command")}";

        if (!ImportPathShape.HasDirectoryPart(command))
        {
            findings.Add(new ImportFinding(
                ImportReason.PathLookup,
                ImportSeverity.Risk,
                $"'{command}' steht ohne Verzeichnis da. Welches Programm startet, entscheidet damit "
                + "die PATH-Variable des Gateway-Dienstes und nicht diese Konfiguration.",
                commandPath,
                "Den vollstaendigen Pfad des Programms eintragen — oder den Server im Container "
                + "starten, wo das Programm im Image liegt."));
        }
        else if (!ImportPathShape.IsAbsolute(command))
        {
            findings.Add(new ImportFinding(
                ImportReason.RelativePath,
                ImportSeverity.Risk,
                ImportPathShape.IsEnvironmentRelative(command)
                    ? $"'{command}' haengt an einer Umgebungsvariablen oder am Heimatverzeichnis. Der "
                        + "Wert wird hier ausdruecklich NICHT aufgeloest: Auf der Zielinstanz zeigt er "
                        + "woandershin als auf der Quellmaschine."
                    : $"'{command}' ist ein relativer Pfad. Worauf er zeigt, entscheidet das "
                        + "Arbeitsverzeichnis des Gateway-Dienstes.",
                commandPath,
                "Den Pfad auf der Zielinstanz absolut angeben."));
        }

        if (workingDirectory is { Length: > 0 } && !ImportPathShape.IsAbsolute(workingDirectory))
        {
            findings.Add(new ImportFinding(
                ImportReason.RelativePath,
                ImportSeverity.Risk,
                $"Das Arbeitsverzeichnis '{workingDirectory}' ist nicht absolut angegeben.",
                $"{path}/{(field == "Cli" ? "cli" : "stdio")}/workingDirectory",
                "Absoluten Pfad eintragen; der Validator weist relative Arbeitsverzeichnisse ab."));
        }

        ScanFetchingLauncher(path, command, arguments, commandPath, findings);
    }

    /// <summary><b>BFR-IMP-0103.</b> Lädt dieses Kommando beim Start Code aus dem Netz nach?</summary>
    private static void ScanFetchingLauncher(
        string path,
        string command,
        IReadOnlyList<string> arguments,
        string commandPath,
        List<ImportFinding> findings)
    {
        var program = ImportPathShape.Program(command);
        var fetches = FetchingLaunchers.Contains(program);

        if (!fetches && FetchingSubcommands.TryGetValue(program, out var subcommands))
        {
            // Der Unterbefehl steht vorn, hinter ihm folgt das Paket. Weiter hinten kann derselbe
            // Text ein Paketname sein — deshalb nur die ersten beiden Argumente.
            fetches = arguments.Take(2).Any(argument =>
                subcommands.Contains(argument, StringComparer.Ordinal));
        }

        if (!fetches)
        {
            return;
        }

        var confirmsAutomatically = arguments.Any(argument =>
            string.Equals(argument, "-y", StringComparison.Ordinal)
            || string.Equals(argument, "--yes", StringComparison.Ordinal));

        findings.Add(new ImportFinding(
            ImportReason.FetchesCodeAtStart,
            ImportSeverity.Risk,
            $"'{program}' laedt das Programm beim Start aus einem Paketregister nach"
            + (confirmsAutomatically
                ? " und bestaetigt die Installation selbst (-y)."
                : ".")
            + " Was laeuft, steht damit nicht in dieser Konfiguration, sondern im Register — und kann "
            + "sich zwischen zwei Starts aendern.",
            commandPath,
            "Die Paketversion festnageln, das Paket vorab installieren und mit absolutem Pfad "
            + "starten, oder den Server im Container mit festgelegtem Image ausfuehren."));
    }

    /// <summary>
    /// <b>BFR-IMP-0104.</b> Ein Image ohne Digest — in der Isolationsangabe wie in einer
    /// <c>docker run</c>-Kommandozeile.
    /// </summary>
    private static void ScanImages(string path, UpstreamServerConfig config, List<ImportFinding> findings)
    {
        var isolation = config.Stdio?.Isolation ?? config.Cli?.Isolation;
        if (isolation is { Mode: IsolationMode.Container, Image: { Length: > 0 } declaredImage })
        {
            ReportImage(declaredImage, $"{path}/isolation/image", findings);
        }

        var command = config.Stdio?.Command ?? config.Cli?.Executable;
        if (command is null || !ContainerRuntimes.Contains(ImportPathShape.Program(command)))
        {
            return;
        }

        var arguments = config.Stdio?.Arguments ?? [];
        var image = FindContainerImage(arguments);
        if (image is null)
        {
            findings.Add(new ImportFinding(
                ImportReason.UnpinnedImage,
                ImportSeverity.Warning,
                "Der Server startet eine Container-Runtime, aber welches Image gemeint ist, liess sich "
                + "aus der Kommandozeile nicht bestimmen. Es wird nicht geraten — ob das Image "
                + "festgenagelt ist, bleibt hier offen.",
                $"{path}/args",
                "Das Image von Hand pruefen: Nur die Form repo@sha256:… legt fest, was laeuft."));
            return;
        }

        ReportImage(image, $"{path}/args", findings);
    }

    private static void ReportImage(string image, string path, List<ImportFinding> findings)
    {
        if (ImageReference.Parse(image).Pin is ImagePinKind.Digest)
        {
            return;
        }

        findings.Add(new ImportFinding(
            ImportReason.UnpinnedImage,
            ImportSeverity.Risk,
            ImageReference.DescribeRisk(image)
                ?? $"Image '{image}' ist nicht per Digest festgelegt.",
            path,
            "Das Image per Digest angeben (repo@sha256:…) — ein Tag ist ein Zeiger und kann "
            + "umgehaengt werden, ohne dass hier etwas anders aussieht."));
    }

    /// <summary>
    /// Das erste Argument einer Container-Kommandozeile, das keine Option und kein Optionswert ist.
    /// Liefert <c>null</c>, wenn sich das nicht sicher sagen lässt — geraten wird nicht.
    /// </summary>
    private static string? FindContainerImage(IReadOnlyList<string> arguments)
    {
        var index = 0;

        // Der Unterbefehl. Nur 'run' und 'create' tragen ein Image an dieser Stelle.
        while (index < arguments.Count && arguments[index].StartsWith('-'))
        {
            index++;
        }

        if (index >= arguments.Count
            || arguments[index] is not ("run" or "create"))
        {
            return null;
        }

        index++;

        while (index < arguments.Count)
        {
            var argument = arguments[index];
            if (!argument.StartsWith('-'))
            {
                return argument;
            }

            // '--env=FOO=bar' trägt den Wert bei sich; '--env FOO=bar' zieht ihn nach.
            if (!argument.Contains('=', StringComparison.Ordinal)
                && ValueTakingContainerFlags.Contains(argument))
            {
                index++;
            }

            index++;
        }

        return null;
    }

    /// <summary><b>BFR-IMP-0106.</b> Zugangsdaten in Umgebungsvariablen.</summary>
    private static void ScanEnvironment(
        string path, UpstreamServerConfig config, List<ImportFinding> findings, List<ImportSecret> secrets)
    {
        var environment = config.Stdio?.EnvironmentVariables ?? config.Cli?.EnvironmentVariables;
        if (environment is null)
        {
            return;
        }

        foreach (var entry in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var verdict = ImportSecretDetection.InspectEnvironment(entry.Key, entry.Value);
            Report(verdict, $"Umgebungsvariable '{entry.Key}'", $"{path}/env/{entry.Key}", findings, secrets);
        }
    }

    /// <summary>
    /// <b>BFR-IMP-0106.</b> Zugangsdaten auf der Kommandozeile.
    /// <para>
    /// Ein Token als Argument ist schlimmer als eines in einer Umgebungsvariablen: Es steht in der
    /// Prozessliste des Rechners und damit für jeden lesbar, der dort einen Prozess sehen darf.
    /// Der Befund selbst nennt nur die Position, nie den Wert.
    /// </para>
    /// </summary>
    private static void ScanArguments(
        string path, UpstreamServerConfig config, List<ImportFinding> findings, List<ImportSecret> secrets)
    {
        var arguments = config.Stdio?.Arguments;
        if (arguments is null)
        {
            return;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var position = index.ToString(CultureInfo.InvariantCulture);

            // '--api-key=…' trägt den Namen bei sich; darauf greift dieselbe Namensheuristik wie
            // bei einer Umgebungsvariablen.
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var verdict = argument.StartsWith('-') && separator > 0
                ? ImportSecretDetection.InspectEnvironment(
                    argument[..separator].TrimStart('-'), argument[(separator + 1)..])
                : ImportSecretDetection.InspectValueOnly(argument);

            Report(verdict, $"Kommandoargument an Position {position}", $"{path}/args[{position}]",
                findings, secrets);
        }
    }

    /// <summary><b>BFR-IMP-0106.</b> Zugangsdaten in HTTP-Headern (Klartextheader).</summary>
    private static void ScanHeaders(
        string path, UpstreamServerConfig config, List<ImportFinding> findings, List<ImportSecret> secrets)
    {
        if (config.Http?.Headers is not { } headers)
        {
            return;
        }

        foreach (var entry in headers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var verdict = ImportSecretDetection.InspectHeader(entry.Key, entry.Value);
            Report(verdict, $"HTTP-Header '{entry.Key}'", $"{path}/headers/{entry.Key}", findings, secrets);
        }
    }

    /// <summary>
    /// Trägt einen Fund ein. <b>Der Wert taucht hier nirgends auf</b> — weder im Befund noch in der
    /// Fundstelle. Was gemeldet wird, ist der Ort und der Grund.
    /// </summary>
    private static void Report(
        ImportSecretVerdict verdict,
        string location,
        string path,
        List<ImportFinding> findings,
        List<ImportSecret> secrets)
    {
        if (!verdict.IsSecret)
        {
            return;
        }

        secrets.Add(new ImportSecret(location, verdict.Looked, verdict.ValuePresent));

        if (verdict.Masked)
        {
            findings.Add(new ImportFinding(
                ImportReason.MaskedValue,
                ImportSeverity.Warning,
                $"{location}: Der Wert ist maskiert oder eine Verweisform. Er wird NICHT rekonstruiert "
                + "— ein erratener Wert, der fast stimmt, ist schlimmer als ein fehlender.",
                path,
                "Das Zugangsdatum auf der Zielinstanz nachtragen, bevor der Server eingeschaltet wird."));
            return;
        }

        if (verdict.ValuePresent)
        {
            findings.Add(new ImportFinding(
                ImportReason.PlaintextSecret,
                ImportSeverity.Risk,
                $"{location}: Die Quelle traegt das Zugangsdatum im Klartext ({verdict.Looked}).",
                path,
                "Nach dem Anlegen im Gateway hinterlegen und die Quelldatei als kompromittiert "
                + "behandeln — wer sie gelesen hat, hat das Zugangsdatum."));
        }
    }

    /// <summary><b>BFR-IMP-0105.</b> Ziele im privaten, Loopback- oder Link-Local-Netz.</summary>
    private static void ScanTargets(string path, UpstreamServerConfig config, List<ImportFinding> findings)
    {
        Check(config.Http?.Endpoint, $"{path}/url");
        Check(config.OpenApi?.SpecLocation, $"{path}/openapi/specLocation");
        Check(config.OpenApi?.BaseAddress, $"{path}/openapi/baseAddress");
        Check(config.OpenRpc?.Endpoint, $"{path}/openrpc/endpoint");
        Check(config.OpenRpc?.SpecLocation, $"{path}/openrpc/specLocation");

        void Check(Uri? target, string targetPath)
        {
            if (target is null || ImportNetworkTarget.Classify(target) is not ImportTargetReach.Private)
            {
                return;
            }

            findings.Add(new ImportFinding(
                ImportReason.PrivateTarget,
                ImportSeverity.Risk,
                $"Das Ziel '{target.Host}' liegt im privaten, Loopback- oder Link-Local-Netz. Ein "
                + "Gateway, das solche Adressen abruft, ist ein Weg, interne Dienste zu erreichen "
                + "(SSRF). Beurteilt wird nur die Schreibweise: Aufgeloest wird hier nichts, der "
                + "Import fasst kein Netz an.",
                targetPath,
                "Wenn der Dienst wirklich im eigenen Netz steht, AllowPrivateTargets beim Anlegen "
                + "ausdruecklich setzen — sonst weist der Transport das Ziel ab."));
        }
    }
}
