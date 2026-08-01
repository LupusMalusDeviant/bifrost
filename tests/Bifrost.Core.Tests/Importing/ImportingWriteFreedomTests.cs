using System.Reflection;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Bifrost.Core.Importing;

using Xunit;

namespace Bifrost.Core.Tests.Importing;

/// <summary>
/// <b>Die DoD von WP4.1:</b> „Kein Providerparser erzeugt direkt eine aktive
/// Upstreamkonfiguration."
/// <para>
/// <b>Warum dieser Test sucht statt aufzuzählen.</b> Eine Liste der heute vorhandenen Dateien
/// beschriebe den Stand des Tages, an dem sie geschrieben wurde. Sie bliebe grün, wenn morgen ein
/// Providerparser dazukommt (WP4.2 bringt vier davon) — und genau dann sollte sie rot werden.
/// Deshalb hat dieser Test <em>keine</em> Dateiliste. Er sucht seine Kandidaten auf drei Wegen, und
/// jeder davon nimmt eine neue Datei automatisch mit:
/// </para>
/// <list type="number">
/// <item><b>Über das Verzeichnis.</b> Jede <c>.cs</c>-Datei unter <c>src/Bifrost.Core/Importing/</c>
/// wird vom Dateisystem geholt und Zeile für Zeile gegen die Verbotsliste gehalten.</item>
/// <item><b>Über den Namensraum.</b> Jede dieser Dateien muss in <c>Bifrost.Core.Importing</c>
/// liegen. Ohne diese Regel könnte eine neue Datei sich der Typprüfung entziehen, indem sie einen
/// anderen Namensraum wählt.</item>
/// <item><b>Über die Typen und den IL-Code.</b> Kein Typ dieses Namensraums hält, nimmt, liefert
/// oder ruft einen Schreibweg. Das fängt auch den Fall, den eine Textsuche nicht fängt: einen
/// Schreibweg, der über eine Basisklasse oder ein <c>var</c> hereinkommt.</item>
/// </list>
/// <para>
/// Dass die Regel bei einer neuen Datei wirklich rot wird, behauptet dieser Test nicht — er zeigt
/// es: <see cref="Die_regel_wird_bei_einer_neuen_datei_rot"/> lässt dieselbe Prüfmechanik über eine
/// erfundene Datei laufen.
/// </para>
/// </summary>
public sealed class ImportingWriteFreedomTests
{
    /// <summary>Der Namensraum, für den diese Regeln gelten.</summary>
    private const string Zone = "Bifrost.Core.Importing";

    /// <summary>Das Verzeichnis, das ihm entspricht — ab der Wurzel des Arbeitsbaums.</summary>
    private const string ZoneDirectory = "src/Bifrost.Core/Importing";

    // ── 1. Über das Verzeichnis ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Keine Quelldatei der Importzone berührt einen Schreibweg — Store, Supervisor, Datenbank,
    /// Dateisystem, Netz, Prozessstart.
    /// </summary>
    [Fact]
    public void Keine_datei_der_importzone_beruehrt_einen_schreibweg()
    {
        var verstoesse = ImportingSources.All()
            .SelectMany(datei => ImportingRule.Violations(datei.RelativePath, datei.Lines))
            .ToList();

        verstoesse.Should().BeEmpty(
            "der Import analysiert, er legt nicht an. Wer aus einem Plan Wirklichkeit macht, geht "
            + "ueber die bestehenden Stores und den Supervisor — dort sitzen Validierung, "
            + "Ausfuehrungs-Policy und Audit (ADR-0025 E4). Gefunden:\n"
            + string.Join('\n', verstoesse));
    }

    // ── 2. Über den Namensraum ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Jede Datei der Zone gehört in den Namensraum der Zone. Ohne diese Regel wäre die Typprüfung
    /// mit einer Zeile zu umgehen.
    /// </summary>
    [Fact]
    public void Jede_datei_der_importzone_liegt_im_namensraum_der_zone()
    {
        var fremde = ImportingSources.All()
            .Where(datei => !datei.Lines.Any(zeile =>
                zeile.TrimStart().StartsWith($"namespace {Zone}", StringComparison.Ordinal)))
            .Select(datei => datei.RelativePath)
            .ToList();

        fremde.Should().BeEmpty(
            $"eine Datei in diesem Verzeichnis, die nicht in '{Zone}' liegt, entzieht sich der "
            + "Typpruefung. Gefunden:\n" + string.Join('\n', fremde));
    }

