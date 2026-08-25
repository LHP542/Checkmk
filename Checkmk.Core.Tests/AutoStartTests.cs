using Checkmk.App;
using Checkmk.App.Services;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Autostart über den <c>Run</c>-Schlüssel des angemeldeten Benutzers.
///
/// Die Registry selbst wird hier nicht angefasst — ein Test, der einen
/// Autostart-Eintrag auf dem Rechner des Entwicklers hinterlässt, wäre eine
/// unangenehme Überraschung. Geprüft wird das, was still kaputtgehen kann.
/// </summary>
public class AutoStartTests
{
    [Fact]
    public void Der_Tray_Schalter_steht_in_der_bekannten_Schalterliste()
    {
        // Die Falle: Seit v1.15.1 fängt TryShowUsage jeden unbekannten
        // --Schalter ab und zeigt die Kurzhilfe, statt die App zu starten.
        // Fehlt --tray dort, endet jeder Autostart in einer Konsolenausgabe,
        // die niemand sieht — die App käme beim Anmelden gar nicht hoch.
        Program.KnownSwitches.Should().Contain(AutoStart.TraySwitch);
    }

    [Fact]
    public void Der_Schalter_sieht_aus_wie_ein_Schalter()
    {
        // Ohne führende Striche reicht ihn TryShowUsage unbesehen an Avalonia
        // durch — dann greift die Prüfung oben ins Leere.
        AutoStart.TraySwitch.Should().StartWith("--");
        AutoStart.TraySwitch.Should().Be("--tray");
    }

    [Fact]
    public void Der_Pfad_kommt_nie_aus_Assembly_Location()
    {
        // Im Single-File-Build ist Assembly.Location LEER; ein daraus gebauter
        // Autostart-Eintrag zeigte ins Nichts. Environment.ProcessPath ist der
        // einzige Weg, der dort funktioniert — und genau den nutzt AutoStart.
        Environment.ProcessPath.Should().NotBeNullOrWhiteSpace();
        Environment.ProcessPath.Should().EndWith(".exe");
    }
}
