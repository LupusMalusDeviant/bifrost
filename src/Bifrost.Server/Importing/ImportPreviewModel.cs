using Bifrost.Abstractions;
using Bifrost.Abstractions.Importing;

namespace Bifrost.Server.Importing;

/// <summary>
/// Das <b>normalisierte Vorschaumodell</b> — die Sicht, die über die Schnittstelle geht.
///
/// <para>
/// <b>Der Kern dieses Pakets.</b> WP4.1 hat es ausdrücklich offengelassen:
/// <see cref="ImportCandidate.Config"/> trägt die <em>Klartextwerte</em>, weil ein Plan sonst beim
/// Anwenden nutzlos wäre — man kann keinen Upstream anlegen, dessen Token man nicht mehr hat. Was
/// über die API geht, darf sie nicht tragen. Diese Datei ist die Trennlinie: Der Plan mit den Werten
/// bleibt im <see cref="ImportPlanStore"/>, hinaus geht ausschließlich dieses Modell.
/// </para>
///
/// <para>
/// <b>Positivliste, nicht Maskierung.</b> Es gibt bereits eine Maskierung für gespeicherte
/// Konfigurationen (<c>UpstreamConfigRedactor</c>). Sie ist eine <em>Negativliste</em>: Sie kennt
/// die Felder, die erfahrungsgemäß Geheimnisse tragen, und leert genau die. Für eine <b>fremde</b>
/// Konfiguration reicht das nicht, und zwar nachweisbar an zwei Stellen, die der Redactor nicht
/// anfasst: <c>Stdio.Arguments</c> (<c>["--api-key", "ghp_…"]</c> ist die verbreitetste Form
/// überhaupt) und der Query-Teil von <c>Http.Endpoint</c> (<c>?token=…</c>). Eine Negativliste ist
/// eine Liste der Fehler, die schon jemand gemacht hat.
/// </para>
///
/// <para>
/// Deshalb wird hier <b>nichts entfernt, sondern etwas aufgebaut</b>: Jedes Feld dieses Modells ist
/// eine ausdrückliche Entscheidung. Ein neues Feld in <see cref="UpstreamServerConfig"/> erscheint
/// hier von selbst <em>nicht</em> — das ist der Unterschied, auf den es ankommt.
/// </para>
///
/// <para>
/// <b>Die Grenze, klar benannt.</b> Sichtbar sind Strukturangaben: Slug, Anzeigename, Transportart,
/// das Programm, die Zieladresse ohne Query und Benutzerteil, der Container. Nicht sichtbar ist
/// alles, was ein <em>Wert</em> ist: Argumente, Umgebungswerte, Headerwerte, Credentials,
/// WASI-Secrets. Von wertetragenden Feldern reisen <b>Namen und Anzahlen</b>, nie Inhalte. Die
/// Regel ist absichtlich gröber als jede Heuristik: Ein Wert, den die Erkennung nicht für ein
/// Geheimnis hielt, ist immer noch ein Wert.
/// </para>
/// </summary>
/// <param name="Token">
/// Das Handle auf den vorgemerkten Plan — dieselbe Bauart wie
/// <see cref="Bifrost.Abstractions.Operations.RestorePlan.Token"/>. <c>null</c> auf dem lokalen
/// Setup-Weg: Der legt nichts an und merkt deshalb auch nichts vor.
/// </param>
/// <param name="ExpiresAt">Bis wann das Handle gilt. <c>null</c>, wenn es keines gibt.</param>
public sealed record ImportPreviewView(
    ImportSourceView Source,
    IReadOnlyList<ImportCandidateView> Candidates,
    IReadOnlyList<ImportFinding> Findings,
    bool CanApply,
    IReadOnlyList<ImportFinding> RequiresConfirmation,
    string? Token = null,
    DateTimeOffset? ExpiresAt = null);

/// <param name="OriginPath">
/// Die Herkunftsangabe, die der Aufrufer mitgeschickt hat — <b>eine Beschriftung, kein Leseauftrag</b>.
/// Der Dienst öffnet diesen Pfad nie; ein Endpunkt, der einen Pfad vom Client entgegennimmt und ihn
/// serverseitig liest, wäre ein Werkzeug zum Auslesen fremder Dateien.
/// </param>
public sealed record ImportSourceView(
    string Provider,
    string? SchemaVersion,
    double Confidence,
    string? OriginPath);

public sealed record ImportCandidateView(
    string SourceName,
    string Slug,
    string DisplayName,
    string Kind,
    bool Enabled,
    ImportTransportView Transport,
    IReadOnlyList<ImportFinding> Findings,
    IReadOnlyList<ImportSecret> Secrets);

