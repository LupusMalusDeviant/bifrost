namespace Bifrost.Web;

/// <summary>Authorization-Policy-Namen für die UI-Rollen (FR-30). In Program.cs registriert, in Komponenten via [Authorize]/AuthorizeView genutzt.</summary>
public static class UiPolicies
{
    /// <summary>Nur Admin: RBAC-, Key- und Nutzerverwaltung.</summary>
    public const string Admin = "ui-admin";

    /// <summary>Admin oder Operator: Server verwalten, Tools testen.</summary>
    public const string Operator = "ui-operator";

    /// <summary>Alle angemeldeten UI-Rollen (inkl. Auditor): lesen.</summary>
    public const string Authenticated = "ui-authenticated";

    public const string RoleClaim = "bifrost-ui-role";
}
