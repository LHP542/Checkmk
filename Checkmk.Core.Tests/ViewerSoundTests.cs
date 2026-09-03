using Checkmk.App.Services;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Warnton, wenn die Kiosk-Ausgabe wegen einer Verschlechterung nach vorn
/// kommt.
///
/// Ein Bildschirm im Leitstand oder beim Wachschutz steht oft seitlich — dass
/// das Fenster aufgeht, sieht nur, wer gerade hinschaut.
/// </summary>
public class ViewerSoundTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "cockpit-viewersound-tests-" + Guid.NewGuid().ToString("N"));

    public ViewerSoundTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Best-effort */ }
        GC.SuppressFinalize(this);
    }

    private ViewerProfile Load(string json)
    {
        var path = Path.Combine(_dir, ViewerProfile.FileName);
        File.WriteAllText(path, json);
        return ViewerProfile.LoadFrom(path)!;
    }

    private const string MinimalConnection = """
        "connection": {
          "host": "cmk.beispiel.intern", "site": "Musterstadt",
          "username": "viewer", "secret": "geheim"
        }
        """;

    /// <summary>
    /// <b>Der wichtigste Test hier.</b> Eine bestehende Kiosk-Ausgabe darf nach
    /// einem Update nicht ungefragt anfangen zu piepen — dieselbe Ueberlegung
    /// wie bei <c>map.show</c>.
    /// </summary>
    [Fact]
    public void Ohne_Angabe_bleibt_der_Ton_aus()
    {
        var profile = Load($$"""
            { {{MinimalConnection}} }
            """);

        profile.PopUpSound.Should().BeFalse();
        profile.PopUpSoundFile.Should().BeEmpty();
    }

    [Fact]
    public void Der_Ton_laesst_sich_einschalten()
    {
        var profile = Load($$"""
            { {{MinimalConnection}}, "popUpSound": true }
            """);

        profile.PopUpSound.Should().BeTrue();
        profile.PopUpSoundFile.Should().BeEmpty("leer heisst Systemklang");
    }

    [Fact]
    public void Eine_eigene_WAV_Datei_wird_uebernommen()
    {
        var profile = Load($$"""
            {
              {{MinimalConnection}},
              "popUpSound": true,
              "popUpSoundFile": "C:\\kiosk\\alarm.wav"
            }
            """);

        profile.PopUpSound.Should().BeTrue();
        profile.PopUpSoundFile.Should().Be(@"C:\kiosk\alarm.wav");
    }

    /// <summary>
    /// Das Aufspringen bleibt an; der Ton haengt daran. Ein Profil, das
    /// <c>popUpOnProblem</c> abschaltet, bekommt auch keinen Ton — ein Ton ohne
    /// sichtbare Ursache waere schlimmer als keiner.
    /// </summary>
    [Fact]
    public void Ohne_Aufspringen_ergibt_der_Ton_keinen_Sinn()
    {
        var profile = Load($$"""
            {
              {{MinimalConnection}},
              "popUpOnProblem": false,
              "popUpSound": true
            }
            """);

        profile.PopUpOnProblem.Should().BeFalse();
        profile.PopUpSound.Should().BeTrue(
            "der Wert wird gelesen — ausgewertet wird er nur im Aufspring-Zweig");
    }

    /// <summary>Der Warnton darf nie werfen: Ein Kiosk-Rechner hat womoeglich
    /// gar keine Tonausgabe, und das Aufspringen des Fensters ist das
    /// eigentliche Signal.</summary>
    [Fact]
    public void Ein_nicht_abspielbarer_Ton_wirft_nicht()
    {
        if (!OperatingSystem.IsWindows()) return;

        var act = () =>
        {
            AlertSound.PlayProblem(Path.Combine(_dir, "gibt-es-nicht.wav"));
            AlertSound.PlayProblem(null);
        };

        act.Should().NotThrow();
    }
}
