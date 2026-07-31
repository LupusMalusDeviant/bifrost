using System.Reflection;
using System.Runtime.CompilerServices;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Core.Execution;

using Xunit;

namespace Bifrost.Integration.Tests.Execution;

/// <summary>
/// Das Gegenstück zum Architekturtest aus <c>Bifrost.Core.Tests</c>, hier für die Assembly
/// <c>Bifrost.Server</c> (WP3.1, DoD; ADR-0025 E4). Es steht in diesem Projekt, weil nur hier die
/// Server-Assembly referenziert ist.
/// <para>
/// Dieselben zwei Regeln, dieselbe Absicht: Der Test <em>sucht</em> seine Kandidaten, statt sie
/// aufzuzählen. Ein neuer Endpunkt, ein neuer Importweg oder eine neue Probe, die sich ihre
/// Konfiguration selbst zusammensetzt, wird rot — nicht erst, wenn jemand daran denkt, die Liste zu
/// pflegen.
/// </para>
/// </summary>
public sealed class HostExecutionServerArchitectureTests
{
    private static readonly Assembly Server = typeof(Bifrost.Server.Execution.HostExecutionStartup).Assembly;

    [Fact]
    public void JederServerwegMitEinerUpstreamKonfigurationIstEingeordnet()
    {
        var unclassified = ServerArchitecture.ConfigurationEntryPoints(Server)
            .Where(method =>
                method.GetCustomAttribute<HostExecutionCheckedAttribute>() is null
                && method.GetCustomAttribute<NoHostExecutionAttribute>() is null)
            .Select(ServerArchitecture.Describe)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        unclassified.Should().BeEmpty(
            "auch im Server ist jeder Weg mit einer UpstreamServerConfig entweder ein Startweg "
            + "([HostExecutionChecked]) oder nachweislich keiner ([NoHostExecution]): {0}",
            string.Join(", ", unclassified));
    }

    [Fact]
    public void WerImServerEinenConnectorStartetHatVorherGefragt()
    {
        var ungated = ServerArchitecture.ConnectorStarters(Server)
            .Where(owner => owner.GetCustomAttribute<HostExecutionCheckedAttribute>() is null)
            .Select(ServerArchitecture.Describe)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        ungated.Should().BeEmpty(
            "die Paketprobe startet einen Prozess und baut sich ihre Konfiguration selbst — genau "
            + "der Weg, den eine Formularpruefung nicht sieht: {0}",
            string.Join(", ", ungated));
    }

    [Fact]
    public void DieSucheFindetUeberhauptEtwas()
        => ServerArchitecture.ConnectorStarters(Server).Should().NotBeEmpty();
}

/// <summary>Dieselbe Suche wie in Bifrost.Core.Tests, auf die Server-Assembly angewandt.</summary>
internal static class ServerArchitecture
{
    private const int Call = 0x28;
    private const int Callvirt = 0x6F;
    private const int Newobj = 0x73;

    public static IReadOnlyList<MethodBase> AllMethods(Assembly assembly)
        => [.. assembly.GetTypes()
            .Where(type => !type.IsInterface)
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.DeclaredOnly)))];

    public static IReadOnlyList<MethodBase> ConfigurationEntryPoints(Assembly assembly)
        => [.. AllMethods(assembly)
            .Where(method => !IsCompilerGenerated(method))
            .Where(method => method.IsPublic || method.IsFamily || method.IsAssembly
                || method.IsFamilyOrAssembly)
            .Where(method => !method.IsAbstract && !method.IsSpecialName)
            .Where(method => method.GetParameters().Any(p => CarriesConfig(p.ParameterType)))];

    public static IReadOnlyList<MethodBase> ConnectorStarters(Assembly assembly)
    {
        var connect = typeof(IUpstreamConnector).GetMethod(nameof(IUpstreamConnector.ConnectAsync))!;
        var owners = new Dictionary<string, MethodBase>(StringComparer.Ordinal);

        foreach (var method in AllMethods(assembly))
        {
            if (!Calls(method).Any(called => called.Name == connect.Name))
            {
                continue;
            }

            var owner = LogicalOwner(method, assembly);
            owners.TryAdd(Describe(owner), owner);
        }

        return [.. owners.Values];
    }

    public static string Describe(MethodBase method)
        => $"{method.DeclaringType?.FullName}.{method.Name}";

    private static bool CarriesConfig(Type type)
        => type == typeof(UpstreamServerConfig)
            || (type.IsGenericType && type.GetGenericArguments().Any(CarriesConfig))
            || (type.Namespace?.StartsWith("Bifrost.", StringComparison.Ordinal) == true
                && type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Any(property => property.PropertyType == typeof(UpstreamServerConfig)));

    private static bool IsCompilerGenerated(MethodBase method)
        => method.GetCustomAttribute<CompilerGeneratedAttribute>() is not null
            || method.Name.Contains('<', StringComparison.Ordinal)
            || method.DeclaringType?.Name.Contains('<', StringComparison.Ordinal) == true
            || method.DeclaringType?.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;

    private static MethodBase LogicalOwner(MethodBase method, Assembly assembly)
    {
        var name = OwnerName(method);
        if (name is null)
        {
            return method;
        }

        var outer = method.DeclaringType!;
        while (outer.DeclaringType is not null
            && (outer.Name.Contains('<', StringComparison.Ordinal)
                || outer.GetCustomAttribute<CompilerGeneratedAttribute>() is not null))
        {
            outer = outer.DeclaringType;
        }

        return AllMethods(assembly)
            .FirstOrDefault(other => other.DeclaringType == outer && other.Name == name) ?? method;
    }

    private static string? OwnerName(MethodBase method)
        => Extract(method.Name) ?? (method.DeclaringType is { } type ? Extract(type.Name) : null);

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
}
