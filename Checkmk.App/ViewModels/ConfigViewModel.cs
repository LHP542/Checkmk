using System.Collections.ObjectModel;
using Checkmk.App.Services;
using Checkmk.Core;
using Checkmk.Core.Models;
using Checkmk.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;

namespace Checkmk.App.ViewModels;

public sealed partial class ConfigViewModel : ViewModelBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly ICheckmkClientProvider _clients;
    private readonly HostFactsLoader _facts;
    private List<CheckmkObject<HostConfigExtensions>> _allHosts = [];

    public HostFilterCollection Filters { get; }

    /// <summary>Wird nur eingeblendet, wenn die zentrale Einstellung
    /// <c>ShowHostCreation</c> true ist. Steuert die Sichtbarkeit des
    /// Host-anlegen-Formulars im Konfig-Tab.</summary>
    public bool IsHostCreationVisible { get; }

    public ObservableCollection<CheckmkObject<HostConfigExtensions>> Hosts { get; } = [];

    [ObservableProperty]
    private CheckmkObject<HostConfigExtensions>? _selectedHost;

    [ObservableProperty]
    private string _newHostName = "";

    [ObservableProperty]
    private string _newHostFolder = "/";

    [ObservableProperty]
    private string _newHostIp = "";

    [ObservableProperty]
    private string _newHostAlias = "";

    public ConfigViewModel(ICheckmkClientProvider clients, HostFilterCollection filters,
        HostFactsLoader facts, IGlobalSettingsProvider globals)
    {
        _clients = clients;
        Filters = filters;
        _facts = facts;
        IsHostCreationVisible = globals.Current.ShowHostCreation;
        Filters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HostFilterCollection.Active))
                ApplyFilter();
        };
    }

    [RelayCommand]
    private async Task RefreshHostsAsync()
    {
        var client = _clients.Current;
        if (client is null) { StatusMessage = "Nicht konfiguriert."; return; }

        try
        {
            IsBusy = true;
            // Ueber den HostFactsLoader, damit die beiden Caches an EINER Stelle
            // gefuellt werden. Frueher passierte das hier nebenbei — wer den
            // Hosts-Tab nie oeffnete, hatte weder OS-Symbole noch Ortstags.
            var hosts = await _facts.LoadAsync();
            _allHosts = hosts.OrderBy(h => h.Id).ToList();
            ApplyFilter();
            StatusMessage = Filters.Active is { } f
                ? $"{Hosts.Count} von {hosts.Count} Hosts (Filter: {f.Name})."
                : $"{hosts.Count} Hosts geladen.";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Host-Liste laden fehlgeschlagen.");
            StatusMessage = $"Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void ApplyFilter()
    {
        Hosts.Clear();
        IEnumerable<CheckmkObject<HostConfigExtensions>> q = _allHosts;
        if (Filters.Active is { } activeFilter)
            q = q.Where(h => activeFilter.Matches(h.Id ?? ""));
        foreach (var h in q) Hosts.Add(h);
    }

    [RelayCommand]
    private async Task CreateHostAsync()
    {
        var client = _clients.Current;
        if (client is null) { StatusMessage = "Nicht konfiguriert."; return; }
        if (string.IsNullOrWhiteSpace(NewHostName)) { StatusMessage = "Hostname fehlt."; return; }

        try
        {
            IsBusy = true;
            var attrs = new HostAttributes
            {
                IpAddress = string.IsNullOrWhiteSpace(NewHostIp) ? null : NewHostIp,
                Alias = string.IsNullOrWhiteSpace(NewHostAlias) ? null : NewHostAlias
            };
            await client.CreateHostAsync(NewHostName.Trim(), NewHostFolder.Trim(), attrs);
            StatusMessage = $"Host '{NewHostName}' angelegt — nicht vergessen: Änderungen aktivieren.";
            NewHostName = NewHostIp = NewHostAlias = "";
            await RefreshHostsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Host anlegen fehlgeschlagen.");
            StatusMessage = $"Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Bringt einen bestehenden Host ins Monitoring: startet die Service-Discovery
    /// im Modus <c>fix_all</c>, pollt bis der Run fertig ist und aktiviert danach die
    /// Aenderungen. Kann bei vielen Services mehrere Sekunden dauern.
    /// </summary>
    [RelayCommand]
    private async Task DiscoverServicesAsync()
    {
        var client = _clients.Current;
        if (client is null) { StatusMessage = "Nicht konfiguriert."; return; }

        var host = SelectedHost?.Id;
        if (string.IsNullOrWhiteSpace(host))
        {
            StatusMessage = "Kein Host ausgewählt.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = $"Service-Discovery läuft für {host}…";
            await client.DiscoverServicesAsync(host, ServiceDiscoveryMode.FixAll);

            StatusMessage = $"Discovery beendet für {host}. Aktiviere Änderungen…";
            await client.ActivateChangesAsync();
            StatusMessage = $"Fertig — {host} ist im Monitoring.";
            await RefreshHostsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Service-Discovery fuer {Host} fehlgeschlagen.", host);
            StatusMessage = $"Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ActivateChangesAsync()
    {
        var client = _clients.Current;
        if (client is null) { StatusMessage = "Nicht konfiguriert."; return; }

        try
        {
            IsBusy = true;
            StatusMessage = "Aktiviere Änderungen…";
            await client.ActivateChangesAsync();
            StatusMessage = "Änderungen aktiviert.";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Activate Changes fehlgeschlagen.");
            StatusMessage = $"Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}
