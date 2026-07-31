using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Execution;
using Bifrost.Persistence;
using Bifrost.Persistence.Startup;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Bifrost.Server.Diagnostics;

/// <summary>
/// <see cref="IDiagnosticService"/> im laufenden Serverprozess (WP2.7).
/// <para>
/// Der Dienst aus WP2.4 bekommt einen <see cref="DiagnosticContext"/> mit; dieser hier baut ihn je
/// Lauf neu, weil er Dinge enthält, die sich ändern: die tatsächlich gebundenen Adressen, die
/// Upstream-Konfiguration und die Zustände. Ein einmal beim Start eingefrorener Kontext hätte einen
/// Bericht ergeben, der die Instanz von vor Stunden beschreibt.
/// </para>
/// <para>
/// Zusätzlich hängt der Bericht die Befunde der <b>Startkoordination</b> an
/// (<see cref="DatabaseInitializer.InspectAsync"/>, Codes BFR-DB-0100…0112). Ohne sie stünde der
/// wichtigste Datenbankbefund überhaupt — ein offener Migrationseintrag, BFR-DB-0101 — in keinem
/// Diagnosebericht, obwohl genau er den Gateway am Starten hindert.
/// </para>
/// </summary>
public sealed class ServerDiagnosticService : IDiagnosticService
{
    private readonly ServerDiagnosticContextFactory _contexts;
    private readonly DatabaseInitializer _initializer;
    private readonly TimeProvider _time;

    public ServerDiagnosticService(
        ServerDiagnosticContextFactory contexts,
        DatabaseInitializer initializer,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentNullException.ThrowIfNull(time);
        _contexts = contexts;
        _initializer = initializer;
        _time = time;
    }

    public async Task<DiagnosticReport> RunAsync(DiagnosticScope scope, CancellationToken ct)
    {
        var context = await _contexts.CreateAsync(ct).ConfigureAwait(false);
        var report = await DiagnosticService.CreateDefault(context, _time).RunAsync(scope, ct).ConfigureAwait(false);

        if ((scope & DiagnosticScope.Database) == 0)
        {
            return report;
        }

        var startup = await InspectStartupAsync(ct).ConfigureAwait(false);
        if (startup.Count == 0)
        {
            return report;
        }

        // Nach Code sortiert wie im Dienst selbst: Zwei Läufe auf demselben Zustand sollen dieselbe
        // Ausgabe ergeben. Codes, die bereits im Bericht stehen, gewinnen — der Bericht bleibt in
        // jedem Fall eindeutig, ein Code steht nie zweimal darin.
        var merged = report.Checks.ToList();
        var known = merged.Select(check => check.Code).ToHashSet(StringComparer.Ordinal);
        merged.AddRange(startup.Where(check => known.Add(check.Code)));
        merged.Sort((left, right) => string.CompareOrdinal(left.Code, right.Code));

        return report with { Checks = merged };
    }

    private async Task<IReadOnlyList<DiagnosticCheck>> InspectStartupAsync(CancellationToken ct)
    {
        try
        {
            return await _initializer.InspectAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Ein Diagnosebericht, der an einem Teilbefund abstürzt, ist nutzlos.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return
            [
                new DiagnosticCheck(
                    MigrationDiagnosticCodes.SafetyMechanismUnavailable,
                    CheckStatus.Fail,
                    "Der Migrationszustand ließ sich nicht beurteilen.",
                    "Erreichbarkeit und Schreibrechte der Datenbank prüfen (siehe BFR-DB-0002).",
                    new Dictionary<string, string> { ["fehlerart"] = exception.GetType().Name }),
            ];
        }
    }
}

