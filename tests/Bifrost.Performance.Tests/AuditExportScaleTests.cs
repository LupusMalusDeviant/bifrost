using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Audit;
using Bifrost.Persistence;
using Bifrost.Persistence.Audit;
using Bifrost.Persistence.Startup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Bifrost.Performance.Tests;

/// <summary>
/// M6/WP6.2, der Nachweis aus dem Vertrag: <b>Eine Million Einträge, und der Export zieht den
/// Arbeitsspeicher nicht linear mit</b> (FR-P072).
/// <para>
/// <b>Warum zwei Größen und nicht nur eine Million:</b> Ein einzelner Lauf, der durchgeht, belegt
/// nur, dass der Rechner groß genug war. „Nicht linear" ist eine Aussage über das Verhältnis — also
/// wird bei 100 000 und bei 1 000 000 gemessen und beides verglichen. Ein Export, der erst eine
/// Liste baut, wächst zwischen diesen beiden Größen um das Zehnfache; einer, der streamt, gar nicht.
/// </para>
/// <para>
/// <b>Gemessen wird der belegte verwaltete Speicher während des Durchlaufs</b>, nicht die Summe
/// aller Zuweisungen. Zuweisungen wachsen zwangsläufig mit der Zeilenzahl — jede Zeile wird ja
/// einmal angefasst. Die Frage ist, ob sie <em>gehalten</em> werden.
/// </para>
/// </summary>
public sealed class AuditExportScaleTests
{
    /// <summary>
    /// Eigenes Tor, weil dieser Nachweis Minuten dauert und Plattenplatz braucht. Gesetzt wird es
    /// vom CI-Job <c>audit-scale</c> — ein Tor ohne Job wäre ein Nachweis, den niemand fährt.
    /// </summary>
    private static bool Enabled => Environment.GetEnvironmentVariable("BIFROST_RUN_AUDIT_SCALE") == "1";

    private const int Small = 100_000;
    private const int Large = 1_000_000;

    /// <summary>
    /// Obergrenze des zusätzlich belegten Speichers je Lauf. Großzügig gewählt — es geht nicht um
    /// Feinabstimmung, sondern um den Unterschied zwischen „eine Konstante" und „eine Million
    /// Objekte".
    /// </summary>
    private const long CeilingBytes = 32L * 1024 * 1024;

    private readonly ITestOutputHelper _output;

    public AuditExportScaleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A_million_entries_export_without_pulling_memory_along()
    {
        Assert.SkipUnless(Enabled, "Grossmengen-Nachweis nur mit BIFROST_RUN_AUDIT_SCALE=1.");

        var ct = TestContext.Current.CancellationToken;
        var klein = await MeasureAsync(Small, ct);
        var gross = await MeasureAsync(Large, ct);

        _output.WriteLine(
            $"{Small:N0}: {klein.PeakBytes / 1024 / 1024} MB, {klein.Elapsed.TotalSeconds:F1} s");
        _output.WriteLine(
            $"{Large:N0}: {gross.PeakBytes / 1024 / 1024} MB, {gross.Elapsed.TotalSeconds:F1} s");

        klein.Count.Should().Be(Small);
        gross.Count.Should().Be(Large);

        gross.PeakBytes.Should().BeLessThan(CeilingBytes,
            "eine Million Einträge dürfen nicht als eine Million Objekte im Speicher stehen");

        // Der eigentliche Satz aus dem Vertrag: NICHT linear. Bei zehnfacher Menge darf der
        // Speicher nicht mitwachsen — etwas Rauschen durch den GC ist erlaubt, ein Faktor 10 nicht.
        gross.PeakBytes.Should().BeLessThan(klein.PeakBytes * 3 + 8L * 1024 * 1024,
            "zehnmal so viele Zeilen, aber nicht annähernd zehnmal so viel Speicher");
    }

    /// <summary>
    /// Und die Kette hält auch in dieser Größenordnung — der Prüfer liest denselben Datenstrom und
    /// hält ebenfalls nichts fest.
    /// </summary>
    [Fact]
    public async Task The_verifier_walks_a_million_entries_without_holding_them()
    {
        Assert.SkipUnless(Enabled, "Grossmengen-Nachweis nur mit BIFROST_RUN_AUDIT_SCALE=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await AuditFixture.CreateAsync(Large, ct);

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var report = await AuditChainVerifier.VerifyAsync(
            new EfAuditExport(fixture.Factory).StreamAsync(new AuditExportRequest(new AuditFilter()), ct), ct);
        var after = GC.GetTotalMemory(forceFullCollection: true);

        report.Checked.Should().Be(Large);
        report.IsIntact.Should().BeTrue(
            $"die Fixture legt eine echte Kette (Befund: {report.FirstBreak?.Reason})");
        (after - before).Should().BeLessThan(CeilingBytes);
        _output.WriteLine($"Prüfer: {(after - before) / 1024 / 1024} MB für {Large:N0} Einträge");
    }

    private static async Task<(long Count, long PeakBytes, TimeSpan Elapsed)> MeasureAsync(
        int rows, CancellationToken ct)
    {
        await using var fixture = await AuditFixture.CreateAsync(rows, ct);

        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = 0L;
        var count = 0L;
        var watch = Stopwatch.StartNew();

        await foreach (var entry in new EfAuditExport(fixture.Factory)
            .StreamAsync(new AuditExportRequest(new AuditFilter()), ct)
            .WithCancellation(ct))
        {
            // Die Zeile wird benutzt und wieder losgelassen — genau wie in der Antwort des
            // Endpunkts. Sie zu sammeln wäre die Materialisierung, die hier widerlegt wird.
            _ = AuditExportWriter.Line(entry, AuditExportFormat.Jsonl).Length;

            // Mit erzwungener Sammlung gemessen: Ohne sie zeigt die Zahl nur, wann der GC
            // zuletzt gelaufen ist — also Muell, kein Bestand. Die Frage hier ist aber, was
            // GEHALTEN wird, und das ist bei einem Streaming-Export eine Konstante.
            if (++count % 100_000 == 0)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
            }
        }

        return (count, Math.Max(peak, 0), watch.Elapsed);
    }

