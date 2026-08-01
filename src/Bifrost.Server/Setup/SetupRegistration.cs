using Bifrost.Abstractions.Setup;

namespace Bifrost.Server.Setup;

/// <summary>
/// Verdrahtung des gefuehrten Erstaufbaus (WP4.4).
/// <para>
/// Beides ist ein <c>Singleton</c>: Der Vorgangsspeicher <b>muss</b> es sein, sonst haette jeder
/// Circuit seine eigene Ablage und ein Neuladen verlaere den Stand — genau das, was dieses Paket
/// verhindern soll. Der Dienst selbst haelt keinen Zustand; er ist es der Einfachheit halber.
/// </para>
/// </summary>
public static class SetupRegistration
{
    public static IServiceCollection AddBifrostSetupWizard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<SetupSessionStore>();
        services.AddSingleton<ISetupSessionStore>(sp => sp.GetRequiredService<SetupSessionStore>());
        services.AddSingleton<ISetupWizard, SetupWizardService>();
        return services;
    }
}
