using System.Collections.Concurrent;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;
using Bifrost.Abstractions.Operations;

namespace Bifrost.Server.Importing;

/// <summary>
/// Die vorgemerkten Importpläne. <b>Der Zustand bleibt hier; nur das Handle reist.</b>
///
/// <para>
/// Das ist kein neues Muster, sondern dasselbe wie bei <c>RestoreService</c> und
/// <c>ConfigurationExportService</c>: <see cref="PlanTokens"/> gibt Geltungsdauer und Bauart vor
/// (30 Minuten, kryptografisch zufällig), der Vorgang liegt beim Dienst, und ein unbekanntes oder
/// abgelaufenes Handle führt zu einer klaren Absage statt zu einem Versuch auf geratenen Daten.
/// </para>
///
/// <para>
/// <b>Warum es hier <em>zwingend</em> ein Handle braucht.</b> Beim Restore war das Handle die
/// Antwort auf eine Passphrase, die nicht durch eine API-Antwort laufen soll. Hier ist es die
/// Antwort auf dieselbe Frage in schärferer Form: Der Plan trägt die Klartextwerte der fremden
/// Konfiguration, und ohne sie ließe sich nichts anlegen. Gäbe man ihn hinaus und nähme ihn zurück,
/// stünden diese Werte in jedem Proxy-Log auf dem Weg — <b>zweimal</b>. Mit dem Handle verlassen sie
/// den Prozess nie.
/// </para>
///
/// <para>
/// <b>Der Dienst muss ein Singleton sein.</b> Zwei Instanzen hießen: Der Plan aus dem einen Aufruf
/// ist im nächsten unbekannt — derselbe Grund, aus dem <c>OperationsRegistration</c> den
/// Restore-Dienst als Singleton anmeldet.
/// </para>
/// </summary>
public sealed class ImportPlanStore
{
    /// <summary>
    /// Wie viele Pläne gleichzeitig vorgemerkt sein dürfen. Jeder hält Klartextwerte; eine
    /// unbegrenzte Ablage wäre ein Speicher, den ein Aufrufer mit fremden Zugangsdaten vollschreiben
    /// kann. Über der Grenze wird die älteste Vormerkung verworfen — sie ist die, deren Frist
    /// ohnehin zuerst abläuft.
    /// </summary>
    public const int MaxEntries = 64;

    private readonly ConcurrentDictionary<string, ImportPlanEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ImportPlanStore(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Wie viele Pläne gerade vorgemerkt sind — für Diagnose und Tests.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Merkt einen Plan vor und gibt das Handle zurück.
    /// </summary>
    /// <param name="owner">
    /// Wem der Plan gehört. Ein Handle ist an seinen Aussteller gebunden: Wer ein fremdes Handle in
    /// die Finger bekommt, soll damit keinen fremden Import auslösen können.
    /// </param>
    public (string Token, DateTimeOffset ExpiresAt) Register(ImportPlan plan, IdentityId owner)
    {
        ArgumentNullException.ThrowIfNull(plan);

        DropExpired();
        Trim();

        var now = _time.GetUtcNow();
        var token = PlanTokens.New();
        _entries[token] = new ImportPlanEntry(plan, owner, now, now + PlanTokens.Lifetime);
        return (token, now + PlanTokens.Lifetime);
    }

    /// <summary>
    /// Sieht einen vorgemerkten Plan an, <b>ohne</b> ihn zu verbrauchen — für die Probe, die
    /// mehrfach laufen darf, weil sie nichts ändert.
    /// </summary>
    public ImportPlanEntry? Peek(string? token, IdentityId owner)
    {
        DropExpired();

        if (string.IsNullOrWhiteSpace(token)
            || !_entries.TryGetValue(token, out var entry)
            || entry.Owner != owner)
        {
            return null;
        }

        return entry.ExpiresAt > _time.GetUtcNow() ? entry : null;
    }

    /// <summary>
    /// Holt einen vorgemerkten Plan und entwertet ihn <b>im selben Schritt</b>.
    ///
    /// <para>
    /// Entnehmen und Entwerten müssen eine Bewegung sein: Andernfalls endeten zwei gleichzeitige
    /// Übernahmen desselben Handles beide erfolgreich, und jeder Server der Quelldatei stünde
    /// zweimal da — mit einer Slug-Kollision, die vorher niemand gemeldet hat.
    /// </para>
    /// </summary>
    public ImportPlanEntry? Claim(string? token, IdentityId owner)
    {
        DropExpired();

        if (string.IsNullOrWhiteSpace(token) || !_entries.TryGetValue(token, out var found))
        {
            return null;
        }

        if (found.Owner != owner)
        {
            // Kein Entwerten: Ein fremdes Handle darf sich nicht durch Vorlegen abräumen lassen.
            return null;
        }

        if (!_entries.TryRemove(token, out var entry))
        {
            return null;
        }

        return entry.ExpiresAt > _time.GetUtcNow() ? entry : null;
    }

    /// <summary>
    /// Wirft abgelaufene Vormerkungen weg. Sie enthalten Klartextwerte — ein Plan, den niemand mehr
    /// anwenden wird, darf sie nicht bis zum Prozessende festhalten.
    /// </summary>
    private void DropExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var (token, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(token, out _);
            }
        }
    }

    private void Trim()
    {
        while (_entries.Count >= MaxEntries)
        {
            var oldest = _entries
                .OrderBy(pair => pair.Value.CreatedAt)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (oldest is null || !_entries.TryRemove(oldest, out _))
            {
                return;
            }
        }
    }
}

/// <summary>Ein vorgemerkter Plan samt Eigentümer und Frist. Verlässt den Prozess nie.</summary>
public sealed record ImportPlanEntry(
    ImportPlan Plan,
    IdentityId Owner,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
