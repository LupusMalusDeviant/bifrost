namespace Bifrost.Abstractions;

/// <summary>
/// OAuth-Anbindung an einen HTTP-Upstream (MCP-Autorisierung, RFC 9728 / RFC 8707 / OAuth 2.1).
/// <para>
/// <b>Warum vorregistriert und nicht dynamisch:</b> Dynamic Client Registration ist im Standard
/// inzwischen abgelöst, und Client-ID-Metadata-Documents verlangen ein öffentlich abrufbares
/// Dokument — ein selbst gehosteter Gateway steht aber oft nicht im Netz. Ein am Authorization
/// Server registrierter Client ist der Weg, der ohne öffentliche Erreichbarkeit funktioniert.
/// </para>
/// <para>
/// <see cref="ClientSecret"/> liegt im DataProtection-verschlüsselten Config-Blob wie jedes andere
/// Credential und wird in Ausgaben maskiert.
/// </para>
/// </summary>
/// <param name="Scopes">
/// Gewünschte Berechtigungen. Leer heißt: die Auswahl kommt aus der Protected Resource Metadata des
/// Upstreams (<c>scopes_supported</c>) bzw. aus der <c>WWW-Authenticate</c>-Aufforderung — der
/// Standard nennt genau diese Reihenfolge.
/// </param>
/// <param name="AllowPrivateTargets">
/// Erlaubt Discovery- und Token-Endpunkte im internen Netz. Vorgabe <c>false</c>: Der Upstream sagt
/// uns über seine Metadaten, <em>wohin</em> wir Anfragen schicken — das ist eine vom Gegenüber
/// gesteuerte Adresse und damit derselbe SSRF-Weg wie bei importierten Schemabeschreibungen.
/// </param>
public sealed record UpstreamOAuthOptions(
    string ClientId,
    string? ClientSecret = null,
    IReadOnlyList<string>? Scopes = null,
    bool AllowPrivateTargets = false);

