using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace McpMcp.Core.Tests.Upstreams;

/// <summary>
/// Katalogpflege bei Upstreams, die sich <b>nicht mehr von selbst melden</b>.
/// <para>
/// Bis zur Spec-Revision <c>2025-11-25</c> war der Weg klar: Ein Upstream schickte
/// <c>tools/list_changed</c>, der Gateway hörte zu. Die Revision <c>2026-07-28</c> hat
/// unaufgeforderte Server-zu-Client-Nachrichten gestrichen — <b>ohne Ersatz wäre ein dort neu
/// hinzugekommenes Werkzeug für immer unsichtbar</b>, bis jemand in der Oberfläche „Neu einlesen"
/// drückt. Das ist ein Ausfall, den niemand bemerkt, weil nichts kaputt aussieht.
/// </para>
/// </summary>
public sealed class CatalogPollingTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeUpstreamConnector _connector = new();
    private readonly InMemoryUpstreamConfigStore _store;
    private readonly UpstreamSupervisor _supervisor;
    private readonly List<UpstreamChangedEventArgs> _events = [];

    public CatalogPollingTests()
    {
        _store = new InMemoryUpstreamConfigStore(_time);
        _supervisor = new UpstreamSupervisor(
            [_connector],
            _store,
            new SupervisorOptions
            {
                HealthCheckInterval = TimeSpan.FromSeconds(1),
                CatalogPollInterval = TimeSpan.FromSeconds(3),
            },
            _time,
            logger: null);
        _supervisor.Changed += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    private int InventoryEvents
    {
        get
        {
            lock (_events)
            {
                return _events.Count(e => e.Kind == UpstreamChangeKind.InventoryChanged);
            }
        }
    }

    [Fact]
    public async Task An_upstream_that_does_not_push_is_asked_again()
    {
        FakeUpstreamConnection? connection = null;
        _connector.DefaultBehavior = (id, _) =>
            connection = new FakeUpstreamConnection { Id = id, PushesCatalogChanges = false };

        var id = await _supervisor.AddAsync(TestData.StdioConfig("poll"), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        // Ein Werkzeug kommt beim Upstream dazu — ohne dass er es meldet.
        connection!.Inventory = TestData.InventoryWithTools("echo", "neu");

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => _supervisor.GetInventory(id)?.Tools.Count == 2,
            because: "ein Upstream ohne Benachrichtigungen muss turnusmaessig gefragt werden");

        _supervisor.GetInventory(id)!.Tools.Should().Contain(t => t.Name == "neu");
    }

    /// <summary>
    /// <b>Die andere Hälfte der Aussage:</b> Eine Abfrage, die nichts Neues findet, darf kein
    /// Katalog-Ereignis auslösen. Sonst liefe jede Minute ein „der Katalog hat sich geändert" durch
    /// das ganze System — samt <c>tools/list_changed</c> an jede Sitzung und einem neu gebauten
    /// Katalog für jede Identität. Aus einer Reparatur würde eine Dauerlast.
    /// </summary>
    [Fact]
    public async Task Polling_an_unchanged_catalog_stays_quiet()
    {
        FakeUpstreamConnection? connection = null;
        _connector.DefaultBehavior = (id, _) =>
            connection = new FakeUpstreamConnection { Id = id, PushesCatalogChanges = false };

        var id = await _supervisor.AddAsync(TestData.StdioConfig("quiet"), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var afterStartup = InventoryEvents;
        var discoveriesAtStart = connection!.DiscoverCalls;

        await TestData.WaitUntilAdvancingAsync(
            _time,
            () => connection.DiscoverCalls >= discoveriesAtStart + 2,
            because: "zwei Abfragerunden muessen wirklich gelaufen sein");

        InventoryEvents.Should().Be(afterStartup,
            "ein unveraenderter Katalog ist keine Aenderung");
    }

    /// <summary>
    /// Ein Upstream, der weiterhin von selbst meldet (alter Stand), wird <b>nicht</b> gefragt — die
    /// Abfrage ist ein Ersatz für eine fehlende Fähigkeit, kein Zusatz.
    /// </summary>
    [Fact]
    public async Task An_upstream_that_pushes_is_left_alone()
    {
        FakeUpstreamConnection? connection = null;
        _connector.DefaultBehavior = (id, _) =>
            connection = new FakeUpstreamConnection { Id = id, PushesCatalogChanges = true };

        var id = await _supervisor.AddAsync(TestData.StdioConfig("push"), TestContext.Current.CancellationToken);
        await TestData.WaitUntilAsync(() => _supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var discoveries = connection!.DiscoverCalls;
        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        connection.DiscoverCalls.Should().Be(discoveries,
            "wer seine Aenderungen meldet, muss nicht gefragt werden");
    }
}