/// <summary>
/// Baut den <see cref="DiagnosticContext"/> aus dem, was nur der laufende Serverprozess weiß
/// (M2-Vertrag, WP2.7-Auftrag Punkt 2).
/// </summary>
public sealed class ServerDiagnosticContextFactory
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IUpstreamConfigStore _upstreams;
    private readonly IDatabaseDiagnosticProbe _database;
    private readonly IUpstreamDiagnosticProbe _upstreamProbe;
    private readonly HostExecutionCoordinator _hostExecution;

    public ServerDiagnosticContextFactory(
        IServiceProvider services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IUpstreamConfigStore upstreams,
        IDatabaseDiagnosticProbe database,
        IUpstreamDiagnosticProbe upstreamProbe,
        HostExecutionCoordinator hostExecution)
    {
        ArgumentNullException.ThrowIfNull(hostExecution);
        _hostExecution = hostExecution;
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(upstreams);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(upstreamProbe);
        _services = services;
        _configuration = configuration;
        _environment = environment;
        _upstreams = upstreams;
        _database = database;
        _upstreamProbe = upstreamProbe;
    }

    public async Task<DiagnosticContext> CreateAsync(CancellationToken ct)
    {
        var (isolationRequired, runtimeName) = await InspectIsolationAsync(ct).ConfigureAwait(false);

        return new DiagnosticContext
        {
            // Aus der KONFIGURATION, nicht aus den Prozess-Umgebungsvariablen: Der Gateway liest
            // seine Einstellungen über IConfiguration, und dort steht auch, was ein Host per
            // UseSetting oder appsettings gesetzt hat. Eine Diagnose, die nur die Umgebung liest,
            // beschreibt eine andere Instanz als die laufende.
            Environment = Snapshot(),
            HostEnvironmentName = _environment.EnvironmentName,
            ListenAddresses = ResolveListenAddresses(),
            // Aus dem Serverprozess heraus ist der eigene Port belegt — das ist der Normalfall und
            // kein Befund. Aus der CLI heraus wäre es einer.
            GatewayRunsInThisProcess = true,
            ContainerIsolationConfigured = isolationRequired,
            ContainerRuntimeName = runtimeName,
            Database = _database,
            Upstreams = _upstreamProbe,
            // Der ERMITTELTE Zustand, nicht die Umgebungsvariable: Eine Instanz kann ihren Wert
            // uebernommen haben (ADR-0025 E3), und genau dieser Unterschied ist der Befund.
            HostExecution = _hostExecution.State,
        };
    }

    private Dictionary<string, string> Snapshot()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _configuration.AsEnumerable())
        {
            if (value is not null)
            {
                snapshot[key] = value;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Die <b>tatsächlich gebundenen</b> Adressen, nicht <c>ASPNETCORE_URLS</c>. Der Unterschied ist
    /// der ganze Zweck: Wer <c>http://+:8080</c> konfiguriert, aber auf Port 0 landet, bekäme sonst
    /// einen Bericht über einen Port, auf dem nichts lauscht.
    /// </summary>
    private IReadOnlyList<string> ResolveListenAddresses()
    {
        var addresses = _services.GetService<IServer>()?.Features
            .Get<IServerAddressesFeature>()?.Addresses;
        return addresses is { Count: > 0 } ? [.. addresses] : [];
    }

    /// <summary>
    /// Verlangt mindestens ein Upstream Container-Isolation, und unter welchem Runtime-Namen?
    /// <c>null</c> heißt „nicht beantwortbar" — dann wertet BFR-RT-0001 eine fehlende Runtime nur
    /// als <c>Skipped</c>.
    /// </summary>
    private async Task<(bool? Required, string Runtime)> InspectIsolationAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<ServerId, UpstreamConfigVersion> latest;
        try
        {
            latest = await _upstreams.GetAllLatestAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Ist der Konfigurationsspeicher nicht lesbar, ist die Antwort "unbekannt".
        catch (Exception)
#pragma warning restore CA1031
        {
            return (null, "docker");
        }

        var isolated = latest.Values
            .Select(version => version.Config.Cli?.Isolation)
            .Where(isolation => isolation is { Mode: CliIsolationMode.Container })
            .ToList();

        return isolated.Count == 0
            ? (false, "docker")
            : (true, isolated[0]!.Runtime);
    }
}
