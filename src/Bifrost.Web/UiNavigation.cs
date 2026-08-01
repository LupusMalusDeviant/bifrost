namespace Bifrost.Web;

/// <summary>
/// Die beiden Darstellungsstufen der Oberflaeche (WP4.5).
/// <para>
/// <b>Der Modus ist reine Darstellung.</b> Er entscheidet, was im Menue angeboten wird — nicht,
/// was jemand darf. Wer eine Advanced-Seite direkt aufruft, bekommt sie, sofern seine Rolle es
/// erlaubt. Deshalb steht dieser Wert nirgends in einer Autorisierungsentscheidung: nicht in einer
/// Policy, nicht in einem <c>[Authorize]</c>, nicht im Router. Ein Modus, der wie eine Berechtigung
/// aussieht, wird irgendwann fuer eine gehalten.
/// </para>
/// </summary>
public enum UiMode
{
    /// <summary>Der Kern: was den Betrieb traegt.</summary>
    Basic = 0,

    /// <summary>Zusaetzlich die Feinsteuerung. Nimmt nichts weg, legt nur mehr offen.</summary>
    Advanced = 1,
}

/// <summary>In welcher Stufe ein Menuepunkt angeboten wird.</summary>
public enum UiNavSection
{
    Basic = 0,
    Advanced = 1,
}

/// <summary>
/// Ein Eintrag der Seitennavigation.
/// </summary>
/// <param name="Route">Der Ziel-Pfad. Bei Untereintraegen mit Sprungmarke (<c>#…</c>).</param>
/// <param name="Label">Die Beschriftung.</param>
/// <param name="Policy">
/// Die Policy, die die Zielseite traegt. Sie wird beim Rendern an <c>AuthorizeView</c> gereicht —
/// die Rollenpruefung macht also der echte Autorisierungsdienst, nicht dieses Modell. Das Feld ist
/// hier, damit ein Test es gegen das <c>[Authorize]</c> der Seite haelt: Ein Menuepunkt, der eine
/// andere Policy behauptet als seine Seite, ist entweder ein toter Link oder ein Leck.
/// </param>
/// <param name="Section">Basic oder Advanced — ausschliesslich Darstellung.</param>
/// <param name="Parent">
/// Bei einem Untereintrag der Pfad des Elterneintrags. Untereintraege zeigen auf Abschnitte
/// innerhalb einer Seite, nicht auf eigene Routen.
/// </param>
public sealed record UiNavEntry(
    string Route,
    string Label,
    string Policy,
    UiNavSection Section,
    string? Parent = null)
{
    /// <summary>Der Pfad ohne Sprungmarke — das, was der Router sieht.</summary>
    public string Path => Route.Split('#')[0];

    /// <summary>Ein Untereintrag zeigt in eine Seite hinein und ist selbst keine Route.</summary>
    public bool IsSubEntry => Parent is not null;
}

/// <summary>
/// Die Informationsarchitektur der Oberflaeche an einer Stelle (WP4.5).
/// <para>
/// Warum ein Modell und nicht einfach Markup im Layout: Die Zuordnung „welche Seite gehoert in
/// welche Stufe" ist die eigentliche Aussage dieses Pakets, und sie muss pruefbar sein. Als
/// Razor-Markup waere sie nur mit einem Browser zu pruefen; als Liste haelt ein Test sie gegen die
/// <c>[Authorize]</c>-Attribute der Seiten.
/// </para>
/// <para>
/// <b>Hier steht keine Rollenregel.</b> Jeder Eintrag nennt nur die Policy seiner Seite; wer sie
/// erfuellt, entscheidet der Autorisierungsdienst. Eine zweite Fassung der Rollenlogik im
/// Navigationsmodell waere eine zweite Wahrheit — und die falsche waere die, die sichtbar ist.
/// </para>
/// </summary>
public static class UiNavigation
{
    /// <summary>
    /// Name des Cookies, in dem die gewaehlte Stufe liegt.
    /// <para>
    /// Bewusst ohne <c>HttpOnly</c> und vom Browser aus beschreibbar: Der Wert traegt keine
    /// Sicherheitsaussage. Wer ihn faelscht, sieht ein anderes Menue und sonst nichts — das ist
    /// der Beleg dafuer, dass der Modus keine Berechtigungsgrenze ist, und kein Versehen.
    /// </para>
    /// </summary>
    public const string ModeCookieName = "bifrost-ui-mode";

    /// <summary>Der Wert im Cookie fuer <see cref="UiMode.Advanced"/>.</summary>
    public const string AdvancedCookieValue = "advanced";

