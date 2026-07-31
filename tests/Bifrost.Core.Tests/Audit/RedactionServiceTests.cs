using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Audit;
using Xunit;

namespace Bifrost.Core.Tests.Audit;

public class RedactionServiceTests
{
    private static readonly NamespacedToolName Tool = NamespacedToolName.Create("srv", "tool");

    private readonly RedactionService _service = new();

    private JsonElement Redact(string json, NamespacedToolName? tool = null)
        => _service.RedactArguments(tool ?? Tool, JsonSerializer.Deserialize<JsonElement>(json));

    [Theory]
    [InlineData("password")]
    [InlineData("Passwort")]
    [InlineData("api_token")]
    [InlineData("clientSecret")]
    [InlineData("ApiKey")]
    [InlineData("AUTHORIZATION")]
    [InlineData("db_credential")]
    [InlineData("ssh_key")]
    public void Default_secret_property_names_are_masked(string property)
    {
        var result = Redact($$"""{"{{property}}":"geheim123"}""");

        result.GetProperty(property).GetString().Should().Be(RedactionService.Mask);
    }

    [Fact]
    public void Non_secret_properties_stay_untouched()
    {
        var result = Redact("""{"message":"hallo","count":3,"active":true}""");

        result.GetProperty("message").GetString().Should().Be("hallo");
        result.GetProperty("count").GetInt32().Should().Be(3);
        result.GetProperty("active").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Nested_objects_and_arrays_are_redacted_recursively()
    {
        var result = Redact("""
            {
              "config": { "connection": { "password": "p", "host": "h" } },
              "items": [ { "token": "t1", "name": "a" }, { "token": "t2", "name": "b" }, null ],
              "note": "ok"
            }
            """);

        result.GetProperty("config").GetProperty("connection").GetProperty("password").GetString()
            .Should().Be(RedactionService.Mask);
        result.GetProperty("config").GetProperty("connection").GetProperty("host").GetString().Should().Be("h");
        foreach (var item in result.GetProperty("items").EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
        {
            item.GetProperty("token").GetString().Should().Be(RedactionService.Mask);
            item.GetProperty("name").GetString().Should().NotBe(RedactionService.Mask);
        }
    }

    [Fact]
    public void Secret_values_of_any_shape_are_replaced_entirely()
    {
        var result = Redact("""{"secret": {"inner":"x","deep":{"a":1}}, "tokens": ["t1","t2"]}""");

        result.GetProperty("secret").GetString().Should().Be(RedactionService.Mask, "auch Objekt-Werte werden komplett ersetzt");
        result.GetProperty("tokens").GetString().Should().Be(RedactionService.Mask, "auch Array-Werte unter Secret-Namen");
    }

    [Fact]
    public void Tool_specific_rules_apply_only_to_that_tool()
    {
        var otherTool = NamespacedToolName.Create("srv", "other");
        _service.SetToolRules(Tool, ["kontostand"]);

        var redacted = Redact("""{"kontostand":"1234","notiz":"harmlos"}""");
        redacted.GetProperty("kontostand").GetString()
            .Should().Be(RedactionService.Mask, "Per-Tool-Regel greift für dieses Tool (FR-24)");
        redacted.GetProperty("notiz").GetString()
            .Should().Be("harmlos", "was weder Default- noch Tool-Regel matcht, bleibt stehen");
        Redact("""{"kontostand":"1234"}""", otherTool).GetProperty("kontostand").GetString()
            .Should().Be("1234", "anderes Tool bleibt bei den Default-Regeln");
    }

    [Fact]
    public void Configured_rules_from_the_store_are_applied()
    {
        // FR-24 verlangt konfigurierbare Regeln — bis dahin war SetToolRules nur aus Tests erreichbar.
        var rules = new FakeRedactionRules();
        rules.Set(Tool, ["iban"]);
        var service = new RedactionService(rules);

        var redacted = service.RedactArguments(
            Tool, JsonSerializer.Deserialize<JsonElement>("""{"iban":"DE123","notiz":"harmlos"}"""));

        redacted.GetProperty("iban").GetString().Should().Be(RedactionService.Mask);
        redacted.GetProperty("notiz").GetString().Should().Be("harmlos");
    }

    private sealed class FakeRedactionRules : IRedactionRules
    {
        private readonly Dictionary<NamespacedToolName, IReadOnlyList<string>> _rules = [];

        public IReadOnlyDictionary<NamespacedToolName, IReadOnlyList<string>> All => _rules;

        public void Set(NamespacedToolName tool, IReadOnlyList<string> patterns) => _rules[tool] = patterns;

        public IReadOnlyList<string>? GetPatterns(NamespacedToolName tool) => _rules.GetValueOrDefault(tool);

        public Task SetAsync(NamespacedToolName tool, IReadOnlyList<string>? patterns, CancellationToken ct)
        {
            _rules[tool] = patterns ?? [];
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(""" "nur-ein-string" """)]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void Scalar_arguments_pass_through_unchanged(string json)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = _service.RedactArguments(Tool, element);

        result.GetRawText().Should().Be(element.GetRawText());
    }

    [Fact]
    public void Undefined_arguments_pass_through()
    {
        var result = _service.RedactArguments(Tool, default);

        result.ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public void Array_root_with_nested_secrets_is_redacted()
    {
        var result = Redact("""[{"password":"p"}, "plain", 7]""");

        result[0].GetProperty("password").GetString().Should().Be(RedactionService.Mask);
        result[1].GetString().Should().Be("plain");
    }

    [Fact]
    public void Null_property_values_are_tolerated()
    {
        var result = Redact("""{"data": null, "password": null}""");

        result.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("password").GetString().Should().Be(RedactionService.Mask, "auch null-Secrets werden maskiert");
    }

    [Fact]
    public void Original_element_is_not_mutated()
    {
        var original = JsonSerializer.Deserialize<JsonElement>("""{"password":"geheim"}""");

        _ = _service.RedactArguments(Tool, original);

        original.GetProperty("password").GetString().Should().Be("geheim");
    }
}
