using System.Security.Cryptography;
using Bifrost.Abstractions;
using Bifrost.Persistence;
using Bifrost.Persistence.Startup;
using Bifrost.Server.Bootstrap;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// Stellt ein neues Setup-Token aus und gibt es <b>auf der Konsole</b> aus (WP3.4).
    /// <para>
    /// Der Weg für eine Installation, deren Übergabedatei jemand weggeworfen hat, und der einzige,
    /// der auf einer Installation mit bestehenden Zugängen überhaupt noch ein Token liefert — dort
    /// aber nur gegen den lokalen Recovery-Nachweis (<see cref="IBootstrapRecoveryProof"/>).
    /// </para>
    /// <para>
    /// Er läuft im Serverprozess und nicht in der CLI, aus demselben Grund wie die
    /// Key-Ring-Kommandos: Hier entsteht ein Geheimnis, und es soll den Rechner nicht verlassen.
    /// Im Container: <c>docker compose run --rm bifrost dotnet Bifrost.Server.dll --bootstrap-init</c>.
    /// </para>
    /// </summary>
    public const string BootstrapInit = "--bootstrap-init";

    /// <summary>
    /// Der Ausweg aus <c>BFR-DB-0101</c> <b>ohne laufenden Gateway</b> (M2, WP2.7).
    /// <para>
    /// Genau das ist der Punkt: Ein offener Migrationseintrag verweigert den Schreibbetrieb, indem
    /// der Start abbricht — der Prozess kommt also gar nicht erst hoch. Der gleichnamige
    /// REST-Endpunkt und <c>bifrost db unblock</c> greifen dann ins Leere, weil niemand antwortet.
    /// Deshalb läuft dieser Weg hier, im Serverprozess, vor dem Gateway-Start und ohne ihn.
    /// </para>
    /// <para>
    /// Er repariert nichts und beurteilt nichts. Er löst, was der Betreiber geprüft hat —
    /// <see cref="MigrationJournal.ClearUnfinishedAsync"/> ist ausdrücklich kein Teil des Startpfads.
    /// </para>
    /// </summary>
    public const string UnblockDatabase = "--db-unblock";

    public static bool IsAdminCommand(string[] args)
        => args.Contains(ResetUiAdmin)
            || args.Contains(IssueBootstrapKey)
            || args.Contains(BootstrapInit)
            || args.Contains(UnblockDatabase);

    public static async Task<int> RunAsync(WebApplication app, string[] args, CancellationToken ct = default)
    {
        try
        {
            // ZUERST und ohne Schema-Initialisierung: Der Riegel aus BFR-DB-0101 lässt den
            // Initializer werfen — ihn vorher aufzurufen hieße, das Kommando genau an dem Zustand
            // scheitern zu lassen, den es lösen soll.
            if (args.Contains(UnblockDatabase))
            {
                var removed = await UnblockDatabaseAsync(
                    app.Services.GetRequiredService<IDbContextFactory<BifrostDbContext>>(), ct);
                Console.WriteLine(removed == 0
                    ? "Es stand kein offener Migrationseintrag an; es wurde nichts geändert."
                    : $"{removed} offene(r) Migrationseintrag/-einträge entfernt. Der Schreibbetrieb ist "
                      + "wieder freigegeben — der Schemazustand ist damit NICHT geprüft.");
                return 0;
            }

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
                await AuditAsync(
                    app,
                    result.WasExisting
                        ? $"Recovery: Passwort des UI-Nutzers '{username}' ueber {ResetUiAdmin} zurueckgesetzt."
                        : $"Recovery: UI-Admin '{username}' ueber {ResetUiAdmin} neu angelegt.",
                    ct);
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
                await AuditAsync(
                    app,
                    $"Recovery: Notfall-Identitaet '{result.IdentityName}' mit Global-Grant ueber "
                    + $"{IssueBootstrapKey} angelegt.",
                    ct);
            }

            if (args.Contains(BootstrapInit))
            {
                return await RunBootstrapInitAsync(app, ct);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Kommando fehlgeschlagen: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Stellt ein Setup-Token aus und gibt es auf der <b>Konsole</b> aus — nicht ins Log.
    /// <para>
    /// Der Unterschied ist die halbe Miete dieses Pakets: Die Standardausgabe eines
    /// <c>docker compose run</c> gehört dem Menschen, der davorsitzt. Das Anwendungslog gehört der
    /// Logaggregation, dem Ticketanhang und der Sicherung des Logverzeichnisses.
    /// </para>
    /// </summary>
    private static async Task<int> RunBootstrapInitAsync(WebApplication app, CancellationToken ct)
    {
        var bootstrap = app.Services.GetRequiredService<IBootstrapService>();

        // Erst sagen, was gilt. Ein Betreiber, der dieses Kommando aufruft, weiss meistens nicht
        // mehr, in welchem Zustand die Installation ist — und das neue Token entwertet ein
        // eventuell noch ausstehendes.
        var before = await bootstrap.GetStatusAsync(ct);
        Console.WriteLine($"Bisheriger Zustand: {before.Phase}"
            + (before.IsPending ? $" (ein Token stand noch aus, gueltig bis {before.ExpiresAt:u} — es gilt ab jetzt nicht mehr)" : string.Empty));

        var result = await bootstrap.IssueAsync(BootstrapOrigin.LocalRecovery, ct);

        if (result.Outcome is not BootstrapOutcome.Issued)
        {
            Console.Error.WriteLine($"Kein Setup-Token ausgestellt: {result.Description}");
            return 1;
        }

        Console.WriteLine("Setup-Token ausgestellt. Es gilt EINMAL und nur bis:");
        Console.WriteLine($"  {result.ExpiresAt:u}");
        Console.WriteLine();
        Console.WriteLine($"  {result.Token}");
        Console.WriteLine();
        Console.WriteLine($"Es steht auch in {result.HandoverPath}");
        Console.WriteLine($"  Rechte: {result.HandoverPermissions?.Description ?? "unbekannt"}");
        Console.WriteLine("Einloesen in der Web-UI unter /setup. Im Anwendungslog steht es nicht.");

        await AuditAsync(
            app,
            "Recovery: Setup-Token nach lokalem Nachweis ueber --bootstrap-init ausgestellt.",
            ct);
        return 0;
    }

    /// <summary>
    /// Schreibt einen Auditeintrag <b>direkt</b> in die Datenbank.
    /// <para>
    /// Der übliche Weg über <c>IAuditSink</c> läuft hier ins Leere: Diese Kommandos starten den
    /// Gateway nicht, und ohne ihn läuft auch der Batch-Writer nicht, der den Channel leert. Ein
    /// Eintrag, der nur im Arbeitsspeicher eines gleich beendeten Prozesses stand, ist kein Audit.
    /// </para>
    /// <para>
    /// Fehlschläge werden geschluckt: Ein Kommando, das den Zugang gerade wiederhergestellt hat,
    /// darf nicht daran scheitern, dass die Auditzeile nicht geschrieben werden konnte — der
    /// Betreiber hätte dann weder Zugang noch Eintrag.
    /// </para>
    /// </summary>
    private static async Task AuditAsync(WebApplication app, string detail, CancellationToken ct)
    {
        try
        {
            var factory = app.Services.GetRequiredService<IDbContextFactory<BifrostDbContext>>();
            await using var db = await factory.CreateDbContextAsync(ct);
            db.AuditEvents.Add(new AuditEventRow
            {
                Timestamp = DateTimeOffset.UtcNow,
                Origin = (int)CallOrigin.System,
                Kind = (int)AuditEventKind.Authentication,
                Tool = "recovery",
                Status = (int)InvocationStatus.Success,
                Detail = detail,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"Hinweis: Der Auditeintrag zu diesem Kommando konnte nicht geschrieben werden ({exception.Message}).");
        }
    }

    /// <summary>
    /// Entfernt offene Einträge aus dem Migrationsjournal und liefert deren Anzahl. Der Aufrufer hat
    /// den Zustand der Datenbank vorher geprüft oder sie wiederhergestellt — dieser Weg tut beides
    /// nicht (ADR-0024 E7: Verweigerung, nicht Reparatur).
    /// </summary>
    internal static async Task<int> UnblockDatabaseAsync(
        IDbContextFactory<BifrostDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await MigrationJournal.EnsureTableAsync(db, ct);
        return await MigrationJournal.ClearUnfinishedAsync(db, ct);
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