/// <summary>
/// Die Transportangaben, die ein Mensch braucht, um <em>Ja</em> oder <em>Nein</em> zu sagen — und
/// keine mehr.
/// </summary>
/// <param name="Program">
/// Das Programm, das starten würde. Sichtbar, weil es die Angabe ist, wegen der die Vorschau
/// überhaupt gelesen wird: <c>npx</c> ist eine andere Entscheidung als <c>/usr/local/bin/foo</c>.
/// </param>
/// <param name="ArgumentCount">
/// Nur die Anzahl. Argumente tragen in fremden Konfigurationen regelmäßig Zugangsdaten
/// (<c>--token …</c>), und welches Argument eines ist, entscheidet keine Heuristik zuverlässig
/// genug, um daraus eine Ausgabe zu bauen. <em>Was</em> an den Argumenten auffällig ist, steht in
/// den Befunden — dort mit Ortsangabe und ohne Wert.
/// </param>
/// <param name="EnvironmentNames">Namen der Umgebungsvariablen. Nie ihre Werte.</param>
/// <param name="Endpoint">
/// Die Zieladresse ohne Query, Fragment und Benutzerteil. <c>https://api.example/mcp?token=…</c>
/// und <c>https://user:pass@api.example/mcp</c> sind die beiden Formen, in denen eine URL selbst ein
/// Zugangsdatum ist.
/// </param>
/// <param name="EndpointCarriedQuery">
/// Ob an der Adresse ein Query-Teil abgeschnitten wurde. Ein Betreiber soll wissen, dass da etwas
/// war — nur nicht was.
/// </param>
/// <param name="CredentialPresent">
/// Ob die Quelle ein Credential mitbringt. Das ist die Aussage, die zählt; der Wert ist es nicht.
/// </param>
public sealed record ImportTransportView(
    string Kind,
    string? Program = null,
    int ArgumentCount = 0,
    IReadOnlyList<string>? EnvironmentNames = null,
    string? WorkingDirectory = null,
    string? Endpoint = null,
    bool EndpointCarriedQuery = false,
    IReadOnlyList<string>? HeaderNames = null,
    string? SpecLocation = null,
    string? AuthKind = null,
    bool CredentialPresent = false,
    string? ApiKeyHeaderName = null,
    bool OAuthConfigured = false,
    string? OAuthClientId = null,
    bool OAuthClientSecretPresent = false,
    IReadOnlyList<string>? SecretNames = null,
    int ToolCount = 0,
    string? IsolationMode = null,
    string? ContainerImage = null,
    string? ComponentPath = null,
    int PinnedPublisherCount = 0);

/// <summary>
/// Baut das Vorschaumodell aus einem Plan. <b>Die einzige Stelle</b>, an der eine
/// <see cref="UpstreamServerConfig"/> aus dem Import in eine Ausgabe übersetzt wird.
/// </summary>
public static class ImportPreviewProjection
{
    /// <summary>Was an die Stelle einer abgeschnittenen Angabe tritt.</summary>
    public const string Unresolvable = "(nicht darstellbar)";

    public static ImportPreviewView From(
        ImportPlan plan, string? token = null, DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new ImportPreviewView(
            new ImportSourceView(
                plan.Source.Provider,
                plan.Source.SchemaVersion,
                plan.Source.Confidence,
                plan.Source.OriginPath),
            [.. plan.Candidates.Select(Candidate)],
            plan.Findings,
            plan.CanApply,
            plan.RequiresConfirmation,
            token,
            expiresAt);
    }

    private static ImportCandidateView Candidate(ImportCandidate candidate) => new(
        candidate.SourceName,
        candidate.Config.Slug,
        candidate.Config.DisplayName,
        candidate.Config.Kind.ToString(),
        candidate.Config.Enabled,
        Transport(candidate.Config),
        // Befunde sind Fliesstext aus dem Kern. Sie nennen heute nur Strukturangaben (Kommando,
        // Arbeitsverzeichnis) — aber sie sind der einzige Teil dieser Ausgabe, den die Positivliste
        // nicht abdeckt, weil sie fertig ankommen. Der Durchlauf durch den Wert-Entferner ist die
        // zweite Sicherung: Er kennt die Werte GENAU DIESER Konfiguration und braucht dafuer nichts
        // zu raten.
        [.. candidate.Findings.Select(finding => finding with
        {
            Summary = ImportValueScrubber.Scrub(finding.Summary, candidate.Config)!,
            Remediation = ImportValueScrubber.Scrub(finding.Remediation, candidate.Config),
        })],
        // Die Secretbefunde tragen laut Vertrag Ort und Begründung, nie den Wert
        // (ImportSecret.Location/Looked). Sie gehen deshalb unverändert hinaus — genau dafür gibt
        // es sie.
        candidate.Secrets);

