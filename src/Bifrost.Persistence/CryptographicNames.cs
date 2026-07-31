namespace Bifrost.Persistence;

/// <summary>
/// Die Bezeichner, die in die Schlüsselableitung eingehen — der Anwendungsname der
/// DataProtection-Ablage und die Zwecke der einzelnen Ablagen.
/// <para>
/// <b>Warum sie hier gesammelt stehen:</b> Jeder dieser Namen ist Teil des Schlüssels, mit dem
/// gespeicherter Geheimtext entschlüsselt wird. Wird einer geändert, ist jede bestehende
/// Installation ihre Upstream-Zugangsdaten, OAuth-Token und Webhook-Secrets los — <b>ohne
/// Fehlermeldung beim Start</b>. Der Dienst läuft weiter und kann nur nichts mehr entschlüsseln.
/// </para>
/// <para>
/// Sie tragen weiterhin <c>McpMcp</c> bzw. <c>MCPMCP</c> im Namen, obwohl das Produkt
/// B.I.F.R.O.S.T heißt. Das ist kein Versehen: Ein kryptografischer Bezeichner ist kein
/// Markenname. Wer ihn ändern will, braucht einen Migrationslauf, der alles entschlüsselt und neu
/// verschlüsselt — keine Textersetzung.
/// </para>
/// <para>
/// Gesichert waren sie bis zur M2-Welle ausschließlich durch Kommentare. Dass daran nichts hing,
/// was rot wird, fiel erst auf, als der Upgrade-Harness aus WP2.6 nach genau dieser Regression
/// suchte und feststellte, dass er sie prinzipiell nicht finden kann — er benutzt seinen eigenen
/// Anwendungsnamen und würde eine repo-weite Umbenennung stillschweigend mitmachen.
/// Deshalb <c>CryptographicNamesTests</c>: Sie sind die einzige Stelle, an der das Vergessen
/// auffällt.
/// </para>
/// </summary>
public static class CryptographicNames
{
    /// <summary>
    /// Der Anwendungsname der DataProtection-Ablage (<c>SetApplicationName</c>). Er geht in
    /// <b>jede</b> Ableitung ein, also auch in die der drei Zwecke unten.
    /// </summary>
    public const string DataProtectionApplication = "MCPMCP";

    /// <summary>Zweck der verschlüsselten Upstream-Konfiguration.</summary>
    public const string UpstreamConfigPurpose = "McpMcp.UpstreamConfig.v1";

    /// <summary>Zweck der gespeicherten OAuth-Token eines Upstreams.</summary>
    public const string UpstreamOAuthTokenPurpose = "McpMcp.UpstreamOAuthToken.v1";

    /// <summary>Zweck der Webhook-Secrets.</summary>
    public const string WebhookSecretPurpose = "McpMcp.Webhook.Secret.v1";
}