    // ── 3. Über die Typen und den IL-Code ─────────────────────────────────────────────────────

    /// <summary>
    /// Kein Typ der Zone <b>hält</b> einen Schreibweg: kein Feld, kein Konstruktorparameter, kein
    /// Methodenparameter, kein Rückgabewert. Wer einen Store in die Hand bekommt, benutzt ihn
    /// irgendwann — dieselbe Begründung wie bei den Upstream-Verbindungen in
    /// <c>Bifrost.Security.Tests</c>.
    /// </summary>
    [Fact]
    public void Kein_typ_der_importzone_haelt_einen_schreibweg()
    {
        const BindingFlags Alle = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var verstoesse = new List<string>();

        foreach (var typ in ImportingSources.Types())
        {
            foreach (var feld in typ.GetFields(Alle).Where(f => ImportingRule.IsWritePath(f.FieldType)))
            {
                verstoesse.Add($"{typ.FullName}.{feld.Name} : {feld.FieldType.Name}");
            }

            foreach (var glied in typ.GetConstructors(Alle).Cast<MethodBase>().Concat(typ.GetMethods(Alle)))
            {
                foreach (var parameter in glied.GetParameters()
                    .Where(p => ImportingRule.IsWritePath(p.ParameterType)))
                {
                    verstoesse.Add($"{typ.FullName}.{glied.Name}({parameter.Name} : {parameter.ParameterType.Name})");
                }

                if (glied is MethodInfo methode && ImportingRule.IsWritePath(methode.ReturnType))
                {
                    verstoesse.Add($"{typ.FullName}.{glied.Name} -> {methode.ReturnType.Name}");
                }
            }
        }

        verstoesse.Should().BeEmpty(
            "ein Typ, der einen Schreibweg haelt, ist ein Schreibweg. Gefunden:\n"
            + string.Join('\n', verstoesse));
    }

    /// <summary>
    /// Und keiner <b>ruft</b> einen — geprüft am IL-Code, weil ein Aufruf über eine lokale Variable
    /// in keiner Signatur auftaucht.
    /// </summary>
    [Fact]
    public void Kein_code_der_importzone_ruft_einen_schreibweg()
    {
        var verstoesse = new List<string>();

        foreach (var methode in ImportingSources.Methods())
        {
            foreach (var gerufen in ImportingRule.Calls(methode))
            {
                if (gerufen.DeclaringType is { } besitzer && ImportingRule.IsWritePath(besitzer))
                {
                    verstoesse.Add(
                        $"{methode.DeclaringType?.FullName}.{methode.Name} ruft "
                        + $"{besitzer.FullName}.{gerufen.Name}");
                }
            }
        }

        verstoesse.Distinct(StringComparer.Ordinal).Should().BeEmpty(
            "kein Netz, kein Dateisystem, kein Prozessstart, keine Persistenz aus dem Importpfad. "
            + "Gefunden:\n" + string.Join('\n', verstoesse.Distinct(StringComparer.Ordinal)));
    }

    // ── Der Test über den Test ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Der Nachweis, dass die Regel bei einer NEUEN Datei rot wird.</b> Dieselbe Prüfmechanik
    /// läuft über eine erfundene Datei, die es im Arbeitsbaum nicht gibt — sie muss auffallen.
    /// <para>
    /// Ohne diesen Fall wäre der obige Test eine Behauptung: Er ist grün, solange niemand etwas
    /// falsch macht, und er wäre auch grün, wenn er gar nichts prüfte.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("    private readonly IUpstreamConfigStore _store;")]
    [InlineData("        await _store.AppendVersionAsync(id, config, ct);")]
    [InlineData("        await _supervisor.AddAsync(config, ct);")]
    [InlineData("        await _db.SaveChangesAsync(ct);")]
    [InlineData("        File.WriteAllText(pfad, inhalt);")]
    [InlineData("        Directory.CreateDirectory(pfad);")]
    [InlineData("        using var client = new HttpClient();")]
    [InlineData("        var adressen = await Dns.GetHostAddressesAsync(host, ct);")]
    [InlineData("        Process.Start(\"docker\", \"run\");")]
    [InlineData("    private readonly BifrostDbContext _db;")]
    public void Die_regel_wird_bei_einer_neuen_datei_rot(string zeile)
    {
        string[] erfunden =
        [
            $"namespace {Zone};",
            string.Empty,
            "internal sealed class NeuerProviderparser",
            "{",
            zeile,
            "}",
        ];

        ImportingRule.Violations($"{ZoneDirectory}/NeuerProviderparser.cs", erfunden)
            .Should().NotBeEmpty(
                "eine neue Datei mit einem Schreibweg muss auffallen, nicht erst eine geaenderte");
    }

