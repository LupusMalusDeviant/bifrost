using System.Security.Cryptography;

using Bifrost.Abstractions.Setup;

namespace Bifrost.Server.Setup;

/// <summary>
/// Die prozesslokale Ablage der Wizard-Vorgaenge (WP4.4).
///
/// <para>
/// <b>Warum der Zustand hier liegt und nicht im Browser.</b> Ein Wizard, der seinen Stand in
/// versteckten Feldern oder in der Adresszeile fuehrt, verliert ihn beim Neuladen — und was er
/// nicht verliert, steht danach im Browserverlauf. Im Browser liegt deshalb nur eine Kennung;
/// alles andere bleibt im Serverprozess. Die Kennung ist kein Zugangsdatum: Sie benennt einen
/// Vorgang, und ab Schritt 3 gehoert der Vorgang einem angemeldeten Administrator, der beim
/// Fortsetzen verglichen wird.
/// </para>
///
/// <para>
/// <b>Prozesslokal und nicht persistent.</b> Ein Neustart des Gateways verliert die laufenden
/// Vorgaenge. Das ist kein Datenverlust: Was der Wizard angelegt hat — Server, Rollen, Identitaeten
/// — steht in der Instanz, und der Wizard baut seinen Stand daraus neu auf. Verloren geht die
/// gerade eingelesene Datei, und genau das sagt die Oberflaeche dann auch. Ein Vorgang in der
/// Datenbank waere die Alternative gewesen; sie haette den Plan mitsamt den Klartextwerten der
/// fremden Konfiguration persistiert — also genau das, was WP4.3 mit dem Vorschaumodell verhindert.
/// </para>
/// </summary>
public sealed class SetupSessionStore : ISetupSessionStore
{
    /// <summary>
    /// Wie lange ein unberuehrter Vorgang lebt. Lang genug fuer eine Kaffeepause und einen
    /// Neustart des Browsers, kurz genug, dass ein eingelesenes Dokument nicht ueber Nacht im
    /// Speicher liegt.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>
    /// Obergrenze. Der Erstaufbau ist ein Vorgang, kein Massenbetrieb — ohne Deckel waere eine
    /// anonyme Seite ein Weg, den Speicher des Gateways zu fuellen.
    /// </summary>
    public const int MaxSessions = 32;

    private readonly Dictionary<string, SetupSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    public SetupSessionStore(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    public SetupSession Start()
    {
        var now = _time.GetUtcNow();
        var session = new SetupSession
        {
            Handle = NewHandle(),
            StartedAt = now,
            LastSeenAt = now,
        };

        lock (_gate)
        {
            Sweep(now);

            // Ist trotz Aufraeumen kein Platz, faellt der aelteste heraus. Ein neuer Vorgang, der
            // an einem alten scheitert, waere die schlechtere Absage: Der alte gehoert vielleicht
            // niemandem mehr, der neue steht vor einem Menschen.
            if (_sessions.Count >= MaxSessions)
            {
                var oldest = _sessions.Values.OrderBy(item => item.LastSeenAt).First();
                _sessions.Remove(oldest.Handle);
            }

            _sessions[session.Handle] = session;
        }

        return session;
    }

    public SetupResume Reopen(string? handle, string? owner)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return new SetupResume(null, null);
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);

            if (!_sessions.TryGetValue(handle, out var session))
            {
                return new SetupResume(
                    null,
                    "Dieser Einrichtungsvorgang ist nicht mehr bekannt — die Frist ist abgelaufen "
                    + "oder das Gateway wurde neu gestartet. Was bereits angelegt wurde, ist "
                    + "unveraendert da; der Wizard liest den Stand gleich neu aus der Instanz.");
            }

            // Ab Schritt 3 hat der Vorgang einen Eigentuemer. Ein anderer Angemeldeter — und erst
            // recht ein Nichtangemeldeter — bekommt ihn nicht, auch nicht mit der Kennung.
            if (session.Owner is { Length: > 0 } existing
                && !string.Equals(existing, owner, StringComparison.Ordinal))
            {
                return new SetupResume(
                    null,
                    "Dieser Einrichtungsvorgang gehoert einem anderen Zugang. Er wird nicht "
                    + "uebernommen; ein neuer beginnt hier.");
            }

            session.LastSeenAt = now;
            return new SetupResume(session, null);
        }
    }

    public void Touch(SetupSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            session.LastSeenAt = _time.GetUtcNow();
            _sessions[session.Handle] = session;
        }
    }

    public void Discard(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return;
        }

        lock (_gate)
        {
            _sessions.Remove(handle);
        }
    }

    /// <summary>Wie viele Vorgaenge gerade gefuehrt werden — fuer Tests und Diagnose.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        var expired = _sessions.Values
            .Where(item => now - item.LastSeenAt > Lifetime)
            .Select(item => item.Handle)
            .ToList();

        foreach (var handle in expired)
        {
            _sessions.Remove(handle);
        }
    }

    /// <summary>
    /// 128 Bit aus dem Zufallsgenerator, base64url. Nicht zaehlend: Eine fortlaufende Nummer waere
    /// eine Einladung, den Vorgang des Nachbarn zu erraten — und vor Schritt 2 gibt es noch keinen
    /// Eigentuemer, der das auffangen wuerde.
    /// </summary>
    private static string NewHandle()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
