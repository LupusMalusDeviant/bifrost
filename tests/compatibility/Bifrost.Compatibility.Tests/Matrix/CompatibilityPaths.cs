namespace Bifrost.Compatibility.Tests.Matrix;

/// <summary>
/// Wo Matrix und Bericht liegen. Die Pfade stehen im M5-Vertrag §4 und deshalb genau einmal hier.
/// </summary>
public static class CompatibilityPaths
{
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bifrost.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName
                ?? throw new InvalidOperationException("Repo-Root (Bifrost.slnx) nicht gefunden.");
        }
    }

    public static string MatrixFile => Path.Combine(RepoRoot, "tests", "compatibility", "matrix.json");

    public static string ReportFile => Path.Combine(RepoRoot, "docs", "acceptance", "compatibility.md");

    public static string ProtocolDirectory => Path.Combine(RepoRoot, "tests", "compatibility", "protocols");
}
