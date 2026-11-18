using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Upgrade.Tests.Harness;

/// <summary>Was auf dem Fixturestand geschrieben wurde — der Bezugspunkt der Nachpruefung.</summary>
internal sealed record UpgradePayload(
    IdentityId Identity,
    Guid RoleId,
    Guid ProfileId,
    ServerId Server,
    string ApiKeyPlaintext,
    Guid ApiKeyId,
    string UiUsername,
    bool OAuthTokenSeeded,
    bool OAuthTokenWithoutBinding = false);

/// <summary>
/// Der Datenbestand, der ein Upgrade ueberleben muss — geschrieben durch die <b>echten</b> Stores,
/// nicht per INSERT von Hand.
///
/// <para>
/// <b>Warum die echten Stores:</b> Ein Upgrade, das Geheimtext unlesbar macht, faellt nur auf, wenn
/// der Geheimtext auch so entstanden ist, wie ihn der Betrieb erzeugt — mit demselben
/// DataProtection-Purpose, derselben Serialisierung, demselben Schluesselring. Ein von Hand
/// eingefuegtes BLOB wuerde dieselbe Zeile fuellen und trotzdem nichts belegen.
/// </para>
///
/// <para>
/// <b>Warum genau diese Tabellen:</b> Der Bestand muss auf <i>jedem</i> Fixturestand schreibbar
/// sein, auch auf dem aeltesten. Benutzt werden deshalb nur Tabellen, deren Spalten seit
/// <c>InitialCreate</c> unveraendert sind (<c>Identities</c>, <c>Roles</c>, <c>Profiles</c>,
/// <c>ConfigVersions</c>, <c>ApiKeys</c>, <c>UiUsers</c>) — plus <c>UpstreamOAuthTokens</c>, sobald
/// der Fixturestand die Tabelle kennt. <c>AuditEvents</c> und <c>Assets</c> haben spaeter Spalten
/// bekommen; das heutige EF-Modell laesst sich dort nicht gegen ein altes Schema schreiben. Diese
/// Luecke steht in <c>docs/upgrade-matrix.md</c>, statt sie mit handgeschriebenem SQL zu ueberdecken.
/// </para>
/// </summary>
internal static class UpgradePayloadWriter
{
    /// <summary>Das Geheimnis, das im verschluesselten Config-Blob liegt.</summary>
    public const string UpstreamSecret = "API_TOKEN_dieses-geheimnis-muss-das-upgrade-ueberleben";

    public const string OAuthAccessToken = "at_dieses-zugriffstoken-muss-lesbar-bleiben";

    public const string OAuthRefreshToken = "rt_und-dieses-erneuerungstoken-auch";

    public const string UiPassword = "bestands-passwort-4711";

    /// <summary>Der Migrationsstand, ab dem es die Tabelle der Upstream-OAuth-Token gibt.</summary>
    private const string OAuthTokenMigrationSuffix = "_UpstreamOAuthTokens";

    /// <summary>Der Migrationsstand, ab dem ein Token Bindung und Kennung traegt.</summary>
    private const string OAuthTokenBindingMigrationSuffix = "_UpstreamOAuthTokenBinding";

    private static readonly string[] OAuthScopes = ["mcp:read"];

    private static readonly DateTimeOffset OAuthExpiry = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    public static async Task<UpgradePayload> WriteAsync(
        IDbContextFactory<BifrostDbContext> factory,
        IDataProtectionProvider protection,
        IReadOnlyList<string> appliedMigrations,
        CancellationToken ct)
    {
        var identity = IdentityId.New();
        var roleId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var server = ServerId.New();

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            db.Profiles.Add(new ProfileRow
            {
                Id = profileId,
                Name = "bestandsprofil",
                LazyToolsEnabled = true,
                PinnedToolsJson = """["srv__tool"]""",
            });
            db.Roles.Add(new RoleRow
            {
                Id = roleId,
                Name = "bestandsrolle",
                RateLimitPerMinute = 42,
                GrantsJson = "[]",
            });
            db.Identities.Add(new IdentityRow
            {
                Id = identity.Value,
                Name = "bestandsagent",
                Kind = (int)IdentityKind.Agent,
                ProfileId = profileId,
                RolesJson = $"[\"{roleId}\"]",
            });
            await db.SaveChangesAsync(ct);
        }

