namespace Bifrost.Abstractions;

/// <summary>Aktionstyp eines Zugriffs (FR-28).</summary>
public enum ToolAction
{
    UseTool = 0,
    ReadResource = 1,
    UsePrompt = 2,
}

/// <summary>Wirkungsebene eines Grants: ganzer Server (<see cref="Tool"/> == null) oder einzelnes Tool (ADR-0006).</summary>
public sealed record PermissionScope(ServerId? Server, NamespacedToolName? Tool);

/// <summary>Ergebnis einer Autorisierungsentscheidung. Bei Deny ist <see cref="DenyReason"/> gesetzt (wird auditiert, FR-22).</summary>
public sealed record AuthorizationDecision(bool Allowed, string? DenyReason)
{
    public static AuthorizationDecision Allow { get; } = new(true, null);
    public static AuthorizationDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Einzige Autorisierungsquelle im System (ADR-0006). Default-Deny; Sichtbarkeit folgt Berechtigung (FR-29).
/// Implementierungen werten einen vorkompilierten In-Memory-Snapshot aus — beide Methoden sind reine Funktionen.
/// </summary>
public interface IAuthorizationService
{
    /// <summary>Einzelentscheidung im Hot Path (Ziel &lt; 1 ms).</summary>
    AuthorizationDecision Evaluate(IdentityId identity, PermissionScope scope, ToolAction action);

    /// <summary>
    /// RBAC-gefilterte Sicht auf einen Katalog. Speist <c>tools/list</c>, <c>search_tools</c>,
    /// REST-Tool-Liste und OpenAPI-Generierung — es gibt keine zweite Sichtbarkeitsquelle (DON'T Nr. 4).
    /// </summary>
    IReadOnlyList<CatalogEntry> FilterVisible(IdentityId identity, IReadOnlyList<CatalogEntry> catalog);

    /// <summary>
    /// Profil/Rollen des Aufrufers als Klartext fürs Audit-Log (FR-21) — die Id allein sagt beim
    /// Nachvollziehen nichts. Läuft im Hot Path, wird deshalb mit dem Snapshot gecacht.
    /// Null, wenn die Identität nicht registriert ist.
    /// </summary>
    string? DescribeCaller(IdentityId identity);
}

/// <summary>Prüft einen präsentierten API-Key gegen die gespeicherten Hashes (NFR-04). Null = ungültig/widerrufen/abgelaufen.</summary>
public interface IApiKeyValidator
{
    ValueTask<IdentityId?> ValidateAsync(string presentedKey, CancellationToken ct);
}
