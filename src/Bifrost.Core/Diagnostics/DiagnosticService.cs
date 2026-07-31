using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics.Checks;

namespace Bifrost.Core.Diagnostics;

/// <summary>
/// Führt die Checks aus und macht daraus einen Bericht (M2-Vertrag §3).
/// <para>
/// <b>Der Bericht kommt immer.</b> Ein Check, der hängt, wird nach seinem Zeitlimit abgeschnitten
/// und als Fehler mit Begründung geführt; einer, der wirft, ebenso. Ein Diagnosewerkzeug, das
/// selbst hängt, ist die schlechteste Sorte Werkzeug: Es beantwortet die Frage nicht und nimmt dem
/// Betreiber gleichzeitig die Zeit, sie anders zu beantworten.
/// </para>
/// <para>
/// Die Checks laufen nebenläufig. Sie lesen nur; keiner hängt vom Ergebnis eines anderen ab —
/// Querbezüge stehen als <i>Code</i> im Text („siehe BFR-DB-0002"), nicht als Aufrufreihenfolge.
/// </para>
/// </summary>
public sealed class DiagnosticService : IDiagnosticService
{
    private readonly IReadOnlyList<IDiagnosticCheck> _checks;
    private readonly DiagnosticContext _context;
    private readonly TimeProvider _time;

    public DiagnosticService(
        DiagnosticContext context,
        IEnumerable<IDiagnosticCheck> checks,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(checks);

        _context = context;
        _time = timeProvider ?? TimeProvider.System;
        _checks = [.. checks];

        // Doppelte Codes werden hier abgewiesen und nicht erst im Bericht sichtbar: Zwei Befunde
        // unter demselben Code machen jede Auswertung mehrdeutig, und ein Runbook kann sich dann
        // auf nichts mehr stützen.
        var duplicates = _checks
            .GroupBy(check => check.Code, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new ArgumentException(
                $"Diagnosecode doppelt vergeben: {string.Join(", ", duplicates)}. "
                + "Codes sind stabil und stehen in DiagnosticCodes; ein Code gehört zu genau einem Check.",
                nameof(checks));
        }
    }

    /// <summary>Der ausgelieferte Satz Checks. Reihenfolge egal — der Bericht wird nach Code sortiert.</summary>
    public static IReadOnlyList<IDiagnosticCheck> DefaultChecks { get; } =
    [
        new DataDirectoryCheck(),
        new LegacyEnvironmentVariablesCheck(),
        new PublicBaseUrlCheck(),

        new DatabaseProviderCheck(),
        new DatabaseReachabilityCheck(),
        new AppliedMigrationsCheck(),
        new PendingMigrationsCheck(),
        new SqliteDatabaseFileCheck(),

        new KeyRingPresenceCheck(),
        new KeyRingProtectionCheck(),
        new KeyRingCertificateCheck(),
        new KeyRingLossCheck(),
        new KeyRingPasswordSourceCheck(),

        new ListenPortCheck(),
        new InsecureCookieTransportCheck(),
        new TrustedProxiesCheck(),

        new ContainerRuntimeCheck(),
        new WasiHostCheck(),

        new UpstreamStatesCheck(),

        new HostExecutionPolicyCheck(),
        new HostExecutionAdoptionCheck(),
    ];

    public static DiagnosticService CreateDefault(DiagnosticContext context, TimeProvider? timeProvider = null)
        => new(context, DefaultChecks, timeProvider);

    public async Task<DiagnosticReport> RunAsync(DiagnosticScope scope, CancellationToken ct)
    {
        var startedAt = _time.GetUtcNow();
        var stopwatch = _time.GetTimestamp();

        var selected = _checks.Where(check => (scope & check.Scope) != 0).ToList();
        var results = await Task.WhenAll(selected.Select(check => RunOneAsync(check, ct)))
            .ConfigureAwait(false);

        // Nach Code sortiert: Zwei Läufe auf demselben Zustand sollen dieselbe Ausgabe ergeben,
        // sonst sieht ein Diff nach Veränderung aus, wo nur die Nebenläufigkeit gewürfelt hat.
        var ordered = results
            .OrderBy(result => result.Code, StringComparer.Ordinal)
            .ToList();

        return new DiagnosticReport(scope, startedAt, _time.GetElapsedTime(stopwatch), ordered);
    }

    private async Task<DiagnosticCheck> RunOneAsync(IDiagnosticCheck check, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(check.Timeout);

        // Der Check bekommt den Token — aber der Dienst verlässt sich NICHT darauf, dass er ihn
        // beachtet. Deshalb das Rennen gegen die Frist statt eines blossen await: Ein Check, der
        // synchron blockiert oder einen Token ignoriert, kostet dann seine eigene Zeile im Bericht
        // und nicht den ganzen Bericht.
        var work = Task.Run(() => check.RunAsync(_context, deadline.Token), CancellationToken.None);
        var expiry = Task.Delay(check.Timeout, _time, CancellationToken.None);
        var finished = await Task.WhenAny(work, expiry).ConfigureAwait(false);

        if (finished != work)
        {
            Abandon(work);
            return DiagnosticRedaction.Scrub(TimedOut(check));
        }

        try
        {
            var result = await work.ConfigureAwait(false);
            if (result is null)
            {
                return DiagnosticRedaction.Scrub(CheckOutcome.Fail(
                    check.Code,
                    "Der Check lieferte kein Ergebnis.",
                    "Das ist ein Fehler im Diagnosedienst selbst — bitte melden."));
            }

            if (!string.Equals(result.Code, check.Code, StringComparison.Ordinal))
            {
                // Ein Check, der unter fremdem Code antwortet, macht den Bericht unbrauchbar.
                return DiagnosticRedaction.Scrub(CheckOutcome.Fail(
                    check.Code,
                    $"Der Check antwortete unter dem fremden Code '{result.Code}'.",
                    "Das ist ein Fehler im Diagnosedienst selbst — bitte melden."));
            }

            return DiagnosticRedaction.Scrub(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DiagnosticRedaction.Scrub(TimedOut(check));
        }
#pragma warning disable CA1031 // Ein Diagnosedienst, der an einem Check abstürzt, ist nutzlos.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return DiagnosticRedaction.Scrub(CheckOutcome.Fail(
                check.Code,
                $"Der Check brach mit einem Fehler ab: {exception.Message}",
                "Die Meldung nennt die Ursache. Bleibt sie unklar, hilft der Serverlog zum selben Zeitpunkt.",
                CheckOutcome.Details(("fehlerart", exception.GetType().Name))));
        }
    }

    private static DiagnosticCheck TimedOut(IDiagnosticCheck check) => CheckOutcome.Fail(
        check.Code,
        $"Der Check hat sein Zeitlimit von {check.Timeout.TotalSeconds:0.#} s überschritten und "
        + "wurde abgebrochen. Der übrige Bericht ist vollständig.",
        "Ein Zeitlimit heisst hier: Das Geprüfte antwortet nicht. Bei Datenbank oder Netz zuerst "
        + "die Erreichbarkeit von Hand prüfen, bei Prozessen die Runtime.",
        CheckOutcome.Details(("zeitlimit_sekunden", check.Timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))));

    /// <summary>
    /// Die abgeschnittene Arbeit läuft weiter — beenden lässt sie sich nicht, wenn sie den Token
    /// ignoriert. Ihre Ausnahme wird hier abgeholt, sonst reisst sie später den Finalizer-Thread mit.
    /// </summary>
    private static void Abandon(Task<DiagnosticCheck> work)
        => _ = work.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
