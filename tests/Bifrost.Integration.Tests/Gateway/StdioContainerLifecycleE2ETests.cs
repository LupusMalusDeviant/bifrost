using System.Diagnostics;

using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream.Isolation;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Der stdio-Container an einer <b>laufenden</b> Runtime (ADR-0025 E5, WP3.2).
/// <para>
/// <b>Was hier belegt wird und was nicht.</b> Belegt wird der Startweg einer <em>stehenden
/// Sitzung</em>: dass stdin offen bleibt und in beide Richtungen trägt, dass die Mindestpolicy im
/// laufenden Container ankommt, und dass ein abgeräumter oder abgestürzter Gateway keinen Container
/// stehen lässt. <b>Nicht</b> belegt wird ein vollständiger MCP-Handshake im Container: Dafür
/// bräuchte es ein Image mit einem echten MCP-Server, und auf dieser Maschine gibt es keins. Der
/// Unterschied steht in der Abgabe, statt dass er unter „grün" verschwindet.
/// </para>
/// <para>
/// <c>cat</c> ist der Stellvertreter des Servers, und ein guter: Es hält stdin offen, gibt zurück,
/// was es bekommt, und beendet sich bei EOF — dasselbe Verhalten, an dem der ganze stdio-Vertrag
/// hängt.
/// </para>
/// <para>
/// Ohne Runtime werden die Tests übersprungen; <c>BIFROST_REQUIRE_CONTAINER=1</c> erzwingt sie
/// (im Docker-CI-Job gesetzt), damit der Nachweis nicht still ausfällt.
/// </para>
/// </summary>
public sealed class StdioContainerLifecycleE2ETests
{
    private const string Image = "alpine:3.20";
    private static readonly IsolationOptions Isolation =
        new(IsolationMode.Container, Image: Image, StopTimeoutSeconds: 2);

    private static void RequireRuntime()
    {
        var required = Environment.GetEnvironmentVariable("BIFROST_REQUIRE_CONTAINER") is "1" or "true";
        var available = ContainerLaunchPolicy.ProbeAsync(Isolation, CancellationToken.None)
            .GetAwaiter().GetResult() is null;
        if (!available)
        {
            Assert.SkipUnless(required,
                "Keine Container-Runtime erreichbar — Docker starten oder BIFROST_REQUIRE_CONTAINER setzen.");
            Assert.Fail("BIFROST_REQUIRE_CONTAINER ist gesetzt, aber keine Container-Runtime erreichbar.");
        }
    }

