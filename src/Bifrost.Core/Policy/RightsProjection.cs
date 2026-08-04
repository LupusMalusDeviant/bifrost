using Bifrost.Abstractions;

namespace Bifrost.Core.Policy;

/// <summary>Ein einzelnes Recht in lesbarer, vergleichbarer Form: <c>identität → wirkung</c>.</summary>
public sealed record RightsEntry(string Identity, string Scope, ToolAction Action)
{
    public override string ToString() => $"{Identity}: {Action} auf {Scope}";
}

/// <param name="Added">Was nach der Übernahme <b>zusätzlich</b> erlaubt wäre — die Rechteausweitung.</param>
/// <param name="Removed">Was wegfiele. Steht daneben, weil ein Pack auch zu viel wegnehmen kann.</param>
public sealed record RightsDelta(IReadOnlyList<RightsEntry> Added, IReadOnlyList<RightsEntry> Removed)
{
    public bool GrantsMore => Added.Count > 0;
}

/// <summary>
/// Rechnet aus, <b>wer danach was darf</b> — und nicht, welche Objekte sich ändern.
/// <para>
/// <b>Der Unterschied ist der Kern von WP6.1.</b> Ein Diff auf Objektebene sagt „Rolle
/// <c>entwickler</c> hat sich geändert". Das liest sich harmlos und kann bedeuten, dass drei
/// Agenten ab sofort auf jedem Server schreiben dürfen. Genau diese Verwechslung macht
/// Rechteausweitung unsichtbar (M6-Vertrag §2).
/// </para>
/// <para>
/// <b>Rollen werden über den Namen zugeordnet, nicht über die Id.</b> Ein Pack ist zum Weitergeben
/// gedacht; Guids sind auf jeder Instanz andere. Der Name ist das, was ein Betreiber liest — und
/// das, worauf er sich bezieht, wenn er sagt „unsere Entwicklerrolle".
/// </para>
/// </summary>
public static class RightsProjection
{
    /// <summary>
    /// Alle Rechte je Identität. <paramref name="rolesByName"/> ist der Stand, der gelten soll —
    /// die Zuordnung Identität→Rolle bleibt <b>immer</b> die lokale: Ein Pack bringt Rollen mit,
    /// keine Menschen.
    /// </summary>
    public static IReadOnlyList<RightsEntry> Project(
        IReadOnlyList<Identity> identities,
        IReadOnlyList<Role> localRoles,
        IReadOnlyDictionary<string, IReadOnlyList<Grant>> rolesByName)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(localRoles);
        ArgumentNullException.ThrowIfNull(rolesByName);

        var nameById = localRoles.ToDictionary(r => r.Id, r => r.Name);
        var entries = new List<RightsEntry>();

        foreach (var identity in identities)
        {
            foreach (var roleId in identity.Roles)
            {
                if (!nameById.TryGetValue(roleId, out var roleName)
                    || !rolesByName.TryGetValue(roleName, out var grants))
                {
                    // Eine Rolle, die der neue Stand nicht kennt, gibt auch keine Rechte. Das ist
                    // kein Fehler — es ist eine Wegnahme, und sie taucht als solche im Diff auf.
                    continue;
                }

                foreach (var grant in grants)
                {
                    foreach (var action in grant.Actions)
                    {
                        entries.Add(new RightsEntry(identity.Name, Describe(grant.Scope), action));
                    }
                }
            }
        }

        return [.. entries.Distinct().OrderBy(e => e.Identity, StringComparer.Ordinal)
            .ThenBy(e => e.Scope, StringComparer.Ordinal).ThenBy(e => e.Action)];
    }

    public static RightsDelta Diff(IReadOnlyList<RightsEntry> before, IReadOnlyList<RightsEntry> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var previous = before.ToHashSet();
        var next = after.ToHashSet();

        return new RightsDelta(
            [.. next.Except(previous).OrderBy(e => e.Identity, StringComparer.Ordinal).ThenBy(e => e.Scope, StringComparer.Ordinal)],
            [.. previous.Except(next).OrderBy(e => e.Identity, StringComparer.Ordinal).ThenBy(e => e.Scope, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Die Wirkungsebene im Klartext. „alles" ist absichtlich das Wort und nicht ein leerer Wert:
    /// Ein globaler Grant, der als leere Zelle erscheint, wird beim Lesen übersehen.
    /// </summary>
    private static string Describe(PermissionScope scope) => scope switch
    {
        { Tool: { } tool } => tool.Value,
        { Server: { } server } => $"Server {server.Value:N}",
        _ => "alles",
    };
}
