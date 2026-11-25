using System.Reflection;

namespace Faolan.Abstractions;

/// <summary>Eine Build-Quelle für Protokoll-, Telemetrie-, Assembly- und Paketversion.</summary>
public static class FaolanProductInfo
{
    public static string Version { get; } =
        typeof(FaolanProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? typeof(FaolanProductInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