    private static ImportTransportView Transport(UpstreamServerConfig config)
    {
        var kind = config.Kind.ToString();

        if (config.Stdio is { } stdio)
        {
            return new ImportTransportView(
                kind,
                Program: stdio.Command,
                ArgumentCount: stdio.Arguments?.Count ?? 0,
                EnvironmentNames: Names(stdio.EnvironmentVariables),
                WorkingDirectory: stdio.WorkingDirectory,
                IsolationMode: stdio.Isolation?.Mode.ToString(),
                ContainerImage: stdio.Isolation?.Image);
        }

        if (config.Http is { } http)
        {
            var (endpoint, hadQuery) = SafeUri(http.Endpoint);
            return new ImportTransportView(
                kind,
                Endpoint: endpoint,
                EndpointCarriedQuery: hadQuery,
                HeaderNames: Names(http.Headers),
                OAuthConfigured: http.OAuth is not null,
                OAuthClientId: http.OAuth?.ClientId,
                OAuthClientSecretPresent: !string.IsNullOrEmpty(http.OAuth?.ClientSecret));
        }

        if (config.OpenApi is { } openApi)
        {
            var (spec, specQuery) = SafeUri(openApi.SpecLocation);
            var (baseAddress, baseQuery) = SafeUri(openApi.BaseAddress);
            return new ImportTransportView(
                kind,
                Endpoint: baseAddress,
                EndpointCarriedQuery: specQuery || baseQuery,
                SpecLocation: spec,
                AuthKind: openApi.AuthKind.ToString(),
                CredentialPresent: !string.IsNullOrEmpty(openApi.Credential),
                ApiKeyHeaderName: openApi.ApiKeyHeaderName);
        }

        if (config.OpenRpc is { } openRpc)
        {
            var (endpoint, endpointQuery) = SafeUri(openRpc.Endpoint);
            var (spec, specQuery) = SafeUri(openRpc.SpecLocation);
            return new ImportTransportView(
                kind,
                Endpoint: endpoint,
                EndpointCarriedQuery: endpointQuery || specQuery,
                SpecLocation: spec,
                AuthKind: openRpc.AuthKind.ToString(),
                CredentialPresent: !string.IsNullOrEmpty(openRpc.Credential),
                ApiKeyHeaderName: openRpc.ApiKeyHeaderName);
        }

        if (config.Cli is { } cli)
        {
            return new ImportTransportView(
                kind,
                Program: cli.Executable,
                EnvironmentNames: Names(cli.EnvironmentVariables),
                WorkingDirectory: cli.WorkingDirectory,
                // Nur die Anzahl der Kommandos. Ein CliToolSpec traegt FixedArguments — also
                // Werte, und damit dieselbe Frage wie bei Stdio.Arguments.
                ToolCount: cli.Tools?.Count ?? 0,
                IsolationMode: cli.Isolation?.Mode.ToString(),
                ContainerImage: cli.Isolation?.Image);
        }

        if (config.Wasi is { } wasi)
        {
            return new ImportTransportView(
                kind,
                Program: wasi.HostExecutable,
                ArgumentCount: wasi.HostArguments?.Count ?? 0,
                ComponentPath: wasi.ComponentPath,
                PinnedPublisherCount: wasi.PinnedPublishers?.Count ?? 0,
                SecretNames: Names(wasi.Secrets));
        }

        return new ImportTransportView(kind);
    }

    /// <summary>Die Schlüssel einer Zuordnung — sortiert, damit die Ausgabe stabil ist.</summary>
    private static IReadOnlyList<string>? Names(IReadOnlyDictionary<string, string>? values)
        => values is { Count: > 0 }
            ? [.. values.Keys.OrderBy(name => name, StringComparer.Ordinal)]
            : null;

    /// <summary>
    /// Eine Adresse ohne Query, Fragment und Benutzerteil. Der zweite Rückgabewert sagt, ob dabei
    /// etwas weggefallen ist.
    /// </summary>
    private static (string? Value, bool HadQuery) SafeUri(Uri? uri)
    {
        if (uri is null)
        {
            return (null, false);
        }

        if (!uri.IsAbsoluteUri)
        {
            // Eine relative Angabe hat keine zerlegbaren Teile. Sie geht nicht durch: Was daran
            // Wert und was Struktur ist, laesst sich nicht entscheiden.
            return (Unresolvable, false);
        }

        var hadQuery = !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo);

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };

        return (builder.Uri.ToString(), hadQuery);
    }
}
