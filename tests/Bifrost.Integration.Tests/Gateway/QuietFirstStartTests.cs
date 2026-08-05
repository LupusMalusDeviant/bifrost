using AwesomeAssertions;

using Bifrost.Integration.Tests.Gateway;

using Xunit;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// <b>BFR-F-0052 — ein fehlerfreier Erststart schreibt keine Fehlerzeile.</b>
/// <para>
/// Im Piloten vom 2026-08-05 protokollierte die gesunde Erstinstallation <b>vier</b> Zeilen auf
/// Stufe <c>Error</c> samt Ausnahmetext: zweimal „no such table". Beides waren Sonden, deren
/// Fehlschlag die Antwort war — der Aufrufer fing sie und kam damit zurecht, aber EF Core meldete
/// sie trotzdem als Fehler.
/// </para>
/// <para>
/// <b>Warum das mehr ist als Kosmetik.</b> Wer sein Log auf Fehler alarmiert, wird von der
/// funktionierenden Erstinstallation geweckt. Und was danach kommt, ist schlimmer: Man gewöhnt sich
/// daran, dass da Fehler stehen, die nichts bedeuten — und übersieht den, der etwas bedeutet.
/// </para>
/// <para>
/// <b>Und eine Sonde hatte eine Nebenwirkung.</b> Der fehlgeschlagene Zugriff legte bei SQLite die
/// Datenbankdatei an. Damit war die Existenzprüfung des Starts („lag die Datenbank schon vorher
/// da?") ab dem allerersten Start unbrauchbar — genau die Prüfung, an der eine bestehende
/// Alt-Installation von einer neuen unterschieden wird. Der leere Rumpf einer Fehlerzeile war also
/// die Spitze von etwas.
/// </para>
/// </summary>
public class QuietFirstStartTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _fixture;

    public QuietFirstStartTests(GatewayFixture fixture) => _fixture = fixture;

    [Fact]
    public void A_healthy_first_start_logs_nothing_on_error_level()
    {
        // Der Zugriff auf die Fixture ist der Start: Sie baut den echten Host aus Program.cs auf
        // einem frischen Datenverzeichnis auf.
        _ = _fixture.Services;

        var errors = _fixture.Log.Lines
            .Where(line => line.StartsWith("[Error]", StringComparison.Ordinal)
                || line.StartsWith("[Critical]", StringComparison.Ordinal))
            .ToList();

        errors.Should().BeEmpty(
            "eine Fehlerzeile im Normalbetrieb entwertet die Stufe fuer den Fall, in dem sie "
            + "etwas bedeutet:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// Die Gegenprobe zur Nebenwirkung: Bis die Datenbank vom Start angelegt wird, darf sie niemand
    /// vorher anlegen. Belegt über das Ergebnis, das der Start selbst meldet — eine neu erzeugte
    /// Datenbank heißt <c>CreatedFromMigrations</c>; hätte eine frühere Sonde die Datei angelegt,
    /// hinge das Ergebnis an einer geworfenen Ausnahme statt an der Existenzprüfung.
    /// </summary>
    [Fact]
    public void The_first_start_reports_a_newly_created_database()
    {
        _ = _fixture.Services;

        _fixture.Log.Lines
            .Should().Contain(line => line.Contains("CreatedFromMigrations", StringComparison.Ordinal));
    }
}
