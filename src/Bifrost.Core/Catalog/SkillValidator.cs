using Bifrost.Abstractions;

namespace Bifrost.Core.Catalog;

/// <summary>
/// Prüft die deklarierten Angaben eines Skills gegen die Wirklichkeit (FR-40).
/// <para>
/// <b>Das ist der Grund, warum die Felder überhaupt strukturiert sind.</b> Ein Verweis in der Prosa
/// („Details siehe codebase-mapper/references/x") hängt still ins Leere, sobald jemand umbenennt.
/// Deklariert man ihn, lässt sich sagen, dass er nicht aufgeht. Und bei den vorausgesetzten Tools
/// kann das <em>nur der Gateway</em>: Er kennt den Katalog, ein Datei-Editor nicht.
/// </para>
/// <para>
/// <b>Befunde sind Warnungen.</b> Wer Skill A schreibt, der B referenziert, legt B vielleicht erst
/// danach an — ein hartes Nein erzwänge eine Reihenfolge, die niemand einhalten will, und die
/// naheliegende Reaktion wäre, das Feld leer zu lassen. Ein leeres Feld prüft nichts.
/// </para>
/// </summary>
public sealed class SkillValidator : ISkillValidator
{
    private readonly IAssetStore _assets;
    private readonly IToolCatalog _catalog;

    public SkillValidator(IAssetStore assets, IToolCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(catalog);
        _assets = assets;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<SkillFinding>> ValidateAsync(
        string name, SkillMetadata metadata, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var findings = new List<SkillFinding>();

        if (metadata.ReferencesOrEmpty.Count > 0)
        {
            var known = (await _assets.ListAsync(ct).ConfigureAwait(false))
                .Select(a => a.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var reference in metadata.ReferencesOrEmpty)
            {
                if (string.Equals(reference, name, StringComparison.Ordinal))
                {
                    findings.Add(new SkillFinding(
                        nameof(SkillMetadata.References),
                        $"'{reference}' verweist auf sich selbst."));
                }
                else if (!known.Contains(reference))
                {
                    findings.Add(new SkillFinding(
                        nameof(SkillMetadata.References),
                        $"Skill '{reference}' existiert nicht. Ein Agent, der dem Verweis folgt, "
                        + "bekommt nichts."));
                }
            }
        }

        foreach (var tool in metadata.RequiredToolsOrEmpty)
        {
            // Über den Katalog-Snapshot, nicht über die Sichtbarkeit einer Identität: Ob ein Tool
            // EXISTIERT, ist eine andere Frage als ob ein bestimmter Agent es sehen darf. Hier geht
            // es um den Skill, nicht um einen Aufrufer.
            if (_catalog.Find(new NamespacedToolName(tool)) is null)
            {
                findings.Add(new SkillFinding(
                    nameof(SkillMetadata.RequiredTools),
                    $"Tool '{tool}' steht nicht im Katalog. Entweder ist der Upstream nicht "
                    + "angeschlossen, oder der Name stimmt nicht."));
            }
        }

        return findings;
    }
}
