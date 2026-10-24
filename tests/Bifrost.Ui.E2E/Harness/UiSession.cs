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

    /// <summary>Anmeldung über das ausgelieferte Formular.</summary>
    public async Task LoginAsync(string? username = null, string? password = null)
    {
        await Page.GotoAsync("login");
        await Page.FillAsync("#username", username ?? Gateway.UiUsername!);
        await Page.FillAsync("#password", password ?? Gateway.UiPassword!);
        await Page.ClickAsync("button[type=submit]");
    }

    /// <summary>
    /// Blazor Server rendert zuerst statisch und wird erst mit der Verbindung bedienbar. Ein Klick
    /// davor läuft ins Leere — und der Test wäre nicht falsch, sondern <em>flatterig</em>, was
    /// schlimmer ist.
    /// </summary>
    public async Task WaitForInteractivityAsync(string readySelector)
    {
        await Page.WaitForSelectorAsync(readySelector, new PageWaitForSelectorOptions { Timeout = 30000 });
        await Page.WaitForFunctionAsync(
            "() => window.Blazor !== undefined && document.readyState === 'complete'",
            new PageWaitForFunctionOptions { Timeout = 30000 });
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
