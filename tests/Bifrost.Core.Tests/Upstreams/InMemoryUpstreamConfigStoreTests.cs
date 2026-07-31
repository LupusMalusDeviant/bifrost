using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Upstreams;
using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

public class InMemoryUpstreamConfigStoreTests
{
    private readonly InMemoryUpstreamConfigStore _store = new();
    private readonly ServerId _id = ServerId.New();

    [Fact]
    public async Task Append_assigns_sequential_versions()
    {
        var v1 = await _store.AppendVersionAsync(_id, TestData.StdioConfig("a"), TestContext.Current.CancellationToken);
        var v2 = await _store.AppendVersionAsync(_id, TestData.StdioConfig("b"), TestContext.Current.CancellationToken);

        v1.Should().Be(new ConfigVersionId(1));
        v2.Should().Be(new ConfigVersionId(2));
    }

    [Fact]
    public async Task GetVersion_returns_exact_config_or_null()
    {
        await _store.AppendVersionAsync(_id, TestData.StdioConfig("a"), TestContext.Current.CancellationToken);
        await _store.AppendVersionAsync(_id, TestData.StdioConfig("b"), TestContext.Current.CancellationToken);

        (await _store.GetVersionAsync(_id, new ConfigVersionId(1), TestContext.Current.CancellationToken))!.Slug.Should().Be("a");
        (await _store.GetVersionAsync(_id, new ConfigVersionId(3), TestContext.Current.CancellationToken)).Should().BeNull();
        (await _store.GetVersionAsync(ServerId.New(), new ConfigVersionId(1), TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task History_is_ordered_and_isolated_per_server()
    {
        var other = ServerId.New();
        await _store.AppendVersionAsync(_id, TestData.StdioConfig("a"), TestContext.Current.CancellationToken);
        await _store.AppendVersionAsync(other, TestData.StdioConfig("x"), TestContext.Current.CancellationToken);
        await _store.AppendVersionAsync(_id, TestData.StdioConfig("b"), TestContext.Current.CancellationToken);

        var history = await _store.GetHistoryAsync(_id, TestContext.Current.CancellationToken);

        history.Select(h => h.Config.Slug).Should().ContainInOrder("a", "b");
        (await _store.GetHistoryAsync(other, TestContext.Current.CancellationToken)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Remove_clears_history()
    {
        await _store.AppendVersionAsync(_id, TestData.StdioConfig(), TestContext.Current.CancellationToken);

        await _store.RemoveAsync(_id, TestContext.Current.CancellationToken);

        (await _store.GetHistoryAsync(_id, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }
}
