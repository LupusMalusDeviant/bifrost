namespace McpMcp.Server;

/// <summary>
/// Wie lange ein Client unsere Listen (<c>tools/list</c>, <c>resources/list</c>,
/// <c>prompts/list</c>) für frisch halten darf — der Cache-Hinweis <c>ttlMs</c> aus der
/// Spec-Revision 2026-07-28 (SEP-2549).
/// <para>
/// <b>Warum das hier eine Betriebsentscheidung ist:</b> Im stateless Betrieb gibt es keine
/// <c>tools/list_changed</c>-Benachrichtigung mehr — die Frist ist der <em>einzige</em> Weg, auf dem
/// ein angeschlossener Agent von einem neuen Werkzeug erfährt. Kurz heißt: schnelle Sichtbarkeit,
/// mehr Listenabrufe. Lang heißt: ruhiger Verkehr, aber ein frisch freigeschaltetes Werkzeug bleibt
/// bis zu einer Frist lang unsichtbar. Eine Minute ist der Kompromiss, den ein Betreiber
/// verschieben können muss.
/// </para>
/// </summary>
/// <param name="ListTimeToLive">
/// Gültigkeitsdauer. <see cref="TimeSpan.Zero"/> bedeutet „kein Hinweis": Der Client behandelt die
/// Antwort dann als sofort veraltet und holt sie, wann immer er sie braucht.
/// </param>
public sealed record McpCacheOptions(TimeSpan ListTimeToLive)
{
    public static McpCacheOptions Default { get; } = new(TimeSpan.FromMinutes(1));
}
