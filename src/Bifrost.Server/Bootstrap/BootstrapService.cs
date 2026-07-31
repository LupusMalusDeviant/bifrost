using Bifrost.Abstractions;
using Bifrost.Server.KeyRing;

namespace Bifrost.Server.Bootstrap;

/// <summary>Wer ein Token anfordert — und damit, unter welcher Bedingung es überhaupt eines gibt.</summary>
public enum BootstrapOrigin
{
    /// <summary>Der Startpfad einer frischen Installation. Nur dort, nie sonst.</summary>
    FirstStart = 0,

    /// <summary>
    /// Ein Kommando auf dem Rechner, auf dem der Gateway läuft. Es muss den lokalen
    /// Recovery-Nachweis erbringen (<see cref="IBootstrapRecoveryProof"/>).
    /// </summary>
    LocalRecovery = 1,
}

/// <summary>Warum ein Erstzugangs-Vorgang so ausgegangen ist, wie er ausgegangen ist.</summary>
public enum BootstrapOutcome
{
    /// <summary>Token ausgestellt.</summary>
    Issued = 0,

    /// <summary>Token eingelöst, Zugang eingerichtet.</summary>
    Redeemed = 1,

    /// <summary>Die Installation hat bereits Zugänge. Ohne Recovery-Nachweis gibt es kein Token.</summary>
    AlreadyEstablished = 2,

    /// <summary>Der lokale Recovery-Nachweis ist nicht erbracht.</summary>
    RecoveryProofMissing = 3,

    /// <summary>Es steht nichts zum Einlösen aus.</summary>
    NotPending = 4,

    /// <summary>Die Frist ist verstrichen. Das Token ist entwertet.</summary>
    Expired = 5,

    /// <summary>Das vorgelegte Token passt nicht.</summary>
    InvalidToken = 6,

    /// <summary>Der gewünschte Benutzername ist vergeben.</summary>
    UsernameTaken = 7,

    /// <summary>Das gewählte Passwort ist zu kurz.</summary>
    PasswordTooShort = 8,

    /// <summary>Benutzername oder Passwort fehlen.</summary>
    CredentialsMissing = 9,
}

/// <summary>Was der Erstzugang gerade zulässt — für Startmeldungen, Diagnose und die Oberfläche.</summary>
/// <param name="Phase">Der Zustand der Ablage.</param>
/// <param name="IsPending">Kann jetzt jemand ein Token einlösen?</param>
/// <param name="ExpiresAt">Bis wann.</param>
/// <param name="HandoverPath">Wo der Zettel mit dem Token liegt, falls er noch da ist.</param>
public sealed record BootstrapStatus(
    BootstrapPhase Phase,
    bool IsPending,
    DateTimeOffset? ExpiresAt,
    string? HandoverPath);

/// <summary>Das Ergebnis einer Ausstellung. <see cref="Token"/> ist nur im Erfolgsfall gesetzt.</summary>
public sealed record BootstrapIssueResult(
    BootstrapOutcome Outcome,
    string? Token,
    DateTimeOffset? ExpiresAt,
    string? HandoverPath,
    SecretFilePermissionState? HandoverPermissions,
    string Description);

/// <summary>Das Ergebnis eines Einlösens.</summary>
/// <param name="Outcome">Wie es ausgegangen ist.</param>
/// <param name="Username">Der angelegte Administrator, im Erfolgsfall.</param>
/// <param name="UserId">Seine Id — der Einlösepfad meldet ihn direkt an.</param>
/// <param name="Description">Ein Satz für Menschen.</param>
public sealed record BootstrapRedeemResult(
    BootstrapOutcome Outcome,
    string? Username,
    Guid? UserId,
    string Description);

/// <summary>
/// Der Erstzugang einer Installation (WP3.4) — <b>ohne</b> dauerhaftes Klartextsecret im
/// Anwendungslog.
/// </summary>
public interface IBootstrapService
{
    /// <summary>Was gerade gilt.</summary>
    Task<BootstrapStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Stellt ein Token aus, wenn die Herkunft es zulässt.</summary>
    Task<BootstrapIssueResult> IssueAsync(BootstrapOrigin origin, CancellationToken ct);

    /// <summary>Löst ein Token ein und richtet den ersten Administrator ein.</summary>
    Task<BootstrapRedeemResult> RedeemAsync(
        string? token, string? username, string? password, CancellationToken ct);