    /// <summary>
    /// Die Gegenprobe: Eine harmlose neue Datei darf <b>nicht</b> auffallen. Eine Regel, die immer
    /// feuert, wird beim ersten falschen Alarm abgeschaltet.
    /// </summary>
    [Fact]
    public void Eine_harmlose_neue_datei_faellt_nicht_auf()
    {
        string[] erfunden =
        [
            $"namespace {Zone};",
            string.Empty,
            "/// <summary>Ein Parser, der einen Store nie anfasst und keine Datei schreibt.</summary>",
            "internal sealed class HarmloserParser",
            "{",
            "    public double Recognize(string document) => document.Length > 0 ? 0.5 : 0;",
            "}",
        ];

        ImportingRule.Violations($"{ZoneDirectory}/HarmloserParser.cs", erfunden).Should().BeEmpty();
    }

    /// <summary>
    /// Die Kommentarregel steht ausdrücklich unter Test: Die Regeln dieses Projekts werden in den
    /// Kommentaren <em>beschrieben</em>, und eine Beschreibung ist kein Aufruf. Umgekehrt darf ein
    /// Verstoß sich nicht hinter einem Kommentarzeichen mitten in der Zeile verstecken.
    /// </summary>
    [Fact]
    public void Ein_kommentar_ueber_einen_schreibweg_ist_kein_schreibweg()
        => ImportingRule.Violations(
                $"{ZoneDirectory}/X.cs",
                [
                    $"namespace {Zone};",
                    "/// <summary>Schreibt nie: kein IUpstreamConfigStore, kein DbContext.</summary>",
                    "// Die Aktivierung laeuft ueber IUpstreamSupervisor.AddAsync.",
                ])
            .Should().BeEmpty();

    /// <summary>
    /// Findet die Suche nichts, ist nicht alles in Ordnung, sondern die Suche kaputt — etwa weil
    /// jemand das Verzeichnis umbenannt hat.
    /// </summary>
    [Fact]
    public void Die_suche_findet_ueberhaupt_etwas()
    {
        ImportingSources.All().Should().NotBeEmpty();
        ImportingSources.All().Select(d => d.RelativePath)
            .Should().Contain($"{ZoneDirectory}/ConfigurationImporter.cs");
        ImportingSources.Types().Should().Contain(typeof(ConfigurationImporter));
        ImportingSources.Methods().Should().NotBeEmpty();
    }

    /// <summary>
    /// Verzeichnis und Namensraum decken sich — die andere Richtung. Eine Datei <em>außerhalb</em>
    /// des Verzeichnisses, die den Namensraum der Zone benutzt, wäre von der Verzeichnissuche
    /// unsichtbar und stünde trotzdem im selben Namensraum. Umgekehrt genügt Regel 2.
    /// <para>
    /// Erst beide Richtungen zusammen ergeben die Zusage: Was in der Zone liegt, wird geprüft — und
    /// was zur Zone gehört, liegt in der Zone.
    /// </para>
    /// </summary>
    [Fact]
    public void Kein_code_ausserhalb_des_verzeichnisses_benutzt_den_namensraum_der_zone()
    {
        var fremde = ImportingSources.Production()
            .Where(datei => !datei.RelativePath.StartsWith(ZoneDirectory + "/", StringComparison.Ordinal))
            .Where(datei => datei.Lines.Any(zeile =>
                zeile.TrimStart().StartsWith($"namespace {Zone}", StringComparison.Ordinal)))
            .Select(datei => datei.RelativePath)
            .ToList();

        fremde.Should().BeEmpty(
            $"eine Datei ausserhalb von '{ZoneDirectory}' im Namensraum '{Zone}' waere von der "
            + "Verzeichnissuche unsichtbar. Gefunden:\n" + string.Join('\n', fremde));
    }
}

