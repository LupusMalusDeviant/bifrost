using Bifrost.Performance.Tests.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E.Harness;

/// <summary>
/// Ein Browser vor einer echten Installation: eigener Gateway-Prozess, eigene SQLite-Datenbank,
/// eigenes TLS, echte Anmeldung.
/// <para>
/// <b>Warum über HTTPS und nicht über HTTP:</b> Das Anmelde-Cookie ist außerhalb der
/// Entwicklungsumgebung <c>Secure</c> (siehe <c>Program.cs</c>). Über HTTP käme es nie im Browser
/// an — ein Browsertest, der die Umgebung auf „Development" stellt, prüft eine Konfiguration, die
/// im Betrieb nicht läuft. Also fährt der Test das, was ausgeliefert wird, und vertraut dem
/// selbst erzeugten Testzertifikat.
/// </para>
/// <para>
/// <b>Kein Auth-Bypass.</b> Der Benutzer entsteht über <c>--reset-ui-admin</c>, also über den Weg
/// eines Betreibers ohne Oberfläche, und meldet sich danach im Formular an wie jeder andere
/// (M5-Vertrag §8).
/// </para>
/// </summary>
internal sealed class UiSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly string _artifactDirectory;
    private readonly List<string> _browserLog = [];

    private UiSession(
        GatewayProcess gateway, IPlaywright playwright, IBrowser browser, IBrowserContext context,
        IPage page, string artifactDirectory)
    {
        Gateway = gateway;
        _playwright = playwright;
        _browser = browser;
        _context = context;
        Page = page;
        _artifactDirectory = artifactDirectory;
        ListenToBrowser();
    }

    public GatewayProcess Gateway { get; }

    public IPage Page { get; }

    public static string BrowserName =>
        Environment.GetEnvironmentVariable("BIFROST_E2E_BROWSER") ?? "chromium";

    public static bool Enabled => Environment.GetEnvironmentVariable("BIFROST_RUN_E2E") == "1";

    /// <param name="withUiAdmin">
    /// <c>false</c> startet eine Instanz <b>ohne</b> UI-Zugang — der Zustand, in dem ein Betreiber
    /// den gefuehrten Erstaufbau vorfindet. Fuer alle anderen Wege bleibt es bei <c>true</c>: Der
    /// Zugang entsteht dann ueber <c>--reset-ui-admin</c>, also ueber denselben Weg wie im Betrieb,
    /// und nicht ueber einen Testschalter an der Anmeldung vorbei (M5-Vertrag §8).
    /// </param>
    public static async Task<UiSession> StartAsync(
        string testName, CancellationToken ct, bool withUiAdmin = true)
    {
        var gateway = await GatewayProcess.StartAsync(
            new GatewayProcessOptions(
                Transport: GatewayTransport.Https,
                WithUiAdmin: withUiAdmin,
                ExecutablePath: await PublishedGateway.EnsureAsync(ct)),
            ct);

        try
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await Launch(playwright);
            var artifactDirectory = Path.Combine(
                TestServerPaths.RepoRoot, "artifacts", "e2e", $"{BrowserName}-{testName}");
            Directory.CreateDirectory(artifactDirectory);

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                // Das Testzertifikat ist selbst erzeugt; ohne das hier scheitert jede Navigation.
                IgnoreHTTPSErrors = true,
                BaseURL = gateway.BaseAddress.ToString(),
                RecordVideoDir = artifactDirectory,
                ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
            });

            await context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
                Sources = false,
            });

            var page = await context.NewPageAsync();
            return new UiSession(gateway, playwright, browser, context, page, artifactDirectory);
        }
        catch
        {
            await gateway.DisposeAsync();
            throw;
        }
    }

    private static Task<IBrowser> Launch(IPlaywright playwright) => BrowserName switch
    {
        "firefox" => playwright.Firefox.LaunchAsync(),
        "webkit" => playwright.Webkit.LaunchAsync(),
        _ => playwright.Chromium.LaunchAsync(),
    };

    /// <summary>
    /// Anmeldung über das ausgelieferte Formular — und zwar bis zu ihrem Ende.
    /// <para>
    /// <b>Befund vom 2026-11-16.</b> Der Klick auf „Anmelden" kehrt nicht in jedem Browser erst
    /// zurück, wenn die Antwort da ist. In WebKit stand die Seite danach noch auf <c>/login</c>;
    /// das nächste <c>GotoAsync</c> des Tests brach die laufende Anmeldung ab („POST /auth/login —
    /// Load request cancelled"), und der Weg landete unangemeldet wieder auf der Anmeldeseite.
    /// Fertig ist die Anmeldung erst, wenn die Seite das Formular verlassen hat: nach <c>/</c>
    /// bei Erfolg, nach <c>/login?failed=true</c> bei falschen Zugangsdaten.
    /// </para>
    /// </summary>
    public async Task LoginAsync(string? username = null, string? password = null)
    {
        await Page.GotoAsync("login");
        var form = Page.Url;
        await Page.FillAsync("#username", username ?? Gateway.UiUsername!);
        await Page.FillAsync("#password", password ?? Gateway.UiPassword!);
        await SubmitAndWaitToLeaveAsync("button[type=submit]", form);
    }

    /// <summary>
    /// Klickt einen Knopf, der ein gewöhnliches Formular abschickt, und wartet, bis die Seite die
    /// Adresse <paramref name="from"/> verlassen hat. Für alles, was der Browser selbst abschickt
    /// (Anmelden, Abmelden) — dort gibt es keinen Circuit, der das Ende meldet.
    /// <para>
    /// <b>Gefragt wird der Browser, nicht Playwrights Navigationsbuchhaltung.</b> Bis 2026-11-17
    /// stand hier <c>Page.WaitForURLAsync</c> — erst bis <c>load</c>, dann bis
    /// <c>DOMContentLoaded</c>. Beides lief zweimal 30 s ins Leere, obwohl die Seite längst
    /// angekommen war: lokal einmal in 95 Läufen (Chromium, 2026-11-16), dann im e2e-Lauf auf
    /// <c>main</c> (2026-11-17, „Adresse: /login?failed=true; readyState: complete"). Playwright
    /// hatte die Adresse vor sich und wartete trotzdem auf ein Ladeereignis, das es dieser
    /// Navigation nicht mehr zuordnete. Warum, ist nicht nachgestellt; der Ablauf ist ein POST mit
    /// anschließender Weiterleitung.
    /// </para>
    /// <para>
    /// Jetzt prüft die Seite selbst: Adresse nicht mehr die des Formulars und
    /// <c>document.readyState</c> nicht mehr <c>loading</c>. <c>WaitForFunctionAsync</c> wiederholt
    /// die Prüfung über den Seitenwechsel hinweg. Läuft die Frist ab, nennt der Fehler den Zustand
    /// der Seite.
    /// </para>
    /// </summary>
    public async Task SubmitAndWaitToLeaveAsync(string buttonSelector, string from)
    {
        // Die Prüfung unten vergleicht mit `location.href`. Stimmt `from` damit schon vor dem
        // Klick nicht überein, wäre sie sofort erfüllt und wartete auf nichts — genau der Fehler,
        // gegen den sie gebaut ist (WebKit, 2026-11-16).
        var before = await Page.EvaluateAsync<string>("() => location.href");
        if (before != from)
        {
            throw new InvalidOperationException(
                $"SubmitAndWaitToLeaveAsync: Die Seite steht auf '{before}', nicht auf '{from}'. "
                + "Ohne gleichen Ausgangspunkt würde das Warten sofort enden.");
        }

        await Page.ClickAsync(buttonSelector);

        try
        {
            await Page.WaitForFunctionAsync(
                "from => location.href !== from && document.readyState !== 'loading'",
                from,
                new PageWaitForFunctionOptions { Timeout = 30000, PollingInterval = 100 });
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException(
                await DescribeStuckPageAsync($"Nach dem Abschicken ({buttonSelector}) kam die Seite in 30 s nicht an."),
                timeout);
        }
    }

    /// <summary>
    /// Blazor Server rendert zuerst statisch und wird erst mit der Verbindung bedienbar. Ein Klick
    /// davor läuft ins Leere — und der Test wäre nicht falsch, sondern <em>flatterig</em>, was
    /// schlimmer ist.
    /// <para>
    /// <b>Befund vom 2026-11-16.</b> Gewartet wurde hier auf <c>window.Blazor !== undefined</c>.
    /// Das gilt, sobald das Skript geladen ist — also <em>bevor</em> der Circuit die Seite
    /// übernommen hat. Der Test tippte in das vorgerenderte Formular, der Circuit ersetzte es
    /// durch ein leeres, und abgeschickt wurde nichts („Slug '' ist ungültig", „Zielpfad fehlt.").
    /// Aufgefallen ist es selten, weil die Anmeldeseite zuvor schon einen Circuit aufgebaut hatte
    /// und der zweite schnell genug stand. Jetzt wartet der Weg auf das, was die Oberfläche selbst
    /// meldet: <c>data-interactive="true"</c> an <c>circuit-state</c> (siehe <c>Routes.razor</c>).
    /// </para>
    /// </summary>
    public async Task WaitForInteractivityAsync(string readySelector)
    {
        try
        {
            await Page.WaitForSelectorAsync(
                "[data-testid=circuit-state][data-interactive=true]",
                new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 30000 });
        }
        catch (TimeoutException timeout)
        {
            // Ohne das hier ist der Fehlschlag nur rot: "Timeout 30000ms exceeded" sagt nicht, ob
            // die Seite die falsche war (zurueck auf der Anmeldung), ob das Skript fehlt oder ob
            // die Verbindung nicht zustande kam. Trace und Bildschirmfoto fehlen im Fehlerfall
            // ebenfalls (siehe DisposeAsync) — also sagt es der Fehler selbst.
            throw new TimeoutException(
                await DescribeStuckPageAsync("Die Seite wurde in 30 s nicht bedienbar."), timeout);
        }

        await Page.WaitForSelectorAsync(readySelector, new PageWaitForSelectorOptions { Timeout = 30000 });
    }

    private async Task<string> DescribeStuckPageAsync(string what)
    {
        string state;
        try
        {
            state = await Page.EvaluateAsync<string>(
                """
                () => {
                    const marker = document.querySelector('[data-testid=circuit-state]');
                    return 'circuit-state: ' + (marker ? marker.getAttribute('data-interactive') : 'fehlt')
                        + '; Blazor-Skript geladen: ' + (window.Blazor !== undefined)
                        + '; readyState: ' + document.readyState;
                }
                """);
        }
        catch (PlaywrightException e)
        {
            state = $"Zustand nicht lesbar ({e.Message})";
        }

        string[] seen;
        lock (_browserLog)
        {
            seen = [.. _browserLog];
        }

        return $"{what} Adresse: {new Uri(Page.Url).PathAndQuery}; {state}. "
            + (seen.Length == 0
                ? "Der Browser hat nichts gemeldet."
                : "Vom Browser gemeldet:\n  " + string.Join("\n  ", seen));
    }

    /// <summary>
    /// Hält fest, was der Browser an Fehlern meldet: Konsole, gescheiterte Anfragen, WebSockets.
    /// Begrenzt auf die letzten Einträge — genug für eine Ursache, zu wenig für ein Protokoll.
    /// </summary>
    private void ListenToBrowser()
    {
        Page.Console += (_, message) =>
        {
            if (message.Type is "error" or "warning")
            {
                Note($"Konsole ({message.Type}): {message.Text}");
            }
        };
        Page.PageError += (_, error) => Note($"Skriptfehler: {error}");
        Page.RequestFailed += (_, request) =>
            Note($"Anfrage gescheitert: {request.Method} {PathOf(request.Url)} — {request.Failure}");
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                Note($"Antwort {response.Status}: {response.Request.Method} {PathOf(response.Url)}");
            }
        };
        Page.WebSocket += (_, socket) =>
        {
            Note($"WebSocket geöffnet: {PathOf(socket.Url)}");
            socket.SocketError += (_, error) => Note($"WebSocket-Fehler: {error}");
            socket.Close += (_, _) => Note($"WebSocket geschlossen: {PathOf(socket.Url)}");
        };
    }

    // Nur der Pfad: In der Abfrage stehen Verbindungskennungen, die niemandem helfen.
    private static string PathOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : url;

    private void Note(string entry)
    {
        lock (_browserLog)
        {
            if (_browserLog.Count == 40)
            {
                _browserLog.RemoveAt(0);
            }

            _browserLog.Add(entry.Length > 300 ? entry[..300] : entry);
        }
    }

    /// <summary>
    /// Beweise nur im Fehlerfall — Ablage von Trace, Video und Bildschirmfoto. Ein grüner Lauf, der
    /// Videos sammelt, füllt nur das Artefaktverzeichnis.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var failed = TestContext.Current.TestState?.Result == TestResult.Failed;

        try
        {
            if (failed)
            {
                await Page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(_artifactDirectory, "failure.png"),
                    FullPage = true,
                });
            }

            await _context.Tracing.StopAsync(new TracingStopOptions
            {
                Path = failed ? Path.Combine(_artifactDirectory, "trace.zip") : null,
            });

            await _context.CloseAsync();
            await _browser.DisposeAsync();
            _playwright.Dispose();
        }
        finally
        {
            await Gateway.DisposeAsync();

            if (!failed)
            {
                TryDelete(_artifactDirectory);
            }
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