    /// <summary>
    /// Der Startpfad. Er stellt bei einer frischen Installation ein Token aus, vermerkt bei einer
    /// bestehenden, dass es nie eines geben wird — und tut sonst nichts.
    /// </summary>
    Task EnsureFirstAccessAsync(CancellationToken ct);
}

/// <summary>Die Stellschrauben des Erstzugangs.</summary>
/// <param name="TimeToLive">Wie lange ein ausgestelltes Token gilt.</param>
/// <param name="MinimumPasswordLength">Kürzere Passwörter nimmt der Einlösepfad nicht an.</param>
public sealed record BootstrapOptions(
    TimeSpan TimeToLive,
    int MinimumPasswordLength = 12)
{
    /// <summary>
    /// Eine Stunde. Lang genug, dass ein Mensch zwischen <c>docker compose up</c> und dem ersten
    /// Login noch etwas anderes tun kann; kurz genug, dass ein vergessenes Token nicht monatelang
    /// als Zweitschlüssel im Datenverzeichnis liegt.
    /// </summary>
    public static BootstrapOptions Default { get; } = new(TimeSpan.FromHours(1));
}

/// <summary>
/// Die Umsetzung des Erstzugangs.
///
/// <para>
/// <b>Wogegen das gebaut ist.</b> Bis hierher schrieb der erste Start ein Adminpasswort und einen
/// API-Key ins Log. Das ist ein Geheimnis an genau dem Ort, den man weitergibt, wenn etwas nicht
/// funktioniert: Supportanfragen, Ticketanhänge, Logaggregation, Sicherungen des Logverzeichnisses.
/// Und es ist der Ort, den niemand rotiert.
/// </para>
///
/// <para>
/// <b>Was stattdessen passiert.</b> Der erste Start legt <i>keinen</i> Zugang an. Er stellt ein
/// einmaliges, kurzlebiges Token aus, legt davon <b>nur den Hash</b> in die Zustandsdatei und
/// schreibt den Klartext in eine Übergabedatei mit den Rechten des privaten Schlüssels aus WP3.3.
/// Wer das Token einlöst, wählt Benutzername und Passwort selbst. Danach ist das Token tot und die
/// Übergabedatei gelöscht.
/// </para>
///
/// <para>
/// <b>Die Regel, die dahinter zählt.</b> Ein zweites Token gibt es nur mit lokalem
/// Recovery-Nachweis. Ein Angreifer am HTTP-Endpunkt kann den Erstzugang also nicht zurückdrehen —
/// und eine bestehende Installation verliert durch dieses Paket nichts: Sie bekommt den Vermerk
/// <see cref="BootstrapPhase.Established"/> und sonst gar nichts. Ihre Administratoren melden sich
/// unverändert an.
/// </para>
/// </summary>
public sealed partial class BootstrapService : IBootstrapService
{
    /// <summary>Name der Agenten-Identität, die beim Einlösen entsteht.</summary>
    public const string AgentIdentityName = "bootstrap-admin";

    private readonly IBootstrapStateStore _state;
    private readonly IBootstrapHandover _handover;
    private readonly IBootstrapRecoveryProof _proof;
    private readonly IUiUserService _uiUsers;
    private readonly IRbacManagement _rbac;
    private readonly IAuditSink _audit;
    private readonly TimeProvider _time;
    private readonly BootstrapOptions _options;
    private readonly ILogger<BootstrapService> _logger;

