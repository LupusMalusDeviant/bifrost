using System.Text;
using System.Text.RegularExpressions;

using McpMcp.Abstractions;

namespace McpMcp.Core.Upstreams;

/// <summary>Validiert Upstream-Konfigurationen vor Add/Reconfigure. Wirft <see cref="ArgumentException"/> mit präziser Meldung (DON'T Nr. 6: keine stillen Teilerfolge).</summary>
public static partial class UpstreamConfigValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex CliNamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentNamePattern();

    public static void Validate(UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.Slug) || !SlugPattern().IsMatch(config.Slug))
        {
            throw new ArgumentException(
                $"Slug '{config.Slug}' ist ungültig: erlaubt sind a-z, 0-9, '-' und '_', 1-64 Zeichen, Beginn mit a-z/0-9.",
                nameof(config));
        }

        if (config.Slug.Contains(NamespacedToolName.Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Slug '{config.Slug}' darf den Namespace-Separator '{NamespacedToolName.Separator}' nicht enthalten (FR-03).",
                nameof(config));
        }

        if (config.Slug.Equals(AssetDelivery.Namespace, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Slug '{AssetDelivery.Namespace}' ist reserviert für die zentrale Asset-Auslieferung (FR-40).",
                nameof(config));
        }

        if (string.IsNullOrWhiteSpace(config.DisplayName))
        {
            throw new ArgumentException("DisplayName darf nicht leer sein.", nameof(config));
        }

        ValidateTransport(config);

        if (config.CallTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("CallTimeout muss positiv sein.", nameof(config));
        }

        if (config.Restart is { } restart)
        {
            if (restart.MaxRetries < 0 ||
                restart.InitialBackoff <= TimeSpan.Zero ||
                restart.BackoffMultiplier < 1.0 ||
                restart.MaxBackoff < restart.InitialBackoff)
            {
                throw new ArgumentException(
                    "RestartPolicy ungültig: MaxRetries ≥ 0, InitialBackoff > 0, Multiplier ≥ 1, MaxBackoff ≥ InitialBackoff.",
                    nameof(config));
            }
        }
    }

    private static void ValidateTransport(UpstreamServerConfig config)
    {
        var (expected, actualSet) = config.Kind switch
        {
            UpstreamTransportKind.Stdio => ("Stdio", config.Stdio is not null),
            UpstreamTransportKind.StreamableHttp => ("Http", config.Http is not null),
            UpstreamTransportKind.OpenApi => ("OpenApi", config.OpenApi is not null),
            UpstreamTransportKind.Cli => ("Cli", config.Cli is not null),
            UpstreamTransportKind.Wasi => ("Wasi", config.Wasi is not null),
            _ => throw new ArgumentException($"Unbekannter Transport: {config.Kind}.", nameof(config)),
        };

        if (!actualSet)
        {
            throw new ArgumentException(
                $"Transport {config.Kind} verlangt gesetzte {expected}-Optionen.", nameof(config));
        }

        var extras = new[]
        {
            (Name: "Stdio", Set: config.Stdio is not null, For: UpstreamTransportKind.Stdio),
            (Name: "Http", Set: config.Http is not null, For: UpstreamTransportKind.StreamableHttp),
            (Name: "OpenApi", Set: config.OpenApi is not null, For: UpstreamTransportKind.OpenApi),
            (Name: "Cli", Set: config.Cli is not null, For: UpstreamTransportKind.Cli),
            (Name: "Wasi", Set: config.Wasi is not null, For: UpstreamTransportKind.Wasi),
        };
        foreach (var extra in extras)
        {
            if (extra.Set && extra.For != config.Kind)
            {
                throw new ArgumentException(
                    $"{extra.Name}-Optionen sind gesetzt, aber Kind ist {config.Kind} — widersprüchliche Konfiguration.",
                    nameof(config));
            }
        }

        if (config.Kind == UpstreamTransportKind.Stdio && string.IsNullOrWhiteSpace(config.Stdio!.Command))
        {
            throw new ArgumentException("Stdio.Command darf nicht leer sein.", nameof(config));
        }

        if (config.Kind == UpstreamTransportKind.Wasi)
        {
            ValidateWasi(config.Wasi!, config);
        }

        if (config.Kind == UpstreamTransportKind.Cli)
        {
            ValidateCli(config.Cli!, config);
        }
    }

    /// <summary>
    /// Prüft die WASI-Upstream-Konfiguration (ADR-0020). Fail-closed: ohne gepinnten Publisher
    /// wird gar nichts geladen — eine leere Liste ist ein Konfigurationsfehler, kein "alles
    /// erlaubt".
    /// </summary>
    private static void ValidateWasi(WasiTransportOptions wasi, UpstreamServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(wasi.HostExecutable))
        {
            throw new ArgumentException("Wasi.HostExecutable darf nicht leer sein.", nameof(config));
        }

        if (string.IsNullOrWhiteSpace(wasi.ComponentPath))
        {
            throw new ArgumentException("Wasi.ComponentPath darf nicht leer sein.", nameof(config));
        }

        if (string.IsNullOrWhiteSpace(wasi.SignaturePath))
        {
            throw new ArgumentException(
                "Wasi.SignaturePath darf nicht leer sein — unsignierte Components werden nicht geladen.",
                nameof(config));
        }

        if (wasi.PinnedPublishers is null || wasi.PinnedPublishers.Count == 0)
        {
            throw new ArgumentException(
                "Wasi.PinnedPublishers darf nicht leer sein (fail-closed: ohne gepinnten Publisher lädt der Host nichts).",
                nameof(config));
        }

        foreach (var publisher in wasi.PinnedPublishers)
        {
            if (string.IsNullOrWhiteSpace(publisher)
                || !Convert.TryFromBase64String(publisher, new byte[64], out var written)
                || written != 32)
            {
                throw new ArgumentException(
                    "Jeder Wasi.PinnedPublishers-Eintrag muss ein Base64-kodierter 32-Byte-Ed25519-Public-Key sein.",
                    nameof(config));
            }
        }

        if (wasi.StartupTimeoutSeconds <= 0)
        {
            throw new ArgumentException("Wasi.StartupTimeoutSeconds muss positiv sein.", nameof(config));
        }

        if (wasi.Limits is { } limits)
        {
            if (limits.MaxOutputBytes <= 0)
            {
                throw new ArgumentException("Wasi.Limits.MaxOutputBytes muss positiv sein.", nameof(config));
            }

            if (limits.MaxMemoryBytes is <= 0)
            {
                throw new ArgumentException("Wasi.Limits.MaxMemoryBytes muss positiv sein.", nameof(config));
            }

            if (limits.TimeoutMs is 0 || limits.Fuel is 0)
            {
                throw new ArgumentException(
                    "Wasi.Limits: TimeoutMs und Fuel müssen positiv sein (weglassen = kein Limit).",
                    nameof(config));
            }
        }

        if (wasi.Grants is { } grants)
        {
            ValidateWasiGrants(grants, config);
        }
    }

    /// <summary>
    /// Prüft die Grants, die der Host pro Interface durchsetzt (Plan 0003, WP3). Ein unbrauchbarer
    /// Grant soll hier auffallen und nicht erst beim Laden: Der Host lehnt eine nicht auflösbare
    /// Preopen-Wurzel oder ein Netzwerkziel ohne Port fail-closed ab, und dann steht der Upstream
    /// mit einem Fehler da, dessen Ursache in der Konfiguration liegt.
    /// </summary>
    private static void ValidateWasiGrants(WasiCapabilityGrants grants, UpstreamServerConfig config)
    {
        foreach (var preopen in grants.FilesystemPreopens ?? [])
        {
            if (string.IsNullOrWhiteSpace(preopen) || !Path.IsPathFullyQualified(preopen))
            {
                throw new ArgumentException(
                    $"Wasi.Grants.FilesystemPreopens: '{preopen}' muss ein absoluter Pfad sein — "
                    + "ein relativer Pfad zeigt je nach Arbeitsverzeichnis des Hosts woanders hin.",
                    nameof(config));
            }
        }

        foreach (var target in grants.NetworkAllow ?? [])
        {
            // Der Host löst die Allowlist einmalig zu Socket-Adressen auf; ohne Port geht das nicht.
            var separator = target?.LastIndexOf(':') ?? -1;
            if (string.IsNullOrWhiteSpace(target) || separator <= 0
                || !ushort.TryParse(target[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var port)
                || port == 0)
            {
                throw new ArgumentException(
                    $"Wasi.Grants.NetworkAllow: '{target}' muss die Form host:port haben.",
                    nameof(config));
            }
        }

        if (grants.Secrets is { Count: > 0 })
        {
            // Ehrlich statt stillschweigend: Der Host kennt heute keine Secret-Capability, ein
            // solcher Grant wäre wirkungslos. Secret-Injection kommt mit dem Trust-Store (WP4).
            throw new ArgumentException(
                "Wasi.Grants.Secrets wird noch nicht durchgesetzt — der Host injiziert keine Secrets (Plan 0003, WP4).",
                nameof(config));
        }
    }

    private static void ValidateCli(CliTransportOptions cli, UpstreamServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(cli.Executable))
        {
            throw new ArgumentException("Cli.Executable darf nicht leer sein.", nameof(config));
        }

        if (!cli.AllowPathLookup && !Path.IsPathFullyQualified(cli.Executable))
        {
            throw new ArgumentException(
                "Cli.Executable muss im sicheren Modus ein absoluter Pfad sein. "
                + "PATH-Auflösung ist nur mit AllowPathLookup=true zulässig.",
                nameof(config));
        }

        ValidateAbsoluteRoots(cli.AllowedExecutableRoots, "Cli.AllowedExecutableRoots", config);
        ValidateAbsoluteRoots(cli.AllowedWorkingDirectoryRoots, "Cli.AllowedWorkingDirectoryRoots", config);
        ValidateAbsoluteRoots(cli.AllowedReadRoots, "Cli.AllowedReadRoots", config);
        ValidateAbsoluteRoots(cli.AllowedWriteRoots, "Cli.AllowedWriteRoots", config);

        if (cli.WorkingDirectory is not null && !Path.IsPathFullyQualified(cli.WorkingDirectory))
        {
            throw new ArgumentException("Cli.WorkingDirectory muss absolut sein.", nameof(config));
        }

        if (cli.Tools is null || cli.Tools.Count == 0)
        {
            throw new ArgumentException("Cli verlangt mindestens ein Tool (CliToolSpec).", nameof(config));
        }

        var duplicateTool = cli.Tools
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTool is not null)
        {
            throw new ArgumentException(
                $"Cli-Toolname '{duplicateTool.Key}' ist doppelt.", nameof(config));
        }

        foreach (var tool in cli.Tools)
        {
            ValidateCliTool(tool, cli.MaxConcurrency, config);
        }

        if (cli.TimeoutSeconds is { } timeoutSeconds and (<= 0 or > 3600))
        {
            throw new ArgumentException(
                "Cli.TimeoutSeconds muss zwischen 1 und 3600 liegen.", nameof(config));
        }

        if (cli.MaxOutputBytes is <= 0 or > 16 * 1024 * 1024)
        {
            throw new ArgumentException(
                "Cli.MaxOutputBytes muss zwischen 1 und 16777216 liegen.", nameof(config));
        }

        if (cli.MaxConcurrency is <= 0 or > 64)
        {
            throw new ArgumentException(
                "Cli.MaxConcurrency muss zwischen 1 und 64 liegen.", nameof(config));
        }

        try
        {
            _ = Encoding.GetEncoding(cli.OutputEncoding);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"Cli.OutputEncoding '{cli.OutputEncoding}' ist unbekannt.", nameof(config), ex);
        }

        if (cli.ExecutableSha256 is { } hash
            && (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException(
                "Cli.ExecutableSha256 muss ein 64-stelliger SHA-256-Hexwert sein.", nameof(config));
        }

        foreach (var name in cli.EnvironmentVariables?.Keys ?? [])
        {
            if (!EnvironmentNamePattern().IsMatch(name))
            {
                throw new ArgumentException(
                    $"CLI-Environment-Name '{name}' ist plattformübergreifend ungültig.", nameof(config));
            }
        }
    }

    private static void ValidateCliTool(
        CliToolSpec tool, int upstreamMaxConcurrency, UpstreamServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(tool.Name) || !CliNamePattern().IsMatch(tool.Name))
        {
            throw new ArgumentException(
                $"Cli-Toolname '{tool.Name}' ist ungültig.", nameof(config));
        }

        if (tool.AllowCallerArguments && tool.Parameters is { Count: > 0 })
        {
            throw new ArgumentException(
                $"Cli-Tool '{tool.Name}' darf freie Argumente und typisierte Parameter nicht mischen.",
                nameof(config));
        }

        if (tool.MaxConcurrency is { } commandConcurrency
            && (commandConcurrency <= 0 || commandConcurrency > upstreamMaxConcurrency))
        {
            throw new ArgumentException(
                $"Cli-Tool '{tool.Name}' hat ein ungültiges MaxConcurrency.", nameof(config));
        }

        var parameters = tool.Parameters ?? [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var positions = new HashSet<int>();
        foreach (var parameter in parameters)
        {
            if (!CliNamePattern().IsMatch(parameter.Name) || !names.Add(parameter.Name))
            {
                throw new ArgumentException(
                    $"Cli-Parametername '{parameter.Name}' ist ungültig oder doppelt.", nameof(config));
            }

            if ((parameter.Position is null) == (parameter.Flag is null))
            {
                throw new ArgumentException(
                    $"Cli-Parameter '{parameter.Name}' braucht entweder Position oder Flag.", nameof(config));
            }

            if (parameter.Position is { } position && (position < 0 || !positions.Add(position)))
            {
                throw new ArgumentException(
                    $"Cli-Parameter '{parameter.Name}' hat eine ungültige oder doppelte Position.",
                    nameof(config));
            }

            if (parameter.Flag is { } flag && (flag.Length == 0 || flag[0] != '-' || flag.Length > 64))
            {
                throw new ArgumentException(
                    $"Cli-Parameter '{parameter.Name}' hat ein ungültiges Flag.", nameof(config));
            }

            if (parameter.Type == CliParameterType.Enum
                && parameter.AllowedValues is not { Count: > 0 })
            {
                throw new ArgumentException(
                    $"Enum-Parameter '{parameter.Name}' braucht erlaubte Werte.", nameof(config));
            }

            if (parameter.Type == CliParameterType.Path && parameter.PathAccess == CliPathAccess.None
                || parameter.Type != CliParameterType.Path && parameter.PathAccess != CliPathAccess.None)
            {
                throw new ArgumentException(
                    $"Cli-Parameter '{parameter.Name}' hat widersprüchliche Pfadregeln.", nameof(config));
            }

            if (parameter.MaxLength is <= 0)
            {
                throw new ArgumentException(
                    $"Cli-Parameter '{parameter.Name}' hat eine ungültige Maximallänge.", nameof(config));
            }

            if (parameter.Minimum is { } min && parameter.Maximum is { } max && min > max)
            {
                throw new ArgumentException(
                    $"Cli-Parameter '{parameter.Name}' hat einen widersprüchlichen Wertebereich.",
                    nameof(config));
            }

            if (parameter.Pattern is { } pattern)
            {
                try
                {
                    _ = Regex.IsMatch(string.Empty, pattern);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException(
                        $"Cli-Parameter '{parameter.Name}' hat ein ungültiges Pattern.",
                        nameof(config),
                        ex);
                }
            }
        }

        foreach (var parameter in parameters)
        {
            var conflicts = parameter.ConflictsWith ?? [];
            var requirements = parameter.Requires ?? [];
            if (conflicts.Concat(requirements).Any(reference =>
                    reference == parameter.Name || !names.Contains(reference))
                || conflicts.Intersect(requirements, StringComparer.Ordinal).Any())
            {
                throw new ArgumentException(
                    $"Cli-Parameter '{parameter.Name}' hat widersprüchliche Abhängigkeiten.",
                    nameof(config));
            }
        }
    }

    private static void ValidateAbsoluteRoots(
        IReadOnlyList<string>? roots, string propertyName, UpstreamServerConfig config)
    {
        if (roots?.Any(root => string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) == true)
        {
            throw new ArgumentException($"{propertyName} darf nur absolute Pfade enthalten.", nameof(config));
        }
    }
}
