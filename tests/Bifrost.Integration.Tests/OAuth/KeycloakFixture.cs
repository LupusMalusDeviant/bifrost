using System.Net;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Bifrost.Integration.Tests.OAuth;

/// <summary>
/// Ein echtes Keycloak als Gegenstelle (WP5.2, FR-P062).
/// <para>
/// <b>Warum ein Container und kein nachgebauter Authorization Server:</b> Ein selbst geschriebener
/// Testserver bestätigt genau die Annahmen, die man beim Schreiben hatte. Genau die Annahmen sind
/// aber das Risiko — Discovery-Pfade, Fehlerformate, das Verhalten bei falschem
/// <c>code_verifier</c>, die Rotation von Refresh-Token. Was hier antwortet, ist eine
/// Implementierung, die niemand von uns kennt.
/// </para>
/// <para>
/// <b>Klartext auf Loopback ist hier kein Verstoß, sondern die Konfiguration des Produkts:</b>
/// <c>AllowPrivateTargets</c> lässt HTTP ausdrücklich auf Loopback zu („für lokale Entwicklung",
/// <c>OAuthDiscovery.EnsureSecure</c>). Ein Test, der stattdessen die HTTPS-Pflicht abschaltete,
/// prüfte einen Gateway, den niemand ausliefert.
/// </para>
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "bifrost-test";
    public const string ClientId = "bifrost-gateway";
    public const string ClientSecret = "gateway-test-secret";
    public const string Username = "pilot";
    public const string Password = "pilot-test-passwort";

    /// <summary>
    /// Auf Linux ist Docker da — ein übersprungener OAuth-Nachweis wäre dort ein stiller Ausfall.
    /// Dieselbe Regel wie bei <c>BIFROST_REQUIRE_POSTGRES</c>.
    /// </summary>
    private static bool Required =>
        Environment.GetEnvironmentVariable("BIFROST_REQUIRE_KEYCLOAK") == "1";

    private IContainer? _container;

    public string? SkipReason { get; private set; }

    /// <summary>Der Issuer, so wie er in den Token steht und in der Discovery auftaucht.</summary>
    public string Issuer => $"{BaseAddress}realms/{Realm}";

    public string BaseAddress { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var realm = Path.Combine(AppContext.BaseDirectory, "OAuth", "bifrost-test-realm.json");

        try
        {
            _container = new ContainerBuilder("quay.io/keycloak/keycloak:26.0")
                .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
                .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
                .WithEnvironment("KC_HEALTH_ENABLED", "true")
                .WithResourceMapping(new FileInfo(realm), "/opt/keycloak/data/import/")
                .WithCommand("start-dev", "--import-realm")
                .WithPortBinding(8080, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                    .ForPath($"/realms/{Realm}/.well-known/openid-configuration")
                    .ForPort(8080)
                    .ForStatusCode(HttpStatusCode.OK)))
                .Build();

            await _container.StartAsync();
            BaseAddress = $"http://127.0.0.1:{_container.GetMappedPublicPort(8080)}/";
        }
        catch (Exception exception)
        {
            if (Required)
            {
                throw;
            }

            // Ohne Docker wird uebersprungen — und zwar sichtbar mit Grund, nicht still.
            SkipReason = $"Keycloak-Container nicht startbar ({exception.GetType().Name}: "
                + $"{exception.Message.Split('\n')[0]}). Mit BIFROST_REQUIRE_KEYCLOAK=1 ist das ein Fehler.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Meldet den Testnutzer an und liefert den Autorisierungscode aus der Weiterleitung — der
    /// Browserschritt, ohne Browser.
    /// <para>
    /// Der Weg ist bewusst der echte: Formular holen, Zugangsdaten an die <c>action</c>-Adresse
    /// schicken, der Weiterleitung <b>nicht</b> folgen und den Code aus <c>Location</c> lesen. Ein
    /// abgekürzter Weg über die Admin-API würde den Teil überspringen, der in der Praxis bricht.
    /// </para>
    /// </summary>
    public static async Task<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken ct)
    {
        // Cookies von Hand statt ueber den CookieContainer: Keycloak setzt seine Sitzungscookies
        // mit 'Secure' (SameSite=None verlangt das), und ein .NET-Client schickt solche Cookies
        // ueber HTTP grundsaetzlich nicht zurueck. Ueber Loopback ist das hier kein Schutz, sondern
        // nur der Grund, warum die Anmeldung sonst mit 400 endet. Am Produkt aendert das nichts —
        // diese Schleife ist der Browser, nicht der Gateway.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        using var form = await http.GetAsync(authorizationUrl, ct);
        var page = await form.Content.ReadAsStringAsync(ct);
        if (!form.IsSuccessStatusCode)
        {
            // Der Grund steht im Rumpf, nicht im Status: "invalid redirect_uri" sieht von aussen
            // aus wie jeder andere 400.
            throw new InvalidOperationException(
                $"Keycloak hat die Autorisierungsanfrage abgelehnt ({(int)form.StatusCode}): "
                + $"{Excerpt(page)}");
        }

        var action = Regex.Match(page, "action=\"(?<url>[^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(5));
        if (!action.Success)
        {
            throw new InvalidOperationException(
                "Keycloak hat kein Anmeldeformular geliefert — die Autorisierungsanfrage wurde "
                + $"vermutlich abgelehnt: {Excerpt(page)}");
        }

        var loginUrl = WebUtility.HtmlDecode(action.Groups["url"].Value);
        using var login = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = Username,
                ["password"] = Password,
                ["credentialId"] = string.Empty,
            }),
        };
        login.Headers.TryAddWithoutValidation("Cookie", CookieHeader(form));

        using var response = await http.SendAsync(login, ct);
        if (response.Headers.Location is { } location)
        {
            return location;
        }

        throw new InvalidOperationException(
            $"Keine Weiterleitung nach der Anmeldung ({(int)response.StatusCode}): "
            + Excerpt(await response.Content.ReadAsStringAsync(ct)));
    }

    /// <summary>Alle gesetzten Cookies zurueckgeben — ohne Ruecksicht auf das Secure-Attribut.</summary>
    private static string CookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies.Select(c => c.Split(';')[0]))
            : string.Empty;

    /// <summary>
    /// Der lesbare Teil einer Keycloak-Fehlerseite. Ohne ihn steht in einem Fehlschlag nur
    /// "400" und der Doctype — und die Ursache stuende genau darunter.
    /// </summary>
    private static string Excerpt(string html)
    {
        var text = Regex.Replace(html, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
        text = Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5)).Trim();
        return text[..Math.Min(400, text.Length)];
    }
}

/// <summary>
/// Alle OAuth-Wege teilen sich einen Container. Ein eigener je Testklasse waere derselbe Nachweis
/// zum vierfachen Preis — Keycloak braucht rund zwanzig Sekunden bis zur ersten Antwort.
/// </summary>
[CollectionDefinition(KeycloakTests.Name)]
public sealed class KeycloakTests : ICollectionFixture<KeycloakFixture>
{
    public const string Name = "keycloak";
}