    /// <summary>
    /// Startet einen Container über <b>genau</b> die Argumente, die der stdio-Connector baut. Der
    /// Test prüft damit den Produktpfad und nicht einen nachgebauten Aufruf.
    /// </summary>
    private static Process StartSession(
        ContainerIdentity identity, params string[] command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Isolation.Runtime,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in ContainerLaunchPolicy.BuildRunArguments(
            new ContainerLaunchRequest(Isolation, identity, ContainerLifetime.Session)))
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var part in command)
        {
            startInfo.ArgumentList.Add(part);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Die Container-Runtime liess sich nicht starten.");
    }

    /// <summary>
    /// Der Kern des stdio-Vertrags: stdin bleibt offen, und was hineingeht, kommt zurück. Ohne
    /// <c>--interactive</c> bekäme der Server sofort EOF und beendete sich, bevor die erste
    /// Nachricht ankommt.
    /// </summary>
    [Fact]
    public async Task A_session_container_keeps_stdin_open_and_pipes_both_ways()
    {
        RequireRuntime();
        var ct = TestContext.Current.CancellationToken;
        var identity = ContainerIdentity.ForUpstream("stdio-pipe", Guid.NewGuid().ToString("N"));

        using var process = StartSession(identity, "/bin/cat");
        try
        {
            await process.StandardInput.WriteLineAsync("hallo-durch-die-roehre".AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);

            var line = await process.StandardOutput.ReadLineAsync(ct);
            line.Should().Be("hallo-durch-die-roehre",
                "der Container muss die Sitzung tragen — darauf liegt das MCP-Protokoll");

            // Und die Gegenrichtung des Vertrags: Schliesst stdin, endet der Prozess.
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            await ContainerLifecycle.KillAsync(Isolation.Runtime, identity.Name);
        }
    }

    /// <summary>
    /// Die Mindestpolicy im laufenden Container — nicht in den Argumenten, sondern in ihrer
    /// Wirkung. Ein Aufruf, der als root läuft oder ins Netz kommt, wäre in der Argumentliste
    /// trotzdem vollständig.
    /// </summary>
    [Fact]
    public async Task The_minimum_policy_actually_holds_inside_the_container()
    {
        RequireRuntime();
        var ct = TestContext.Current.CancellationToken;
        var identity = ContainerIdentity.ForUpstream("stdio-policy", Guid.NewGuid().ToString("N"));

        using var process = StartSession(
            identity,
            "/bin/sh",
            "-c",
            "id -u; (echo x > /etc/versuch && echo SCHREIBBAR) || echo READONLY; "
            + "echo geht > /tmp/probe && cat /tmp/probe; "
            + "(ping -c1 -W1 1.1.1.1 >/dev/null 2>&1 && echo NETZ) || echo KEINNETZ; "
            + "cat /sys/fs/cgroup/memory.max 2>/dev/null || echo keine-cgroup-datei");

        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            output.Should().Contain("65532", "der konfigurierte Nicht-root-Benutzer");
            output.Should().Contain("READONLY", "das Wurzeldateisystem bleibt unbeschreibbar");
            output.Should().Contain("geht", "und /tmp ist trotzdem beschreibbar");
            output.Should().Contain("KEINNETZ", "ohne Allowlist gibt es kein Ziel");

            // Die Speichergrenze ist die einzige, die sich von innen ablesen laesst. 512 MiB.
            output.Should().Contain(
                (512L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "die RAM-Grenze ist im Container wirksam und nicht nur im Argument");
        }
        finally
        {
            await ContainerLifecycle.KillAsync(Isolation.Runtime, identity.Name);
        }
    }

    /// <summary>
    /// <b>Gatewayabbruch räumt ab.</b> Vorbild ist <c>ProcessLifecycleTests</c>: Dort stirbt ein
    /// hart getöteter stdio-Kindprozess mit seinem Wirt, weil das Betriebssystem dafür sorgt. Hier
    /// gibt es kein Betriebssystem, das hilft — <c>docker run</c> ist ein Client zum Daemon, kein
    /// Elternprozess.
    /// <para>
    /// Getragen wird es von zwei Zusagen, die zusammen wirken: Stirbt der Client, schliesst der
    /// Daemon das angehängte stdin; der Server bekommt EOF und endet; <c>--rm</c> räumt den
    /// Container ab. Genau die Kette wird hier getötet und nachgemessen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_hard_killed_client_leaves_no_container_behind()
    {
        RequireRuntime();
        var ct = TestContext.Current.CancellationToken;
        var identity = ContainerIdentity.ForUpstream("stdio-abbruch", Guid.NewGuid().ToString("N"));

        using var process = StartSession(identity, "/bin/cat");
        try
        {
            // Erst muss der Container wirklich laufen — sonst prüfte der Test das Nichts.
            await WaitUntilAsync(
                async () => await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, identity.Name, ct),
                TimeSpan.FromSeconds(30),
                "der Container muss vor dem Abschuss laufen");

            // Hart, ohne Baum: nur der Client. Würde der Test den ganzen Baum töten, prüfte er
            // seine eigene Aufräumarbeit statt die Zusage des Produkts.
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync(ct);

            await WaitUntilAsync(
                async () => !await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, identity.Name, ct),
                TimeSpan.FromSeconds(45),
                "ein abgestuerzter Gateway darf keinen laufenden Container hinterlassen");
        }
        finally
        {
            await ContainerLifecycle.KillAsync(Isolation.Runtime, identity.Name);
        }
    }

    /// <summary>
    /// Der Aufräumlauf trifft <b>nur</b> die eigenen Container. Zwei Gateways am selben Daemon sind
    /// ein realer Betriebsfall, und ein Lauf, der fremde Container abräumt, ist gefährlicher als
    /// der Zustand, den er beheben soll.
    /// </summary>
    [Fact]
    public async Task The_sweep_removes_only_this_instances_containers()
    {
        RequireRuntime();
        var ct = TestContext.Current.CancellationToken;
        var eigene = Guid.NewGuid().ToString("N");
        var fremde = Guid.NewGuid().ToString("N");
        var meiner = ContainerIdentity.ForUpstream("sweep-eigen", eigene);
        var anderer = ContainerIdentity.ForUpstream("sweep-fremd", fremde);

        using var mine = StartSession(meiner, "/bin/cat");
        using var theirs = StartSession(anderer, "/bin/cat");
        try
        {
            await WaitUntilAsync(
                async () =>
                    await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, meiner.Name, ct)
                    && await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, anderer.Name, ct),
                TimeSpan.FromSeconds(30),
                "beide Container muessen vor dem Aufraeumlauf laufen");

            var removed = await ContainerLifecycle.SweepInstanceAsync(Isolation.Runtime, eigene);

            removed.Should().ContainSingle("genau ein Container traegt diese Instanzkennung");
            (await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, meiner.Name, ct))
                .Should().BeFalse("der eigene Container ist weg");
            (await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, anderer.Name, ct))
                .Should().BeTrue("der fremde laeuft weiter — er gehoert einem anderen Gateway");
        }
        finally
        {
            await ContainerLifecycle.KillAsync(Isolation.Runtime, meiner.Name);
            await ContainerLifecycle.KillAsync(Isolation.Runtime, anderer.Name);
        }
    }

    /// <summary>
    /// Ordentliches Beenden: Nach <c>StopAsync</c> gibt es den Container nicht mehr — weder laufend
    /// noch als Leiche, die beim nächsten Start den Namen belegt.
    /// </summary>
    [Fact]
    public async Task Stopping_a_session_removes_the_container()
    {
        RequireRuntime();
        var ct = TestContext.Current.CancellationToken;
        var identity = ContainerIdentity.ForUpstream("stdio-stop", Guid.NewGuid().ToString("N"));

        using var process = StartSession(identity, "/bin/cat");
        await WaitUntilAsync(
            async () => await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, identity.Name, ct),
            TimeSpan.FromSeconds(30),
            "der Container muss vor dem Stoppen laufen");

        await ContainerLifecycle.StopAsync(
            Isolation.Runtime, identity.Name, Isolation.StopTimeoutSeconds);

        (await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, identity.Name, ct))
            .Should().BeFalse();
    }

    /// <summary>
    /// Der Weg, den der Wirt tatsächlich geht: Der Konnektor ist ein Singleton, und wird er beim
    /// Herunterfahren entsorgt, räumt er die Container dieser Instanz ab. Das ist die Zusage für
    /// den <em>geordneten</em> Abbau — für den ungeordneten steht der Test darüber.
    /// </summary>
    [Fact]
    public async Task Disposing_the_connector_reclaims_this_instances_containers()
    {
        RequireRuntime();
        var ct = TestContext.Current.CancellationToken;
        var gateway = new GatewayIdentity();
        var connector = new Bifrost.Upstream.StdioUpstreamConnector(gateway);
        var identity = ContainerIdentity.ForUpstream("stdio-shutdown", gateway.InstanceId);

        using var process = StartSession(identity, "/bin/cat");
        try
        {
            await WaitUntilAsync(
                async () => await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, identity.Name, ct),
                TimeSpan.FromSeconds(30),
                "der Container muss vor dem Herunterfahren laufen");

            await connector.DisposeAsync();

            (await ContainerLifecycle.IsRunningAsync(Isolation.Runtime, identity.Name, ct))
                .Should().BeFalse(
                    "ein heruntergefahrener Gateway laesst keinen Container stehen — 'docker run' "
                    + "ist ein Client, kein Elternprozess");
        }
        finally
        {
            await ContainerLifecycle.KillAsync(Isolation.Runtime, identity.Name);
        }
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Bedingung nicht erreicht innerhalb {timeout.TotalSeconds:0}s: {because}");
    }
}
