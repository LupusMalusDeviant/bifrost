using System.Net;
using AwesomeAssertions;
using Xunit;

namespace Faolan.Upstream.Tests;

/// <summary>
/// Der Unterbau der OAuth-Testserver räumt sauber auf — der Teil, an dem der ci-Lauf auf main am
/// 27. November 2026 scheiterte („Address already in use" beim Schließen) und an dem ein Fehler
/// bisher unbeobachtet in einer verworfenen Task verschwand.
/// </summary>
public sealed class LoopbackHttpServerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_request_is_answered()
    {
        using var server = new LoopbackHttpServer(context => Respond(context, "hallo"));
        using var client = new HttpClient();

        var body = await client.GetStringAsync(server.Address, TestContext.Current.CancellationToken);

        body.Should().Be("hallo");
    }

    [Fact]
    public void Closing_ends_a_pending_accept()
    {
        var server = new LoopbackHttpServer(context => Respond(context, "nie gefragt"));

        server.Dispose();

        server.Completion.IsCompleted.Should().BeTrue("Dispose wartet das Ende der Annahme-Schleife ab");
        server.Completion.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public void Closing_twice_is_harmless()
    {
        var server = new LoopbackHttpServer(context => Respond(context, "x"));

        server.Dispose();
        var again = server.Dispose;

        again.Should().NotThrow();
    }

    [Fact]
    public async Task Closing_during_a_response_waits_for_the_handler_and_does_not_fail()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new LoopbackHttpServer(async context =>
        {
            entered.SetResult();
            await release.Task;
            await Respond(context, "zu spät");
        });
        using var client = new HttpClient();
        var request = client.GetAsync(server.Address, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var closing = Task.Run(server.Dispose, TestContext.Current.CancellationToken);
        // Dispose hält an der Schleife fest, solange der Handler läuft.
        (await Task.WhenAny(closing, Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken)))
            .Should().NotBeSameAs(closing, "die Schleife ist noch im Handler");
        release.SetResult();

        await closing.WaitAsync(Patience, TestContext.Current.CancellationToken);
        server.Completion.IsCompletedSuccessfully.Should().BeTrue();
        // Der Client sieht den Abbruch oder die späte Antwort; beides ist für den Server kein Fehler.
        await request.ContinueWith(_ => { }, TestContext.Current.CancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    [Fact]
    public async Task A_client_that_leaves_does_not_stop_the_server()
    {
        var calls = 0;
        using var server = new LoopbackHttpServer(context =>
            Interlocked.Increment(ref calls) == 1
                ? throw new IOException("Client ist gegangen")
                : Respond(context, "zweiter"));
        using var client = new HttpClient();

        var first = async () => await client.GetStringAsync(server.Address, TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<HttpRequestException>();
        var second = await client.GetStringAsync(server.Address, TestContext.Current.CancellationToken);

        second.Should().Be("zweiter", "ein abgebrochener Client beendet die Schleife nicht");
    }

    [Fact]
    public async Task A_fault_in_the_handler_is_reported_when_closing()
    {
        var server = new LoopbackHttpServer(_ => throw new FormatException("Fehler im Testserver"));
        using var client = new HttpClient();
        var request = async () => await client.GetStringAsync(server.Address, TestContext.Current.CancellationToken);
        await request.Should().ThrowAsync<HttpRequestException>();

        var closing = server.Dispose;

        closing.Should().Throw<InvalidOperationException>()
            .WithInnerException<FormatException>()
            .WithMessage("Fehler im Testserver");
    }

    [Fact]
    public void Binding_retries_with_a_new_listener_when_the_port_is_taken()
    {
        using var occupied = new LoopbackHttpServer(context => Respond(context, "belegt"));
        var taken = occupied.Address.Port;
        var ports = new Queue<int>([taken, taken]);

        var (listener, address) = LoopbackHttpServer.Bind(
            () => ports.TryDequeue(out var port) ? port : Random.Shared.Next(20000, 60000));

        try
        {
            ports.Should().BeEmpty("beide belegten Versuche wurden verbraucht");
            address.Port.Should().NotBe(taken);
            listener.IsListening.Should().BeTrue();
        }
        finally
        {
            listener.Close();
        }
    }

    private static async Task Respond(HttpListenerContext context, string body)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "text/plain";
        await context.Response.OutputStream.WriteAsync(payload);
        context.Response.Close();
    }
}
