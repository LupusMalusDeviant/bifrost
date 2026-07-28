namespace McpMcp.Abstractions;

/// <summary>Konventionen der Asset-Auslieferung an Agenten (FR-40).</summary>
public static class AssetDelivery
{
    /// <summary>Reservierter Namespace: Assets erscheinen als <c>assets__{name}</c> — kollisionsfrei zu Upstream-Slugs.</summary>
    public const string Namespace = "assets";

    /// <summary>URI-Schema, unter dem Assets zusätzlich als MCP-Resource lesbar sind.</summary>
    public const string UriPrefix = "mcpmcp://assets/";

    public static string PromptName(string assetName) => $"{Namespace}{NamespacedToolName.Separator}{assetName}";

    public static string ResourceUri(string assetName) => $"{UriPrefix}{Uri.EscapeDataString(assetName)}";

    /// <summary>Liefert den Asset-Namen aus einem Prompt-Namen oder einer Resource-URI; null, wenn es keiner ist.</summary>
    public static string? TryGetAssetName(string promptNameOrUri)
    {
        if (promptNameOrUri.StartsWith(UriPrefix, StringComparison.Ordinal))
        {
            return Uri.UnescapeDataString(promptNameOrUri[UriPrefix.Length..]);
        }

        var prefix = Namespace + NamespacedToolName.Separator;
        return promptNameOrUri.StartsWith(prefix, StringComparison.Ordinal)
            ? promptNameOrUri[prefix.Length..]
            : null;
    }
}

/// <summary>
/// Die strukturierten Angaben eines Skills neben seinem Fließtext.
/// <para>
/// <b>Warum überhaupt Struktur, wo ein Skill doch Text ist:</b> Nur was deklariert ist, lässt sich
/// prüfen. Ein Verweis in der Prosa („Details siehe codebase-mapper/references/x") hängt still ins
/// Leere, sobald jemand umbenennt. Deklariert man ihn, kann der Gateway sagen, dass er nicht
/// aufgeht — und bei <see cref="RequiredTools"/> kann er es sogar gegen den Katalog prüfen, was
/// kein Datei-Editor könnte.
/// </para>
/// <para>
/// Bewusst <b>schmal</b>: Alles Weitere gehört in den Körper. Ein Formular mit zwanzig Feldern
/// füllt niemand aus, und der Wert eines Skills steckt im Text, nicht in Metadaten.
/// </para>
/// </summary>
/// <param name="WhenToUse">
/// Wann ein Agent zu diesem Skill greifen soll. Geht mit in <c>list_skills</c> — das ist die
/// Angabe, die über den Zugriff entscheidet, und deshalb gehört sie in die Liste und nicht erst in
/// den Text.
/// </param>
/// <param name="References">Namen anderer Skills, die dieser voraussetzt oder ergänzt.</param>
/// <param name="RequiredTools">Namespaced Tool-Namen, die der Skill voraussetzt.</param>
public sealed record SkillMetadata(
    string? WhenToUse = null,
    IReadOnlyList<string>? References = null,
    IReadOnlyList<string>? RequiredTools = null)
{
    public static SkillMetadata Empty { get; } = new();

    public IReadOnlyList<string> ReferencesOrEmpty => References ?? [];

    public IReadOnlyList<string> RequiredToolsOrEmpty => RequiredTools ?? [];

    public bool IsEmpty
        => string.IsNullOrWhiteSpace(WhenToUse)
            && ReferencesOrEmpty.Count == 0
            && RequiredToolsOrEmpty.Count == 0;
}

public sealed record AssetInfo(
    AssetId Id,
    string Name,
    string? Description,
    AssetVersion LatestVersion,
    DateTimeOffset UpdatedAt,
    SkillMetadata? Metadata = null)
{
    public SkillMetadata MetadataOrEmpty => Metadata ?? SkillMetadata.Empty;
}

public sealed record AssetContent(
    AssetId Id,
    AssetVersion Version,
    string Name,
    string Content,
    DateTimeOffset PublishedAt,
    SkillMetadata? Metadata = null)
{
    public SkillMetadata MetadataOrEmpty => Metadata ?? SkillMetadata.Empty;
}

/// <summary>Ein Befund der Skill-Prüfung. Warnung, nicht Fehler — siehe <see cref="ISkillValidator"/>.</summary>
public sealed record SkillFinding(string Field, string Message);

/// <summary>
/// Prüft die deklarierten Angaben eines Skills gegen die Wirklichkeit: Gibt es die referenzierten
/// Skills, gibt es die vorausgesetzten Tools?
/// <para>
/// <b>Befunde sind Warnungen, keine Fehler.</b> Wer Skill A schreibt, der B referenziert, legt
/// vielleicht B erst danach an — ein hartes Nein erzwänge eine Reihenfolge, die niemand einhalten
/// will, und die naheliegende Reaktion darauf wäre, das Feld leer zu lassen.
/// </para>
/// </summary>
public interface ISkillValidator
{
    Task<IReadOnlyList<SkillFinding>> ValidateAsync(
        string name, SkillMetadata metadata, CancellationToken ct);
}

/// <summary>
/// Zentrale Verwaltung versionierter Text-Assets (Skills/Prompts/Instructions, FR-40).
/// Auslieferung an Agenten erfolgt MCP-nativ als Prompts/Resources über den Katalog.
/// <para>
/// <b>Assets sind für jede authentifizierte Identität sichtbar</b> — es gibt keine per-Asset-RBAC,
/// und FR-40 verlangt sie nicht. Sie sind zentrale Instruktionstexte und eröffnen keinen Zugriff auf
/// Fremdsysteme. Wer hier Schutz annimmt, irrt: <b>keine Secrets in Assets ablegen.</b>
/// </para>
/// <para>
/// Deshalb nimmt <see cref="ListAsync"/> <b>keine Identität</b> entgegen. Ein Parameter, der nie
/// ausgewertet wird, sieht an jeder Aufrufstelle wie eine Berechtigungsprüfung aus und ist keine —
/// das ist genau die Sorte Struktur-ohne-Wirkung, die dieses Projekt mehrfach teuer bezahlt hat. Wird
/// eine Einschränkung später beschlossen, kommt der Parameter zurück; das ist eine mechanische
/// Änderung, die der Compiler an jeder Stelle anzeigt.
/// </para>
/// </summary>
public interface IAssetStore
{
    /// <summary>Alle Assets in ihrer neuesten Version — ungefiltert, siehe Hinweis am Interface.</summary>
    Task<IReadOnlyList<AssetInfo>> ListAsync(CancellationToken ct);

    Task<AssetContent> GetAsync(AssetId id, AssetVersion? version, CancellationToken ct);

    /// <summary>
    /// Hängt eine neue Version an. Die Metadaten gehören zur Version, nicht zum Skill: Wer die
    /// Referenzen ändert, ändert den Skill — das gehört in die Historie wie der Text.
    /// </summary>
    Task<AssetVersion> PublishAsync(
        AssetId id, string content, SkillMetadata? metadata, CancellationToken ct);

    /// <summary>Legt ein neues Asset (Name + Beschreibung) als Version 1 an.</summary>
    Task<AssetId> CreateAsync(
        string name, string? description, string content, SkillMetadata? metadata, CancellationToken ct);

    /// <summary>Alle Versionen eines Skills, neueste zuerst — für Historie und Zurückschalten.</summary>
    Task<IReadOnlyList<AssetContent>> GetVersionsAsync(AssetId id, CancellationToken ct);
}
