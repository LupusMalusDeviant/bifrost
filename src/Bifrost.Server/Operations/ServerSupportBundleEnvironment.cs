using System.Globalization;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Server.Operations;

/// <summary>
/// Die Angaben für das Support-Bundle, die nur der Serverprozess hat (WP5.6).
/// <para>
/// Wie bei <see cref="ServerConfigurationPorts"/> steht hier <b>nur</b> Verdrahtung: Was in ein
/// Bundle darf und was maskiert wird, entscheidet der Dienst im Kern. Diese Klasse beantwortet
/// Fragen, sie trifft keine.
/// </para>
/// </summary>
public sealed class ServerSupportBundleEnvironment : ISupportBundleEnvironment
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly IUpstreamSupervisor _supervisor;
    private readonly string _databaseProvider;

    public ServerSupportBundleEnvironment(
        IDbContextFactory<BifrostDbContext> factory,
        IUpstreamSupervisor supervisor,
        string dataDirectory,
        string databaseProvider)
    {
        _factory = factory;
        _supervisor = supervisor;
        _databaseProvider = databaseProvider;
        DataDirectory = dataDirectory;
        InstanceId = InstanceIdentityFile.EnsureCreated(dataDirectory);
    }

    public string InstanceId { get; }

    public string DataDirectory { get; }

    public async Task<IReadOnlyList<string>> AppliedMigrationsAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return [.. await db.Database.GetAppliedMigrationsAsync(ct)];
    }

    public Task<IReadOnlyDictionary<string, string>> RuntimeFactsAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["os"] = Environment.OSVersion.VersionString,
            ["osArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["dotnet"] = Environment.Version.ToString(),
            ["processorCount"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            ["databaseProvider"] = _databaseProvider,
            // Der Unterschied Container/Bare-Metal erklaert die Haelfte aller Betriebsfragen —
            // Pfade, Rechte, Netzwerk. Deshalb steht er drin, statt erfragt zu werden.
            ["container"] = (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
                .ToString(CultureInfo.InvariantCulture),
            // Nur ob einer konfiguriert ist und ob die Datei liegt — nicht der Pfad. Ein Pfad
            // verraet die Verzeichnisstruktur des Wirts und beantwortet die Frage nicht.
            ["wasiHostConfigured"] = (Environment.GetEnvironmentVariable("BIFROST_WASI_HOST") is { Length: > 0 })
                .ToString(CultureInfo.InvariantCulture),
            ["wasiHostPresent"] = (Environment.GetEnvironmentVariable("BIFROST_WASI_HOST") is { Length: > 0 } host
                && File.Exists(host)).ToString(CultureInfo.InvariantCulture),
        };

        return Task.FromResult(facts);
    }

    /// <summary>
    /// Der Zustand, nicht die Historie: Ist die Datenbank erreichbar, und wie stehen die Upstreams.
    /// <para>
    /// Slugs stehen hier, Ziele und Zugangsdaten nicht. Ein Slug ist der Name, unter dem ein
    /// Betreiber ohnehin fragt („warum ist github rot?"); die Adresse dahinter beantwortet die
    /// Frage nicht und gehört nicht in ein Archiv, das das Haus verlässt.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> HealthFactsAsync(CancellationToken ct)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            facts["databaseReachable"] = (await db.Database.CanConnectAsync(ct)).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            facts["databaseReachable"] = bool.FalseString;
            facts["databaseError"] = exception.GetType().Name;
        }

        var statuses = _supervisor.Statuses;
        facts["upstreamsTotal"] = statuses.Count.ToString(CultureInfo.InvariantCulture);
        facts["upstreamsHealthy"] = statuses
            .Count(s => s.State == UpstreamState.Healthy).ToString(CultureInfo.InvariantCulture);

        foreach (var status in statuses)
        {
            facts[$"upstream:{status.Slug}"] = status.State.ToString();
        }

        return facts;
    }
}