/// <summary>Der Zugriff auf die Quellen und Typen der Importzone.</summary>
internal static class ImportingSources
{
    private const string Zone = "Bifrost.Core.Importing";

    private static readonly Lazy<string> RootPath = new(FindRoot);

    private static readonly Lazy<IReadOnlyList<ImportingSourceFile>> Files =
        new(() => Load(Path.Combine(RootPath.Value, "src", "Bifrost.Core", "Importing")));

    private static readonly Lazy<IReadOnlyList<ImportingSourceFile>> ProductionFiles =
        new(() => Load(Path.Combine(RootPath.Value, "src")));

    /// <summary>
    /// Alle <c>.cs</c>-Dateien der Zone, vom Dateisystem geholt. <b>Hier steckt die Zusage „auch
    /// eine neue Datei":</b> Es gibt keine Liste, es gibt ein Verzeichnis.
    /// </summary>
    public static IReadOnlyList<ImportingSourceFile> All() => Files.Value;

    /// <summary>Alle Produktionsquellen unter <c>src/</c> — für die Gegenrichtung der Zonenprüfung.</summary>
    public static IReadOnlyList<ImportingSourceFile> Production() => ProductionFiles.Value;

    /// <summary>Alle Typen des Zonen-Namensraums, einschließlich verschachtelter.</summary>
    public static IReadOnlyList<Type> Types()
        => [.. typeof(ConfigurationImporter).Assembly.GetTypes()
            .Where(typ => string.Equals(typ.Namespace, Zone, StringComparison.Ordinal))];

    /// <summary>Alle Methoden und Konstruktoren dieser Typen, samt vom Compiler erzeugten.</summary>
    public static IReadOnlyList<MethodBase> Methods()
    {
        const BindingFlags Alle = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return [.. Types()
            .SelectMany(typ => typ.GetMethods(Alle).Cast<MethodBase>()
                .Concat(typ.GetConstructors(Alle)))];
    }

    private static string FindRoot()
    {
        var verzeichnis = new DirectoryInfo(AppContext.BaseDirectory);
        while (verzeichnis is not null)
        {
            if (File.Exists(Path.Combine(verzeichnis.FullName, "Bifrost.slnx")))
            {
                return verzeichnis.FullName;
            }

            verzeichnis = verzeichnis.Parent;
        }

        throw new InvalidOperationException(
            $"Arbeitsbaum nicht gefunden — ueber '{AppContext.BaseDirectory}' liegt keine Bifrost.slnx.");
    }

