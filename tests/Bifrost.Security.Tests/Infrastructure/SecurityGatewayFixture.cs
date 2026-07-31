using Bifrost.Abstractions;
using Bifrost.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

using Role = Bifrost.Abstractions.Role;

namespace Bifrost.Security.Tests.Infrastructure;

/// <summary>
/// Startet den echten Gateway-Host (die Zusammensetzung aus <c>Program.cs</c>) im Arbeitsspeicher.
/// <para>
/// <b>Warum der echte Host und keine Nachbildung:</b> Die Frage dieses Pakets lautet, ob ein
/// <em>neu hinzugefuegter</em> Endpunkt geschuetzt ist. Eine nachgebaute Routentabelle kennt nur
/// die Endpunkte, die jemand eingetragen hat — sie waere genau die Liste, gegen die dieses Paket
/// antritt. Nur der laufende Host kennt alle.
/// </para>
/// </summary>
public class SecurityGatewayFixture : WebApplicationFactory<Program>
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), $"bifrost-sec-{Guid.NewGuid():N}");

    /// <summary>Alles, was der Dienst waehrend des Tests geschrieben hat.</summary>
    public CapturingLogProvider Log { get; } = new();

    /// <summary>
    /// Das Wegwerf-Datenverzeichnis dieses Laufs. Der Erstzugang legt darin seine Uebergabedatei
    /// ab — und genau die braucht der Leck-Scan, um den ECHTEN Wert zu kennen, nach dem er im Log
    /// sucht. Ein fest verdrahteter Korpuswert koennte das nicht: Das Token entsteht zur Laufzeit.
    /// </summary>
    public string DataDirectory => _dataDir;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Directory.CreateDirectory(_dataDir);
        builder.UseSetting("environment", "Development");
        builder.UseSetting("BIFROST_DATA_DIR", _dataDir);
        builder.UseSetting("BIFROST_DB_CONNECTION", $"Data Source={Path.Combine(_dataDir, "sec.db")}");
        builder.ConfigureLogging(logging =>
        {
            // Alles mitschreiben: Ein Filter auf Information waere die Stelle, an der ein
            // Debug-Aufruf mit Klartext unbemerkt bliebe — und der Debug-Modus ist in der
            // Stoerungssuche genau der, den ein Betreiber einschaltet.
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Log);
        });
    }

    public PersistentRbacStore RbacStore => Services.GetRequiredService<PersistentRbacStore>();

    public IApiKeyService ApiKeys => Services.GetRequiredService<IApiKeyService>();

    /// <summary>Identitaet mit genau den uebergebenen Grants und einem frischen API-Key.</summary>
    public async Task<(IdentityId Identity, string ApiKey)> SeedIdentityAsync(
        string name, IReadOnlyList<Grant> grants)
    {
        var ct = TestContext.Current.CancellationToken;
        var role = new Role(RoleId.New(), $"{name}-rolle", grants);
        await RbacStore.UpsertRoleAsync(role, ct);
        var identity = new Identity(IdentityId.New(), name, IdentityKind.Agent, [role.Id], null);
        await RbacStore.UpsertIdentityAsync(identity, ct);
        var key = await ApiKeys.IssueAsync(identity.Id, $"{name}-key", null, ct);
        return (identity.Id, key.PlaintextKey);
    }

    /// <summary>Global-Grant — die Identitaet, die die Management-API bedienen darf.</summary>
    public Task<(IdentityId Identity, string ApiKey)> SeedAdminAsync(string name = "sec-admin")
        => SeedIdentityAsync(
            name,
            [new Grant(
                new PermissionScope(null, null),
                [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])]);

    /// <summary>
    /// Authentifiziert, aber ohne Global-Grant: der Fall, den die Management-API mit 403 abweisen
    /// muss. Der Grant zeigt bewusst auf einen Server, den es nicht gibt — die Identitaet ist
    /// gueltig, ihre Rechte gehen nur nirgendwohin.
    /// </summary>
    public Task<(IdentityId Identity, string ApiKey)> SeedPlainAsync(string name = "sec-plain")
        => SeedIdentityAsync(
            name,
            [new Grant(new PermissionScope(ServerId.New(), null), [ToolAction.UseTool])]);

    public HttpClient CreateApiClient(string? apiKey)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        }

        return client;
    }
}
