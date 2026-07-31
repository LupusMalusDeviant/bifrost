using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Approvals;
using Xunit;

namespace Bifrost.Core.Tests.Approvals;

/// <summary>
/// ADR-0012: Eine Freigabe bindet an genau einen Aufruf. Der Fingerprint entscheidet, ob ein
/// Retry als „derselbe Call" gilt — hier liegt die Sicherheitsgrenze.
/// </summary>
public class ApprovalFingerprintTests
{
    private static readonly IdentityId Caller = IdentityId.New();
    private static readonly NamespacedToolName Tool = NamespacedToolName.Create("files", "delete_file");

    private static JsonElement Args(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Same_call_yields_the_same_fingerprint()
    {
        var a = ApprovalFingerprint.Compute(Caller, Tool, Args("""{"path":"/tmp/x"}"""));
        var b = ApprovalFingerprint.Compute(Caller, Tool, Args("""{"path":"/tmp/x"}"""));

        a.Should().Be(b);
    }

    [Fact]
    public void Key_order_does_not_change_the_fingerprint()
    {
        // Sonst würde ein umsortiertes, aber identisches Argument-Objekt die Freigabe verfehlen.
        var a = ApprovalFingerprint.Compute(Caller, Tool, Args("""{"path":"/tmp/x","force":true}"""));
        var b = ApprovalFingerprint.Compute(Caller, Tool, Args("""{"force":true,"path":"/tmp/x"}"""));

        a.Should().Be(b);
    }

    [Fact]
    public void Different_arguments_yield_a_different_fingerprint()
    {
        // Der Kern der Sicherheit: Wer delete_file{/tmp/x} freigibt, gibt nicht /etc/passwd frei.
        var approved = ApprovalFingerprint.Compute(Caller, Tool, Args("""{"path":"/tmp/x"}"""));
        var other = ApprovalFingerprint.Compute(Caller, Tool, Args("""{"path":"/etc/passwd"}"""));

        approved.Should().NotBe(other);
    }

    [Fact]
    public void Different_caller_or_tool_yields_a_different_fingerprint()
    {
        var baseline = ApprovalFingerprint.Compute(Caller, Tool, Args("""{"path":"/tmp/x"}"""));

        ApprovalFingerprint.Compute(IdentityId.New(), Tool, Args("""{"path":"/tmp/x"}"""))
            .Should().NotBe(baseline);
        ApprovalFingerprint.Compute(Caller, NamespacedToolName.Create("files", "read_file"), Args("""{"path":"/tmp/x"}"""))
            .Should().NotBe(baseline);
    }
}
