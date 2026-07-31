using Bifrost.Abstractions.Operations;

namespace Bifrost.Core.Diagnostics.Checks;

/// <summary>
/// BFR-CFG-0001 — Datenverzeichnis vorhanden und beschreibbar.
/// <para>
/// Dort liegen Datenbank <b>und</b> Key-Ring. Zeigt der Pfad ins Leere, richtet der Gateway sich
/// dort neu ein und meldet sich fehlerfrei als bereit — ohne Server, ohne Rollen, ohne Schlüssel.
/// Dieser Ausfall sieht aus wie ein gelungener Start und fällt erst auf, wenn jemand ein Tool
/// aufruft (docs/operations.md, „Der Volume-Name").
/// </para>
/// </summary>
public sealed class DataDirectoryCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.DataDirectory;

    public DiagnosticScope Scope => DiagnosticScope.Configuration;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.DataDirectory;
        var details = CheckOutcome.Details(("verzeichnis", path));

        if (!context.Files.DirectoryExists(path))
        {
            return Task.FromResult(context.Files.FileExists(path)
                ? CheckOutcome.Fail(
                    Code,
                    $"Das Datenverzeichnis '{path}' ist eine Datei, kein Verzeichnis.",
                    "BIFROST_DATA_DIR auf ein Verzeichnis zeigen lassen.",
                    details)
                : CheckOutcome.Warning(
                    Code,
                    $"Das Datenverzeichnis '{path}' existiert nicht.",
                    "Bei einer Neuinstallation legt der Start es an. Bei einer bestehenden zeigt der "
                    + "Pfad ins Leere: Der Gateway richtet dort eine leere Datenbank ein und meldet "
                    + "sich fehlerfrei als bereit. Vor dem Start Volume bzw. Mount prüfen "
                    + "(docs/operations.md, Abschnitt 'Der Volume-Name').",
                    details));
        }

        var reason = context.Files.ProbeWritable(path);
        return Task.FromResult(reason is null
            ? CheckOutcome.Pass(
                Code, $"Das Datenverzeichnis '{path}' ist vorhanden und beschreibbar.", details)
            : CheckOutcome.Fail(
                Code,
                $"Das Datenverzeichnis '{path}' ist nicht beschreibbar: {reason}",
                "Der Gateway läuft als Nicht-root-Benutzer. Rechte und Eigentümer prüfen; nach einem "
                + "Volume-Umzug ohne 'cp -a' gehört der Inhalt oft noch root.",
                details));
    }
}

/// <summary>
/// BFR-CFG-0002 — alt benannte <c>MCPMCP_*</c>-Variablen sind noch in Benutzung.
/// <para>
/// Der Server übernimmt sie beim Start als <c>BIFROST_*</c>. Das ist eine Übergangshilfe und keine
/// Zusage: Sie stehen in keiner Doku mehr, und der nächste Mensch sucht die Einstellung vergeblich.
/// </para>
/// <para>
/// <b>Es werden ausschliesslich Namen ausgegeben.</b> Unter den alten Namen steckt unter anderem
/// <c>MCPMCP_KEYRING_CERT_PASSWORD</c>.
/// </para>
/// </summary>
public sealed class LegacyEnvironmentVariablesCheck : IDiagnosticCheck
{
    private const string OldPrefix = "MCPMCP_";
    private const string NewPrefix = "BIFROST_";

    public string Code => DiagnosticCodes.LegacyEnvironmentVariables;

    public DiagnosticScope Scope => DiagnosticScope.Configuration;