        // Zwei Versionen: Der Verlauf ist Teil des Bestands, nicht nur der letzte Stand.
        var configs = new EfUpstreamConfigStore(factory, protection);
        await configs.AppendVersionAsync(server, ConfigWithSecret("alt"), ct);
        await configs.AppendVersionAsync(server, ConfigWithSecret("neu"), ct);

        var issued = await new ApiKeyService(factory)
            .IssueAsync(identity, "bestands-key", expiresAt: null, ct);

        var username = $"bestandsbetreiber-{Guid.NewGuid():N}";
        await new UiUserService(factory).CreateAsync(username, UiPassword, UiRole.Operator, ct);

        var oauth = KnowsOAuthTokens(appliedMigrations);
        var withoutBinding = oauth && !KnowsOAuthTokenBinding(appliedMigrations);
        if (withoutBinding)
        {
            await WriteTokenWithoutBindingAsync(factory, protection, server, ct);
        }
        else if (oauth)
        {
            await new UpstreamOAuthTokenStore(factory, protection).SaveAsync(
                new UpstreamOAuthToken(
                    server, OAuthAccessToken, OAuthRefreshToken, OAuthExpiry,
                    ["mcp:read"], "https://as.example.com", DateTimeOffset.UnixEpoch),
                ct);
        }

