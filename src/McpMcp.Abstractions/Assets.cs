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
/// Grenzen, die für <b>jeden</b> Skill gelten — egal ob von Hand angelegt oder aus einem Paket.
/// <para>
/// Die Zahl steht hier und nicht im Paketleser, weil sie sonst zweimal existierte und
/// auseinanderliefe: dieselbe Auslieferung, zwei Regeln. Genau das war nach dem ersten Wurf der
/// Fall — der Paketweg war gedeckelt, der tägliche nicht.
/// </para>
/// </summary>
public static class SkillLimits
{
    /// <summary>
    /// Größter Skill-Text in UTF-8-Bytes.
    /// <para>
    /// <b>Warum es überhaupt eine Grenze gibt:</b> <c>read_skill</c> liefert den Text vollständig in
    /// den Kontext eines Agenten. Ein unbegrenzter Skill hebelt damit genau das Argument aus, für das
    /// die Meta-Tools existieren — entdecken billig, Inhalt auf Abruf.
    /// </para>
    /// </summary>
    public const int MaxContentBytes = 256 * 1024;

    /// <summary>
    /// Prüft beim <b>Schreiben</b>. Bewusst nicht beim Ausliefern: Ein bereits gespeicherter,
    /// zu großer Skill (aus der Zeit vor dieser Grenze) wird weiter vollständig geliefert. Ihn
    /// stillschweigend abzuschneiden hieße, einem Agenten eine halbe Anweisung zu geben.
    /// </summary>
    public static void EnsureWithinLimit(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxContentBytes)
        {
            throw new InvalidOperationException(
                $"Der Skill ist {bytes / 1024} KB groß; erlaubt sind {MaxContentBytes / 1024} KB. "
                + "Der Text geht bei jedem Abruf vollständig in den Kontext eines Agenten — was so "
                + "lang ist, gehört auf mehrere Skills verteilt, die sich gegenseitig referenzieren.");
        }
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

/// <summary>
/// Woher eine Skill-Version stammt, wenn sie nicht von Hand geschrieben wurde: aus einem
/// installierten Connector-Paket (ADR-0016, Material 0021-EM).
/// <para>
/// Die Herkunft hängt an der <b>Version</b>, nicht am Skill. Genau das macht den Fall lesbar, um
/// den es geht: Steht sie an der neuesten Version nicht, hat jemand den Text nach dem Installieren
/// angepasst — und ein Paket-Update darf ihn nicht stillschweigend verdrängen.
/// </para>
/// </summary>
public sealed record SkillSource(string PackageId, string PackageVersion);

public sealed record AssetInfo(
    AssetId Id,
    string Name,
    string? Description,
    AssetVersion LatestVersion,
    DateTimeOffset UpdatedAt,
    SkillMetadata? Metadata = null,
    SkillSource? Source = null)
{
    public SkillMetadata MetadataOrEmpty => Metadata ?? SkillMetadata.Empty;
}

public sealed record AssetContent(
    AssetId Id,
    AssetVersion Version,
    string Name,
    string Content,
    DateTimeOffset PublishedAt,
    SkillMetadata? Metadata = null,
    SkillSource? Source = null)
{
    public SkillMetadata MetadataOrEmpty => Metadata ?? SkillMetadata.Empty;
}

/// <summary>
/// Was beim Einspielen eines Skills aus einem Paket passiert ist.
/// </summary>
/// <param name="ReplacedLocalEdit">
/// Die bisherige neueste Fassung stammte <b>nicht</b> aus einem Paket — jemand hat den Text
/// angepasst. Das Update wird trotzdem angehängt (die Historie behält beide), aber es wird
/// <b>gemeldet</b>: Ein still verdrängter Text wäre genau der Vertrauensbruch, den die
/// Versionierung verhindern soll.
/// </param>
public sealed record SkillPublication(
    string Name, AssetId Id, AssetVersion Version, bool ReplacedLocalEdit);

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
    /// <param name="description">
    /// Neue Beschreibung, oder <c>null</c> für „unverändert übernehmen".
    /// <para>
    /// Sie war bisher <b>nach dem Anlegen unveränderlich</b> — weder über die Oberfläche noch über
    /// die API. Das ist ausgerechnet das Feld, an dem ein Agent entscheidet, ob er einen Skill
    /// nimmt: Ein Tippfehler darin wäre dauerhaft gewesen. Sie hängt an der Version wie die übrigen
    /// Angaben, aus demselben Grund — wer sie ändert, ändert, wofür der Skill gehalten wird.
    /// </para>
    /// </param>
    Task<AssetVersion> PublishAsync(
        AssetId id, string content, SkillMetadata? metadata, CancellationToken ct,
        string? description = null);

    /// <summary>Legt ein neues Asset (Name + Beschreibung) als Version 1 an.</summary>
    Task<AssetId> CreateAsync(
        string name, string? description, string content, SkillMetadata? metadata, CancellationToken ct);

    /// <summary>Alle Versionen eines Skills, neueste zuerst — für Historie und Zurückschalten.</summary>
    Task<IReadOnlyList<AssetContent>> GetVersionsAsync(AssetId id, CancellationToken ct);

    /// <summary>
    /// Spielt einen Skill aus einem Paket ein: legt ihn unter <paramref name="name"/> an oder hängt
    /// eine neue Version an, wenn es ihn schon gibt.
    /// <para>
    /// Als eigene Operation und nicht als Zusatzparameter an <see cref="CreateAsync"/>, weil die
    /// Entscheidung „anlegen oder anhängen" eine Namenssuche braucht — die gehört in den Store, wo
    /// die Namen liegen, und nicht in jeden Aufrufer.
    /// </para>
    /// </summary>
    Task<SkillPublication> PublishFromPackageAsync(
        string name,
        string? description,
        string content,
        SkillMetadata? metadata,
        SkillSource source,
        CancellationToken ct);

    /// <summary>
    /// Skills, die aus diesem Paket stammen — für die <b>Ankündigung</b> vor dem Entfernen
    /// (ADR-0021, F5).
    /// <para>
    /// Gefunden wird über <em>irgendeine</em> Version mit dieser Herkunft, nicht nur über die
    /// neueste: Wer den Text angepasst hat, hat eine Version ohne Herkunft obenauf gelegt — der
    /// Skill kam trotzdem aus dem Paket. Genau diese Fälle sind die, die man vorher sehen muss;
    /// erkennbar sind sie daran, dass <see cref="AssetInfo.Source"/> leer ist.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AssetInfo>> ListFromPackageAsync(string packageId, CancellationToken ct);

    /// <summary>
    /// Entfernt alle Versionen der Skills aus diesem Paket und liefert deren Namen zurück
    /// (ADR-0021, F5).
    /// <para>
    /// <b>Die einzige Stelle, an der Historie verloren geht.</b> Sonst gilt append-only ohne
    /// Ausnahme. Der Grund steht im ADR: Ein verwaister Skill bliebe über <c>list_skills</c> für
    /// jeden Agenten sichtbar, während die Kennzeichnung nur ein Mensch in der Oberfläche sieht —
    /// eine Anleitung für Tools, die es nicht mehr gibt, ist schlimmer als keine. Die Auflage dazu
    /// ist, dass vorher angekündigt wird, was mitgeht (<see cref="ListFromPackageAsync"/>).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> DeleteFromPackageAsync(string packageId, CancellationToken ct);
}
