using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Xunit;

namespace Bifrost.Core.Tests.Configuration;

/// <summary>
/// Die Wertetypen müssen den JSON-Weg überstehen — sie gehen über die API hinaus und kommen von
/// dort zurück.
/// <para>
/// <b>Gefunden über den Rückweg eines Policy Packs (M6/WP6.1):</b> Ein
/// <see cref="NamespacedToolName"/> kam als <c>null</c> zurück, weil System.Text.Json bei Structs
/// den impliziten parameterlosen Konstruktor nimmt und <c>Value</c> keinen Setter hat. Kein Fehler,
/// keine Ausnahme — der Wert war einfach weg.
/// </para>
/// <para>
/// <b>Warum das mehr ist als ein Schönheitsfehler:</b> Ein Grant mit
/// <c>Scope.Tool = "github__read"</c> wird beim Lesen zu einem Grant ohne Werkzeugeinschränkung.
/// Aus „darf genau dieses eine Werkzeug" wird „darf alles". Die Einschränkung verschwindet lautlos
/// in die falsche Richtung.
/// </para>
/// </summary>
public sealed class ValueTypeJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_tool_name_survives_the_json_round_trip()
    {
        var zurueck = Roundtrip(new NamespacedToolName("github__read"));

        zurueck.Value.Should().Be("github__read");
    }

    [Fact]
    public void A_capability_id_survives_the_json_round_trip()
    {
        var zurueck = Roundtrip(new CapabilityId("github:read:v1"));

        zurueck.Value.Should().Be("github:read:v1");
    }

    /// <summary>
    /// Der Fall, um den es wirklich geht: die Einschränkung <em>innerhalb</em> einer Berechtigung.
    /// Ein Test nur auf den nackten Wertetyp hätte den Schaden nicht benannt.
    /// </summary>
    [Fact]
    public void A_grant_restricted_to_one_tool_stays_restricted()
    {
        var grant = new Grant(
            new PermissionScope(null, new NamespacedToolName("github__read")), [ToolAction.UseTool]);

        var zurueck = Roundtrip(grant);

        zurueck.Scope.Tool.Should().NotBeNull("sonst wird aus „genau dieses Werkzeug\" stillschweigend „alles\"");
        zurueck.Scope.Tool!.Value.Value.Should().Be("github__read");
    }

    /// <summary>
    /// Die positionalen <c>record struct</c>s waren nie betroffen. Der Test hält fest, dass der
    /// Unterschied bekannt ist — sonst wirkt der <c>[JsonConstructor]</c> an den anderen beliebig.
    /// </summary>
    [Fact]
    public void A_positional_record_struct_was_never_affected()
    {
        var id = new ServerId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        Roundtrip(id).Value.Should().Be(id.Value);
    }

    private static T Roundtrip<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Web), Web)!;
}