    private static ImportingSourceFile[] Load(string verzeichnis)
    {
        if (!Directory.Exists(verzeichnis))
        {
            throw new InvalidOperationException(
                $"'{verzeichnis}' gibt es nicht. Wurde das Verzeichnis verschoben, prueft dieser "
                + "Test nichts mehr — und das soll auffallen.");
        }

        return [.. Directory
            .EnumerateFiles(verzeichnis, "*.cs", SearchOption.AllDirectories)
            .Where(pfad => !pfad.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(pfad => !pfad.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(pfad => new ImportingSourceFile(
                Path.GetRelativePath(RootPath.Value, pfad).Replace('\\', '/'),
                File.ReadAllLines(pfad)))
            .OrderBy(datei => datei.RelativePath, StringComparer.Ordinal)];
    }
}

/// <param name="RelativePath">Pfad ab der Wurzel des Arbeitsbaums, immer mit <c>/</c>.</param>
internal sealed record ImportingSourceFile(string RelativePath, IReadOnlyList<string> Lines);

/// <summary>
/// Die Regel selbst — als Funktion über Inhalt, nicht als Liste über Dateinamen. Genau deshalb
/// lässt sie sich über eine erfundene Datei laufen und dabei beim Wort nehmen.
/// </summary>
internal static partial class ImportingRule
{
    /// <summary>
    /// Was ein Schreibweg ist. Jede Zeile ist eine Tür, durch die eine Analyse zu einer Änderung
    /// würde: Persistenz, Lebenszyklus, Dateisystem, Netz, fremder Prozess.
    /// </summary>
    [GeneratedRegex(
        @"\b(IUpstreamConfigStore|IUpstreamSupervisor|UpstreamSupervisor|IUpstreamConnector"
        + @"|IUpstreamConnection|\w*DbContext|SaveChangesAsync|AppendVersionAsync|ReconfigureAsync"
        + @"|SetEnabledAsync|RollbackAsync|ConnectAsync|CallToolAsync|HttpClient|HttpMessageHandler"
        + @"|WebRequest|Dns|Socket|Process|StreamWriter|FileStream)\b"
        + @"|\b(File|Directory|FileInfo|DirectoryInfo)\s*\.\s*\w+"
        + @"|\b(?:_\w+|\w+)\s*\.\s*AddAsync\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex Verbotenes();

    private const int Call = 0x28;

    private const int Callvirt = 0x6F;

    private const int Newobj = 0x73;

    /// <summary>Namen von Typen, die einen Schreibweg <em>sind</em>.</summary>
    private static readonly string[] ForbiddenTypeNames =
    [
        "IUpstreamConfigStore", "IUpstreamSupervisor", "IUpstreamConnector", "IUpstreamConnection",
        "HttpClient", "HttpMessageInvoker", "HttpMessageHandler", "WebRequest", "Dns", "Socket",
        "Process", "FileStream", "StreamWriter", "File", "Directory", "FileInfo", "DirectoryInfo",
    ];

    /// <summary>
    /// Die Fundstellen einer Datei. <paramref name="lines"/> kommt vom Dateisystem oder aus einem
    /// Test — der Regel ist das gleich, und das ist der Punkt.
    /// </summary>
    public static IReadOnlyList<string> Violations(string relativePath, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var hits = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            // Die Regeln dieses Projekts werden in den Kommentaren beschrieben, und eine
            // Beschreibung ist kein Aufruf.
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('*')
                || trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Verbotenes().Match(line);
            if (match.Success)
            {
                hits.Add($"{relativePath}:{index + 1}  [{match.Value.Trim()}]  {trimmed}");
            }
        }

        return hits;
    }

    /// <summary>Ist dieser Typ ein Schreibweg?</summary>
    public static bool IsWritePath(Type? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.IsByRef || type.IsArray || type.IsPointer)
        {
            return IsWritePath(type.GetElementType());
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(IsWritePath))
        {
            return true;
        }

        if (type.Namespace?.StartsWith("Bifrost.Persistence", StringComparison.Ordinal) == true)
        {
            return true;
        }

        var name = type.Name.Split('`')[0];
        return ForbiddenTypeNames.Contains(name, StringComparer.Ordinal)
            || name.EndsWith("DbContext", StringComparison.Ordinal)
            || name.EndsWith("Store", StringComparison.Ordinal)
            || name.EndsWith("Repository", StringComparison.Ordinal);
    }

    /// <summary>
    /// Die im IL-Code aufgerufenen Methoden. Bewusst eine einfache Suche nach den drei
    /// Aufruf-Opcodes samt folgendem Metadaten-Token — sie kann einen Aufruf zu viel finden, aber
    /// keinen zu wenig, und für „wird hier geschrieben?" ist das die richtige Richtung des Irrtums.
    /// </summary>
    public static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);

        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (InvalidOperationException)
        {
            yield break;
        }

        if (il is null)
        {
            yield break;
        }

        var module = method.Module;
        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        for (var index = 0; index + 4 < il.Length; index++)
        {
            if (il[index] is not (Call or Callvirt or Newobj))
            {
                continue;
            }

            MethodBase? called;
            try
            {
                called = module.ResolveMethod(
                    BitConverter.ToInt32(il, index + 1), typeArguments, methodArguments);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (called is not null)
            {
                yield return called;
            }
        }
    }
}
