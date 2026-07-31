using Bifrost.Abstractions;

namespace Bifrost.Web;

/// <summary>
/// Auditiert UI-Aktionen (WP6-DoD: "UI-Zugriffe erscheinen im Audit-Log"). UI-Nutzer sind keine
/// Agenten-Identitäten, daher Caller=null und Origin=Ui; der handelnde Nutzer + die Aktion stehen
/// im Tool-Feld als <c>ui:{user}:{action}</c> — so bleibt es über den bestehenden Tool-Filter abfragbar.
/// </summary>
public static class UiAudit
{
    public static void Record(
        IAuditSink sink, TimeProvider time, string username, AuditEventKind kind, string action, ServerId? server = null)
        => sink.Record(new AuditEvent(
            time.GetUtcNow(), null, CallOrigin.Ui, kind, server, $"ui:{username}:{action}",
            null, null, null, null, null));
}
