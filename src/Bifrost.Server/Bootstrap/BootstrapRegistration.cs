using System.Globalization;

namespace Bifrost.Server.Bootstrap;

/// <summary>Name der Einstellung, die die Frist des Setup-Tokens steuert.</summary>
public static class BootstrapSwitch
{
    /// <summary>Gültigkeitsdauer des Setup-Tokens in Minuten. Ungültige Angabe fällt auf den Default.</summary>
    public const string TimeToLiveMinutes = "BIFROST_BOOTSTRAP_TTL_MINUTES";
}

/// <summary>Verdrahtung des Erstzugangs (WP3.4).</summary>
public static class BootstrapRegistration
{
    /// <summary>
    /// Meldet Zustandsdatei, Übergabedatei, Recovery-Nachweis und den Dienst an. Alles hängt am
    /// Datenverzeichnis — dieselbe Zuordnung wie bei Key-Ring und Instanz-Id.
    /// </summary>
    public static IServiceCollection AddBifrostBootstrap(
        this IServiceCollection services, IConfiguration configuration, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        services.AddSingleton(ResolveOptions(configuration[BootstrapSwitch.TimeToLiveMinutes]));
        services.AddSingleton<IBootstrapStateStore>(_ => new BootstrapStateFile(dataDirectory));
        services.AddSingleton<IBootstrapHandover>(_ => new BootstrapHandoverFile(dataDirectory));
        services.AddSingleton<IBootstrapRecoveryProof>(_ => new DataDirectoryRecoveryProof(dataDirectory));
        services.AddSingleton<IBootstrapService, BootstrapService>();
        return services;
    }

    /// <summary>Getrennt und öffentlich, damit die Auflösung prüfbar ist, ohne einen Host zu bauen.</summary>
    public static BootstrapOptions ResolveOptions(string? timeToLiveMinutes)
        => int.TryParse(timeToLiveMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes > 0
                ? BootstrapOptions.Default with { TimeToLive = TimeSpan.FromMinutes(minutes) }
                : BootstrapOptions.Default;
}
