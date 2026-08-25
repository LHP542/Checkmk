using Checkmk.App.Services;
using Checkmk.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;

namespace Checkmk.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IConnectionSettingsStore _store;
    private readonly ICheckmkClientProvider _clients;

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _site = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _secret = "";
    [ObservableProperty] private bool _useHttps = true;
    [ObservableProperty] private bool _ignoreCertificateErrors;

    /// <summary>Weitere Sites am selben Server (kommasepariert) — z. B. "LHP-Prod, Schul_IT".</summary>
    [ObservableProperty] private string _knownSitesCsv = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUserBasic))]
    [NotifyPropertyChangedFor(nameof(IsAutomationBearer))]
    [NotifyPropertyChangedFor(nameof(UsernameLabel))]
    [NotifyPropertyChangedFor(nameof(SecretLabel))]
    [NotifyPropertyChangedFor(nameof(UsernameHint))]
    [NotifyPropertyChangedFor(nameof(SecretHint))]
    private CheckmkAuthMode _authMode;

    /// <summary>Two-way-bindable Convenience fuer die RadioButtons.</summary>
    public bool IsUserBasic
    {
        get => AuthMode == CheckmkAuthMode.UserBasic;
        set { if (value) AuthMode = CheckmkAuthMode.UserBasic; }
    }

    public bool IsAutomationBearer
    {
        get => AuthMode == CheckmkAuthMode.AutomationBearer;
        set { if (value) AuthMode = CheckmkAuthMode.AutomationBearer; }
    }

    public string UsernameLabel => IsUserBasic ? "Windows-/LDAP-Anmeldename" : "Automation-User";
    public string SecretLabel => IsUserBasic ? "Windows-Passwort (LDAP)" : "Automation-Secret";
    public string UsernameHint => IsUserBasic
        ? $"Default: dein Windows-User ({Environment.UserName}). Damit taucht dein Name in Checkmks Audit-Log auf."
        : "Dedizierter Automation-User (nicht personengebunden).";
    public string SecretHint => IsUserBasic
        ? "Dein AD-Passwort (nicht das GUI-Passwort eines Automation-Users). Wird DPAPI-verschlüsselt lokal gespeichert."
        : "Automation-Secret aus der User-Verwaltung in Checkmk — nicht das GUI-Passwort.";

    public string StorageLocationLabel { get; }

    /// <summary>
    /// Startet das Cockpit beim Anmelden mit.
    ///
    /// <b>Wirkt sofort, nicht erst beim Speichern.</b> Der Autostart ist keine
    /// Verbindungseinstellung, die zusammen mit Host und Secret in
    /// <c>settings.json</c> gehört, sondern ein Eintrag in der Registry des
    /// angemeldeten Benutzers. Ihn an „Speichern" zu hängen hieße, dass ein
    /// „Abbrechen" ihn stillschweigend zurücknimmt — oder eben nicht, je
    /// nachdem, wie man es baut. Ein Häkchen, das sofort tut, was draufsteht,
    /// ist ehrlicher.
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value)) return;
            if (!OperatingSystem.IsWindows()) return;

            if (AutoStart.Set(value) is { } error)
            {
                StatusMessage = error;
                // Zurueckdrehen, damit das Haekchen nicht etwas behauptet, das
                // nicht passiert ist.
                SetProperty(ref _startWithWindows, AutoStart.IsEnabled,
                    nameof(StartWithWindows));
                return;
            }

            StatusMessage = value
                ? "Autostart eingeschaltet — das Cockpit startet künftig ins Tray."
                : "Autostart ausgeschaltet.";
        }
    }
    private bool _startWithWindows;

    /// <summary>Wird true, sobald erfolgreich gespeichert wurde (Fenster kann schliessen).</summary>
    public bool Saved { get; private set; }

    public event EventHandler? RequestClose;

    public SettingsViewModel(IConnectionSettingsStore store, ICheckmkClientProvider clients)
    {
        _store = store;
        _clients = clients;

        var s = _store.Load();
        Host = s.Host;
        Site = s.Site;
        AuthMode = s.AuthMode;
        // Bei erstmaliger Einrichtung (kein User gespeichert) Windows-User vorbelegen.
        Username = string.IsNullOrWhiteSpace(s.Username) ? Environment.UserName : s.Username;
        UseHttps = s.UseHttps;
        IgnoreCertificateErrors = s.IgnoreCertificateErrors;
        Secret = _store.LoadSecret(s) ?? "";
        KnownSitesCsv = string.Join(", ", s.KnownSites);

        // Direkt aus der Registry, nicht aus settings.json: Der Eintrag kann
        // auch von aussen verschwunden sein (Profil neu, Aufraeum-Skript), und
        // dann soll das Haekchen das zeigen statt zu behaupten, es sei an.
        _startWithWindows = OperatingSystem.IsWindows() && AutoStart.IsEnabled;

        var isShared = _store.SettingsFilePath.StartsWith(@"\\", StringComparison.Ordinal);
        StorageLocationLabel = isShared
            ? $"Zentrale Datei: {_store.SettingsFilePath}"
            : $"Lokale Datei: {_store.SettingsFilePath}";
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Teste Verbindung…";
            var settings = BuildSettings();
            _clients.Configure(settings, Secret);
            var ver = await _clients.Current!.GetVersionAsync();
            StatusMessage = $"OK — {ver.Edition} {ver.Versions?.Checkmk} (Site {ver.Site}).";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Verbindungstest fehlgeschlagen.");
            StatusMessage = $"Fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Save()
    {
        var settings = BuildSettings();
        try
        {
            _store.Save(settings, Secret);
        }
        catch (Exception ex)
        {
            // Ohne diesen Fang reisst ein nicht beschreibbares Ziel die ganze App
            // mit: die Exception laeuft aus dem RelayCommand in den Avalonia-
            // Dispatcher und beendet den Prozess. Genau das ist Nutzern passiert,
            // deren bootstrap.json auf ein fremdes Benutzerprofil zeigte.
            Log.Error(ex, "Einstellungen konnten nicht gespeichert werden ({Path}).",
                _store.SettingsFilePath);
            StatusMessage = $"Speichern fehlgeschlagen: {ex.Message} — Ziel: {_store.SettingsFilePath}";
            return;   // Dialog offen lassen, damit die Eingaben nicht verloren gehen
        }

        _clients.Configure(settings, Secret);
        Saved = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, EventArgs.Empty);

    private ConnectionSettings BuildSettings() => new()
    {
        Host = Host.Trim(),
        Site = Site.Trim(),
        Username = Username.Trim(),
        AuthMode = AuthMode,
        UseHttps = UseHttps,
        IgnoreCertificateErrors = IgnoreCertificateErrors,
        KnownSites = ParseSitesCsv(KnownSitesCsv)
    };

    private static List<string> ParseSitesCsv(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        return csv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
