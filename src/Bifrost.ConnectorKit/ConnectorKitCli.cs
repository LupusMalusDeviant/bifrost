using System.Globalization;
using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Core.Packaging;

namespace Bifrost.ConnectorKit;

/// <summary>Exitcodes wie bei den Betriebsbefehlen (M2-Vertrag §4) — ein Skript soll nicht raten.</summary>
internal static class ConnectorKitExit
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int UsageError = 2;
    public const int PackageInvalid = 5;
}

internal sealed class ConnectorKitException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Das Connector Developer Kit (M6/WP6.5): fünf Befehle auf dem <b>vorhandenen</b> Paketformat.
/// <para>
/// <b>Es entsteht kein zweites Format.</b> Gebaut wird mit <see cref="ConnectorPackageBuilder"/>,
/// geprüft mit <see cref="ConnectorPackageReader"/> — denselben Klassen, die im Gateway laufen. Ein
/// Werkzeug mit eigener Formatkenntnis wäre eine zweite Wahrheit darüber, was ein gültiges Paket
/// ist, und die eine bekäme irgendwann eine Korrektur, die die andere nicht kennt.
/// </para>
/// </summary>
internal sealed class ConnectorKitCli(TextWriter output, TextWriter error)
{
    public const string UsageText =
        """
        bifrost-connector — Pakete für B.I.F.R.O.S.T bauen, signieren und prüfen.

          bifrost-connector init <verzeichnis> [--id <name>] [--key <datei>] [--label <text>]
          bifrost-connector build <verzeichnis> --key <datei> [--out <paket.mcpkg>]
          bifrost-connector sign <paket.mcpkg> --key <datei> --out <paket.mcpkg>
          bifrost-connector verify <paket.mcpkg> --publisher <schluessel.json | keyid:base64>
          bifrost-connector test <paket.mcpkg> --publisher <...>

        Exitcodes: 0 ok · 1 Fehler · 2 Bedienfehler · 5 Paket ungueltig.

        Der private Schluessel bleibt auf diesem Rechner und erscheint in keiner Ausgabe.
        Es gibt keine oeffentliche Registry: Ein Paket ist eine Datei, die man weitergibt.
        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            return args.ToArray() switch
            {
                ["init", var directory, .. var options] => await InitAsync(directory, new KitOptions(options), ct),
                ["build", var directory, .. var options] => await BuildAsync(directory, new KitOptions(options), ct),
                ["sign", var package, .. var options] => await SignAsync(package, new KitOptions(options), ct),
                ["verify", var package, .. var options] => await InspectAsync(package, new KitOptions(options), probe: false, ct),
                ["test", var package, .. var options] => await InspectAsync(package, new KitOptions(options), probe: true, ct),
                _ => await UsageAsync(),
            };
        }
        catch (ConnectorKitException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return exception.ExitCode;
        }
        catch (ConnectorPackageException exception)
        {
            // Die Prüfseite des Produkts hat gesprochen — ihr Urteil wird durchgereicht, nicht
            // umformuliert.
            await error.WriteLineAsync(exception.Message);
            return ConnectorKitExit.PackageInvalid;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message);
            return ConnectorKitExit.UnexpectedError;
        }
    }

    private async Task<int> InitAsync(string directory, KitOptions options, CancellationToken ct)
    {
        var id = options.Value("--id") ?? Path.GetFileName(Path.GetFullPath(directory));
        var keyPath = options.Value("--key") ?? Path.Combine(directory, "publisher.key.json");
        var label = options.Value("--label") ?? id;
        options.EnsureOnly("--id", "--key", "--label");

        Directory.CreateDirectory(directory);

        var key = File.Exists(keyPath) ? PublisherKeyFile.Load(keyPath) : Fresh(keyPath, label);
        const string entryPoint = "payload/connector.wasm";

        var manifestPath = Path.Combine(directory, ConnectorProject.FileName);
        if (File.Exists(manifestPath))
        {
            throw new ConnectorKitException(
                $"'{manifestPath}' gibt es bereits — init überschreibt nichts.", ConnectorKitExit.UsageError);
        }

        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(
                ConnectorProject.Skeleton(id, key.KeyId, entryPoint), ConnectorProject.Json),
            ct);

        Directory.CreateDirectory(Path.Combine(directory, "payload"));
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), ReadmeFor(id, entryPoint), ct);

        await output.WriteLineAsync($"Projekt:      {Path.GetFullPath(directory)}");
        await output.WriteLineAsync($"Herausgeber:  {key.KeyId}");
        await output.WriteLineAsync($"Schlüssel:    {Path.GetFullPath(keyPath)} (bleibt hier)");
        await output.WriteLineAsync(
            $"Nächster Schritt: '{entryPoint}' ablegen, dann 'bifrost-connector build {directory} --key {keyPath}'.");
        return ConnectorKitExit.Success;
    }

    private static PublisherKeyFile Fresh(string keyPath, string label)
    {
        var key = PublisherKeyFile.Create(label);
        key.Save(keyPath);
        return key;
    }

    private async Task<int> BuildAsync(string directory, KitOptions options, CancellationToken ct)
    {
        var keyPath = options.Value("--key")
            ?? throw new ConnectorKitException("build verlangt --key <datei>.", ConnectorKitExit.UsageError);
        var explicitTarget = options.Value("--out");

        // **Befund aus dem ersten Testlauf:** Die Pruefung stand hinter dem Laden des Projekts —
        // ein vertippter Schalter fiel damit erst auf, wenn sonst alles stimmte, und sonst gar
        // nicht. Ein Bedienfehler gehoert vor die Arbeit, nicht dahinter.
        options.EnsureOnly("--key", "--out");

        var key = PublisherKeyFile.Load(keyPath);
        var project = ConnectorProject.Load(directory, key.KeyId);
        var target = explicitTarget
            ?? Path.Combine(directory, "out", $"{project.Manifest.Id}-{project.Manifest.Version}.mcpkg");

        // **Befund aus dem ersten Ende-zu-Ende-Lauf.** Ein WASI-Paket deklariert eine abgetrennte
        // Signatur der Component (der Host prueft sie beim Laden gegen die gepinnten Herausgeber).
        // Fehlte sie, lehnte die Probe zu Recht ab — und der Entwickler haette sie von Hand mit
        // einem fremden Werkzeug erzeugen muessen. Genau diese Reibung soll das Kit beseitigen:
        // Wer den Schluessel hat, kann auch die Component signieren.
        var files = new Dictionary<string, byte[]>(project.Files, StringComparer.Ordinal);
        var componentSignature = project.Manifest.SignaturePath;
        if (!string.IsNullOrEmpty(componentSignature) && !files.ContainsKey(componentSignature))
        {
            files[componentSignature] = key.Sign(files[project.Manifest.EntryPoint]);
            await output.WriteLineAsync($"Signiert:     {componentSignature} (aus dem Einstiegspunkt erzeugt)");
        }

        var package = ConnectorPackageBuilder.Build(project.Manifest, files, key.Sign);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        await File.WriteAllBytesAsync(target, package, ct);

        await output.WriteLineAsync($"Paket:        {Path.GetFullPath(target)}");
        await output.WriteLineAsync($"Größe:        {package.Length.ToString("N0", CultureInfo.InvariantCulture)} Bytes");
        await output.WriteLineAsync($"Herausgeber:  {key.KeyId}");
        await output.WriteLineAsync($"Dateien:      {files.Count}");
        await output.WriteLineAsync(
            "Selbst geprüft: 'bifrost-connector test <paket> --publisher <schlüssel>'.");
        return ConnectorKitExit.Success;
    }

    /// <summary>
    /// Signiert ein vorhandenes Paket neu — der Fall „Bauserver baut, Freigabeberechtigter
    /// signiert". Das Manifest bleibt <b>bytegleich</b>; alles andere wäre ein anderes Paket.
    /// </summary>
    private async Task<int> SignAsync(string packagePath, KitOptions options, CancellationToken ct)
    {
        var keyPath = options.Value("--key")
            ?? throw new ConnectorKitException("sign verlangt --key <datei>.", ConnectorKitExit.UsageError);
        var target = options.Value("--out")
            ?? throw new ConnectorKitException("sign verlangt --out <paket.mcpkg>.", ConnectorKitExit.UsageError);
        options.EnsureOnly("--key", "--out");

        var key = PublisherKeyFile.Load(keyPath);
        var (manifest, files) = ConnectorPackageSource.Read(await File.ReadAllBytesAsync(packagePath, ct));

        if (!string.Equals(manifest.PublisherKeyId, key.KeyId, StringComparison.OrdinalIgnoreCase))
        {
            // Das Manifest nennt seinen Herausgeber selbst, und der Leser prueft beides gegeneinander.
            // Mit fremdem Schluessel zu signieren ergaebe ein Paket, das jede Pruefung ablehnt —
            // besser hier abbrechen als ein unbrauchbares Artefakt ausliefern.
            throw new ConnectorKitException(
                $"Das Paket nennt den Herausgeber '{manifest.PublisherKeyId}', der Schlüssel gehört zu "
                + $"'{key.KeyId}'. Ein Paket mit fremdem Schlüssel zu signieren ergibt eines, das "
                + "jede Prüfung ablehnt.",
                ConnectorKitExit.UsageError);
        }

        var resigned = ConnectorPackageBuilder.Build(manifest, files, key.Sign);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        await File.WriteAllBytesAsync(target, resigned, ct);

        await output.WriteLineAsync($"Neu signiert: {Path.GetFullPath(target)}");
        await output.WriteLineAsync($"Herausgeber:  {key.KeyId}");
        return ConnectorKitExit.Success;
    }

    private async Task<int> InspectAsync(string packagePath, KitOptions options, bool probe, CancellationToken ct)
    {
        var publisherArgument = options.Value("--publisher")
            ?? throw new ConnectorKitException(
                "verify/test verlangt --publisher <schlüsseldatei | keyid:base64>.", ConnectorKitExit.UsageError);
        options.EnsureOnly("--publisher");

        var publisher = ReadPublisher(publisherArgument);
        await using var stream = File.OpenRead(packagePath);
        var verified = ConnectorPackageReader.Verify(stream, [publisher]);
        var manifest = verified.Manifest;

        await output.WriteLineAsync($"Paket:        {manifest.Id} {manifest.Version}");
        await output.WriteLineAsync($"Anzeigename:  {manifest.DisplayName}");
        await output.WriteLineAsync($"Transport:    {manifest.Transport}");
        await output.WriteLineAsync($"Einstieg:     {manifest.EntryPoint}");
        await output.WriteLineAsync($"Herausgeber:  {verified.Publisher.KeyId} ({verified.TrustLevel})");
        await output.WriteLineAsync($"Nutzlast:     {manifest.Payloads.Count} Datei(en), Hashes geprüft");
        await output.WriteLineAsync($"Skills:       {manifest.SkillsOrEmpty.Count}");

        if (!probe)
        {
            return ConnectorKitExit.Success;
        }

        // **Was die Probe hinzufuegt — und was ausdruecklich nicht.**
        //
        // Schema, Vertragsversion, Signatur und Prüfsummen prüft der Leser des Produkts oben; eine
        // zweite Prüfung derselben Sache waere toter Code, der Sicherheit vortaeuscht. (Der erste
        // Entwurf dieses Werkzeugs hatte genau den — gefunden hat ihn der eigene Test.)
        //
        // Was der Leser NICHT prüft, ist die abgetrennte Signatur der Component: Er stellt sicher,
        // dass die Datei deklariert ist und ihr Hash stimmt. Ob sie eine gueltige Signatur ueber
        // den Einstiegspunkt IST, entscheidet erst der WASI-Host beim Laden — also auf dem Rechner
        // des Betreibers. Genau das nimmt die Probe vorweg.
        await VerifyEntryPointSignatureAsync(packagePath, manifest, verified.Publisher, ct);

        // Die angeforderten Rechte gehoeren in die Ausgabe, nicht nur ins Manifest: Wer ein Paket
        // ausliefert, soll vorher lesen, wozu ein Betreiber zustimmen muss.
        var grants = manifest.GrantsOrNone.Enumerate();
        await output.WriteLineAsync(grants.Count == 0
            ? "Grants:       keine — der Connector kommt ohne Datei-, Netz- oder Secretzugriff aus."
            : $"Grants:       {string.Join(", ", grants)} (Zustimmung beim Einspielen noetig)");
        await output.WriteLineAsync(
            "Probe bestanden: Manifest-Signatur, Hashes, Schema, Vertragsversion — und die "
            + "abgetrennte Signatur des Einstiegspunkts.");
        return ConnectorKitExit.Success;
    }

    /// <summary>
    /// Prüft die abgetrennte Signatur des Einstiegspunkts gegen den Herausgeber, der das Manifest
    /// signiert hat. Stimmt sie nicht, laedt der WASI-Host die Component spaeter nicht — und der
    /// Fehler faellt beim Betreiber an, nicht hier.
    /// </summary>
    private async Task VerifyEntryPointSignatureAsync(
        string packagePath, ConnectorManifest manifest, PublisherKey publisher, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(manifest.SignaturePath))
        {
            await output.WriteLineAsync(
                "Hinweis:      Das Manifest deklariert keine Signatur des Einstiegspunkts.");
            return;
        }

        var (_, files) = ConnectorPackageSource.Read(await File.ReadAllBytesAsync(packagePath, ct));
        if (!files.TryGetValue(manifest.EntryPoint, out var component)
            || !files.TryGetValue(manifest.SignaturePath, out var signature))
        {
            throw new ConnectorKitException(
                $"'{manifest.EntryPoint}' oder '{manifest.SignaturePath}' fehlt im Archiv.",
                ConnectorKitExit.PackageInvalid);
        }

        // Dieselbe Bibliothek, mit der dieses Werkzeug signiert — und dieselbe Pruefung, die der
        // Rust-Host mit ed25519-dalek fuer die Component macht. Der Verifizierer im Kern ist
        // internal; ihn dafuer zu oeffnen hiesse, Produktionscode fuer ein Werkzeug aufzumachen.
        if (!Ed25519Check.Verify(Convert.FromBase64String(publisher.PublicKeyBase64), component, signature))
        {
            throw new ConnectorKitException(
                $"Die Signatur '{manifest.SignaturePath}' passt nicht zum Einstiegspunkt. Der "
                + "WASI-Host wuerde die Component ablehnen.",
                ConnectorKitExit.PackageInvalid);
        }
    }

    private static PublisherKey ReadPublisher(string argument)
    {
        if (File.Exists(argument))
        {
            // Bequemlichkeit für den Entwickler: die eigene Schlüsseldatei. Gelesen wird daraus
            // ausschließlich der öffentliche Teil.
            var key = PublisherKeyFile.Load(argument);
            return key.AsPublisher();
        }

        var parts = argument.Split(':', 2);
        if (parts.Length != 2)
        {
            throw new ConnectorKitException(
                $"'{argument}' ist weder eine Datei noch 'keyid:base64'.", ConnectorKitExit.UsageError);
        }

        return new PublisherKey(parts[0], parts[1], "extern", DateTimeOffset.UtcNow);
    }

    private async Task<int> UsageAsync()
    {
        await error.WriteLineAsync(UsageText);
        return ConnectorKitExit.UsageError;
    }

    private static string ReadmeFor(string id, string entryPoint) =>
        $"""
        # {id}

        Ein Connector für B.I.F.R.O.S.T.

        1. Die Component nach `{entryPoint}` legen.
        2. `connector.json` ausfüllen: Anzeigename, Beschreibung, benötigte Grants.
        3. Bauen und selbst prüfen:

           ```
           bifrost-connector build . --key publisher.key.json
           bifrost-connector test out/{id}-0.1.0.mcpkg --publisher publisher.key.json
           ```

        Der private Schlüssel gehört nicht ins Versionsverwaltungssystem.
        """;
}
