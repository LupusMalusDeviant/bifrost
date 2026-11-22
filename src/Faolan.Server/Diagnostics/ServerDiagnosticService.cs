using Faolan.Abstractions;
using Faolan.Abstractions.Operations;
using Faolan.Core.Diagnostics;
using Faolan.Core.Execution;
using Faolan.Persistence;
using Faolan.Persistence.Backup;
using Faolan.Persistence.Startup;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Faolan.Server.Diagnostics;

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
    private readonly IAuditDiagnosticProbe _auditProbe;
    private readonly HostExecutionCoordinator _hostExecution;

    public ServerDiagnosticContextFactory(
        IServiceProvider services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IUpstreamConfigStore upstreams,
        IDatabaseDiagnosticProbe database,
        IUpstreamDiagnosticProbe upstreamProbe,
        IAuditDiagnosticProbe auditProbe,
        HostExecutionCoordinator hostExecution)
    {
        ArgumentNullException.ThrowIfNull(hostExecution);
        ArgumentNullException.ThrowIfNull(auditProbe);
        _auditProbe = auditProbe;
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
            // BFR-UP-0002: Gespeicherte Konfigurationen mit dem alten WASI-Hostnamen (Vertrag §2.3).
            UpstreamsWithLegacyHostName = await UpstreamsWithLegacyHostNameAsync(_upstreams, ct).ConfigureAwait(false),
            // BFR-DB-0007: Was die Audit-Senke an Verlusten gezählt hat, gehört in den Bericht —
            // ein stiller Verlust ist genau das, was die Zusage "lückenloses Audit" bricht.
            Audit = _auditProbe,
            // ADR-0024 E2: Ob der vorhandene pg_dump diesen Server sichern kann, ist ein Befund und
            // keine Überraschung für den Ernstfall (BFR-DB-0006). Die Sonde entsteht hier statt in
            // der Verdrahtung, weil sie ausser dem konfigurierten Werkzeugverzeichnis nichts
            // braucht — und dieses steht in den ohnehin registrierten BackupOptions.
            PostgresBackupTools = new PostgresBackupToolProbe(
                _services.GetService<BackupOptions>()?.PostgresToolDirectory),
            // Der ERMITTELTE Zustand, nicht die Umgebungsvariable: Eine Instanz kann ihren Wert
            // uebernommen haben (ADR-0025 E3), und genau dieser Unterschied ist der Befund.
            HostExecution = _hostExecution.State,
        };
    }

    /// <summary>
    /// Die Slugs der Upstreams, deren letzte Konfiguration den WASI-Host unter dem alten Namen nennt.
    /// <c>null</c>, wenn die Konfigurationen nicht lesbar sind — „nicht ermittelt" ist ehrlicher als
    /// „keiner". Auch der Start meldet diese Liste (Program.cs).
    /// </summary>
    internal static async Task<IReadOnlyList<string>?> UpstreamsWithLegacyHostNameAsync(
        IUpstreamConfigStore store, CancellationToken ct)
    {
        try
        {
            var latest = await store.GetAllLatestAsync(ct).ConfigureAwait(false);
            return [.. latest.Values
                .Select(version => version.Config)
                .Where(config => config.Kind is UpstreamTransportKind.Wasi
                    && WasiHostNames.UsesLegacyName(config.Wasi?.HostExecutable))
                .Select(config => config.Slug)
                .Order(StringComparer.Ordinal)];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Eine unlesbare Konfiguration darf den Bericht nicht kippen; der Check meldet „nicht ermittelt".
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
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

        // Beide nativen Transporte, nicht nur CLI. Seit WP3.2 kann auch stdio im Container laufen;
        // die Abfrage sah das nicht, und eine Instanz mit ausschließlich stdio-Container-Upstreams
        // hätte „keine Runtime nötig" gemeldet — eine fehlende Runtime wäre dann eine Diagnose
        // gewesen, die genau den Fall verschweigt, für den sie da ist.
        var isolated = latest.Values
            .SelectMany(version => new[] { version.Config.Cli?.Isolation, version.Config.Stdio?.Isolation })
            .Where(isolation => isolation is { Mode: IsolationMode.Container })
            .ToList();

        return isolated.Count == 0
            ? (false, "docker")
            : (true, isolated[0]!.Runtime);
    }
}