    public BootstrapService(
        IBootstrapStateStore state,
        IBootstrapHandover handover,
        IBootstrapRecoveryProof proof,
        IUiUserService uiUsers,
        IRbacManagement rbac,
        IAuditSink audit,
        TimeProvider time,
        BootstrapOptions options,
        ILogger<BootstrapService> logger)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(handover);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(uiUsers);
        ArgumentNullException.ThrowIfNull(rbac);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _state = state;
        _handover = handover;
        _proof = proof;
        _uiUsers = uiUsers;
        _rbac = rbac;
        _audit = audit;
        _time = time;
        _options = options;
        _logger = logger;
    }

    public async Task<BootstrapStatus> GetStatusAsync(CancellationToken ct)
    {
        var record = _state.Read();
        var now = _time.GetUtcNow();

        if (record is null)
        {
            // Noch kein Eintrag. Ob das eine frische Installation ist, entscheidet die Datenbank —
            // nicht die fehlende Datei.
            var established = await _uiUsers.AnyExistAsync(ct).ConfigureAwait(false);
            return new BootstrapStatus(
                established ? BootstrapPhase.Established : BootstrapPhase.Fresh,
                IsPending: false,
                ExpiresAt: null,
                HandoverPath: null);
        }

        var pending = record.Phase is BootstrapPhase.Pending
            && record.TokenHash is not null
            && record.ExpiresAt > now;

        return new BootstrapStatus(
            record.Phase,
            pending,
            record.ExpiresAt,
            pending ? _handover.Location : null);
    }

    public async Task<BootstrapIssueResult> IssueAsync(BootstrapOrigin origin, CancellationToken ct)
    {
        if (origin is BootstrapOrigin.LocalRecovery)
        {
            var proof = _proof.Verify();
            if (!proof.Proven)
            {
                return new BootstrapIssueResult(
                    BootstrapOutcome.RecoveryProofMissing, null, null, null, null,
                    "Der lokale Recovery-Nachweis ist nicht erbracht: " + proof.Description);
            }
        }
        else if (!await IsFreshAsync(ct).ConfigureAwait(false))
        {
            return new BootstrapIssueResult(
                BootstrapOutcome.AlreadyEstablished, null, null, null, null,
                "Diese Installation hat den Erstzugang hinter sich. Ein zweites Setup-Token gibt es "
                + "nur mit lokalem Recovery-Nachweis, also durch ein Kommando auf dem Rechner, auf "
                + "dem der Gateway laeuft.");
        }

        var now = _time.GetUtcNow();
        var expiresAt = now + _options.TimeToLive;
        var token = BootstrapToken.Create();

        _state.Write(new BootstrapRecord(
            BootstrapPhase.Pending,
            BootstrapToken.Hash(token),
            now,
            expiresAt,
            SettledAt: null,
            Note: "Es steht ein Setup-Token aus. Hier liegt nur sein Hash; der Klartext stand "
                + "einmalig in der Uebergabedatei und in keinem Log."));

        var permissions = _handover.Write(token, expiresAt);

        Record(
            origin is BootstrapOrigin.FirstStart
                ? $"Erstzugang: Setup-Token ausgestellt (erster Start), gueltig bis {expiresAt:u}."
                : $"Erstzugang: Setup-Token nach lokalem Recovery-Nachweis ausgestellt, gueltig bis {expiresAt:u}.");

        Log.TokenIssued(_logger, origin.ToString(), _handover.Location, expiresAt, permissions.Description);
        if (!permissions.Restricted)
        {
            Log.HandoverNotRestricted(_logger, _handover.Location, permissions.Description);
        }

        return new BootstrapIssueResult(
            BootstrapOutcome.Issued, token, expiresAt, _handover.Location, permissions,
            $"Setup-Token ausgestellt, gueltig bis {expiresAt:u}.");
    }

    public async Task<BootstrapRedeemResult> RedeemAsync(
        string? token, string? username, string? password, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var record = _state.Read();

        if (record is not { Phase: BootstrapPhase.Pending, TokenHash: not null })
        {
            return Denied(BootstrapOutcome.NotPending, "Es steht kein Erstzugang aus.");
        }

        if (record.ExpiresAt is null || record.ExpiresAt <= now)
        {
            Invalidate("abgelaufen");
            return Denied(
                BootstrapOutcome.Expired,
                "Die Frist des Setup-Tokens ist verstrichen; es ist entwertet.");
        }

        if (!BootstrapToken.Matches(token, record.TokenHash))
        {
            return Denied(BootstrapOutcome.InvalidToken, "Das vorgelegte Setup-Token passt nicht.");
        }

        // Erst prüfen, dann verbrauchen. Andersherum verbrennte ein Tippfehler im Benutzernamen das
        // Token — und der Betreiber stünde vor einer Installation, in die niemand mehr hineinkommt.
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return Denied(
                BootstrapOutcome.CredentialsMissing, "Benutzername und Passwort sind Pflicht.");
        }

        if (password.Length < _options.MinimumPasswordLength)
        {
            return Denied(
                BootstrapOutcome.PasswordTooShort,
                $"Das Passwort muss mindestens {_options.MinimumPasswordLength} Zeichen haben.");
        }

        var existing = await _uiUsers.ListAsync(ct).ConfigureAwait(false);
        if (existing.Any(user => string.Equals(user.Username, username, StringComparison.Ordinal)))
        {
            return Denied(
                BootstrapOutcome.UsernameTaken, $"Der Benutzername '{username}' ist vergeben.");
        }

        // ── Der Anspruch. Ab hier ist das Token weg, egal was danach passiert. ──────────────────
        var claimed = _state.Exchange(current =>
            current is { Phase: BootstrapPhase.Pending, TokenHash: not null }
            && current.ExpiresAt > now
            && BootstrapToken.Matches(token, current.TokenHash)
                ? current with
                {
                    Phase = BootstrapPhase.Redeemed,
                    TokenHash = null,
                    SettledAt = now,
                    Note = "Der Erstzugang ist eingeloest. Ein zweites Token gibt es nur mit "
                        + "lokalem Recovery-Nachweis.",
                }
                : null);

        if (!claimed)
        {
            // Genau hier endet der zweite von zwei gleichzeitigen Versuchen.
            return Denied(
                BootstrapOutcome.NotPending,
                "Das Setup-Token wurde bereits eingeloest. Es gilt genau einmal.");
        }

        _handover.Remove();

        var created = await _uiUsers
            .CreateAsync(username, password, UiRole.Admin, ct).ConfigureAwait(false);
        await CreateAgentIdentityAsync(ct).ConfigureAwait(false);

        Record($"Erstzugang eingeloest: UI-Administrator '{username}' und Agenten-Identitaet "
            + $"'{AgentIdentityName}' angelegt.");
        Log.Redeemed(_logger, username);

        return new BootstrapRedeemResult(
            BootstrapOutcome.Redeemed, created.Username, created.Id, "Erstzugang eingerichtet.");
    }

    public async Task EnsureFirstAccessAsync(CancellationToken ct)
    {
        var record = _state.Read();
        var hasUsers = await _uiUsers.AnyExistAsync(ct).ConfigureAwait(false);

        // ── Bestehende Installation ─────────────────────────────────────────────────────────────
        // Der Fall, der über Erfolg und Misserfolg dieses Pakets entscheidet: Eine Instanz, die
        // nach dem Upgrade niemanden mehr hereinlässt, ist schlimmer als eine mit einem alten
        // Logeintrag. Deshalb passiert hier genau eines — ein Vermerk. Kein Anlegen, kein Löschen,
        // kein Zurücksetzen von Passwörtern.
        if (hasUsers)
        {
            if (record?.Phase is not BootstrapPhase.Established)
            {
                _state.Write(new BootstrapRecord(
                    BootstrapPhase.Established,
                    TokenHash: null,
                    IssuedAt: record?.IssuedAt,
                    ExpiresAt: null,
                    SettledAt: _time.GetUtcNow(),
                    Note: "Diese Installation hatte bereits Zugaenge, als der Erstzugang auf "
                        + "Setup-Token umgestellt wurde. Es gab nie ein Token."));
                _handover.Remove();
                Log.AlreadyEstablished(_logger, _state.Location);
            }

            return;
        }

        // ── Kein UI-Zugang vorhanden ────────────────────────────────────────────────────────────
        if (record?.Phase is BootstrapPhase.Redeemed or BootstrapPhase.Established)
        {
            // Der Erstzugang ist verbraucht, aber es gibt keinen Administrator mehr. Von selbst
            // wird hier NICHTS neu ausgestellt — sonst hinge die Wiedereröffnung dieser
            // Installation daran, dass jemand die richtige Tabelle leert. Beide Auswege sind
            // lokale Kommandos, und genau das ist der Nachweis.
            Log.LockedOut(_logger, _state.Location);
            return;
        }

        if (record is { Phase: BootstrapPhase.Pending, TokenHash: not null }
            && record.ExpiresAt > _time.GetUtcNow())
        {
            Log.StillPending(_logger, _handover.Location, record.ExpiresAt!.Value);
            return;
        }

        await IssueAsync(BootstrapOrigin.FirstStart, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Frisch heißt: kein Administrator in der Datenbank <b>und</b> kein verbrauchter Erstzugang in
    /// der Ablage. Beide Bedingungen sind nötig — die Datenbank allein ließe sich durch Löschen des
    /// letzten Nutzers umgehen, die Datei allein durch Löschen der Datei.
    /// </summary>
    private async Task<bool> IsFreshAsync(CancellationToken ct)
    {
        var record = _state.Read();
        if (record?.Phase is BootstrapPhase.Redeemed or BootstrapPhase.Established)
        {
            return false;
        }

        return !await _uiUsers.AnyExistAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Die Agenten-Identität mit Global-Grant — <b>ohne</b> API-Key.
    /// <para>
    /// Genau das ist der Unterschied zu vorher. Bis hierher entstand beim ersten Start ein
    /// Klartext-Key, der ins Log ging und dort blieb, ob ihn jemand brauchte oder nicht. Jetzt
    /// entsteht nur die Identität; den Schlüssel dazu stellt der angemeldete Administrator in der
    /// Oberfläche aus (RBAC → Keys). Dort wird er einmal angezeigt, mitsamt fertiger
    /// Client-Konfiguration — und er landet in keinem Logarchiv.
    /// </para>
    /// </summary>
    private async Task CreateAgentIdentityAsync(CancellationToken ct)
    {
        var role = new Role(
            RoleId.New(),
            AgentIdentityName,
            [new Grant(
                new PermissionScope(null, null),
                [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])]);
        var identity = new Identity(
            IdentityId.New(), AgentIdentityName, IdentityKind.Agent, [role.Id]);

        await _rbac.UpsertRoleAsync(role, ct).ConfigureAwait(false);
        await _rbac.UpsertIdentityAsync(identity, ct).ConfigureAwait(false);
    }

    private void Invalidate(string reason)
    {
        _state.Exchange(current => current is { Phase: BootstrapPhase.Pending, TokenHash: not null }
            ? current with
            {
                TokenHash = null,
                Note = $"Das Setup-Token ist {reason}. Ein neues stellt der naechste Start aus, "
                    + "solange diese Installation noch keinen Zugang hat.",
            }
            : null);
        _handover.Remove();
    }

    private BootstrapRedeemResult Denied(BootstrapOutcome outcome, string description)
    {
        // Fehlversuche gehören ins Audit: Der Einlösepfad ist der einzige unauthentifizierte
        // Schreibweg dieser Anwendung, und eine Reihe von 'InvalidToken' ist genau das Muster, das
        // ein Betreiber sehen will.
        Record($"Erstzugang abgelehnt ({outcome}): {description}", InvocationStatus.Denied);
        Log.Denied(_logger, outcome.ToString());
        return new BootstrapRedeemResult(outcome, null, null, description);
    }

    private void Record(string detail, InvocationStatus status = InvocationStatus.Success)
        => _audit.Record(new AuditEvent(
            _time.GetUtcNow(),
            Caller: null,
            CallOrigin.System,
            AuditEventKind.Authentication,
            Server: null,
            Tool: "bootstrap",
            status,
            RedactedArguments: null,
            RequestBytes: null,
            ResponseBytes: null,
            Duration: null,
            CallerRoles: null,
            Detail: detail));

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning,
            Message = "ERSTZUGANG: Setup-Token ausgestellt ({Origin}). Es steht in {HandoverPath} "
                + "und gilt bis {ExpiresAt:u}. Rechte der Datei: {Permissions}. Im Log steht es "
                + "bewusst nicht.")]
        public static partial void TokenIssued(
            ILogger logger, string origin, string handoverPath, DateTimeOffset expiresAt, string permissions);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Die Uebergabedatei {HandoverPath} ist NICHT nachweislich abgeschirmt: "
                + "{Permissions}. Bis das behoben ist, kann jeder mit Lesezugriff auf das "
                + "Datenverzeichnis den Erstzugang uebernehmen.")]
        public static partial void HandoverNotRestricted(
            ILogger logger, string handoverPath, string permissions);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "ERSTZUGANG: Es steht noch ein Setup-Token aus ({HandoverPath}), gueltig bis {ExpiresAt:u}.")]
        public static partial void StillPending(
            ILogger logger, string handoverPath, DateTimeOffset expiresAt);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Diese Installation hatte bereits Zugaenge — kein Erstzugang noetig, kein "
                + "Setup-Token ausgestellt. Vermerkt in {StatePath}.")]
        public static partial void AlreadyEstablished(ILogger logger, string statePath);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Der Erstzugang gilt laut {StatePath} als erledigt, aber es gibt keinen "
                + "UI-Nutzer mehr. Es wird von selbst KEIN neues Setup-Token ausgestellt. Auswege, "
                + "beide auf dem Rechner des Gateways: '--reset-ui-admin' setzt einen Zugang "
                + "zurueck, '--bootstrap-init' stellt nach lokalem Nachweis ein neues Token aus.")]
        public static partial void LockedOut(ILogger logger, string statePath);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Erstzugang eingeloest; UI-Administrator '{Username}' angelegt.")]
        public static partial void Redeemed(ILogger logger, string username);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Einloesen des Erstzugangs abgelehnt: {Outcome}.")]
        public static partial void Denied(ILogger logger, string outcome);
    }
}
