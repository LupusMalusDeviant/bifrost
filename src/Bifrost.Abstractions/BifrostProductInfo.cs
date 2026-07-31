using System.Reflection;

namespace Bifrost.Abstractions;

/// <summary>Eine Build-Quelle für Protokoll-, Telemetrie-, Assembly- und Paketversion.</summary>
public static class BifrostProductInfo
{
    public static string Version { get; } =
        typeof(BifrostProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? typeof(BifrostProductInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
