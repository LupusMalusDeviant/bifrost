using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Bifrost.Abstractions;

namespace Bifrost.Core.Tests.Upstreams;

internal static class TestData
{
    public static JsonElement EmptySchema()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    public static UpstreamInventory InventoryWithTools(params string[] toolNames)
        => new(
            [.. toolNames.Select(n => new ToolDescriptor(n, $"Tool {n}", EmptySchema()))],
            [],
            []);

    public static UpstreamServerConfig StdioConfig(
        string slug = "srv",
        bool enabled = true,
        RestartPolicy? restart = null,
        TimeSpan? callTimeout = null)
        => new(
            slug,
            $"Server {slug}",
            UpstreamTransportKind.Stdio,
            enabled,
            new StdioTransportOptions("dummy-command", []),
            Restart: restart,
            CallTimeout: callTimeout);

    /// <summary>Pollt eine Bedingung in Echtzeit (Loop-Tasks laufen auf dem Threadpool), ohne Fake-Zeit zu bewegen.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Bedingung nicht erreicht{(because is null ? string.Empty : $": {because}")}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Prüft, dass eine Bedingung über mehrere Scheduler-Runden hinweg falsch BLEIBT — für
    /// Negativ-Aussagen wie „der Drain wartet noch".
    ///
    /// Bewusst ohne Wanduhr-Pause: Alles, was hier warten würde, hängt an der Fake-Uhr, die der
    /// Test selbst nicht vorstellt. Ein <c>Task.Delay</c> würde die Aussage nicht stärker machen,
    /// sondern nur den Testlauf verlängern und auf langsamen Runnern kippen können.
    /// </summary>
    public static async Task StaysFalseAsync(Func<bool> condition, int rounds = 20, string? because = null)
    {
        for (var i = 0; i < rounds; i++)
        {
            if (condition())
            {
                throw new InvalidOperationException(
                    $"Bedingung wurde wahr, obwohl sie falsch bleiben sollte{(because is null ? string.Empty : $": {because}")}.");
            }

            await Task.Yield();
        }
    }

    /// <summary>
    /// Pollt eine Bedingung und rückt die Fake-Uhr dabei schrittweise vor. Robust gegen das Registrierungs-Race
    /// (Advance vor Task.Delay/PeriodicTimer-Registrierung): kleine Schritte + Echtzeit-Pausen lassen den Loop
    /// zwischendurch registrieren. Nur mit monotonen Bedingungen (Zähler, Event-Listen, Endzustände) verwenden.
    /// </summary>
    public static async Task WaitUntilAdvancingAsync(
        Microsoft.Extensions.Time.Testing.FakeTimeProvider time,
        Func<bool> condition,
        TimeSpan? step = null,
        int timeoutMs = 5000,
        string? because = null)
    {
        var advance = step ?? TimeSpan.FromMilliseconds(100);
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Bedingung nicht erreicht{(because is null ? string.Empty : $": {because}")}.");
            }

            time.Advance(advance);
            await Task.Delay(10);
        }
    }
}

internal sealed class FakeUpstreamConnection : IUpstreamConnection, IPresentedCredentials
{
    private int _disposeCount;
    private volatile bool _failPing;

    public ServerId Id { get; init; }

    /// <summary>Was diese Verbindung dem Upstream vorgelegt hat — etwa ein OAuth-Token (Plan 0005 D1).</summary>
    public IReadOnlyList<string> PresentedCredentials { get; init; } = [];

    public UpstreamInventory Inventory { get; set; } = TestData.InventoryWithTools("echo");

    /// <summary>Wenn gesetzt, blockiert CallToolAsync bis zur Auflösung (für Drain-/Timeout-Tests).</summary>
    public TaskCompletionSource<JsonElement>? CallGate { get; set; }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public bool FailPing
    {
        get => _failPing;
        set => _failPing = value;
    }

    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived;

    /// <summary>
    /// Meldet dieser Upstream seine Änderungen selbst? Auf <c>false</c> gesetzt, verhält er sich wie
    /// ein Server auf der Spec-Revision 2026-07-28 — und muss gefragt werden.
    /// </summary>
    public bool PushesCatalogChanges { get; set; } = true;

    public int DiscoverCalls => Volatile.Read(ref _discoverCalls);

    private int _discoverCalls;

    /// <summary>Wenn gesetzt, wartet jede Katalogabfrage erst darauf — für Abfragen, die sich mit anderem überschneiden.</summary>
    public Func<Task>? DiscoverHook { get; set; }

    /// <summary>Wenn gesetzt, wirft DiscoverAsync diese Exception.</summary>
    public Exception? DiscoverException { get; set; }