    /// <summary>
    /// Eine SQLite-Datei mit <paramref name="rows"/> echten Audit-Zeilen samt gültiger Kette.
    /// <para>
    /// Geschrieben wird mit rohem SQL in einer Transaktion — über EF wären eine Million Zeilen
    /// nicht der Nachweis, sondern die Wartezeit davor.
    /// </para>
    /// </summary>
    private sealed class AuditFixture : IAsyncDisposable
    {
        private readonly string _path;

        private AuditFixture(string path, IDbContextFactory<BifrostDbContext> factory)
        {
            _path = path;
            Factory = factory;
        }

        public IDbContextFactory<BifrostDbContext> Factory { get; }

        public AuditEvent FirstEvent { get; init; } = null!;

        public static async Task<AuditFixture> CreateAsync(int rows, CancellationToken ct)
        {
            AuditEvent fixtureFirst;
            var path = Path.Combine(Path.GetTempPath(), $"bifrost-audit-scale-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<BifrostDbContext>()
                .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={path}")
                .Options;

            IDbContextFactory<BifrostDbContext> factory = new ScaleDbContextFactory(options);
            await new DatabaseInitializer(factory).InitializeAsync(ct);

            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var connection = db.Database.GetDbConnection();
                await connection.OpenAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO AuditEvents
                      (Timestamp, Origin, Kind, Tool, Status, RequestBytes, ResponseBytes, DurationMs,
                       ChainId, ChainIndex, PreviousHash, EntryHash)
                    VALUES ($ts, 0, 0, 'srv__do_thing', 0, 10, 20, 1.0, $chain, $index, $prev, $hash)
                    """;

                var ts = Parameter(command, "$ts");
                var chain = Parameter(command, "$chain");
                var index = Parameter(command, "$index");
                var prev = Parameter(command, "$prev");
                var hash = Parameter(command, "$hash");

                AuditEvent? firstEvent = null;
                var chainId = Guid.NewGuid();
                var previous = AuditChain.Genesis;
                var start = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

                for (var i = 0; i < rows; i++)
                {
                    var moment = start.AddSeconds(i);
                    var evt = new AuditEvent(
                        moment, null, CallOrigin.Mcp, AuditEventKind.ToolCall, null, "srv__do_thing",
                        InvocationStatus.Success, null, 10, 20, TimeSpan.FromMilliseconds(1));
                    var link = AuditChain.Link(previous, evt);
                    firstEvent ??= evt;

                    // ACHTUNG, hier lag der erste Fehlschlag: Zeitstempel liegen NICHT als Text in
                    // der Ablage, sondern als UTC-Ticks (BifrostDbContext.UtcTicksConverter). Roh
                    // geschriebenes ISO-8601 kam als Jahr 1 zurueck — und die Kette meldete
                    // Manipulation, wo nur die Fixture am Ablageformat vorbeigeschrieben hatte.
                    ts.Value = moment.UtcTicks;
                    chain.Value = chainId.ToString();
                    index.Value = i;
                    prev.Value = previous;
                    hash.Value = link;
                    await command.ExecuteNonQueryAsync(ct);

                    previous = link;
                }

                await transaction.CommitAsync(ct);
                fixtureFirst = firstEvent!;
            }

            return new AuditFixture(path, factory) { FirstEvent = fixtureFirst };
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // Eine liegengebliebene Testdatei ist kein Grund, einen grünen Lauf rot zu machen.
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>Die kleinste Fabrik, die den Vertrag erfüllt — mehr braucht der Nachweis nicht.</summary>
        private sealed class ScaleDbContextFactory : IDbContextFactory<BifrostDbContext>
        {
            private readonly DbContextOptions<BifrostDbContext> _options;

            public ScaleDbContextFactory(DbContextOptions<BifrostDbContext> options) => _options = options;

            public BifrostDbContext CreateDbContext() => new(_options);
        }

        private static DbParameter Parameter(DbCommand command, string name)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
            return parameter;
        }
    }
}
