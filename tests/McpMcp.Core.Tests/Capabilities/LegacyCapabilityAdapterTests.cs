using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Capabilities;
using Xunit;

namespace McpMcp.Core.Tests.Capabilities;

/// <summary>
/// ADR-0015: Die bestehende Deskriptorwelt wird <b>verlustfrei</b> auf Capabilities projiziert. Hier
/// steht, was „verlustfrei" konkret heisst — und wo der Adapter absichtlich nichts behauptet.
/// </summary>
public sealed class LegacyCapabilityAdapterTests
{
    private static JsonElement Schema(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static CatalogEntry Entry(
        string name = "github__create_issue",
        CapabilityRisk risk = CapabilityRisk.Write,
        bool requiresApproval = true,
        CatalogEntryKind kind = CatalogEntryKind.Tool,
        string schema = """{"type":"object","properties":{"title":{"type":"string"}}}""")
        => new(
            new NamespacedToolName(name),
            ServerId.New(),
            "Legt ein Issue an",
            Schema(schema),
            kind,
            EstimatedSchemaTokens: 42,
            Risk: risk,
            RequiresApproval: requiresApproval);

    [Fact]
    public void Every_field_of_the_old_descriptor_survives_the_projection()
    {
        var entry = Entry();

        var capability = LegacyCapabilityAdapter.FromCatalogEntry(entry, UpstreamTransportKind.Stdio);

        capability.NativeName.Should().Be("create_issue", "der Upstream kennt sie ohne Namespace");
        capability.CatalogName.Should().Be(entry.Name);
        capability.Description.Should().Be(entry.Description);
        capability.Upstream.Should().Be(entry.Server);
        capability.Connector.Should().Be(UpstreamTransportKind.Stdio);
        capability.SideEffect.Should().Be(CapabilityRisk.Write, "der Risk ist die Seiteneffekt-Achse");
        capability.RequiresApproval.Should().BeTrue();
        capability.Input!.Provenance.Should().Be(SchemaProvenance.Native);
        capability.Input.Hash.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Der Adapter erfindet nichts. Kein bestehender Connector nennt ein Ausgabeschema, meldet
    /// Fortschritt oder kann paginieren — dann steht das auch so da. Ein Katalog, der Fähigkeiten
    /// behauptet, die der Upstream nicht hat, wäre schlimmer als einer, der zu wenig verspricht.
    /// </summary>
    [Fact]
    public void The_adapter_claims_nothing_the_upstream_did_not_say()
    {
        var capability = LegacyCapabilityAdapter.FromCatalogEntry(Entry(), UpstreamTransportKind.Cli);

        capability.Output.Should().BeNull("kein Connector liefert heute ein Ausgabeschema");
        capability.SupportsProgress.Should().BeFalse();
        capability.SupportsPagination.Should().BeFalse();
        capability.SupportsBinary.Should().BeFalse();
        capability.Execution.Should().Be(CapabilityExecution.Synchronous);
        capability.ExpectedDuration.Should().BeNull();
    }

    /// <summary>
    /// Lesen ist wiederholbar, Schreiben nicht — die Unterscheidung steckt schon im Risk und wird
    /// hier nur sichtbar gemacht.
    /// </summary>
    [Theory]
    [InlineData(CapabilityRisk.Read, CapabilityKind.Query, true)]
    [InlineData(CapabilityRisk.Write, CapabilityKind.Mutation, false)]
    [InlineData(CapabilityRisk.Destructive, CapabilityKind.Mutation, false)]
    [InlineData(CapabilityRisk.Privileged, CapabilityKind.Mutation, false)]
    public void Risk_drives_kind_and_idempotency(
        CapabilityRisk risk, CapabilityKind expectedKind, bool expectedIdempotent)
    {
        var capability = LegacyCapabilityAdapter.FromCatalogEntry(
            Entry(risk: risk), UpstreamTransportKind.StreamableHttp);

        capability.Kind.Should().Be(expectedKind);
        capability.Idempotent.Should().Be(expectedIdempotent);
    }

    /// <summary>
    /// Die stabile Id ist deterministisch und hängt <b>nicht</b> am Schema: Ein zusätzlicher
    /// Parameter am Upstream darf keine neue Id ergeben, sonst brächen RBAC-Grants und gepinnte
    /// Profile bei jeder Schema-Pflege. Sichtbar bleibt die Änderung über den Schema-Hash.
    /// </summary>
    [Fact]
    public void The_id_survives_a_schema_change_but_the_hash_does_not()
    {
        var server = ServerId.New();
        var before = new CatalogEntry(
            new NamespacedToolName("srv__tool"), server, "alt", Schema("""{"type":"object","properties":{"a":{"type":"string"}}}"""),
            CatalogEntryKind.Tool, 10);
        var after = before with
        {
            Description = "neuer Anzeigetext",
            InputSchema = Schema("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"integer"}}}"""),
        };

        var first = LegacyCapabilityAdapter.FromCatalogEntry(before, UpstreamTransportKind.OpenApi);
        var second = LegacyCapabilityAdapter.FromCatalogEntry(after, UpstreamTransportKind.OpenApi);

        second.Id.Should().Be(first.Id, "Beschreibung und Schema ändern die Id nicht");
        second.Input!.Hash.Should().NotBe(first.Input!.Hash, "die Vertragsänderung bleibt sichtbar");
    }

    /// <summary>Ein anderer Upstream ist eine andere Fähigkeit — ein anderer Transport nicht.</summary>
    [Fact]
    public void The_id_separates_upstreams_but_not_connectors()
    {
        var entry = Entry();
        var sameEverything = LegacyCapabilityAdapter.FromCatalogEntry(entry, UpstreamTransportKind.Stdio);
        var otherServer = LegacyCapabilityAdapter.FromCatalogEntry(
            entry with { Server = ServerId.New() }, UpstreamTransportKind.Stdio);

        otherServer.Id.Should().NotBe(sameEverything.Id);
        LegacyCapabilityAdapter.FromCatalogEntry(entry, UpstreamTransportKind.Cli).Id
            .Should().Be(sameEverything.Id, "die Transportart gehoert nicht zur Id — die ServerId ist schon eindeutig");
        LegacyCapabilityAdapter.FromCatalogEntry(entry, UpstreamTransportKind.Stdio).Id
            .Should().Be(sameEverything.Id, "die Ableitung ist deterministisch");
    }

    /// <summary>Ein Aufruf ohne Argumente hat kein Schema — und das ist etwas anderes als „unbekannt".</summary>
    [Fact]
    public void An_empty_schema_means_no_arguments()
    {
        var capability = LegacyCapabilityAdapter.FromCatalogEntry(
            Entry(schema: """{"type":"object","properties":{}}"""), UpstreamTransportKind.Wasi);

        capability.Input!.Provenance.Should().Be(SchemaProvenance.None);
    }

    [Theory]
    [InlineData(CatalogEntryKind.Resource, CapabilityKind.Resource)]
    [InlineData(CatalogEntryKind.Prompt, CapabilityKind.Prompt)]
    public void Resources_and_prompts_keep_their_kind(CatalogEntryKind kind, CapabilityKind expected)
        => LegacyCapabilityAdapter.FromCatalogEntry(Entry(kind: kind), UpstreamTransportKind.Stdio)
            .Kind.Should().Be(expected);

    /// <summary>
    /// Die Freigabe der Arten hängt an ADR-0019, nicht am Vokabular: Tasks sind seit dem
    /// 2026-07-26 anbietbar, Events und Subscriptions nicht — EventV1 ist vertagt. Wer sie
    /// trotzdem nennt, bekommt eine Begründung statt stillem Weglassen.
    /// </summary>
    [Fact]
    public void Only_kinds_with_a_working_path_are_publicly_offered()
    {
        CapabilityKinds.IsPubliclyOffered(CapabilityKind.Task)
            .Should().BeTrue("die Task-Persistenz steht (ADR-0019)");
        CapabilityKinds.IsPubliclyOffered(CapabilityKind.Event).Should().BeFalse();
        CapabilityKinds.IsPubliclyOffered(CapabilityKind.Subscription).Should().BeFalse();
        CapabilityKinds.IsPubliclyOffered(CapabilityKind.AgentDelegation).Should().BeFalse();

        CapabilityKinds.WhyNotOffered(CapabilityKind.Event).Should().Contain("ADR-0019");
        CapabilityKinds.WhyNotOffered(CapabilityKind.AgentDelegation).Should().Contain("A2A");
        CapabilityKinds.WhyNotOffered(CapabilityKind.Query).Should().BeNull();
    }

    /// <summary>
    /// Die Ergebnishülle unterscheidet, was vorher alles Text war: Ein Task, ein Artifact-Verweis
    /// und ein strukturierter Fehler sind keine Varianten desselben Strings.
    /// </summary>
    [Fact]
    public void The_result_wrapper_keeps_the_kinds_apart()
    {
        var taskId = Guid.NewGuid();
        CapabilityResultV1.Accepted(taskId).Kind.Should().Be(CapabilityResultKind.Task);
        CapabilityResultV1.Accepted(taskId).TaskId.Should().Be(taskId);
        CapabilityResultV1.FromText("hallo").Kind.Should().Be(CapabilityResultKind.Text);
        CapabilityResultV1.ArtifactRef(new Uri("mcpmcp://artifact/1")).Kind
            .Should().Be(CapabilityResultKind.Artifact);

        var failed = CapabilityResultV1.Failed(
            new CapabilityError("upstream-error", "E42", "kaputt", Retryable: true));
        failed.Kind.Should().Be(CapabilityResultKind.Error);
        failed.Error!.GatewayCode.Should().Be("upstream-error");
        failed.Error.ConnectorCode.Should().Be("E42", "der Upstream-Code bleibt getrennt vom stabilen");
        failed.Error.Retryable.Should().BeTrue();

        var truncated = CapabilityResultV1.FromText("gekürzt", new ResultTruncation(1000, 100));
        truncated.Truncation!.OriginalChars.Should().Be(1000, "Truncation ist strukturiert, kein Textsuffix");
    }

    /// <summary>
    /// Die Gateway-Codes sind Teil des öffentlichen Vertrags. Sie hier festzunageln ist der Punkt:
    /// Vorher stand die Lage nur im Meldungstext, der sich mit jeder Textpflege ändert.
    /// </summary>
    [Theory]
    [InlineData(InvocationStatus.Success, "ok", false)]
    [InlineData(InvocationStatus.Denied, "denied", false)]
    [InlineData(InvocationStatus.ValidationFailed, "invalid-arguments", false)]
    [InlineData(InvocationStatus.ToolNotFound, "not-found", false)]
    [InlineData(InvocationStatus.Timeout, "timeout", true)]
    [InlineData(InvocationStatus.UpstreamError, "upstream-error", true)]
    [InlineData(InvocationStatus.GuardBlocked, "guard-blocked", false)]
    [InlineData(InvocationStatus.ApprovalRequired, "approval-required", false)]
    public void Every_status_has_a_stable_code_and_a_retry_verdict(
        InvocationStatus status, string expectedCode, bool expectedRetryable)
    {
        CapabilityResultMapper.GatewayCodeFor(status).Should().Be(expectedCode);
        CapabilityResultMapper.IsRetryable(status).Should().Be(expectedRetryable);
    }

    /// <summary>
    /// Ein blockiertes Ergebnis ist nicht wiederholbar — der Upstream-Call ist da schon gelaufen,
    /// der Seiteneffekt eingetreten. Ein Retry legte dasselbe Issue ein zweites Mal an.
    /// </summary>
    [Fact]
    public void A_blocked_result_is_not_retryable_because_the_call_already_ran()
    {
        var blocked = new ToolInvocationResult(
            InvocationStatus.GuardBlocked, null, "Zugangsdaten im Ergebnis", TimeSpan.Zero);

        var capability = CapabilityResultMapper.From(blocked);

        capability.Kind.Should().Be(CapabilityResultKind.Error);
        capability.Error!.Retryable.Should().BeFalse(
            "der Seiteneffekt ist eingetreten — Wiederholen wäre ein zweiter");
        capability.Error.GatewayCode.Should().Be("guard-blocked");
    }

    /// <summary>
    /// Ein freigabepflichtiger Aufruf ist kein Fehler, sondern ein Vorgang. Genau hier treffen sich
    /// ADR-0015 und ADR-0019: Die Id ist maschinenlesbar, nicht in deutscher Prosa versteckt.
    /// </summary>
    [Fact]
    public void An_approval_becomes_a_task_result_not_an_error()
    {
        var taskId = Guid.NewGuid();
        var pending = new ToolInvocationResult(
            InvocationStatus.ApprovalRequired, null, "Freigabe angefordert …", TimeSpan.Zero,
            TaskId: taskId);

        var capability = CapabilityResultMapper.From(pending);

        capability.Kind.Should().Be(CapabilityResultKind.Task);
        capability.TaskId.Should().Be(taskId);
        capability.Error.Should().BeNull("ein Vorgang ist kein Fehler");
    }

    /// <summary>Erfolg reicht die Nutzlast des Upstreams durch — mitsamt strukturierter Kürzung.</summary>
    [Fact]
    public void Success_passes_the_payload_and_the_truncation_through()
    {
        var content = Schema("""{"ok":true}""");
        var result = new ToolInvocationResult(
            InvocationStatus.Success, content, null, TimeSpan.Zero, new ResultTruncation(900, 100));

        var capability = CapabilityResultMapper.From(result);

        capability.Kind.Should().Be(CapabilityResultKind.Structured);
        capability.Data!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();
        capability.Truncation!.OriginalChars.Should().Be(900);
    }
}
