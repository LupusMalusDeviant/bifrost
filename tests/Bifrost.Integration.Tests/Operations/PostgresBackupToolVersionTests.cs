using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Checks;
using Bifrost.Persistence.Backup;
using Bifrost.Server.Diagnostics;
using Bifrost.Tests.Postgres;

using Xunit;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// BFR-DB-0006 (ADR-0024 E2): <b>Ein Client, der diesen Server nicht sichern kann, faellt vorher
/// auf — nicht im Ernstfall.</b>
///
/// <para>
/// Der Anlass ist gemessen: Ubuntu 24.04 liefert <c>pg_dump</c> 16, ein aktueller Server ist 17
/// oder 18, und jede Sicherung bricht dann mit "aborting because of server version mismatch" ab —
/// auch die vor einer Migration (E7). Wer das erst im Ernstfall erfaehrt, hat keinen Rueckweg.
/// </para>
///
/// <para>
/// <b>Diese Suite braucht weder Docker noch einen PostgreSQL-Server.</b> Das ist Absicht und nicht
/// Sparsamkeit: Ein Befund, der nur dort prueft, wo ohnehin alles passt, prueft nichts. Die
/// Versionslage wird deshalb als Zahlenpaar gestellt; genau so bekommt der Check sie auch im
/// Betrieb, weil die Sonden bereits ausgewertete Hauptversionen liefern.
/// </para>
/// </summary>
public sealed class PostgresBackupToolVersionCheckTests
{
    // ── Der Befund selbst ──────────────────────────────────────────────────────────────────────

    /// <summary>Die Lage aus dem CI-Lauf: Client 16, Server 17.</summary>
    [Fact]
    public async Task An_older_client_against_a_newer_server_is_a_failure_with_both_numbers()
    {
        var check = await RunAsync(Postgres(clientMajor: 16, serverMajor: 17));

        check.Status.Should().Be(CheckStatus.Fail,
            "ein Betreiber in dieser Lage kann nicht sichern — das ist kein Hinweis, sondern ein Fehler");
        check.Summary.Should().Contain("16").And.Contain("17");
        check.SafeDetails.Should().Contain(new KeyValuePair<string, string>("client_hauptversion", "16"));
        check.SafeDetails.Should().Contain(new KeyValuePair<string, string>("server_hauptversion", "17"));
        check.Remediation.Should().NotBeNullOrWhiteSpace();
        check.Remediation.Should().Contain(PostgresTools.BinDirectoryVariable,
            "die Abhilfe muss den Weg nennen, der ohne neues Paket auskommt");
    }

