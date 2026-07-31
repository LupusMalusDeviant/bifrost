using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;

namespace Bifrost.Core.Tests.Diagnostics;

/// <summary>
/// Erfundene Aussenwelt für die Diagnose-Checks. Ohne sie liesse sich weder ein nicht
/// beschreibbares Verzeichnis noch eine fehlende Container-Runtime herstellen — und ein Test, der
/// Docker startet, misst die Maschine und nicht den Code.
/// </summary>
internal sealed class FakeFileProbe : IFileProbe
{
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pfad → Grund, warum nicht geschrieben werden kann. Fehlt der Eintrag: beschreibbar.</summary>
    public Dictionary<string, string> NotWritable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool DirectoryExists(string path) => Directories.Contains(path);

    public bool FileExists(string path) => Files.Contains(path);

    public IReadOnlyList<string> ListFiles(string path, string searchPattern)
    {
        if (!Directories.Contains(path))
        {
            return [];
        }

        var head = searchPattern.Split('*')[0];
        return [.. Files.Where(file =>
            file.Length > path.Length
            && file.StartsWith(path, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(file).StartsWith(head, StringComparison.OrdinalIgnoreCase))];
    }

    public string? ProbeWritable(string path) => NotWritable.GetValueOrDefault(path);
}

internal sealed class FakePortProbe : IPortProbe
{
    public Dictionary<int, PortState> States { get; } = [];

    public PortState Inspect(int port) => States.GetValueOrDefault(port, PortState.Free);
}

internal sealed class FakeProcessProbe : IProcessProbe
{
    public ProcessProbeResult Result { get; set; } = new(false, -1, string.Empty, "nicht gefunden");

    public Task<ProcessProbeResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        => Task.FromResult(Result);
}

internal sealed class FakeDatabaseProbe(DatabaseDiagnosticFacts facts) : IDatabaseDiagnosticProbe
{
    public Task<DatabaseDiagnosticFacts> DescribeAsync(CancellationToken ct) => Task.FromResult(facts);
}

internal sealed class FakeUpstreamProbe(params UpstreamDiagnosticFact[] facts) : IUpstreamDiagnosticProbe
{
    public Task<IReadOnlyList<UpstreamDiagnosticFact>> DescribeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<UpstreamDiagnosticFact>>(facts);
}

/// <summary>Ein Check, der nie antwortet — der Beweis, dass er den Bericht nicht aufhält.</summary>
internal sealed class HangingCheck(string code, DiagnosticScope scope, TimeSpan timeout) : IDiagnosticCheck
{
    public string Code => code;

    public DiagnosticScope Scope => scope;

    public TimeSpan Timeout => timeout;

    public async Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        // Ausdrücklich OHNE den Token: Genau der Check, der ihn ignoriert, ist der gefährliche.
        await Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
        return CheckOutcome.Pass(code, "nie erreicht");
    }
}

/// <summary>Ein Check, der wirft.</summary>
internal sealed class ThrowingCheck(string code, Exception exception) : IDiagnosticCheck
{
    public string Code => code;

    public DiagnosticScope Scope => DiagnosticScope.Configuration;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
        => throw exception;
}

/// <summary>Ein Check, der sofort ein festes Ergebnis liefert.</summary>
internal sealed class ConstantCheck(string code, DiagnosticScope scope, CheckStatus status) : IDiagnosticCheck
{
    public string Code => code;

    public DiagnosticScope Scope => scope;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
        => Task.FromResult(new DiagnosticCheck(code, status, "fest verdrahtet"));
}

internal static class DiagnosticWorld
{
    /// <summary>Ein Kontext mit erfundener Aussenwelt; alles Weitere setzt der Test per <c>with</c>.</summary>
    public static DiagnosticContext Context(
        IDictionary<string, string>? environment = null,
        FakeFileProbe? files = null,
        FakePortProbe? ports = null,
        FakeProcessProbe? processes = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            env[key] = value;
        }

        return new DiagnosticContext
        {
            Environment = env,
            HostEnvironmentName = "Production",
            Files = files ?? new FakeFileProbe(),
            Ports = ports ?? new FakePortProbe(),
            Processes = processes ?? new FakeProcessProbe(),
        };
    }
}