        return new UpgradePayload(
            identity, roleId, profileId, server, issued.PlaintextKey, issued.KeyId, username, oauth, withoutBinding);
    }

    /// <summary>
    /// Ein Token, wie es die Ablage vor Plan 0005 F5/F14 geschrieben hat: ohne Bindung und ohne
    /// Kennung.
    ///
    /// <para>
    /// <b>Warum hier ausnahmsweise SQL:</b> Die heutige Ablage schreibt die Spalten der Bindung mit,
    /// und die gibt es auf diesem Fixturestand nicht — ein Speichern scheiterte an „no such column".
    /// Die alte Ablage gibt es nicht mehr. Geschrieben werden deshalb genau die Spalten der Migration
    /// <c>UpstreamOAuthTokens</c>, die Nutzlast aber wie damals: derselbe DataProtection-Purpose,
    /// dieselbe JSON-Form. Dass die heutige Ablage sie lesen kann, prueft <see cref="VerifyAsync"/> —
    /// genau der Bestand, den eine laufende Instanz beim Update mitbringt.
    /// </para>
    /// </summary>
    private static async Task WriteTokenWithoutBindingAsync(
        IDbContextFactory<BifrostDbContext> factory, IDataProtectionProvider protection, ServerId server,
        CancellationToken ct)
    {
        var payload = protection.CreateProtector(CryptographicNames.UpstreamOAuthTokenPurpose).Protect(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                AccessToken = OAuthAccessToken,
                RefreshToken = OAuthRefreshToken,
                Scopes = OAuthScopes,
            }));
        var serverId = server.Value;
        const string issuer = "https://as.example.com";
        long? expires = OAuthExpiry.UtcTicks;
        var obtained = DateTimeOffset.UnixEpoch.UtcTicks;

        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "UpstreamOAuthTokens" ("ServerId", "Payload", "Issuer", "ExpiresAtTicks", "ObtainedAtTicks")
            VALUES ({serverId}, {payload}, {issuer}, {expires}, {obtained})
            """,
            ct);
    }

    /// <summary>
    /// Die Nachpruefung. Sie prueft zweierlei: <b>vollstaendig</b> (die Zeilen sind noch da und
    /// tragen dieselben Werte) und <b>lesbar</b> (der Geheimtext laesst sich mit dem mitgereisten
    /// Schluesselring wieder entschluesseln). Ohne den zweiten Teil bliebe ein Upgrade unbemerkt,
    /// das die Zeilen behaelt und ihren Inhalt verliert.
    /// </summary>
    public static async Task VerifyAsync(
        IDbContextFactory<BifrostDbContext> factory,
        IDataProtectionProvider protection,
        UpgradePayload payload,
        CancellationToken ct)
    {
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var identity = await db.Identities.AsNoTracking()
                .SingleAsync(r => r.Id == payload.Identity.Value, ct);
            identity.Name.Should().Be("bestandsagent", "das Upgrade darf Bestandsdaten nicht anfassen");
            identity.ProfileId.Should().Be(payload.ProfileId, "die Zuordnung zum Profil bleibt bestehen");
            identity.RolesJson.Should().Contain(payload.RoleId.ToString());

            (await db.Roles.AsNoTracking().SingleAsync(r => r.Id == payload.RoleId, ct))
                .RateLimitPerMinute.Should().Be(42);
            (await db.Profiles.AsNoTracking().SingleAsync(r => r.Id == payload.ProfileId, ct))
                .LazyToolsEnabled.Should().BeTrue();
            (await db.ConfigVersions.CountAsync(r => r.ServerId == payload.Server.Value, ct))
                .Should().Be(2, "der Versionsverlauf bleibt vollstaendig");
        }

        // Der Kern: Geheimtext, der VOR dem Upgrade geschrieben wurde, muss DANACH lesbar sein.
        var configs = new EfUpstreamConfigStore(factory, protection);
        var alt = await configs.GetVersionAsync(payload.Server, new ConfigVersionId(1), ct);
        var neu = await configs.GetVersionAsync(payload.Server, new ConfigVersionId(2), ct);

        alt.Should().NotBeNull();
        neu.Should().NotBeNull();
        alt!.Slug.Should().Be("alt");
        neu!.Slug.Should().Be("neu");
        neu.Stdio!.EnvironmentVariables!["API_TOKEN"].Should().Be(
            UpstreamSecret, "ein Upgrade, das Geheimtext unlesbar macht, faellt sonst nicht auf");
        alt.Stdio!.EnvironmentVariables!["API_TOKEN"].Should().Be(UpstreamSecret);

        var keys = new ApiKeyService(factory);
        (await keys.ValidateAsync(payload.ApiKeyPlaintext, ct))
            .Should().Be(payload.Identity, "ein Upgrade darf keinen Agenten aussperren");
        (await keys.ListAsync(payload.Identity, ct))
            .Should().ContainSingle(k => k.KeyId == payload.ApiKeyId);

        var users = new UiUserService(factory);
        var user = await users.ValidateCredentialsAsync(payload.UiUsername, UiPassword, ct);
        user.Should().NotBeNull("ein Upgrade darf den Betreiber nicht aussperren");
        user!.Role.Should().Be(UiRole.Operator);

        if (payload.OAuthTokenSeeded)
        {
            var token = await new UpstreamOAuthTokenStore(factory, protection).GetAsync(payload.Server, ct);
            token.Should().NotBeNull(
                "ein nicht mehr entschluesselbares Token liefert der Store als 'kein Token' — genau "
                + "das waere der stille Datenverlust, den diese Zeile aufdeckt");
            token!.AccessToken.Should().Be(OAuthAccessToken);
            token.RefreshToken.Should().Be(OAuthRefreshToken);
            token.ExpiresAt.Should().Be(OAuthExpiry);
            token.Issuer.Should().Be("https://as.example.com");

            if (payload.OAuthTokenWithoutBinding)
            {
                // Plan 0005 F5: Ein Bestandstoken ohne Bindung wird nach dem Update weiter vorgelegt —
                // sonst muesste jede laufende Instanz ihre Upstreams neu autorisieren.
                token.Resource.Should().BeNull();
                token.ClientId.Should().BeNull();
                token.Revision.Should().Be(Guid.Empty, "die Zeile stammt aus der Zeit vor den Kennungen");
                token.IsBoundTo("https://mcp.example.com/mcp", "gateway").Should().BeTrue(
                    "ein Token ohne Bindung gilt als passend, bis seine erste Erneuerung sie nachschreibt");
            }
        }
    }

    public static bool KnowsOAuthTokens(IReadOnlyList<string> migrations)
        => migrations.Any(m => m.EndsWith(OAuthTokenMigrationSuffix, StringComparison.Ordinal));

    /// <summary>Ab diesem Stand traegt ein Token Bindung und Kennung (Plan 0005 F5, F14).</summary>
    public static bool KnowsOAuthTokenBinding(IReadOnlyList<string> migrations)
        => migrations.Any(m => m.EndsWith(OAuthTokenBindingMigrationSuffix, StringComparison.Ordinal));

    private static UpstreamServerConfig ConfigWithSecret(string slug) => new(
        slug,
        $"Server {slug}",
        UpstreamTransportKind.Stdio,
        Enabled: true,
        Stdio: new StdioTransportOptions(
            "cmd",
            ["--arg"],
            new Dictionary<string, string> { ["API_TOKEN"] = UpstreamSecret }));
}
