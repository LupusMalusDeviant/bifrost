using System.Reflection;
using System.Runtime.CompilerServices;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Core.Execution;
using Bifrost.Core.Upstreams;

using Xunit;

namespace Bifrost.Core.Tests.Execution;

/// <summary>
/// Der Architekturtest aus dem Pflichtenheft (WP3.1, DoD): <b>kein nativer Startweg ohne
/// Policyprüfung</b> (ADR-0025 E4).
/// <para>
/// <b>Warum er nicht die heute bekannten Startwege aufzählt:</b> Eine Liste im Test beschreibt den
/// Stand des Tages, an dem sie geschrieben wurde. Sie wird grün bleiben, wenn morgen ein Adapter
/// dazukommt — und genau dann sollte sie rot werden. Deshalb <em>sucht</em> dieser Test seine
/// Kandidaten selbst:
/// </para>
/// <list type="number">
/// <item>Jede nicht-private Methode in <c>Bifrost.Core</c>, die eine <see cref="UpstreamServerConfig"/>
/// entgegennimmt, muss eingeordnet sein — entweder <see cref="HostExecutionCheckedAttribute"/> oder
/// <see cref="NoHostExecutionAttribute"/> mit Begründung. Eine neue Methode trägt keins von beiden
/// und fällt auf.</item>
/// <item>Eine Kennzeichnung als „geprüft" wird am <b>IL-Code</b> nachgewiesen. Ein Attribut, das
/// niemand einlöst, wäre schlimmer als keins: Es sähe im Code aus wie eine Zusage.</item>
/// <item>Wer einen Connector startet (<c>IUpstreamConnector.ConnectAsync</c>), muss vorher gefragt
/// haben — unabhängig davon, welche Argumente seine Methode nimmt. Das ist die Regel, die auch
/// einen Startweg fängt, der sich seine Konfiguration selbst baut.</item>
/// </list>
/// <para>
/// <b>Grenze, ausdrücklich benannt:</b> Geprüft wird die Assembly <c>Bifrost.Core</c>. Die
/// Connector-Implementierungen liegen in <c>Bifrost.Upstream</c> (Zone WP3.2) und sind
/// ausschließlich über Supervisor und Verbindungstest erreichbar — beide stehen hier unter Regel 3.
/// Das Gegenstück für <c>Bifrost.Server</c> liegt in <c>Bifrost.Integration.Tests</c>, weil nur dort
/// die Server-Assembly referenziert ist.
/// </para>
/// </summary>
public sealed class HostExecutionArchitectureTests
{
    private static readonly Assembly Core = typeof(UpstreamSupervisor).Assembly;

    [Fact]
    public void JederWegMitEinerUpstreamKonfigurationIstEingeordnet()
    {
        var unclassified = HostExecutionArchitecture.ConfigurationEntryPoints(Core)
            .Where(method =>
                method.GetCustomAttribute<HostExecutionCheckedAttribute>() is null
                && method.GetCustomAttribute<NoHostExecutionAttribute>() is null)
            .Select(HostExecutionArchitecture.Describe)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        unclassified.Should().BeEmpty(
            "jeder Weg, der eine UpstreamServerConfig annimmt, ist entweder ein Startweg und fragt "
            + "die Policy ([HostExecutionChecked]), oder er kann nichts starten und sagt warum "
            + "([NoHostExecution(\"…\")]). Nicht eingeordnet: {0}",
            string.Join(", ", unclassified));
    }

    [Fact]
    public void AlsGeprueftGekennzeichneteWegeFragenDenTorpostenWirklich()
    {
        var broken = HostExecutionArchitecture.AllMethods(Core)
            .Where(method => method.GetCustomAttribute<HostExecutionCheckedAttribute>() is not null)
            .Where(method => !HostExecutionArchitecture.ReachesGuard(method, Core))
            .Select(HostExecutionArchitecture.Describe)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        broken.Should().BeEmpty(
            "eine Kennzeichnung als geprüft ist eine Zusage über den Code, nicht über die Absicht. "
            + "Ohne Aufruf von HostExecutionGuard (oder eines geprüften Weges) ist sie unwahr: {0}",
            string.Join(", ", broken));
    }

    [Fact]
    public void WerEinenConnectorStartetHatVorherGefragt()
    {
        var ungated = HostExecutionArchitecture.ConnectorStarters(Core)
            .Where(owner => owner.GetCustomAttribute<HostExecutionCheckedAttribute>() is null)
            .Select(HostExecutionArchitecture.Describe)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        ungated.Should().BeEmpty(
            "ConnectAsync startet ein fremdes Programm. Jeder Weg dorthin muss die Policy gefragt "
            + "haben — auch einer, der seine Konfiguration selbst zusammensetzt: {0}",
            string.Join(", ", ungated));
    }