/// <summary>
/// Ein gespeichertes Zugriffstoken für einen Upstream. Getrennt von der Upstream-Konfiguration
/// abgelegt: Die Konfiguration ist append-only versioniert, ein Token erneuert sich dagegen
/// laufend — beides in derselben Historie hieße, jede Erneuerung als Konfigurationsänderung zu
/// führen.
/// </summary>
public sealed record UpstreamOAuthToken(
    ServerId Server,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes,
    /// <summary>Der Issuer, von dem dieses Token stammt. Ein Wechsel entwertet es (SEP-2352).</summary>
    string Issuer,
    DateTimeOffset ObtainedAt)
{
    /// <summary>
    /// Der Resource Indicator (<see cref="OAuthAuthorizationAttempt.ResourceFor"/>), für den das
    /// Token ausgestellt wurde (Plan 0005 F5). <c>null</c> nur bei Token, die vor Einführung der
    /// Bindung gespeichert wurden — siehe <see cref="IsBoundTo"/>.
    /// </summary>
    public string? Resource { get; init; }

    /// <summary>Die Client-ID, mit der das Token eingelöst wurde (Plan 0005 F5).</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Die Kennung des gespeicherten Standes (Plan 0005 F14). Daran — und nicht am Zeitpunkt des
    /// Erhalts, der nach Trennen und neuem Autorisieren wiederkehren kann — erkennen
    /// <see cref="IUpstreamOAuthTokenStore.ReplaceAsync"/> und
    /// <see cref="IUpstreamOAuthTokenStore.RemoveIfUnchangedAsync"/> „noch dasselbe Token".
    /// <para>
    /// Die Datenbank-Ablage vergibt sie bei <b>jedem</b> Schreiben neu; was ein noch nicht
    /// gespeichertes Token hier trägt, übernimmt sie nicht. Der zufällige Startwert ist für Ablagen,
    /// die das Objekt unverändert ablegen — sie erfüllen damit dieselbe Regel.
    /// <see cref="Guid.Empty"/> heißt: gespeichert, bevor es Kennungen gab.
    /// </para>
    /// </summary>
    public Guid Revision { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Erneuerungsbedarf mit Sicherheitsabstand: Ein Token, das während des Aufrufs abläuft, ist so
    /// gut wie abgelaufen.
    /// </summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan skew)
        => ExpiresAt is { } expires && expires - skew <= now;

    /// <summary>
    /// Gehört das Token zu dieser Anbindung — demselben Resource Indicator und derselben Client-ID
    /// (Plan 0005 F5)?
    /// <para>
    /// <b>Ein Token ohne jede Bindung gilt als passend.</b> Das sind nur Token, die vor Einführung der
    /// Bindung gespeichert wurden: Sie wurden für die damalige Anbindung ausgestellt, und eine
    /// bestehende Instanz soll nach dem Update ohne neues Autorisieren weiterlaufen. Ihre erste
    /// Erneuerung schreibt die Bindung nach; ab dann gilt die strenge Prüfung.
    /// </para>
    /// </summary>
    public bool IsBoundTo(string resource, string clientId)
        => (Resource is null && ClientId is null)
            || (string.Equals(Resource, resource, StringComparison.Ordinal)
                && string.Equals(ClientId, clientId, StringComparison.Ordinal));
}

/// <summary>Ablage der Upstream-Token. Werte liegen verschlüsselt (NFR-04).</summary>
public interface IUpstreamOAuthTokenStore
{
    Task<UpstreamOAuthToken?> GetAsync(ServerId server, CancellationToken ct);

    Task SaveAsync(UpstreamOAuthToken token, CancellationToken ct);

    /// <summary>
    /// Speichert wie <see cref="SaveAsync"/> und liefert die Kennung, unter der das Token jetzt liegt
    /// (Plan 0005 F5, F14) — für den Rückweg der Autorisierung, der später nur sein eigenes Token
    /// entfernen darf (<see cref="RemoveIfUnchangedAsync"/>). Die Standardumsetzung passt für
    /// Ablagen, die das Token-Objekt unverändert ablegen; eine Ablage, die Kennungen selbst vergibt,
    /// setzt sie selbst um.
    /// </summary>
    async Task<Guid> SaveWithRevisionAsync(UpstreamOAuthToken token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        await SaveAsync(token, ct).ConfigureAwait(false);
        return token.Revision;
    }

    Task RemoveAsync(ServerId server, CancellationToken ct);

    /// <summary>
    /// Ersetzt <paramref name="expected"/> durch <paramref name="replacement"/> — aber nur, wenn noch
    /// <paramref name="expected"/> gespeichert ist. Liefert <c>false</c>, wenn inzwischen ein anderes
    /// Token gespeichert oder das Token entfernt wurde; dann bleibt die Ablage, wie sie ist.
    /// <para>
    /// Für die Erneuerung im Hintergrund: Sie liest ein Token, wartet auf den Token-Endpunkt und
    /// speichert danach. Ein neues Autorisieren oder ein „Trennen" dazwischen überschriebe sie sonst
    /// — und nach einem Trennen stünde der Upstream wieder autorisiert da (Codex M3).
    /// </para>
    /// <para>
    /// Muss atomar sein — es gibt bewusst keine Standardumsetzung aus Lesen und Schreiben, denn
    /// zwischen beiden läge genau die Lücke, die diese Methode schließen soll (Plan 0005 D7). Erkannt
    /// wird „noch dasselbe Token" an <see cref="UpstreamOAuthToken.Revision"/> (Plan 0005 F14), nicht
    /// am Zeitpunkt des Erhalts: Der kann nach einem Trennen und neuen Autorisieren wiederkehren.
    /// </para>
    /// </summary>
    Task<bool> ReplaceAsync(UpstreamOAuthToken expected, UpstreamOAuthToken replacement, CancellationToken ct);

    /// <summary>
    /// Entfernt <paramref name="expected"/> — aber nur, wenn noch genau dieser Stand gespeichert ist
    /// (erkannt an <see cref="UpstreamOAuthToken.Revision"/>). Liefert <c>false</c>, wenn inzwischen
    /// ein anderes Token gespeichert oder keins mehr da ist; dann bleibt die Ablage, wie sie ist.
    /// <para>
    /// Für den Rückweg der Autorisierung (Plan 0005 F5): Er räumt nur sein eigenes Token weg, nicht
    /// eine neuere Autorisierung, die ein zweiter Vorgang inzwischen gespeichert hat. Wie
    /// <see cref="ReplaceAsync"/> muss es atomar sein; die Standardumsetzung gibt es nur, damit
    /// Testablagen, die es nicht brauchen, es nicht nachbauen müssen.
    /// </para>
    /// </summary>
    Task<bool> RemoveIfUnchangedAsync(UpstreamOAuthToken expected, CancellationToken ct)
        => throw new NotSupportedException(
            "Diese Token-Ablage kann nicht bedingt entfernen (Plan 0005 F5).");
}

/// <summary>
/// Was der Gateway über einen laufenden Autorisierungsvorgang festhält, bis der Nutzer aus dem
/// Browser zurückkommt.
/// <para>
/// <see cref="ExpectedIssuer"/> ist nicht optional: RFC 9207 verlangt, den <c>iss</c>-Parameter der
/// Antwort gegen den <b>vorher notierten</b> Issuer zu prüfen. Ohne diese Notiz ist die Prüfung
/// wertlos — sie schützt genau gegen den Fall, dass jemand die Antwort eines anderen
/// Authorization Servers unterschiebt.
/// </para>
/// </summary>
public sealed record OAuthAuthorizationAttempt(
    string State,
    ServerId Server,
    string CodeVerifier,
    string ExpectedIssuer,
    Uri TokenEndpoint,
    Uri RedirectUri,
    string Resource,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Hat der Authorization Server beim Start ausgewiesen, dass er den <c>iss</c>-Parameter
    /// mitschickt (<c>authorization_response_iss_parameter_supported</c>)?
    /// <para>
    /// <b>Dann ist ein fehlender <c>iss</c> ein Abbruchgrund</b>, kein Grund zur Nachsicht: Genau so
    /// sähe der Angriff aus, gegen den RFC 9207 schützt — die Antwort eines anderen Authorization
    /// Servers, untergeschoben ohne den Parameter, der sie verraten würde. Die MCP-Autorisierung
    /// verlangt die Prüfung seit der Spec-Revision 2026-07-28 ausdrücklich.
    /// </para>
    /// <para>
    /// Weist der Server den Parameter nicht aus, bleibt es bei der Nachsicht — dort trägt die
    /// Bindung an den Token-Endpunkt aus demselben Metadaten-Dokument.
    /// </para>
    /// </summary>
    public bool IssuerParameterRequired { get; init; }

    /// <summary>
    /// Die Client-ID, mit der der Vorgang begonnen wurde (Plan 0005 E2). Hat sie sich bis zum
    /// Zurückkommen geändert, gehört der Code zu einem anderen Client und wird nicht eingelöst.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Der Resource Indicator (RFC 8707) eines Upstream-Endpunkts: seine kanonische URI ohne Query
    /// und Fragment, mit Pfad, ohne abschließenden Schrägstrich. An ihn ist jedes Token gebunden.
    /// </summary>
    public static string ResourceFor(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>
    /// Hat sich die Bindung eines OAuth-Tokens zwischen zwei Konfigurationen geändert — anderer
    /// Endpunkt (also andere Ressource), andere Client-ID oder keine OAuth-Anbindung mehr? Dann gilt
    /// ein gespeichertes Token nicht mehr (Plan 0005 E2).
    /// </summary>
    public static bool BindingChanged(UpstreamServerConfig before, UpstreamServerConfig after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Http?.OAuth is not { } old)
        {
            return false;
        }

        return after.Http?.OAuth is not { } now
            || !string.Equals(old.ClientId, now.ClientId, StringComparison.Ordinal)
            || !string.Equals(ResourceFor(before.Http.Endpoint), ResourceFor(after.Http.Endpoint), StringComparison.Ordinal);
    }
}
