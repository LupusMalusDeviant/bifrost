namespace Bifrost.Abstractions;

/// <summary>
/// UI-Rollen für menschliche Betreiber (FR-30) — getrennt vom Agenten-RBAC.
/// Admin = alles; Operator = Server verwalten, keine Key-/Rollenverwaltung; Auditor = nur Logs lesen.
/// </summary>
public enum UiRole
{
    Auditor = 0,
    Operator = 1,
    Admin = 2,
}

public sealed record UiUserInfo(Guid Id, string Username, UiRole Role, DateTimeOffset CreatedAt);

/// <summary>Verwaltung der menschlichen UI-Nutzer (WP6.1). Passwörter werden nur als Hash gespeichert (NFR-04).</summary>
public interface IUiUserService
{
    /// <summary>Prüft Anmeldedaten; liefert den Nutzer bei Erfolg, sonst null.</summary>
    Task<UiUserInfo?> ValidateCredentialsAsync(string username, string password, CancellationToken ct);

    Task<UiUserInfo> CreateAsync(string username, string password, UiRole role, CancellationToken ct);

    Task SetPasswordAsync(Guid userId, string newPassword, CancellationToken ct);

    Task DeleteAsync(Guid userId, CancellationToken ct);

    Task<IReadOnlyList<UiUserInfo>> ListAsync(CancellationToken ct);

    Task<bool> AnyExistAsync(CancellationToken ct);
}