    public TimeSpan Timeout => TimeSpan.FromSeconds(2);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var legacy = new List<string>();
        var shadowed = new List<string>();
        foreach (var (name, value) in context.Environment)
        {
            if (!name.StartsWith(OldPrefix, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            legacy.Add(name);
            var newName = NewPrefix + name[OldPrefix.Length..];
            if (context.Value(newName) is not null)
            {
                shadowed.Add(name);
            }
        }

        if (legacy.Count == 0)
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code, "Keine alt benannten MCPMCP_-Umgebungsvariablen in Benutzung."));
        }

        legacy.Sort(StringComparer.Ordinal);
        shadowed.Sort(StringComparer.Ordinal);

        var details = CheckOutcome.Details(
            ("variablen", string.Join(", ", legacy)),
            ("anzahl", DetailFormat.Count(legacy.Count)),
            ("ueberschrieben_durch_neuen_namen", shadowed.Count == 0 ? "-" : string.Join(", ", shadowed)));

        return Task.FromResult(CheckOutcome.Warning(
            Code,
            $"{legacy.Count} Umgebungsvariable(n) tragen noch den alten Präfix MCPMCP_ und werden "
            + "beim Start als BIFROST_ übernommen.",
            "Auf BIFROST_ umbenennen. Die Übernahme ist eine Übergangshilfe, keine Zusage — die "
            + "alten Namen stehen in keiner Doku mehr. Sind beide gesetzt, gewinnt der neue Name.",
            details));
    }
}

/// <summary>
/// BFR-CFG-0003 — öffentliche Basis-URL, wenn ein Proxy oder OAuth davorsteht.
/// <para>
/// <c>BIFROST_PUBLIC_BASE_URL</c> ist die kanonische Adresse: Sie bildet die Redirect-URI der
/// Upstream-Autorisierung und die Vorgabe der OAuth-Audience. Fehlt sie hinter einem Proxy, zeigen
/// beide auf das, was der Gateway selbst sieht — also auf die interne Adresse.
/// </para>
/// </summary>
public sealed class PublicBaseUrlCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.PublicBaseUrl;

    public DiagnosticScope Scope => DiagnosticScope.Configuration;

    public TimeSpan Timeout => TimeSpan.FromSeconds(2);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var url = context.PublicBaseUrl;
        var proxyDeclared = context.TrustedProxies is not null;
        var oauthConfigured = context.OAuthIssuer is not null;

        if (url is null)
        {
            return Task.FromResult(proxyDeclared || oauthConfigured
                ? CheckOutcome.Warning(
                    Code,
                    "BIFROST_PUBLIC_BASE_URL ist nicht gesetzt, obwohl "
                    + (proxyDeclared ? "ein Proxy deklariert ist" : "OAuth konfiguriert ist")
                    + ".",
                    "Die öffentliche Adresse des Gateways setzen (z. B. https://gateway.example.com). "
                    + "Ohne sie zeigt die Redirect-URI der Upstream-Autorisierung "
                    + "(<basis>/oauth/upstream/callback) auf die interne Adresse, und die "
                    + "OAuth-Audience hat keinen Vorgabewert.")
                : CheckOutcome.Skipped(
                    Code,
                    "BIFROST_PUBLIC_BASE_URL ist nicht gesetzt und wird ohne Proxy und ohne OAuth "
                    + "auch nicht gebraucht."));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return Task.FromResult(CheckOutcome.Fail(
                Code,
                "BIFROST_PUBLIC_BASE_URL ist keine absolute http(s)-Adresse.",
                "Vollständig angeben, mit Schema und ohne Pfad: https://gateway.example.com"));
        }

        var details = CheckOutcome.Details(
            ("schema", parsed.Scheme),
            ("proxy_deklariert", DetailFormat.YesNo(proxyDeclared)));

        if (parsed.Scheme == Uri.UriSchemeHttp && proxyDeclared)
        {
            return Task.FromResult(CheckOutcome.Warning(
                Code,
                "BIFROST_PUBLIC_BASE_URL nennt http, obwohl ein Proxy deklariert ist.",
                "Steht ein TLS-Proxy davor, ist die öffentliche Adresse eine https-Adresse. Ein "
                + "OAuth-Fluss über Klartext wird von der Gegenseite abgelehnt.",
                details));
        }

        return Task.FromResult(CheckOutcome.Pass(
            Code, "BIFROST_PUBLIC_BASE_URL ist gesetzt und auswertbar.", details));
    }
}
