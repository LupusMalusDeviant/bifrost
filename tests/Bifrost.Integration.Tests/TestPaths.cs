using System.Reflection;

namespace Bifrost.Integration.Tests;

internal static class TestPaths
{
    /// <summary>Pfad zur gebauten Executable eines TestServers (Ordnername unter tests/Bifrost.TestServers).</summary>
    public static string Executable(string serverFolder)
    {
        var configuration = typeof(TestPaths).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var exeName = $"Bifrost.TestServers.{serverFolder}" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        var path = Path.Combine(
            RepoRoot, "tests", "Bifrost.TestServers", serverFolder,
            "bin", configuration, "net10.0", exeName);

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"TestServer-Executable nicht gefunden: {path}. Zuerst die Solution bauen.", path);
    }

    public static string EchoServerExecutable => Executable("EchoServer");

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null
                && !File.Exists(Path.Combine(dir.FullName, "Bifrost.slnx"))
                && !File.Exists(Path.Combine(dir.FullName, "Bifrost.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName
                ?? throw new InvalidOperationException("Repo-Root (Bifrost.slnx) oberhalb des Test-Verzeichnisses nicht gefunden.");
        }
    }
}