    public async Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _discoverCalls);
        var inventory = Inventory;
        if (DiscoverHook is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        return DiscoverException is { } ex ? throw ex : inventory;
    }

    /// <summary>Wenn gesetzt, wirft CallToolAsync diese Exception.</summary>
    public Exception? CallException { get; set; }

    /// <summary>Wenn gesetzt, liefert CallToolAsync dieses JSON statt der Standardantwort.</summary>
    public string? ResultOverride { get; set; }

    public string? LastToolName { get; private set; }

    /// <summary>Wie oft ein Werkzeugaufruf diese Verbindung erreicht hat — auch die gescheiterten.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    private int _callCount;

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        Interlocked.Increment(ref _callCount);
        LastToolName = toolName;
        if (CallException is { } ex)
        {
            throw ex;
        }

        if (CallGate is { } gate)
        {
            await using var registration = ct.Register(() => gate.TrySetCanceled(ct)).ConfigureAwait(false);
            return await gate.Task.ConfigureAwait(false);
        }

        if (ResultOverride is { } json)
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        // Das token-Feld ist Absicht: Ergebnis-Payloads tragen genauso Secrets wie Argumente,
        // der Debug-Modus muss sie maskieren.
        return JsonSerializer.SerializeToElement(new { tool = toolName, token = "geheim-antwort" });
    }

    /// <summary>Wenn gesetzt, wartet ReadResourceAsync darauf (Drain-Tests, E4).</summary>
    public Task? ResourceGate { get; set; }

    public async Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
    {
        if (ResourceGate is { } gate)
        {
            await gate;
        }

        return JsonSerializer.SerializeToElement(new { contents = new[] { new { uri = uri.ToString() } } });
    }

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => Task.FromResult(JsonSerializer.SerializeToElement(new { messages = Array.Empty<object>() }));

    /// <summary>
    /// Wenn gesetzt, wirft PingAsync diese Exception — etwa den Verlust der Sitzung, den das
    /// Lebenszeichen als Erstes bemerkt.
    /// </summary>
    public Exception? PingException { get; set; }

    /// <summary>Wenn gesetzt, wartet das Lebenszeichen darauf — ohne seinen Abbruch zu beachten.</summary>
    public Task? PingHang { get; set; }

    public Task PingAsync(CancellationToken ct)
        => PingHang is { } hang ? hang
        : PingException is { } ex
            ? Task.FromException(ex)
            : _failPing ? Task.FromException(new IOException("Verbindung tot (Test).")) : Task.CompletedTask;

    /// <summary>Wenn gesetzt, wirft DisposeAsync diese Exception — ein Aufräumen, das scheitert.</summary>
    public Exception? DisposeException { get; set; }

    /// <summary>Wenn gesetzt, hängt DisposeAsync daran — ein Prozess, der beim Beenden hängt (E4).</summary>
    public Task? DisposeHang { get; set; }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        if (DisposeHang is { } hang)
        {
            return new ValueTask(hang);
        }

        return DisposeException is { } ex ? ValueTask.FromException(ex) : ValueTask.CompletedTask;
    }

    public void RaiseNotification(string method)
        => NotificationReceived?.Invoke(this, new UpstreamNotificationEventArgs { Server = Id, Method = method });
}

internal sealed class FakeUpstreamConnector : IUpstreamConnector, IUpstreamCredentialRenewal
{
    private int _renewals;

    /// <summary>Wie oft der Supervisor die Erneuerung der Zugangsdaten verlangt hat (V1).</summary>
    public int CredentialRenewals => Volatile.Read(ref _renewals);

    public void RequestCredentialRenewal(ServerId id) => Interlocked.Increment(ref _renewals);

    /// <summary>Das Token, das der Supervisor zuletzt als abgewiesen gemeldet hat (E11).</summary>
    public string? LastRejectedCredential { get; private set; }

    public void RequestCredentialRenewal(ServerId id, string? rejectedCredential)
    {
        LastRejectedCredential = rejectedCredential;
        RequestCredentialRenewal(id);
    }

    private readonly ConcurrentQueue<Func<ServerId, UpstreamServerConfig, IUpstreamConnection>> _scripted = new();
    private readonly List<FakeUpstreamConnection> _connections = [];
    private int _connectCalls;

    public UpstreamTransportKind Kind { get; init; } = UpstreamTransportKind.Stdio;

    public Func<ServerId, UpstreamServerConfig, IUpstreamConnection> DefaultBehavior { get; set; }
        = static (id, _) => new FakeUpstreamConnection { Id = id };

    public int ConnectCalls => Volatile.Read(ref _connectCalls);

    public UpstreamServerConfig? LastConfig { get; private set; }

    public IReadOnlyList<FakeUpstreamConnection> Connections
    {
        get
        {
            lock (_connections)
            {
                return [.. _connections];
            }
        }
    }

    public void EnqueueConnectFailure(string message = "connect fehlgeschlagen (Test)")
        => _scripted.Enqueue((_, _) => throw new IOException(message));

    public void Enqueue(Func<ServerId, UpstreamServerConfig, IUpstreamConnection> factory)
        => _scripted.Enqueue(factory);

    /// <summary>
    /// Wenn gesetzt, wartet jeder Verbindungsaufbau erst darauf — für Versuche, die hängen. Der
    /// Haken bekommt das Abbruchsignal und darf es beachten oder ignorieren; beides gibt es in echt.
    /// </summary>
    public Func<CancellationToken, Task>? ConnectHook { get; set; }

    public async Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        Interlocked.Increment(ref _connectCalls);
        LastConfig = config;
        if (ConnectHook is { } hook)
        {
            await hook(ct).ConfigureAwait(false);
        }

        var factory = _scripted.TryDequeue(out var scripted) ? scripted : DefaultBehavior;
        var connection = factory(id, config);
        if (connection is FakeUpstreamConnection fake)
        {
            lock (_connections)
            {
                _connections.Add(fake);
            }
        }

        return connection;
    }
}