    /// <summary>
    /// Die Gegenrichtung ist erlaubt: <c>pg_dump</c> sichert aeltere Server. Ohne diese Zeile
    /// koennte der Check auch "alles ausser gleich ist rot" heissen und waere trotzdem gruen.
    /// </summary>
    [Theory]
    [InlineData(17, 17)]
    [InlineData(18, 17)]
    [InlineData(17, 13)]
    public async Task A_client_at_least_as_new_as_the_server_passes(int client, int server)
    {
        var check = await RunAsync(Postgres(client, server));

        check.Status.Should().Be(CheckStatus.Pass);
        check.Summary.Should().Contain(server.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Die Stop-Bedingung in einer Zeile: Ohne Serverversion wird <b>keine</b> Vertraeglichkeit
    /// behauptet. Ein 'Pass' waere hier die gefaehrlichste aller Antworten — er beruhigt genau den,
    /// der sich gerade auf seinen Rueckweg verlaesst.
    /// </summary>
    [Fact]
    public async Task Without_a_server_version_the_check_says_so_instead_of_claiming_compatibility()
    {
        var check = await RunAsync(Postgres(clientMajor: 16, serverMajor: null));

        check.Status.Should().Be(CheckStatus.Warning);
        check.Status.Should().NotBe(CheckStatus.Pass);
        check.SafeDetails.Should().Contain(
            new KeyValuePair<string, string>("server_hauptversion", "nicht ermittelt"));
    }

    /// <summary>Dasselbe, wenn die Datenbank gar nicht erreichbar ist.</summary>
    [Fact]
    public async Task An_unreachable_database_is_not_a_compatibility_statement()
    {
        var context = Context(
            "postgres",
            new FakeDatabaseProbe(new DatabaseDiagnosticFacts(false, "Verbindung abgelehnt")),
            new FakeToolProbe(new PostgresBackupToolFacts(true, "/usr/bin/pg_dump", 16)));

        var check = await new PostgresBackupToolVersionCheck()
            .RunAsync(context, TestContext.Current.CancellationToken);

        check.Status.Should().NotBe(CheckStatus.Pass);
        check.Summary.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Und wenn der gefundene Client keine lesbare Version nennt.</summary>
    [Fact]
    public async Task A_client_without_a_readable_version_is_not_a_compatibility_statement()
    {
        var check = await RunAsync(Postgres(clientMajor: null, serverMajor: 17));

        check.Status.Should().Be(CheckStatus.Warning);
        check.Summary.Should().Contain("unbeantwortet");
    }

    /// <summary>
    /// Ohne Werkzeuge gibt es ueberhaupt keine Sicherung (ADR-0024 E2) — und auch das ist ein
    /// Befund und kein Schweigen.
    /// </summary>
    [Fact]
    public async Task Without_the_tools_the_check_reports_that_there_is_no_backup_at_all()
    {
        var context = Context(
            "postgres",
            new FakeDatabaseProbe(new DatabaseDiagnosticFacts(true, null, [], [], "17.10", 17)),
            new FakeToolProbe(new PostgresBackupToolFacts(false)));

        var check = await new PostgresBackupToolVersionCheck()
            .RunAsync(context, TestContext.Current.CancellationToken);

        check.Status.Should().Be(CheckStatus.Warning);
        check.Summary.Should().Contain("pg_dump");
    }

    /// <summary>
    /// Auf SQLite ist der Befund gegenstandslos — aber er wird <b>uebersprungen mit Grund</b> und
    /// nicht stillschweigend bestanden.
    /// </summary>
    [Fact]
    public async Task On_sqlite_the_check_is_skipped_with_a_reason()
    {
        var check = await new PostgresBackupToolVersionCheck()
            .RunAsync(Context("sqlite", null, null), TestContext.Current.CancellationToken);

        check.Status.Should().Be(CheckStatus.Skipped);
        check.Summary.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Ohne verdrahtete Sonde ebenso: kein Urteil ohne Grundlage.</summary>
    [Fact]
    public async Task Without_a_probe_the_check_is_skipped_with_a_reason()
    {
        var check = await new PostgresBackupToolVersionCheck()
            .RunAsync(Context("postgres", null, null), TestContext.Current.CancellationToken);

        check.Status.Should().Be(CheckStatus.Skipped);
        check.Summary.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Der Code steht im ausgelieferten Satz. Ohne diese Zeile koennte der Check vollstaendig
    /// richtig sein und trotzdem in keinem Bericht auftauchen.
    /// </summary>
    [Fact]
    public void The_check_ships_with_the_default_set()
    {
        DiagnosticService.DefaultChecks.Select(c => c.Code)
            .Should().Contain(DiagnosticCodes.PostgresBackupToolVersion);
        DiagnosticCodes.InstanceReport.Should().Contain(DiagnosticCodes.PostgresBackupToolVersion);
    }

    // ── Die echte Sonde auf DIESEM Rechner ─────────────────────────────────────────────────────

    /// <summary>
    /// Die Sonde beschreibt dieselbe Werkzeuglage, die auch die Sicherung vorfindet — geprueft ohne
    /// feste Erwartung, weil sie auf Rechnern mit und ohne Clientpaket laufen muss.
    /// </summary>
    [Fact]
    public async Task The_probe_describes_the_same_tools_the_backup_would_use()
    {
        var located = PostgresTools.TryLocate(out var toolset);

        var facts = await new PostgresBackupToolProbe()
            .DescribeAsync(TestContext.Current.CancellationToken);

        facts.Located.Should().Be(located);
        facts.DumpPath.Should().Be(located ? toolset!.DumpPath : null);
        if (located)
        {
            facts.ClientMajorVersion.Should().BeGreaterThan(0,
                "ein gefundenes pg_dump muss seine Version nennen koennen");
        }
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private static Task<DiagnosticCheck> RunAsync(DiagnosticContext context)
        => new PostgresBackupToolVersionCheck().RunAsync(context, TestContext.Current.CancellationToken);

    private static DiagnosticContext Postgres(int? clientMajor, int? serverMajor) => Context(
        "postgres",
        new FakeDatabaseProbe(new DatabaseDiagnosticFacts(
            true, null, [], [],
            serverMajor?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            serverMajor)),
        new FakeToolProbe(new PostgresBackupToolFacts(true, "/usr/bin/pg_dump", clientMajor)));

    private static DiagnosticContext Context(
        string provider, IDatabaseDiagnosticProbe? database, IPostgresBackupToolProbe? tools)
        => new()
        {
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BIFROST_DB_PROVIDER"] = provider,
                ["BIFROST_DB_CONNECTION"] = "Host=127.0.0.1;Database=bifrost",
            },
            Database = database,
            PostgresBackupTools = tools,
        };

    private sealed class FakeDatabaseProbe : IDatabaseDiagnosticProbe
    {
        private readonly DatabaseDiagnosticFacts _facts;

        public FakeDatabaseProbe(DatabaseDiagnosticFacts facts) => _facts = facts;

        public Task<DatabaseDiagnosticFacts> DescribeAsync(CancellationToken ct)
            => Task.FromResult(_facts);
    }

    private sealed class FakeToolProbe : IPostgresBackupToolProbe
    {
        private readonly PostgresBackupToolFacts _facts;

        public FakeToolProbe(PostgresBackupToolFacts facts) => _facts = facts;

        public Task<PostgresBackupToolFacts> DescribeAsync(CancellationToken ct)
            => Task.FromResult(_facts);
    }
}

/// <summary>
/// Die Versionsableitung selbst — EINE Stelle, hier belegt. Sie traegt zwei Lasten: Im Produkt
/// entscheidet sie ueber BFR-DB-0006, in den Testsuiten darueber, welchen Server sie ueberhaupt
/// starten duerfen.
/// </summary>
public sealed class PostgresVersionDerivationTests
{
    [Theory]
    // So antwortet der Client auf Ubuntu 24.04 — die Zeile aus dem CI-Lauf.
    [InlineData("pg_dump (PostgreSQL) 16.14 (Ubuntu 16.14-0ubuntu0.24.04.1)", 16)]
    [InlineData("pg_dump (PostgreSQL) 17.2 (Debian 17.2-1)", 17)]
    [InlineData("pg_restore (PostgreSQL) 18.0", 18)]
    // Und so meldet sich ein Server ueber die offene Verbindung.
    [InlineData("17.10", 17)]
    [InlineData("18.1 (Debian)", 18)]
    public void The_major_version_is_read_from_both_shapes_that_occur(string text, int expected)
        => PostgresTools.ParseMajorVersion(text).Should().Be(expected);

    /// <summary>
    /// Unlesbar heisst unlesbar. Ein Ersatzwert waere hier die Wurzel des ganzen Problems: Aus ihm
    /// wuerde eine Vertraeglichkeit abgeleitet, die niemand geprueft hat.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pg_dump: command not found")]
    public void An_unreadable_version_is_null_and_not_a_guess(string? text)
        => PostgresTools.ParseMajorVersion(text).Should().BeNull();

    // Die Vergleichsregel selbst ("ein Client sichert nur Server bis zu seiner eigenen
    // Hauptversion") steht einmal, im Check BFR-DB-0006, und wird oben in
    // PostgresBackupToolVersionCheckTests belegt — nicht hier ein zweites Mal.

    /// <summary>
    /// Die Meldung im Ernstfall sagt, was zu tun ist — und nur dann, wenn es wirklich um die
    /// Versionen geht.
    /// </summary>
    [Fact]
    public void A_version_mismatch_on_stderr_is_explained_instead_of_quoted()
    {
        var explained = PostgresTools.ExplainFailure(
            "pg_dump: error: aborting because of server version mismatch\n"
            + "pg_dump: detail: server version: 17.10; pg_dump version: 16.14",
            PostgresTools.DumpProgram);

        explained.Should().Contain(PostgresTools.BinDirectoryVariable);
        explained.Should().Contain(PostgresTools.VersionDiagnosticCode,
            "die Meldung soll auf den Befund zeigen, der die Lage VORHER gezeigt haette");
    }

    [Fact]
    public void Any_other_failure_is_left_alone()
        => PostgresTools.ExplainFailure("pg_dump: error: connection to server failed", "pg_dump")
            .Should().BeEmpty();

    // ── Das Serverabbild der Testsuiten ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(13, "postgres:13-alpine")]
    [InlineData(16, "postgres:16-alpine")]
    [InlineData(18, "postgres:18-alpine")]
    // Nach oben gekappt: Ein neuerer Client darf einen aelteren Server sichern, und ein Abbild, das
    // es nicht gibt, scheitert mit "manifest unknown" statt mit einem Befund.
    [InlineData(21, "postgres:18-alpine")]
    public void The_server_image_follows_the_local_client(int clientMajor, string expected)
        => PostgresServerImage.For(clientMajor).Should().Be(expected);

    /// <summary>
    /// Nach unten wird <b>nicht</b> gekappt: Ein Client 12 kann einen Server 13 nicht sichern.
    /// Lieber kein Feld als ein Feld, das nur den Versionsunterschied vorfuehrt.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(null)]
    public void Without_a_usable_client_there_is_no_image(int? clientMajor)
        => PostgresServerImage.For(clientMajor).Should().BeNull();
}
