using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Upstream.OpenApi;
using Xunit;

namespace Bifrost.Upstream.Tests;

public class OpenApiSpecParserTests
{
    private const string PetstoreMini = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Petstore Mini", "version": "1.0.0" },
          "servers": [ { "url": "https://api.example.test/v2" } ],
          "paths": {
            "/pets": {
              "get": {
                "operationId": "listPets",
                "summary": "Listet Haustiere",
                "parameters": [
                  { "name": "limit", "in": "query", "required": false, "schema": { "type": "integer" } }
                ]
              },
              "post": {
                "operationId": "createPet",
                "requestBody": {
                  "required": true,
                  "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } }
                }
              }
            },
            "/pets/{petId}": {
              "parameters": [
                { "name": "petId", "in": "path", "required": true, "schema": { "type": "integer" } }
              ],
              "get": { "operationId": "getPet", "description": "Ein Haustier per Id" }
            }
          },
          "components": {
            "schemas": {
              "Pet": {
                "type": "object",
                "properties": { "name": { "type": "string" }, "tag": { "$ref": "#/components/schemas/Tag" } },
                "required": ["name"]
              },
              "Tag": { "type": "string" }
            }
          }
        }
        """;

    [Fact]
    public void Parses_operations_parameters_and_resolves_refs()
    {
        var (operations, serverUrl) = OpenApiSpecParser.Parse(PetstoreMini);

        serverUrl.Should().Be(new Uri("https://api.example.test/v2"));
        operations.Select(o => o.OperationId).Should().BeEquivalentTo("listPets", "createPet", "getPet");

        var listPets = operations.Single(o => o.OperationId == "listPets");
        listPets.HttpMethod.Should().Be("GET");
        listPets.Parameters.Should().ContainSingle(p => p.Name == "limit" && !p.Required);
        listPets.Description.Should().Be("Listet Haustiere");

        var getPet = operations.Single(o => o.OperationId == "getPet");
        getPet.Parameters.Should().ContainSingle(p =>
            p.Name == "petId" && p.Required && p.Location == OpenApiParameterLocation.Path);
        getPet.InputSchema.GetProperty("required")[0].GetString().Should().Be("petId");

        var createPet = operations.Single(o => o.OperationId == "createPet");
        createPet.HasBody.Should().BeTrue();
        var bodySchema = createPet.InputSchema.GetProperty("properties").GetProperty("body");
        bodySchema.GetProperty("required")[0].GetString().Should().Be("name", "$ref auf Pet ist aufgelöst");
        bodySchema.GetProperty("properties").GetProperty("tag").GetProperty("type").GetString()
            .Should().Be("string", "verschachtelte $refs sind aufgelöst");
        createPet.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("body", "required-Body wird Pflichtfeld");
    }

    [Theory]
    [InlineData("""kein json""", "YAML")]
    [InlineData("""{ "swagger": "2.0", "paths": {} }""", "Swagger 2.0")]
    [InlineData("""{ "info": {} }""", "Nur OpenAPI 3.x")]
    [InlineData("""{ "openapi": "3.1.0" }""", "paths")]
    [InlineData("""{ "openapi": "3.1.0", "paths": {} }""", "keine importierbaren Operationen")]
    public void Structural_problems_abort_with_precise_message(string spec, string expectedFragment)
    {
        var act = () => OpenApiSpecParser.Parse(spec);

        act.Should().Throw<OpenApiImportException>().WithMessage($"*{expectedFragment}*");
    }

    [Fact]
    public void Missing_operationId_names_the_operation()
    {
        var act = () => OpenApiSpecParser.Parse("""
            { "openapi": "3.0.0", "paths": { "/x": { "get": { "summary": "ohne id" } } } }
            """);

        act.Should().Throw<OpenApiImportException>().WithMessage("*GET /x*operationId*");
    }

    [Fact]
    public void Cookie_parameters_are_rejected()
    {
        var act = () => OpenApiSpecParser.Parse("""
            { "openapi": "3.0.0", "paths": { "/x": { "get": {
              "operationId": "op",
              "parameters": [ { "name": "session", "in": "cookie", "schema": { "type": "string" } } ] } } } }
            """);

        act.Should().Throw<OpenApiImportException>().WithMessage("*cookie*");
    }

    [Fact]
    public void Non_json_bodies_are_rejected_listing_found_types()
    {
        var act = () => OpenApiSpecParser.Parse("""
            { "openapi": "3.0.0", "paths": { "/upload": { "post": {
              "operationId": "upload",
              "requestBody": { "content": { "multipart/form-data": { "schema": { "type": "object" } } } } } } } }
            """);

        act.Should().Throw<OpenApiImportException>().WithMessage("*application/json*multipart/form-data*");
    }

    [Fact]
    public void External_refs_are_rejected()
    {
        var act = () => OpenApiSpecParser.Parse("""
            { "openapi": "3.0.0", "paths": { "/x": { "post": {
              "operationId": "op",
              "requestBody": { "content": { "application/json": { "schema": { "$ref": "other.json#/Pet" } } } } } } } }
            """);

        act.Should().Throw<OpenApiImportException>().WithMessage("*extern*");
    }

    [Fact]
    public void Cyclic_refs_are_rejected()
    {
        var act = () => OpenApiSpecParser.Parse("""
            { "openapi": "3.0.0",
              "paths": { "/x": { "post": {
                "operationId": "op",
                "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/A" } } } } } } },
              "components": { "schemas": { "A": { "$ref": "#/components/schemas/B" }, "B": { "$ref": "#/components/schemas/A" } } } }
            """);

        act.Should().Throw<OpenApiImportException>().WithMessage("*zyklisch*");
    }

    [Fact]
    public void Duplicate_operation_ids_are_rejected()
    {
        var act = () => OpenApiSpecParser.Parse("""
            { "openapi": "3.0.0", "paths": {
              "/a": { "get": { "operationId": "op" } },
              "/b": { "get": { "operationId": "op" } } } }
            """);

        act.Should().Throw<OpenApiImportException>().WithMessage("*mehrfach*");
    }

    [Fact]
    public void Parameter_named_body_is_rejected()
    {
        var act = () => OpenApiSpecParser.Parse("""
            { "openapi": "3.0.0", "paths": { "/x": { "get": {
              "operationId": "op",
              "parameters": [ { "name": "body", "in": "query", "schema": { "type": "string" } } ] } } } }
            """);

        act.Should().Throw<OpenApiImportException>().WithMessage("*kollidiert*");
    }
}