    /// <summary>
    /// Alle Menuepunkte in Anzeigereihenfolge.
    /// <para>
    /// Die Gruppen sind nach Aufgabe geschnitten, nicht nach Rolle: „Betrieb" ist die taegliche
    /// Schleife, „Feinsteuerung" und „Verwaltung" sind das, was man selten und dann bewusst
    /// anfasst. Ein Schnitt nach Rolle wuerde den Modus zur Berechtigung machen — genau das soll
    /// hier nicht passieren.
    /// </para>
    /// </summary>
    public static IReadOnlyList<UiNavEntry> All { get; } =
    [
        // ── Basic: die Aufgaben, die den Betrieb tragen ──────────────────────────────────────
        new("/", "Dashboard", UiPolicies.Authenticated, UiNavSection.Basic),
        new("/servers", "Server", UiPolicies.Operator, UiNavSection.Basic),
        // „Agents" aus dem Pflichtenheft: Agenten-Identitaeten, ihre Rollen und ihre API-Keys.
        // Die Seite heisst historisch /rbac; die Beschriftung nennt die Sache, nicht das Kuerzel.
        new("/rbac", "Agenten & Keys", UiPolicies.Admin, UiNavSection.Basic),
        new("/tools", "Tools", UiPolicies.Authenticated, UiNavSection.Basic),
        new("/approvals", "Freigaben", UiPolicies.Operator, UiNavSection.Basic),
        new("/logs", "Audit-Log", UiPolicies.Authenticated, UiNavSection.Basic),

        // ── Advanced: Feinsteuerung ──────────────────────────────────────────────────────────
        new("/packages", "Connector-Pakete", UiPolicies.Admin, UiNavSection.Advanced),
        // Untereintraege zeigen auf Abschnitte IN einer Seite. Sie sind der Ersatz fuer Seiten,
        // die es nicht gibt: Zugriffsfreigaben und Herausgeber-Stufen wohnen im Paket-Bildschirm.
        new("/packages#grants", "Zugriffs-Freigaben (WASI)", UiPolicies.Admin, UiNavSection.Advanced, Parent: "/packages"),
        new("/packages#publishers", "Herausgeber & Stufen", UiPolicies.Admin, UiNavSection.Advanced, Parent: "/packages"),
        new("/guardrails", "Guardrails", UiPolicies.Admin, UiNavSection.Advanced),
        new("/profiles", "Profile & Token", UiPolicies.Admin, UiNavSection.Advanced),
        new("/servers#erweitert", "Tool-Pins & OAuth", UiPolicies.Operator, UiNavSection.Advanced, Parent: "/servers"),
        new("/tasks", "Vorgänge", UiPolicies.Operator, UiNavSection.Advanced),
        new("/assets", "Skills & Assets", UiPolicies.Admin, UiNavSection.Advanced),
        new("/webhooks", "Webhooks", UiPolicies.Admin, UiNavSection.Advanced),
        new("/users", "UI-Nutzer", UiPolicies.Admin, UiNavSection.Advanced),
        new("/operations", "Betrieb & Diagnose", UiPolicies.Admin, UiNavSection.Advanced),
    ];

    /// <summary>
    /// Die Policy der Ueberschrift „Betrieb".
    /// <para>
    /// Sie muss von genau den Rollen erfuellbar sein, die mindestens einen Basic-Eintrag sehen —
    /// sonst stuende irgendwo eine Ueberschrift ohne Inhalt oder ein Eintrag ohne Ueberschrift.
    /// Der Wert ist deshalb keine Rollenregel, sondern eine Aussage ueber diese Liste, und ein
    /// Test haelt beides gegen den echten Autorisierungsdienst.
    /// </para>
    /// </summary>
    public const string BasicHeadingPolicy = UiPolicies.Authenticated;

    /// <summary>
    /// Die Policy der Ueberschrift „Feinsteuerung" — dieselbe Zusage wie bei
    /// <see cref="BasicHeadingPolicy"/>, fuer die Advanced-Gruppe.
    /// </summary>
    public const string AdvancedHeadingPolicy = UiPolicies.Operator;

    /// <summary>Die Eintraege einer Stufe in Anzeigereihenfolge — ohne Rollenfilter.</summary>
    public static IReadOnlyList<UiNavEntry> ForSection(UiNavSection section)
        => [.. All.Where(e => e.Section == section)];

    /// <summary>
    /// Die Eintraege, die eine Stufe anbietet — <b>ohne</b> Rollenfilter.
    /// <para>
    /// <see cref="UiMode.Advanced"/> liefert alles, <see cref="UiMode.Basic"/> die Teilmenge.
    /// Dass es eine Teilmenge ist und keine andere Menge, ist die zentrale Zusage dieses Pakets:
    /// Ein Moduswechsel kann nichts hinzufuegen, was nicht ohnehin da waere.
    /// </para>
    /// </summary>
    public static IReadOnlyList<UiNavEntry> ForMode(UiMode mode)
        => mode is UiMode.Advanced
            ? All
            : [.. All.Where(e => e.Section is UiNavSection.Basic)];

    /// <summary>
    /// Die Basisaufgaben, die ohne eine Advanced-Seite auskommen muessen (DoD des Pflichtenhefts),
    /// je mit der Route, auf der sie erledigt werden.
    /// <para>
    /// Die Liste steht hier und nicht nur im Test, weil sie eine Zusage an die Nutzer ist: Wer
    /// diese sechs Dinge tun will, kommt im Basic-Modus an. Ein Test haelt sie gegen
    /// <see cref="All"/> — rutscht eine dieser Routen nach Advanced, wird der Lauf rot.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Task, string Route)> BasicTasks { get; } =
    [
        ("Sehen, ob Gateway und Upstreams laufen", "/"),
        ("Einen MCP-Server anschließen, aktivieren, abschalten", "/servers"),
        ("Ein Tool finden und testweise aufrufen", "/tools"),
        ("Eine wartende Freigabe erteilen oder ablehnen", "/approvals"),
        ("Nachsehen, wer was aufgerufen hat", "/logs"),
        ("Einen Agenten mit Rolle und API-Key ausstellen", "/rbac"),
    ];
}
