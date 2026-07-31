using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Invocation;
using Bifrost.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Die Größengrenze für Skills — <b>eine</b> Zahl für beide Wege.
/// <para>
/// Der Grund für die Grenze: <c>read_skill</c> liefert den Text vollständig in den Kontext eines
/// Agenten. Ein unbegrenzter Skill hebelt genau das Argument aus, für das die Meta-Tools existieren.
/// </para>
/// </summary>
public sealed class SkillSizeLimitTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public SkillSizeLimitTests(GatewayFixture gw) => _gw = gw;

    private IAssetStore Assets => _gw.Services.GetRequiredService<IAssetStore>();

    private static string TooLong => new('x', SkillLimits.MaxContentBytes + 1);

    [Fact]
    public async Task An_oversized_skill_is_refused_when_it_is_created()
    {
        var act = async () => await Assets.CreateAsync(
            $"zu-gross-{Guid.NewGuid():N}", null, TooLong, null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*KB*", "die Meldung soll sagen, wie groß erlaubt ist");
    }

    [Fact]
    public async Task An_oversized_version_is_refused_when_it_is_published()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await Assets.CreateAsync($"waechst-{Guid.NewGuid():N}", null, "klein", null, ct);

        var act = async () => await Assets.PublishAsync(id, TooLong, null, ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task An_oversized_skill_from_a_package_is_refused_the_same_way()
    {
        var act = async () => await Assets.PublishFromPackageAsync(
            $"com.example.gross{Guid.NewGuid():N}"[..28] + "/x", null, TooLong, null,
            new SkillSource("com.example.gross", "1.0.0"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "derselbe Weg in denselben Kontext — dieselbe Grenze");
    }

    [Fact]
    public async Task A_skill_at_the_limit_is_still_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var gerade = new string('x', SkillLimits.MaxContentBytes);

        var id = await Assets.CreateAsync($"grenzwertig-{Guid.NewGuid():N}", null, gerade, null, ct);

        (await Assets.GetAsync(id, null, ct)).Content.Length.Should().Be(SkillLimits.MaxContentBytes);
    }

    /// <summary>
    /// Gemessen wird in <b>Bytes</b>, nicht in Zeichen. Ein Text aus Umlauten oder CJK ist in UTF-8
    /// länger als seine Zeichenzahl — würde man Zeichen zählen, wäre die Grenze für solche Texte
    /// stillschweigend höher.
    /// </summary>
    [Fact]
    public async Task The_limit_counts_bytes_not_characters()
    {
        var zeichen = SkillLimits.MaxContentBytes / 2;
        var text = new string('ä', zeichen + 1);
        Encoding.UTF8.GetByteCount(text).Should().BeGreaterThan(SkillLimits.MaxContentBytes);

        var act = async () => await Assets.CreateAsync(
            $"umlaute-{Guid.NewGuid():N}", null, text, null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Die Grenze wirkt beim <b>Schreiben</b>. Ein Skill aus der Zeit davor wird weiter vollständig
    /// ausgeliefert — ihn stillschweigend abzuschneiden hieße, einem Agenten eine halbe Anweisung zu
    /// geben. Deshalb schreibt dieser Test am Store vorbei direkt in die Datenbank.
    /// </summary>
    [Fact]
    public async Task An_already_stored_oversized_skill_is_still_delivered_in_full()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"altlast-{Guid.NewGuid():N}";
        var text = new string('y', SkillLimits.MaxContentBytes + 100);

        var factory = _gw.Services.GetRequiredService<IDbContextFactory<BifrostDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            db.Assets.Add(new AssetRow
            {
                Id = Guid.NewGuid(),
                Version = 1,
                Name = name,
                Content = text,
                PublishedAt = DateTimeOffset.UnixEpoch,
            });
            await db.SaveChangesAsync(ct);
        }

        var caller = await _gw.SeedAdminAsync($"altlast-{Guid.NewGuid():N}");
        var result = await _gw.Services.GetRequiredService<MetaToolService>().ExecuteAsync(
            caller.Identity, CallOrigin.Mcp, MetaToolService.ReadSkillName,
            JsonSerializer.SerializeToElement(new { name }), ct);

        result.Status.Should().Be(InvocationStatus.Success);
        result.Content!.Value.GetProperty("content").GetString().Should().Be(text);
    }
}