    /// <summary>
    /// Der Test muss auch dann etwas finden, wenn niemand ihn ansieht: Findet die Suche keine
    /// Kandidaten, ist nicht alles in Ordnung, sondern die Suche kaputt.
    /// </summary>
    [Fact]
    public void DieSucheFindetUeberhauptStartwege()
    {
        HostExecutionArchitecture.ConfigurationEntryPoints(Core).Should().NotBeEmpty();
        HostExecutionArchitecture.ConnectorStarters(Core).Should().NotBeEmpty();
        HostExecutionArchitecture.AllMethods(Core)
            .Count(m => m.GetCustomAttribute<HostExecutionCheckedAttribute>() is not null)
            .Should().BeGreaterThan(0);
    }
}

/// <summary>
/// Die Suche selbst. Sie liest Metadaten und IL-Code mit Bordmitteln der Laufzeit — ohne zusätzliche
/// Abhängigkeit, weil ein Architekturtest, der eine eigene Bibliothek mitbringt, beim nächsten
/// Upgrade als Erstes deaktiviert wird.
/// </summary>
internal static class HostExecutionArchitecture
{
    private const int Call = 0x28;
    private const int Callvirt = 0x6F;
    private const int Newobj = 0x73;

    /// <summary>Alle Methoden und Konstruktoren einer Assembly, samt vom Compiler erzeugten.</summary>
    public static IReadOnlyList<MethodBase> AllMethods(Assembly assembly)
        => [.. assembly.GetTypes()
            .Where(type => !type.IsInterface)
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.DeclaredOnly)))];

    /// <summary>
    /// Kandidaten der Regel 1: sichtbare Wege, die eine Upstream-Konfiguration entgegennehmen.
    /// <para>
    /// Private Helfer bleiben außen vor — sie sind nur über eine dieser Methoden erreichbar und
    /// würden die Liste mit Zwischenschritten füllen, die niemand von außen aufrufen kann.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MethodBase> ConfigurationEntryPoints(Assembly assembly)
        => [.. AllMethods(assembly)
            .Where(method => !IsCompilerGenerated(method))
            .Where(IsVisible)
            .Where(method => !method.IsAbstract)
            // Datenträger sind keine Wege: Konstruktoren, Eigenschaftszugriffe, Operatoren und die
            // vom Compiler erzeugten Record-Glieder tragen eine Konfiguration, sie tun nichts damit.
            .Where(method => !method.IsSpecialName)
            .Where(method => method.GetParameters().Any(p => CarriesConfig(p.ParameterType)))];

    /// <summary>
    /// Kandidaten der Regel 3: die logischen Besitzer aller Stellen, die einen Connector aufbauen.
    /// </summary>
    public static IReadOnlyList<MethodBase> ConnectorStarters(Assembly assembly)
    {
        var connect = typeof(IUpstreamConnector).GetMethod(nameof(IUpstreamConnector.ConnectAsync))!;
        var owners = new Dictionary<string, MethodBase>(StringComparer.Ordinal);

        foreach (var method in AllMethods(assembly))
        {
            if (!Calls(method).Any(called => IsSameMethod(called, connect)))
            {
                continue;
            }

            var owner = LogicalOwner(method, assembly);
            owners.TryAdd(Describe(owner), owner);
        }

        return [.. owners.Values];
    }

    /// <summary>
    /// Löst die Kennzeichnung ein: Ruft dieser Weg — oder eine der vom Compiler für ihn erzeugten
    /// Methoden — den Torposten auf, die policyführende Validierung, oder einen anderen geprüften Weg?
    /// </summary>
    public static bool ReachesGuard(MethodBase method, Assembly assembly)
        => BodyGroup(method, assembly)
            .SelectMany(Calls)
            .Any(called =>
                method.DeclaringType == typeof(HostExecutionGuard)
                || called.DeclaringType == typeof(HostExecutionGuard)
                || IsPolicyAwareValidation(called)
                || (called.GetCustomAttribute<HostExecutionCheckedAttribute>() is not null
                    && !IsSameMethod(called, method)));

    public static string Describe(MethodBase method)
        => $"{method.DeclaringType?.FullName}.{method.Name}";

    /// <summary>
    /// Die zweiargumentige <c>UpstreamConfigValidator.Validate</c> — die einzige Überladung, die
    /// eine Policy führt. Die einargumentige prüft nur den Aufbau und zählt hier ausdrücklich nicht.
    /// </summary>
    private static bool IsPolicyAwareValidation(MethodBase called)
        => called.DeclaringType == typeof(UpstreamConfigValidator)
            && called.Name == nameof(UpstreamConfigValidator.Validate)
            && called.GetParameters().Length == 2;

    private static bool IsVisible(MethodBase method)
        => method.IsPublic || method.IsFamily || method.IsAssembly || method.IsFamilyOrAssembly;

    /// <summary>
    /// Trägt dieser Parametertyp eine Upstream-Konfiguration? Direkt, als Element einer Auflistung,
    /// oder als Feld eines Trägertyps (etwa <see cref="UpstreamConfigVersion"/>).
    /// </summary>
    private static bool CarriesConfig(Type type)
    {
        if (type == typeof(UpstreamServerConfig))
        {
            return true;
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(CarriesConfig))
        {
            return true;
        }

        return type.Namespace?.StartsWith("Bifrost.", StringComparison.Ordinal) == true
            && type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.PropertyType == typeof(UpstreamServerConfig));
    }

    private static bool IsCompilerGenerated(MethodBase method)
        => method.GetCustomAttribute<CompilerGeneratedAttribute>() is not null
            || method.Name.Contains('<', StringComparison.Ordinal)
            || method.DeclaringType?.Name.Contains('<', StringComparison.Ordinal) == true
            || method.DeclaringType?.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;

    /// <summary>
    /// Der geschriebene Weg hinter einer vom Compiler erzeugten Methode: die Zustandsmaschine einer
    /// <c>async</c>-Methode, ein Lambda, eine lokale Funktion. Ohne diese Auflösung liefe der Test
    /// gegen Namen wie <c>&lt;AddAsync&gt;d__12.MoveNext</c>, die niemand kennzeichnen kann.
    /// </summary>
    private static MethodBase LogicalOwner(MethodBase method, Assembly assembly)
    {
        var name = OwnerName(method);
        if (name is null)
        {
            return method;
        }

        var outer = method.DeclaringType!;
        while (outer.Name.Contains('<', StringComparison.Ordinal)
            || outer.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
        {
            outer = outer.DeclaringType ?? outer;
            if (outer.DeclaringType is null)
            {
                break;
            }
        }

        var candidate = AllMethods(assembly)
            .FirstOrDefault(other => other.DeclaringType == outer && other.Name == name);
        return candidate ?? method;
    }

    private static string? OwnerName(MethodBase method)
    {
        var fromMethod = Extract(method.Name);
        if (fromMethod is not null)
        {
            return fromMethod;
        }

        return method.DeclaringType is { } type ? Extract(type.Name) : null;
    }

    /// <summary>
    /// Der Name des geschriebenen Weges aus einem vom Compiler erzeugten Bezeichner. Die Namen
    /// schachteln sich (<c>&lt;&lt;Create&gt;b__0&gt;d</c> ist die Zustandsmaschine eines Lambdas in
    /// <c>Create</c>), deshalb wird die innerste oeffnende Klammer vor der ersten schliessenden
    /// gesucht und nicht einfach die erste.
    /// </summary>
    private static string? Extract(string name)
    {
        var end = name.IndexOf('>', StringComparison.Ordinal);
        if (end < 0 || name.Length == 0 || name[0] != '<')
        {
            return null;
        }

        var start = name.LastIndexOf('<', end - 1);
        return start >= 0 && end - start > 1 ? name[(start + 1)..end] : null;
    }

    /// <summary>Die Methode selbst plus alles, was der Compiler für sie ausgelagert hat.</summary>
    private static IEnumerable<MethodBase> BodyGroup(MethodBase method, Assembly assembly)
    {
        yield return method;

        if (method.DeclaringType is not { } declaring)
        {
            yield break;
        }

        foreach (var nested in declaring.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var candidate in nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (OwnerName(candidate) == method.Name)
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>
    /// Die aufgerufenen Methoden aus dem IL-Code. Bewusst eine einfache Suche nach den drei
    /// Aufruf-Opcodes samt folgendem Metadaten-Token: Sie kann einen Aufruf zu viel finden, aber
    /// keinen zu wenig — und für die Frage „wird der Torposten gerufen?" ist genau das die richtige
    /// Richtung des Irrtums.
    /// </summary>
    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
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

    private static bool IsSameMethod(MethodBase left, MethodBase right)
        => left.Name == right.Name
            && (left.DeclaringType == right.DeclaringType
                || (right.DeclaringType?.IsInterface == true
                    && left.DeclaringType?.GetInterfaces().Contains(right.DeclaringType) == true));
}
