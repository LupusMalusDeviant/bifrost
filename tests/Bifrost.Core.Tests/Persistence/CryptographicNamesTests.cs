using AwesomeAssertions;

using Bifrost.Persistence;

using Xunit;

namespace Bifrost.Core.Tests.Persistence;

/// <summary>
/// Diese Werte sind Teil des Schlüssels, mit dem gespeicherter Geheimtext entschlüsselt wird. Ändert
/// sie jemand, verliert <b>jede bestehende Installation</b> ihre Upstream-Zugangsdaten, OAuth-Token
/// und Webhook-Secrets — ohne Fehlermeldung beim Start. Der Dienst läuft weiter und kann nur nichts
/// mehr entschlüsseln.
/// <para>
/// Ein Test, der eine Konstante gegen ihren eigenen Wert prüft, sieht sinnlos aus. Er ist es hier
/// nicht: Er ist die einzige Stelle im Repository, an der eine Umbenennung <em>auffällt</em>. Der
/// Umbenennungs-Commit <c>c7cb446</c> hat gezeigt, wie leicht eine repo-weite Textersetzung Dinge
/// mitnimmt, die niemand dabei im Blick hatte; der Upgrade-Harness aus WP2.6 kann diese Regression
/// prinzipiell nicht fangen, weil er seinen eigenen Anwendungsnamen benutzt.
/// </para>
/// <para>
/// <b>Wer hier rot wird, hat zwei Möglichkeiten</b> — die Änderung zurücknehmen, oder einen
/// Migrationslauf schreiben, der alles entschlüsselt und neu verschlüsselt. Den Test anzupassen ist
/// keine dritte.
/// </para>
/// </summary>
public class CryptographicNamesTests
{
    [Theory]
    [InlineData(CryptographicNames.DataProtectionApplication, "MCPMCP")]
    [InlineData(CryptographicNames.UpstreamConfigPurpose, "McpMcp.UpstreamConfig.v1")]
    [InlineData(CryptographicNames.UpstreamOAuthTokenPurpose, "McpMcp.UpstreamOAuthToken.v1")]
    [InlineData(CryptographicNames.WebhookSecretPurpose, "McpMcp.Webhook.Secret.v1")]
    public void A_cryptographic_name_never_changes(string actual, string expected)
        => actual.Should().Be(
            expected,
            "dieser Name geht in die Schlüsselableitung ein — ihn zu ändern macht jeden "
            + "gespeicherten Geheimtext unlesbar, und zwar ohne Fehlermeldung");

    /// <summary>
    /// Die Zwecke müssen sich unterscheiden. Zwei Ablagen mit demselben Zweck teilten sich einen
    /// Schlüssel — dann entschlüsselt der Webhook-Pfad Upstream-Zugangsdaten.
    /// </summary>
    [Fact]
    public void The_purposes_are_distinct()
    {
        string[] purposes =
        [
            CryptographicNames.UpstreamConfigPurpose,
            CryptographicNames.UpstreamOAuthTokenPurpose,
            CryptographicNames.WebhookSecretPurpose,
        ];

        purposes.Should().OnlyHaveUniqueItems();
    }
}
