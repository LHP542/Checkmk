using Checkmk.App.Services;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Regression zu dem Fehler, bei dem in der <b>zentral geteilten</b> bootstrap.json
/// ein absoluter Profilpfad stand (<c>C:\Users\OsteL\AppData\Roaming\…</c>). Alle
/// anderen Nutzer erbten ihn und bekamen beim Speichern der Einstellungen eine
/// DirectoryNotFoundException — die bis v1.7.9 die ganze App beendet hat.
/// </summary>
public class SettingsPathResolverTests
{
    private const string LocalDefault = @"C:\Users\Meier\AppData\Roaming\Kroste\Checkmk\settings.json";
    private const string MeierProfile = @"C:\Users\Meier";

    [Fact]
    public void Empty_means_user_local()
        => SettingsPathResolver.Resolve("", LocalDefault, MeierProfile).Should().Be(LocalDefault);

    [Fact]
    public void Null_means_user_local()
        => SettingsPathResolver.Resolve(null, LocalDefault, MeierProfile).Should().Be(LocalDefault);

    [Fact]
    public void Whitespace_means_user_local()
        => SettingsPathResolver.Resolve("   ", LocalDefault, MeierProfile).Should().Be(LocalDefault);

    /// <summary>Der gemeldete Produktionsfehler.</summary>
    [Fact]
    public void Foreign_user_profile_is_rejected()
    {
        WindowsOnly.Require();
        var fremd = @"C:\Users\OsteL\AppData\Roaming\Kroste\Checkmk\settings.json";

        SettingsPathResolver.Resolve(fremd, LocalDefault, MeierProfile).Should().Be(LocalDefault);
        SettingsPathResolver.PointsIntoForeignUserProfile(fremd, MeierProfile).Should().BeTrue();
    }

    [Fact]
    public void Own_profile_path_is_kept()
    {
        var eigen = @"C:\Users\Meier\Documents\cockpit.json";

        SettingsPathResolver.PointsIntoForeignUserProfile(eigen, MeierProfile).Should().BeFalse();
        SettingsPathResolver.Resolve(eigen, LocalDefault, MeierProfile).Should().Be(eigen);
    }

    /// <summary>Ein Fileshare ist eine legitime, absichtlich geteilte Ablage.</summary>
    [Fact]
    public void Unc_path_is_kept()
    {
        var unc = @"\\Samba01\542$\CheckMK\settings.json";

        SettingsPathResolver.PointsIntoForeignUserProfile(unc, MeierProfile).Should().BeFalse();
        SettingsPathResolver.Resolve(unc, LocalDefault, MeierProfile).Should().Be(unc);
    }

    [Fact]
    public void Path_outside_any_user_profile_is_kept()
    {
        var eigen = @"D:\cockpit\settings.json";

        SettingsPathResolver.Resolve(eigen, LocalDefault, MeierProfile).Should().Be(eigen);
    }

    [Fact]
    public void Case_differences_do_not_fool_the_check()
        => SettingsPathResolver.PointsIntoForeignUserProfile(
            @"c:\users\MEIER\x.json", MeierProfile).Should().BeFalse();

    [Fact]
    public void A_similarly_named_profile_is_still_foreign()
    {
        WindowsOnly.Require();
        SettingsPathResolver.PointsIntoForeignUserProfile(
        @"C:\Users\Meier2\x.json", MeierProfile).Should().BeTrue(
        "Meier2 ist nicht Meier — der Praefix-Vergleich muss auf der Trennzeichen-Grenze enden");
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        WindowsOnly.Require();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        SettingsPathResolver.Resolve(@"%APPDATA%\Kroste\Checkmk\settings.json", LocalDefault, null)
            .Should().Be(Path.Combine(appData, @"Kroste\Checkmk\settings.json"));
    }

    /// <summary>Unaufloesbare Variable => lieber die Vorgabe als ein Pfad mit '%' darin.</summary>
    [Fact]
    public void Unresolvable_variable_falls_back_to_the_default()
        => SettingsPathResolver.Resolve(@"%GIBT_ES_NICHT_XYZ%\settings.json", LocalDefault, MeierProfile)
            .Should().Be(LocalDefault);

    [Fact]
    public void Without_a_known_user_profile_nothing_is_rejected()
        => SettingsPathResolver.Resolve(@"C:\Users\OsteL\x.json", LocalDefault, null)
            .Should().Be(@"C:\Users\OsteL\x.json");
}
