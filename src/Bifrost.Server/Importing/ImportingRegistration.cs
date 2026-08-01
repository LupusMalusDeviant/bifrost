using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Importing;
using Bifrost.Core.Importing;

namespace Bifrost.Server.Importing;

/// <summary>
/// Verdrahtung des Konfigurationsimports (WP4.3). Bis hierher war der Importer aus WP4.1 gebaut,
/// aber von keinem Aufrufweg erreichbar — dieselbe Lage, in der die Betriebsdienste vor WP2.7 waren.
/// </summary>
public static class ImportingRegistration
{
    public static IServiceCollection AddBifrostImporting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ueber CreateDefault und nicht ueber eine eigene Providerliste: Welche Formate es gibt,
        // entscheidet WP4.2 an einer Stelle. Eine zweite Liste hier hiesse, dass ein neuer Parser
        // in den Tests vorkommt und im Betrieb nicht.
        services.AddSingleton<IConfigurationImporter>(sp =>
            ConfigurationImporter.CreateDefault(sp.GetService<IHostExecutionPolicy>()));

        // Singleton ist hier Vertrag, nicht Geschmack — genau wie beim Restore-Dienst: Der Plan
        // traegt ein Handle, dessen Zustand beim Dienst bleibt. Zwei Instanzen hiessen, dass der
        // Plan aus dem einen Aufruf im naechsten unbekannt ist.
        services.AddSingleton<ImportPlanStore>();
        services.AddSingleton<ImportRateLimiter>();

        return services;
    }
}
