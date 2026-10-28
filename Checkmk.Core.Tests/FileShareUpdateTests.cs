using Checkmk.App.Services;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Update-Kanal auf einem Ordner statt auf GitHub — bei uns
/// <c>\\samba01\542$\5424_IT-Basis-Dienste\CheckMK\CheckMK_Copilot</c>.
/// </summary>
public class FileShareUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "cockpit-share-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Fest vorgegebene „laufende" Version. Vorher verglichen die Tests gegen
    /// die Version des <b>Testhosts</b> — damit prüften sie nichts Sinnvolles,
    /// und der eigentliche Fehler (Vergleich gegen <c>GetName().Version</c>,
    /// von MinVer auf <c>Major.0.0.0</c> gesetzt) konnte durchrutschen.
    /// </summary>
    private static readonly Version Current = new(1, 15, 1);

    public FileShareUpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Best-effort */ }
        GC.SuppressFinalize(this);
    }

    private void Package(string name) => File.WriteAllText(Path.Combine(_dir, name), "ZIP");

    private FileShareUpdateChecker Checker() => new(_dir, new StubPreferences(), Current);

    private sealed class StubPreferences : IUpdatePreferences
    {
        public Version? Skipped { get; set; }
        public Version? LoadSkippedVersion() => Skipped;
        public void SaveSkippedVersion(Version version) => Skipped = version;
    }

    /// <summary>Eine Version, die sicher neuer ist als die laufende Assembly.</summary>
    private static string Newer => "1.16.0";

    // --- Erkennung des Kanaltyps ----------------------------------------

    [Theory]
    [InlineData(@"\\samba01\542$\5424_IT-Basis-Dienste\CheckMK\CheckMK_Copilot")]
    [InlineData(@"C:\updates")]
    [InlineData("//server/share")]
    public void Paths_are_recognised_as_a_folder_channel(string channel)
        => FileShareUpdateChecker.LooksLikeFolder(channel).Should().BeTrue();

    [Theory]
    [InlineData("https://git.flexfamily.de/api/v1/repos/LHP542/Checkmk/releases/latest")]
    [InlineData("http://server/x")]
    [InlineData("")]
    [InlineData(null)]
    public void Addresses_are_not_a_folder_channel(string? channel)
        => FileShareUpdateChecker.LooksLikeFolder(channel).Should().BeFalse();

    // --- Paket finden ----------------------------------------------------

    [Fact]
    public async Task A_newer_package_in_the_folder_is_offered()
    {
        Package($"Checkmk-{Newer}-win-x64.zip");

        var result = await Checker().CheckManuallyAsync(TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(UpdateCheckOutcome.UpdateAvailable);
        result.Info!.Version.Should().Be(new Version(1, 16, 0));
        result.Info.WindowsZipUrl.Should().Contain(_dir);
    }

    [Fact]
    public async Task An_older_package_is_not_offered()
    {
        Package("Checkmk-0.1.0-win-x64.zip");

        (await Checker().CheckManuallyAsync(TestContext.Current.CancellationToken)).Outcome.Should().Be(UpdateCheckOutcome.UpToDate);
    }

    [Fact]
    public async Task A_package_one_minor_behind_is_never_offered_as_a_downgrade()
    {
        // Real passiert (2026-08): Laufend 1.15.1, im Ordner 1.14.0, und die
        // Statusleiste meldete „Update auf 1.14.0 verfuegbar". Ursache war der
        // Vergleich gegen Assembly.GetName().Version — MinVer setzt die auf
        // 1.0.0.0, und damit ist jedes Paket ab 1.0.1 „neuer".
        Package("Checkmk-1.14.0-win-x64.zip");

        var result = await Checker().CheckManuallyAsync(TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(UpdateCheckOutcome.UpToDate);
        result.Info.Should().BeNull();
    }

    [Fact]
    public void The_running_version_never_comes_from_the_assembly_version()
    {
        // Die Wache gegen den Rueckfall: AppVersion.Current muss die
        // InformationalVersion aufloesen. Kaeme sie aus GetName().Version,
        // stuende hier Major.0.0.0 — also Build und Revision auf 0 bei einer
        // Minor-Version ungleich 0.
        var display = AppVersion.Display;
        var current = AppVersion.Current;

        display.Should().NotBeNullOrWhiteSpace();
        // Vorabsuffixe sind abgeschnitten: „1.15.1-alpha.0.2" -> 1.15.1
        display.Should().StartWith(
            $"{current.Major}.{current.Minor}.{current.Build}");
    }

    [Fact]
    public async Task The_highest_version_wins_not_the_newest_file()
    {
        // Kopiert jemand ein aelteres Paket zurueck in den Ordner, hat es den
        // neueren Zeitstempel. Nach Datum zu sortieren wuerde daraus ein
        // „Update" auf eine aeltere Version machen.
        Package("Checkmk-1.16.0-win-x64.zip");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Package("Checkmk-1.2.0-win-x64.zip");

        var result = await Checker().CheckManuallyAsync(TestContext.Current.CancellationToken);

        result.Info!.Version.Should().Be(new Version(1, 16, 0));
    }

    [Fact]
    public async Task An_empty_folder_is_a_failed_check_not_an_update()
    {
        (await Checker().CheckManuallyAsync(TestContext.Current.CancellationToken)).Outcome.Should().Be(UpdateCheckOutcome.Failed);
    }

    [Fact]
    public async Task An_unreachable_folder_does_not_throw()
    {
        // Notebook ohne Netzlaufwerk ist der Normalfall, nicht die Stoerung.
        var checker = new FileShareUpdateChecker(
            Path.Combine(_dir, "gibtsnicht"), new StubPreferences(), Current);

        (await checker.CheckManuallyAsync(TestContext.Current.CancellationToken)).Outcome.Should().Be(UpdateCheckOutcome.Failed);
    }

    [Fact]
    public async Task A_file_without_a_version_in_its_name_is_ignored()
    {
        Package("Checkmk-irgendwas-win-x64.zip");

        (await Checker().CheckManuallyAsync(TestContext.Current.CancellationToken)).Outcome.Should().Be(UpdateCheckOutcome.Failed);
    }

    // --- Manifest als Index ---------------------------------------------

    [Fact]
    public async Task A_manifest_decides_which_package_is_current()
    {
        // Liegen zwei Pakete im Ordner, gibt das Manifest den Ausschlag —
        // sonst koennte ein danebengelegtes ZIP das signierte ueberstimmen.
        Package($"Checkmk-{Newer}-win-x64.zip");
        Package("Checkmk-1.17.0-win-x64.zip");

        File.WriteAllText(Path.Combine(_dir, "update.json"), UpdateSignature.ToJson(
            new SignedUpdateManifest
            {
                Version = Newer,
                File = $"Checkmk-{Newer}-win-x64.zip",
                Sha256 = "egal", Size = 3, Signature = "egal"
            }));

        var result = await Checker().CheckManuallyAsync(TestContext.Current.CancellationToken);

        result.Info!.Version.Should().Be(new Version(1, 16, 0));
        result.Info.ManifestUrl.Should().EndWith("update.json");
    }

    // --- Release-Notes ---------------------------------------------------

    [Fact]
    public async Task Version_specific_notes_beat_the_collected_file()
    {
        Package($"Checkmk-{Newer}-win-x64.zip");
        File.WriteAllText(Path.Combine(_dir, "RELEASE_NOTES.md"), "Sammeldatei");
        File.WriteAllText(Path.Combine(_dir, $"v{Newer}.md"), "Genau diese Version");

        var result = await Checker().CheckManuallyAsync(TestContext.Current.CancellationToken);

        result.Info!.ReleaseNotes.Should().Be("Genau diese Version");
    }

    [Fact]
    public async Task Missing_notes_do_not_hide_the_update()
    {
        Package($"Checkmk-{Newer}-win-x64.zip");

        var result = await Checker().CheckManuallyAsync(TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(UpdateCheckOutcome.UpdateAvailable);
        result.Info!.ReleaseNotes.Should().BeEmpty();
    }

    // --- Uebersprungene Version -----------------------------------------

    [Fact]
    public async Task A_skipped_version_is_honoured_only_on_the_automatic_check()
    {
        Package($"Checkmk-{Newer}-win-x64.zip");
        var prefs = new StubPreferences { Skipped = new Version(1, 16, 0) };
        var checker = new FileShareUpdateChecker(_dir, prefs, Current);

        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        // Wer aktiv prueft, will es wissen — die Uebersprungenheit gilt dort nicht.
        (await checker.CheckManuallyAsync(TestContext.Current.CancellationToken)).Outcome
            .Should().Be(UpdateCheckOutcome.UpdateAvailable);
    }
}
