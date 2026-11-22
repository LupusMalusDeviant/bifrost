using System.Collections;
using Faolan.Abstractions;

namespace Faolan.Server;

/// <summary>
/// Übergang über die Umbenennungen <c>MCPMCP_*</c> → <c>BIFROST_*</c> (2026-07-31) →
/// <c>FAOLAN_*</c> (Vertrag <c>docs/contracts/umbenennung-faolan.md</c> §2.2).
/// <para>
/// <b>Warum das existiert und nicht einfach die Doku umgeschrieben wurde:</b> Die
/// Umgebungsvariablen sind die <em>gesamte</em> Konfiguration dieses Gateways — Datenverzeichnis,
/// Datenbank, Key-Ring-Zertifikat, Audit-Modus, Guardrails. Eine Installation, die nach dem Update
/// mit unbekannten Variablennamen startet, verliert sie nicht mit einer Fehlermeldung, sondern
/// <b>lautlos</b>: Sie fällt auf alle Vorgabewerte zurück, legt eine leere Datenbank neben der
/// vollen an und meldet fröhlich „bereit". Genau diese Sorte Ausfall ist die teuerste, weil sie
/// wie ein Erfolg aussieht.
/// </para>
/// <para>
/// Die Übernahme passiert <b>vor</b> dem Bau der Konfiguration und im Prozessumfeld selbst. Damit
/// erreicht sie beide Lesearten, die es im Code gibt — <c>IConfiguration</c> und den direkten Griff
/// zu <see cref="Environment"/> — ohne dass an 30 Stellen ein Zweitname abgefragt werden muss. Die
/// Regel selbst steht in <see cref="EnvironmentNameChain"/>.
/// </para>
/// </summary>
internal static class LegacyEnvironment
{
    /// <summary>
    /// Übernimmt alle noch alt benannten Variablen ins Prozessumfeld und liefert deren Namen.
    /// </summary>
    public static IReadOnlyList<string> Adopt() => EnvironmentNameChain.Adopt();

    /// <inheritdoc cref="EnvironmentNameChain.PlanAdoption"/>
    internal static IReadOnlyList<(string OldName, string NewName, string Value)> PlanAdoption(
        IDictionary environment)
        => EnvironmentNameChain.PlanAdoption(environment);

    /// <inheritdoc cref="SqliteDatabaseFile.Resolve"/>
    public static string ResolveSqliteFile(string dataDirectory) => SqliteDatabaseFile.Resolve(dataDirectory);
}
