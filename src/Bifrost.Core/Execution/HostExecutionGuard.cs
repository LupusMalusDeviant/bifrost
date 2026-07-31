using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;

namespace Bifrost.Core.Execution;

/// <summary>
/// Der Torposten vor jedem nativen Startweg (ADR-0025 E4): Validieren, Testen, Starten,
/// Paketimport, Konfigurationsimport fragen <b>hier</b>.
/// <para>
/// Es gibt bewusst nur diese eine Methode, und sie nimmt die Policy als Argument statt sie sich zu
/// besorgen. Wer sie aufruft, muss die Policy haben — und wer sie nicht hat, bekommt
/// <see cref="HostExecutionPolicy.Unresolved"/> und damit eine Absage. Ein vergessener
/// Verdrahtungsschritt ist so ein lauter Fehler, kein stiller Durchlass.
/// </para>
/// </summary>
public static class HostExecutionGuard
{
    /// <summary>
    /// Prüft eine Konfiguration und wirft bei Verbot. Liefert die Entscheidung zurück, damit ein
    /// Aufrufer sie protokollieren kann, auch wenn sie positiv ausfiel.
    /// </summary>
    /// <exception cref="HostExecutionDeniedException">
    /// Wenn native Ausführung nicht erlaubt ist. Bewusst eine <see cref="ArgumentException"/>: Die
    /// bestehenden Schreibpfade in API und UI beantworten eine <c>ArgumentException</c> bereits mit
    /// „400 / Formularfehler". Eine neue Ausnahmeart hätte dort einen zweiten Behandlungspfad
    /// gebraucht — und zwei Pfade laufen irgendwann auseinander.
    /// </exception>
    [HostExecutionChecked]
    public static HostExecutionDecision Ensure(IHostExecutionPolicy? policy, UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var decision = Evaluate(policy, config);
        if (!decision.Allowed)
        {
            throw new HostExecutionDeniedException(config.Slug, decision);
        }

        return decision;
    }

    /// <summary>
    /// Dieselbe Frage ohne Ausnahme — für Wege, die einen Befund sammeln statt abzubrechen (etwa
    /// die Vorschau eines Konfigurationsimports).
    /// </summary>
    [HostExecutionChecked]
    public static HostExecutionDecision Evaluate(IHostExecutionPolicy? policy, UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Fail-closed an der Wurzel: keine Policy = keine Entscheidung = nein.
        var effective = policy ?? HostExecutionPolicy.Unresolved;
        return effective.Evaluate(config)
            ?? new HostExecutionDecision(
                false,
                HostExecutionReason.Undetermined,
                $"Die Policy hat für '{config.Slug}' keine Entscheidung geliefert.",
                "Das ist ein Programmfehler in der Policy-Implementierung; bis zur Klärung startet nichts nativ.");
    }

    /// <summary>
    /// Die Prüfung für einen Fall ohne fertige Konfiguration: ein Paketmanifest, das seinen
    /// Transport nennt (ADR-0025 E4 — „ein Paket bringt eine Konfiguration mit, die niemand
    /// eingetippt hat").
    /// </summary>
    [HostExecutionChecked]
    public static HostExecutionDecision EnsureTransport(
        IHostExecutionPolicy? policy, string subject, UpstreamTransportKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        // Ein Stellvertreter, der genau das trägt, worauf die Policy schaut. Ohne Isolationsangabe —
        // ein Paket, das keine mitbringt, würde nativ starten.
        var probe = new UpstreamServerConfig(
            subject, subject, kind, Enabled: false,
            Stdio: kind is UpstreamTransportKind.Stdio ? new StdioTransportOptions(subject, []) : null,
            Cli: kind is UpstreamTransportKind.Cli ? new CliTransportOptions(subject, []) : null);

        var decision = Evaluate(policy, probe);
        if (!decision.Allowed)
        {
            throw new HostExecutionDeniedException(subject, decision);
        }

        return decision;
    }
}

/// <summary>
/// Native Ausführung wurde verweigert. Trägt die vollständige Entscheidung mit, damit Aufrufer den
/// stabilen Reason-Code weiterreichen können, statt ihn aus dem Meldungstext zu raten.
/// </summary>
public sealed class HostExecutionDeniedException : ArgumentException
{
    public HostExecutionDeniedException(string subject, HostExecutionDecision decision)
        : base(Compose(subject, decision), nameof(UpstreamServerConfig))
    {
        Decision = decision;
        Subject = subject;
    }

    public HostExecutionDeniedException()
        : this("unbekannt", new HostExecutionDecision(false, HostExecutionReason.Undetermined, "Keine Angabe."))
    {
    }

    public HostExecutionDeniedException(string message)
        : base(message)
    {
        Subject = "unbekannt";
        Decision = new HostExecutionDecision(false, HostExecutionReason.Undetermined, message);
    }

    public HostExecutionDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Subject = "unbekannt";
        Decision = new HostExecutionDecision(false, HostExecutionReason.Undetermined, message);
    }

    public HostExecutionDecision Decision { get; }

    public string Subject { get; }

    private static string Compose(string subject, HostExecutionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var remediation = decision.Remediation is { Length: > 0 } text ? $" {text}" : string.Empty;
        return $"[{decision.ReasonCode}] {decision.Summary}{remediation}";
    }
}
