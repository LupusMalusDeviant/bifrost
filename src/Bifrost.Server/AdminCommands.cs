using System.Security.Cryptography;
using Bifrost.Abstractions;
using Bifrost.Persistence;

namespace Bifrost.Server;

/// <summary>
/// Betriebliche Recovery-Kommandos (WP8.4) für den Fall „kein Zugang mehr" — bis v1.0 musste man
/// dafür Datenbankzeilen von Hand löschen. Sie laufen gegen die konfigurierte Datenbank, geben den
/// neuen Zugang **einmalig** auf der Konsole aus und beenden den Prozess, ohne den Gateway zu starten.
/// </summary>
internal static class AdminCommands
{
    public const string ResetUiAdmin = "--reset-ui-admin";
    public const string IssueBootstrapKey = "--issue-bootstrap-key";

    public static bool IsAdminCommand(string[] args)
        => args.Contains(ResetUiAdmin) || args.Contains(IssueBootstrapKey);

    public static async Task<int> RunAsync(WebApplication app, string[] args, CancellationToken ct = default)
    {
        try
        {
            // Schema sicherstellen — das Kommando läuft ohne die Hosted Services.
            await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(ct);

            if (args.Contains(ResetUiAdmin))
            {
                var username = ArgumentAfter(args, ResetUiAdmin) ?? "admin";
                var result = await ResetUiAdminAsync(
                    app.Services.GetRequiredService<IUiUserService>(), username, ct);
                Console.WriteLine(result.WasExisting
                    ? $"UI-Nutzer '{username}' zurückgesetzt (Rolle unverändert: {result.Role})."
                    : $"UI-Admin '{username}' neu angelegt.");
                Console.WriteLine($"Passwort (wird NICHT gespeichert und nie wieder angezeigt): {result.Password}");
            }

            if (args.Contains(IssueBootstrapKey))
            {
                var result = await IssueBootstrapKeyAsync(
                    app.Services.GetRequiredService<IRbacManagement>(),
                    app.Services.GetRequiredService<IApiKeyService>(),
                    ct);
                Console.WriteLine($"Notfall-Identität '{result.IdentityName}' mit Global-Grant angelegt.");
                Console.WriteLine($"API-Key (wird NICHT gespeichert und nie wieder angezeigt): {result.ApiKey}");
                Console.WriteLine("Nach Gebrauch entfernen, falls nur zur Wiederherstellung gedacht.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Kommando fehlgeschlagen: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Setzt das Passwort eines UI-Nutzers zurück oder legt ihn als Admin an. Liefert das neue Passwort.</summary>
    internal static async Task<(string Password, bool WasExisting, UiRole Role)> ResetUiAdminAsync(
        IUiUserService users, string username, CancellationToken ct)
    {
        var password = GeneratePassword();
        var existing = (await users.ListAsync(ct)).FirstOrDefault(u => u.Username == username);

        if (existing is not null)
        {
            await users.SetPasswordAsync(existing.Id, password, ct);
            return (password, true, existing.Role);
        }

        await users.CreateAsync(username, password, UiRole.Admin, ct);
        return (password, false, UiRole.Admin);
    }

    /// <summary>
    /// Legt eine NEUE Agenten-Identität mit Global-Grant an (statt eine bestehende zu überschreiben):
    /// nichts wird zerstört, und der Notzugang lässt sich hinterher gezielt wieder entfernen.
    /// </summary>
    internal static async Task<(string IdentityName, string ApiKey)> IssueBootstrapKeyAsync(
        IRbacManagement rbac, IApiKeyService keys, CancellationToken ct)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var role = new Role(RoleId.New(), $"recovery-admin-{stamp}",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])]);
        var identity = new Identity(IdentityId.New(), $"recovery-admin-{stamp}", IdentityKind.Agent, [role.Id]);

        await rbac.UpsertRoleAsync(role, ct);
        await rbac.UpsertIdentityAsync(identity, ct);
        var issued = await keys.IssueAsync(identity.Id, "recovery", expiresAt: null, ct);

        return (identity.Name, issued.PlaintextKey);
    }

    private static string? ArgumentAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : null;
    }

    private static string GeneratePassword()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');
}
