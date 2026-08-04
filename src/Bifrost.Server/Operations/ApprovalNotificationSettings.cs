using Bifrost.Abstractions;
using Bifrost.Core.Approvals;

namespace Bifrost.Server.Operations;

/// <summary>
/// Woher die Benachrichtigungsziele kommen (WP6.3).
/// <para>
/// Aus der Konfiguration, nicht aus der Datenbank: Ein Ziel gehört zur Betriebsumgebung wie eine
/// Datenbankadresse, und diese Welle legt bewusst keine Migration an. Das Geheimnis steht in einer
/// Umgebungsvariablen und damit dort, wo eine Installation ihre Geheimnisse ohnehin hält.
/// </para>
/// <para>
/// <b>Ohne Adresse entsteht ein Zusteller ohne Ziele.</b> Er tut dann nichts — kein Fehler, kein
/// Warnhinweis: Benachrichtigungen sind optional, und eine Instanz ohne Chat ist keine kaputte.
/// </para>
/// </summary>
public static class ApprovalNotificationSettings
{
    public const string EndpointKey = "BIFROST_APPROVAL_WEBHOOK_URL";

    public const string SecretKey = "BIFROST_APPROVAL_WEBHOOK_SECRET";

    /// <summary>
    /// Erlaubt ein Ziel im eigenen Netz. Vorgabe aus: Eine Adresse, die einmal gesetzt und danach
    /// von niemandem mehr angesehen wird, ist derselbe SSRF-Weg wie eine importierte Schemadatei.
    /// </summary>
    public const string AllowPrivateKey = "BIFROST_APPROVAL_WEBHOOK_ALLOW_PRIVATE";

    public static ApprovalNotificationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var endpoint = configuration[EndpointKey];
        var secret = configuration[SecretKey];
        var publicBase = configuration["BIFROST_PUBLIC_BASE_URL"];

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ApprovalNotificationOptions { PublicBaseUrl = publicBase };
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            // Eine unsignierte Benachrichtigung an eine fremde Adresse waere ein Ruf, den jeder
            // faelschen kann. Lieber gar keiner als einer, dem niemand trauen darf.
            throw new InvalidOperationException(
                $"{EndpointKey} ist gesetzt, aber {SecretKey} fehlt. Ohne Geheimnis liesse sich die "
                + "Benachrichtigung nicht signieren — und eine unsignierte kann jeder faelschen.");
        }

        return new ApprovalNotificationOptions
        {
            Targets = [new ApprovalNotificationTarget("webhook", new Uri(endpoint), secret)],
            PublicBaseUrl = publicBase,
        };
    }
}
