using System.Reflection;
using Xunit;

namespace McpMcp.Integration.Tests;

/// <summary>
/// Findet das gebaute Rust-Host-Binary und die signierten Fixture-Dateien (Plan 0003, ADR-0020).
/// <c>MCPMCP_WASI_HOST</c> zeigt direkt auf das Binary, sonst wird
/// <c>spikes/wasi-component-runtime/target/{release,debug}</c> abgesucht.
/// <para>
/// Fehlt der Host, wird der Test übersprungen — eine .NET-Umgebung ohne Rust-Toolchain soll das
/// Gate nicht reißen. <c>MCPMCP_REQUIRE_WASI_HOST=1</c> (im Rust-fähigen CI-Job gesetzt) macht aus
/// dem Skip einen Fehlschlag, damit der Nachweis nicht still ausfallen kann.
/// </para>
/// </summary>
internal static class WasiHostPaths
{
    private static readonly string[] BuildProfiles = ["release", "debug"];

    public static string FixturesDirectory =>
        Path.Combine(RepoRoot, "spikes", "wasi-component-runtime", "fixtures");

    public static string ComponentPath => Path.Combine(FixturesDirectory, "wasi-p2-guest.component.wasm");

    public static string SignaturePath => Path.Combine(FixturesDirectory, "wasi-p2-guest.component.sig");

    public static string PublisherPath => Path.Combine(FixturesDirectory, "wasi-p2-guest.publisher.pub");

    /// <summary>Pfad zum echten Host; überspringt den Test, wenn keiner da ist (siehe Klassendoku).</summary>
    public static string RequireHost()
    {
        var host = Locate();
        if (host is null)
        {
            var required = Environment.GetEnvironmentVariable("MCPMCP_REQUIRE_WASI_HOST") is "1" or "true";
            Assert.SkipUnless(required, "WASI-Host-Binary nicht gefunden — 'cargo build' in spikes/wasi-component-runtime oder MCPMCP_WASI_HOST setzen.");
            Assert.Fail("MCPMCP_REQUIRE_WASI_HOST ist gesetzt, aber kein Host-Binary auffindbar.");
        }

        return host;
    }

    private static string? Locate()
    {
        if (Environment.GetEnvironmentVariable("MCPMCP_WASI_HOST") is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? configured : null;
        }

        var name = OperatingSystem.IsWindows()
            ? "mcpmcp-wasi-component-spike.exe"
            : "mcpmcp-wasi-component-spike";
        var target = Path.Combine(RepoRoot, "spikes", "wasi-component-runtime", "target");
        return BuildProfiles
            .Select(profile => Path.Combine(target, profile, name))
            .FirstOrDefault(File.Exists);
    }

    private static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MCPMCP.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new InvalidOperationException("Repo-Root (MCPMCP.slnx) oberhalb des Test-Verzeichnisses nicht gefunden.");
        }
    }
}
