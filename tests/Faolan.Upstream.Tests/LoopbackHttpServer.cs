using System.Net;

namespace Faolan.Upstream.Tests;

/// <summary>
/// Der gemeinsame Unterbau der kleinen HTTP-Server auf Loopback, gegen die die OAuth-Tests laufen.
/// <para>
/// Drei Testserver hatten je ihre eigene Kopie von Binden, Annehmen und Aufräumen — und damit
/// dreimal dieselben Lücken, von denen eine den ci-Lauf auf main rot machte (27. November 2026):
/// </para>
/// <list type="bullet">
/// <item><b>Binden.</b> Einen freien Port erst zu erfragen und dann zu belegen ist ein Wettlauf;
/// hier wird gebunden und bei Belegung mit einem neuen Listener wiederholt.</item>
/// <item><b>Aufräumen.</b> Genau ein <see cref="HttpListener.Close"/>, kein <c>Stop</c> davor: Unter
/// Linux entfernt <c>Stop</c> den Endpunkt bereits, und ein folgendes <c>Close</c> bindet den Port
/// erneut, um ihn zu entfernen — „Address already in use", sobald ihn inzwischen jemand hat.</item>
/// <item><b>Ende der Schleife.</b> Die Annahme-Schleife wird nicht verworfen, sondern beim Aufräumen
/// mit Zeitgrenze abgewartet. Das Starten einer Annahme und das Schließen laufen unter derselben
/// Sperre, damit keine Annahme mehr eingereiht wird, nachdem <c>Close</c> die Warteschlange geleert
/// hat.</item>
/// <item><b>Fehler im Handler.</b> Ein abgebrochener Client beendet die Schleife nicht mehr; ein
/// echter Fehler im Handler wird festgehalten und beim Aufräumen gemeldet, statt unbeobachtet in
/// einer verworfenen Task zu verschwinden.</item>
/// </list>
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    /// <summary>So lange darf die Schleife nach dem Schließen brauchen, bis das Aufräumen scheitert.</summary>
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private const int BindAttempts = 20;

    private readonly Lock _gate = new();
    private readonly HttpListener _listener;
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly Task _loop;
    private Exception? _handlerFault;
    private bool _closed;

    public LoopbackHttpServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;
        (_listener, Address) = Bind(() => Random.Shared.Next(20000, 60000));
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Die Adresse des Servers ohne Pfad, z. B. <c>http://127.0.0.1:41234</c>.</summary>
    public Uri Address { get; }

    /// <summary>Endet, wenn die Annahme-Schleife beendet ist. Für die Tests des Unterbaus.</summary>
    internal Task Completion => _loop;

    /// <summary>
    /// Bindet einen Listener an einen Port aus <paramref name="nextPort"/> und versucht es bei
    /// Belegung erneut. Ein <see cref="HttpListener"/>, dessen Start scheitert, ist danach entsorgt;
    /// jeder Versuch braucht einen neuen.
    /// </summary>
    internal static (HttpListener Listener, Uri Address) Bind(Func<int> nextPort)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = nextPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, new Uri($"http://127.0.0.1:{port}"));
            }
            catch (HttpListenerException) when (attempt < BindAttempts)
            {
                listener.Close();
            }
            catch
            {
                listener.Close();
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _listener.Close();
        }

        if (!_loop.Wait(ShutdownTimeout))
        {
            throw new TimeoutException(
                $"Die Annahme-Schleife des Testservers {Address} endete nicht binnen {ShutdownTimeout.TotalSeconds:F0} s nach dem Schließen.");
        }

        if (Volatile.Read(ref _handlerFault) is { } fault)
        {
            throw new InvalidOperationException(
                $"Der Handler des Testservers {Address} ist mit einem Fehler ausgestiegen: {fault.Message}", fault);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            Task<HttpListenerContext> pending;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                try
                {
                    pending = _listener.GetContextAsync();
                }
                catch (Exception exception) when (IsListenerGone(exception))
                {
                    return;
                }
            }

            HttpListenerContext context;
            try
            {
                context = await pending.ConfigureAwait(false);
            }
            catch (Exception exception) when (IsListenerGone(exception))
            {
                return;
            }

            try
            {
                await _handler(context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Ein Client, der mitten in der Antwort geht, oder ein Server, der gerade schließt,
                // ist kein Fehler des Tests. Alles andere ist einer und wird beim Aufräumen gemeldet.
                if (!IsConnectionGone(exception) && !Volatile.Read(ref _closed))
                {
                    Interlocked.CompareExchange(ref _handlerFault, exception, null);
                }

                Fail(context);
            }
        }
    }

    private static bool IsListenerGone(Exception exception)
        => exception is HttpListenerException or ObjectDisposedException or InvalidOperationException;

    private static bool IsConnectionGone(Exception exception)
        => exception is HttpListenerException or ObjectDisposedException or IOException;

    /// <summary>
    /// Beendet die Anfrage eines gescheiterten Handlers mit 500. Ein bloßes <c>Abort</c> genügt
    /// nicht: Unter Linux kommt es beim Client nicht als Fehler an (im Container gemessen), und der
    /// Test, der die Anfrage stellte, liefe mit einer leeren Antwort weiter. Sind die Kopfzeilen
    /// schon unterwegs, bleibt nur der Abbruch.
    /// </summary>
    private static void Fail(HttpListenerContext context)
    {
        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.Close();
        }
        catch (Exception exception) when (IsConnectionGone(exception) || exception is InvalidOperationException)
        {
            try
            {
                context.Response.Abort();
            }
            catch (Exception inner) when (IsConnectionGone(inner) || inner is InvalidOperationException)
            {
                // Die Verbindung ist schon weg; mehr gibt es nicht aufzuräumen.
            }
        }
    }
}
