using Bifrost.Abstractions;

namespace Bifrost.Web;

/// <summary>
/// Hält die Agenten-Identität, unter der UI-Test-Aufrufe laufen (Plan: "Test-Aufruf mit Admin-Rechten").
/// Wird beim Bootstrap gesetzt; ihre Rolle trägt einen Global-Grant, sodass die UI jedes Tool testen kann.
/// Der Audit-Eintrag zeigt Origin=Ui und diese Identität.
/// </summary>
public sealed class UiInternalIdentity
{
    public IdentityId Value { get; set; }
}
