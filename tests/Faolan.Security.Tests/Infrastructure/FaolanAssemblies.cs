using System.Reflection;
using Faolan.Abstractions;

namespace Faolan.Security.Tests.Infrastructure;

/// <summary>
/// Die Produktassemblies — die Menge, ueber die die Reflexionstests laufen.
/// <para>
/// <b>Warum ueber einen Typanker je Projekt und nicht ueber
/// <c>AppDomain.CurrentDomain.GetAssemblies()</c>:</b> Assemblies werden traege geladen. Ein Test,
/// der die geladene Menge abfragt, prueft je nach Ausfuehrungsreihenfolge mal fuenf und mal drei
/// Projekte — und meldet in beiden Faellen „bestanden". Ein Anker erzwingt das Laden.
/// </para>
/// </summary>
public static class FaolanAssemblies
{
    private static readonly Lazy<IReadOnlyList<Assembly>> Loaded = new(() =>
    [
        typeof(IToolInvoker).Assembly,                               // Faolan.Abstractions
        typeof(Faolan.Core.Invocation.ToolInvoker).Assembly,        // Faolan.Core
        typeof(Faolan.Upstream.StdioUpstreamConnector).Assembly,    // Faolan.Upstream
        typeof(Faolan.Persistence.CryptographicNames).Assembly,     // Faolan.Persistence
        typeof(Program).Assembly,                                    // Faolan.Server
        typeof(Faolan.Web.UiPolicies).Assembly,                     // Faolan.Web
        typeof(Faolan.Cli.CliVersion).Assembly,                     // Faolan.Cli
    ]);

    public static IReadOnlyList<Assembly> All => Loaded.Value;

    /// <summary>
    /// Alle Typen aller Produktassemblies, auch die internen. Typen, die sich nicht laden lassen
    /// (fehlende optionale Abhaengigkeit), werden uebersprungen statt den Lauf abzubrechen —
    /// aber nur einzeln, nicht als Gruppe.
    /// </summary>
    public static IEnumerable<Type> AllTypes()
    {
        foreach (var assembly in All)
        {
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }

            foreach (var type in types)
            {
                if (type is not null)
                {
                    yield return type;
                }
            }
        }
    }
}
