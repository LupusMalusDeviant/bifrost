using AwesomeAssertions;
using Bifrost.Upstream.OpenRpc;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>
/// Import eines OpenRPC-Dokuments, entlang der Fixtures aus `docs/spikes/openrpc-import.md`.
/// Der Spike nennt sechs Fälle und die Bedingung „Go erst bei fail-closed Referenzauflösung" —
/// hier stehen sie als Tests.
/// </summary>
public sealed class OpenRpcImportTests
{
    private const string ByName = """
    {
      "openrpc": "1.3.2",
      "info": { "title": "demo", "version": "1.0.0" },
      "methods": [
        {
          "name": "sum",
          "summary": "Addiert zwei Zahlen",
          "paramStructure": "by-name",
          "params": [
            { "name": "a", "required": true, "schema": { "type": "integer" } },
            { "name": "b", "required": true, "schema": { "type": "integer" } }
          ],
          "result": { "name": "summe", "schema": { "type": "integer" } }
        }
      ]
    }
    """;

    /// <summary>Fixture 1a: benannte Parameter werden ein Objekt-Schema mit Required-Liste.</summary>
    [Fact]
    public void By_name_methods_become_an_object_schema()
    {
        var methods = OpenRpcDocumentParser.Parse(ByName);

        methods.Should().ContainSingle();
        var method = methods[0];
        method.Name.Should().Be("sum");
        method.Description.Should().Be("Addiert zwei Zahlen");
        method.ParamStructure.Should().Be(ParamStructure.ByName);

        var schema = method.InputSchema;
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").GetProperty("a").GetProperty("type").GetString().Should().Be("integer");
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("a", "b");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse(
            "ein unbekanntes Feld soll im Gateway auffallen, nicht erst im Upstream");
    }

    /// <summary>
    /// Fixture 1b: Bei <c>by-position</c> ist die <b>Reihenfolge</b> der Descriptors der Vertrag.
    /// Sie muss erhalten bleiben — aus einem JSON-Objekt liesse sie sich später nicht zurückgewinnen.
    /// </summary>
    [Fact]
    public void By_position_methods_keep_the_descriptor_order()
    {
        var methods = OpenRpcDocumentParser.Parse("""
        {
          "methods": [
            {
              "name": "divide",
              "paramStructure": "by-position",
              "params": [
                { "name": "dividend", "schema": { "type": "number" } },
                { "name": "divisor", "schema": { "type": "number" } }
              ]
            }
          ]
        }
        """);

        methods[0].ParamStructure.Should().Be(ParamStructure.ByPosition);
        methods[0].ParameterOrder.Should().Equal("dividend", "divisor");
    }

    /// <summary>Fixture 2: Doppelte Methodennamen sind nicht auflösbar und werden abgewiesen.</summary>
    [Fact]
    public void Duplicate_method_names_are_refused()
    {
        var act = () => OpenRpcDocumentParser.Parse("""
        { "methods": [ { "name": "doppelt" }, { "name": "doppelt" } ] }
        """);

        act.Should().Throw<OpenRpcImportException>().WithMessage("*mehrfach*");
    }

    /// <summary>Fixture 3: Ein lokaler Zyklus muss auffallen, statt den Import hängen zu lassen.</summary>
    [Fact]
    public void A_local_reference_cycle_is_caught()
    {
        var act = () => OpenRpcDocumentParser.Parse("""
        {
          "components": { "contentDescriptors": { "A": { "$ref": "#/components/contentDescriptors/A" } } },
          "methods": [ { "name": "zyklus", "params": [ { "$ref": "#/components/contentDescriptors/A" } ] } ]
        }
        """);

        act.Should().Throw<OpenRpcImportException>().WithMessage("*yklisch*");
    }

    /// <summary>
    /// Fixture 4: Eine externe Referenz wird <b>nicht geladen</b>, sondern abgewiesen. Sie wäre ein
    /// zweiter, ungeprüfter Ladevorgang mitten in der Schemaverarbeitung.
    /// </summary>
    [Theory]
    [InlineData("https://example.org/schema.json")]
    [InlineData("file:///etc/passwd")]
    [InlineData("other.json#/definitions/X")]
    public void An_external_reference_is_refused_not_followed(string reference)
    {
        var act = () => OpenRpcDocumentParser.Parse($$"""
        { "methods": [ { "name": "extern", "params": [ { "$ref": "{{reference}}" } ] } ] }
        """);

        act.Should().Throw<OpenRpcImportException>().WithMessage("*Externe Referenz*");
    }

    /// <summary>Ein lokaler Verweis wird aufgelöst — sonst wäre das Dokument kaum benutzbar.</summary>
    [Fact]
    public void A_local_reference_resolves()
    {
        var methods = OpenRpcDocumentParser.Parse("""
        {
          "components": {
            "contentDescriptors": {
              "Id": { "name": "id", "required": true, "schema": { "type": "string" } }
            }
          },
          "methods": [ { "name": "get", "params": [ { "$ref": "#/components/contentDescriptors/Id" } ] } ]
        }
        """);

        methods[0].ParameterOrder.Should().Equal("id");
        methods[0].InputSchema.GetProperty("properties").GetProperty("id")
            .GetProperty("type").GetString().Should().Be("string");
    }

    [Fact]
    public void A_reference_into_nothing_is_refused()
    {
        var act = () => OpenRpcDocumentParser.Parse("""
        { "methods": [ { "name": "leer", "params": [ { "$ref": "#/gibt/es/nicht" } ] } ] }
        """);

        act.Should().Throw<OpenRpcImportException>().WithMessage("*ins Leere*");
    }

    [Theory]
    [InlineData("kein json")]
    [InlineData("{}")]
    [InlineData("""{ "methods": [] }""")]
    [InlineData("""{ "methods": [ { "summary": "ohne Namen" } ] }""")]
    public void A_broken_document_is_refused_with_a_reason(string document)
    {
        var act = () => OpenRpcDocumentParser.Parse(document);

        act.Should().Throw<OpenRpcImportException>();
    }

    /// <summary>Der Katalog bekommt Name, Beschreibung und das erzeugte Schema.</summary>
    [Fact]
    public void A_method_becomes_a_tool_descriptor()
    {
        var tool = OpenRpcDocumentParser.ToToolDescriptor(OpenRpcDocumentParser.Parse(ByName)[0]);

        tool.Name.Should().Be("sum");
        tool.Description.Should().Be("Addiert zwei Zahlen");
        tool.InputSchema.GetProperty("properties").EnumerateObject().Should().HaveCount(2);
    }
}
