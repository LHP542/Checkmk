using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Checkmk.App;
using Checkmk.App.Services;
using Checkmk.App.Services.Plugins;
using Checkmk.App.ViewModels;
using Checkmk.Core.Models;
using Checkmk.PluginContracts;
using Microsoft.Extensions.DependencyInjection;

namespace Checkmk.App.Views;

public partial class StatusView : UserControl
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public StatusView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is StatusViewModel vm)
                vm.SpotlightRequested += OnSpotlightRequested;
        };

        SetUpColumns();

        // Plugin-Kontextmenue-Eintraege dynamisch anhaengen (unten in beiden Menues).
        var gridMenu = this.FindControl<ContextMenu>("ServiceGridContextMenu");
        if (gridMenu is not null)
            PluginContextMenuAdapter.Attach(gridMenu, ContextMenuLocation.StatusServiceRow,
                () => BuildTargetForStatus());
        var treeMenu = this.FindControl<ContextMenu>("ServiceTreeContextMenu");
        if (treeMenu is not null)
            PluginContextMenuAdapter.Attach(treeMenu, ContextMenuLocation.StatusHostNode,
                () => BuildTargetForStatus());
    }

    // --- Spalten: Aufbau, Kontextmenue, Persistenz -----------------------

    private IColumnLayoutStore? _columnStore;
    private bool _columnsLocked;   // Viewer-Modus: Auswahl kommt aus viewer.json
    private bool _suppressColumnSave;

    private DataGrid? Grid => this.FindControl<DataGrid>("ServiceGrid");

    /// <summary>
    /// Baut den Spaltensatz. Im Viewer-Modus fest aus <c>viewer.json</c>, sonst aus
    /// <c>columns.json</c> — dort darf der Anwender per Rechtsklick auf die Kopfzeile
    /// ein-/ausblenden und per Drag umsortieren.
    /// </summary>
    private void SetUpColumns()
    {
        var grid = Grid;
        if (grid is null)
        {
            Log.Warn("DataGrid 'ServiceGrid' nicht gefunden — Tabelle bleibt ohne Spalten.");
            return;
        }

        // GetService statt GetRequiredService wegen des XAML-Previewers (kein DI).
        var profile = App.Services?.GetService<ViewerMode>()?.Profile;
        if (profile is not null)
        {
            _columnsLocked = true;
            grid.CanUserReorderColumns = false;
            var keys = profile.Columns.Count > 0 ? profile.Columns : [.. ViewerProfile.DefaultColumns];
            foreach (var column in StatusColumnFactory.Build(keys))
                grid.Columns.Add(column);
            Log.Info("Viewer-Spalten gesetzt ({Count}): {Headers}",
                grid.Columns.Count, string.Join(" | ", grid.Columns.Select(c => c.Header?.ToString())));
            return;
        }

        _columnStore = App.Services?.GetService<IColumnLayoutStore>();
        var stored = _columnStore?.Load(StatusGridColumns.StatusViewId) ?? new ColumnLayout();
        StatusGridColumns.Apply(grid, StatusGridColumns.Merge(stored));

        // Vom Anwender gezogene Reihenfolge sofort sichern.
        grid.ColumnDisplayIndexChanged += (_, _) => SaveColumnLayout();

        // Rechtsklick auf die Kopfzeile -> Spaltenliste statt Zeilen-Kontextmenue.
        grid.AddHandler(ContextRequestedEvent, OnGridContextRequested, RoutingStrategies.Tunnel);

        // „Spalten"-Untermenue im Zeilen-Kontextmenue erst beim Aufklappen fuellen,
        // damit die Haken den aktuellen Stand zeigen.
        if (this.FindControl<MenuItem>("ColumnsSubmenu") is { } submenu)
            submenu.SubmenuOpened += (_, _) => submenu.ItemsSource = BuildColumnMenuItems();
    }

    /// <summary>
    /// Speichert Reihenfolge, Sichtbarkeit und Breiten. Wird vom MainWindow beim
    /// Schliessen aufgerufen — Avalonias DataGrid meldet das Ende eines
    /// Spalten-Resize nicht, deshalb fangen wir die Breiten dort ein.
    /// </summary>
    internal void SaveColumnLayout()
    {
        if (_columnsLocked || _suppressColumnSave || _columnStore is null) return;
        if (Grid is not { } grid) return;
        _columnStore.Save(StatusGridColumns.StatusViewId, StatusGridColumns.Capture(grid));
    }

    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_columnsLocked) return;
        if (e.Source is not Visual source) return;
        if (!IsInsideColumnHeader(source)) return;   // Zeilen behalten ihr eigenes Menue

        var menu = new ContextMenu { ItemsSource = BuildColumnMenuItems() };
        menu.Open(Grid);
        e.Handled = true;
    }

    /// <summary>Klick in der Kopfzeile? Der Visual-Tree-Walk trennt Header von Zellen —
    /// beide liegen im selben DataGrid und liefern dasselbe ContextRequested.</summary>
    private static bool IsInsideColumnHeader(Visual source)
    {
        for (Visual? v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is DataGridColumnHeader or DataGridColumnHeadersPresenter)
                return true;
            if (v is DataGridRow)
                return false;
        }
        return false;
    }

    private List<MenuItem> BuildColumnMenuItems()
    {
        var items = new List<MenuItem>();
        if (Grid is not { } grid) return items;

        foreach (var column in grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            if (column.Tag is not string key) continue;
            var item = new MenuItem
            {
                Header = StatusColumnFactory.LabelFor(key),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
                StaysOpenOnClick = true
            };
            item.Click += (_, _) => ToggleColumn(column);
            items.Add(item);
        }

        items.Add(new MenuItem { Header = "-" });

        var showAll = new MenuItem { Header = "Alle einblenden" };
        showAll.Click += (_, _) =>
        {
            foreach (var c in grid.Columns) c.IsVisible = true;
            SaveColumnLayout();
        };
        items.Add(showAll);

        var reset = new MenuItem { Header = "Auf Vorgabe zurücksetzen" };
        reset.Click += (_, _) => ResetColumns();
        items.Add(reset);

        return items;
    }

    /// <summary>Letzte sichtbare Spalte nicht ausblenden lassen — eine Tabelle ohne
    /// jede Spalte sieht wie ein Absturz aus und man kaeme per Rechtsklick auf die
    /// dann fehlende Kopfzeile auch nicht mehr ans Menue.</summary>
    private void ToggleColumn(DataGridColumn column)
    {
        if (Grid is not { } grid) return;

        if (column.IsVisible && grid.Columns.Count(c => c.IsVisible) <= 1)
        {
            if (DataContext is StatusViewModel vm)
                vm.StatusMessage = "Mindestens eine Spalte muss sichtbar bleiben.";
            return;
        }

        column.IsVisible = !column.IsVisible;
        SaveColumnLayout();
    }

    private void ResetColumns()
    {
        if (Grid is not { } grid) return;
        _suppressColumnSave = true;
        try
        {
            _columnStore?.Reset(StatusGridColumns.StatusViewId);
            StatusGridColumns.Apply(grid, StatusGridColumns.Merge(new ColumnLayout()));
        }
        finally { _suppressColumnSave = false; }
        Log.Info("Spaltenanordnung auf Vorgabe zurueckgesetzt.");
    }

    private ContextMenuTarget? BuildTargetForStatus()
    {
        var host = GetTargetHostName();
        if (string.IsNullOrEmpty(host)) return null;
        // Eine Host-Zeile (Host DOWN) ist fuer Plugins ein Host-Ziel, kein Service.
        var svc = GetTargetService() is { IsHostRow: false } s ? s : null;
        var owner = TopLevel.GetTopLevel(this) as Window;
        return new ContextMenuTarget(
            svc is null ? ContextMenuLocation.StatusHostNode : ContextMenuLocation.StatusServiceRow,
            host,
            svc?.Description,
            owner);
    }

    private void OnSpotlightRequested(ServiceStatus svc)
    {
        var grid = this.FindControl<DataGrid>("ServiceGrid");
        if (grid is null) return;
        // Auto-Scroll + Selection als Highlight. ScrollIntoView greift auf die
        // Row-Container-Ebene, danach markiert SelectedItem die Zeile im Standard-
        // Selection-Farbschema — sichtbar ohne Custom-Style.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            grid.ScrollIntoView(svc, null);
            grid.SelectedItem = svc;
            Log.Debug("Spotlight auf {Host}/{Service} — markiert={Hit}.",
                svc.HostName, svc.Description, ReferenceEquals(grid.SelectedItem, svc));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private async void OnAcknowledgeClick(object? sender, RoutedEventArgs e)
        => await ShowActionAsync(ServiceActionMode.Acknowledge);

    /// <summary>
    /// Nimmt die Quittierung der markierten Services zurück — ohne Dialog, weil
    /// es nichts einzugeben gibt und die Aktion folgenlos wiederholbar ist.
    /// Funktioniert in Tabelle und Baum über dieselbe Ziel-Auflösung wie Ack.
    /// </summary>
    private async void OnRemoveAckClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite) return;
        await vm.PerformRemoveAcknowledgementAsync(GetTargetServices());
    }

    private async void OnDowntimeClick(object? sender, RoutedEventArgs e)
        => await ShowActionAsync(ServiceActionMode.Downtime);

    private async void OnCommentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var svc = GetTargetService();
        if (svc is null) return;
        vm.SelectedService = svc; // PerformAddCommentAsync arbeitet auf SelectedService

        var dialog = new CommentInputDialog($"{svc.HostName} / {svc.Description}");
        var result = await dialog.ShowDialog<CommentInputResult?>(owner);
        if (result is null) return;

        await vm.PerformAddCommentAsync(result.Comment, result.Persistent);
    }

    /// <summary>Wird aus dem MainWindow-Hotkey-Handler aufgerufen (Ctrl+K/D/A).</summary>
    internal async void TriggerHotkeyAction(ServiceHotkeyAction action)
    {
        var mode = action switch
        {
            ServiceHotkeyAction.Acknowledge => ServiceActionMode.Acknowledge,
            ServiceHotkeyAction.Downtime => ServiceActionMode.Downtime,
            _ => (ServiceActionMode?)null
        };
        if (mode is not null)
            await ShowActionAsync(mode.Value);
        else if (action == ServiceHotkeyAction.Comment)
            OnCommentClick(null, new RoutedEventArgs());
    }

    private async void OnHostSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite || vm.SelectedService is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new HostSettingsDialog(
            vm.SelectedService.HostName,
            App.Services!.GetRequiredService<IHostDomainStore>(),
            App.Services!.GetRequiredService<ISshCredentialStore>());
        await dialog.ShowDialog<bool>(owner);
    }

    private void OnOpenInWebClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || vm.SelectedService is null) return;
        var svc = vm.SelectedService;
        var web = App.Services!.GetRequiredService<CheckmkWebLinker>();
        if (svc.IsHostRow)
            web.OpenHostView(svc.HostName);
        else
            web.OpenServiceView(svc.HostName, svc.Description);
    }

    // Remote-Tools sind Admin-Handgriffe, kein „Gucken" — im Viewer-Modus zu.
    private void OnRdpClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite || vm.SelectedService is null) return;
        App.Services!.GetRequiredService<RemoteTools>().StartRdp(vm.SelectedService.HostName);
    }

    private void OnSshClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite || vm.SelectedService is null) return;
        // Kein User-Argument in Commit B — wird in Commit C aus SshCredentialStore geholt.
        App.Services!.GetRequiredService<RemoteTools>().StartSsh(vm.SelectedService.HostName, null);
    }

    private void OnRemoteShellClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite || vm.SelectedService is null) return;
        var host = vm.SelectedService.HostName;
        var os = vm.OsFor(host);
        App.Services!.GetRequiredService<RemoteTools>().StartRemoteShell(host, os, null);
    }

    private void OnPingClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite || vm.SelectedService is null) return;
        App.Services!.GetRequiredService<RemoteTools>().StartPing(vm.SelectedService.HostName);
    }

    private async void OnManageFiltersClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await new FilterManagerWindow(vm.Filters).ShowDialog(owner);
    }

    private async void OnSaveHostsAsFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var hosts = GetTargetHostNames();
        if (hosts.Count == 0) return;

        var dialog = new NameInputDialog(
            title: "Favorit speichern",
            prompt: $"{hosts.Count} Host(s) als Favorit speichern unter Namen:",
            defaultValue: hosts.Count == 1 ? hosts[0] : "");
        var name = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrWhiteSpace(name)) return;

        vm.Filters.Add(new Models.HostFilter
        {
            Name = name.Trim(),
            ExplicitHosts = hosts.ToList()
        });
        vm.StatusMessage = $"Favorit „{name.Trim()}“ mit {hosts.Count} Host(s) gespeichert.";
    }

    /// <summary>
    /// Ordnet die markierten Hosts einem Bereich zu. Der Weg ueber das
    /// Kontextmenue ist der eigentliche Alltagspfad: Hier stehen die Hosts, die
    /// man gerade vor sich hat, und die Mehrfachauswahl gibt es schon.
    /// Ohne zentrale Datenbank ist <c>AreaViewModel</c> nicht registriert — dann
    /// sagt die Statuszeile, warum nichts passiert.
    /// </summary>
    private async void OnAssignAreaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var areas = App.Services?.GetService<AreaViewModel>();
        if (areas is null)
        {
            vm.StatusMessage = "Bereiche brauchen die zentrale Datenbank — keine Verbindung konfiguriert.";
            return;
        }

        var hosts = GetTargetHostNames();
        if (hosts.Count == 0) return;

        if (areas.AllAreas().Count == 0)
        {
            vm.StatusMessage = "Noch kein Bereich angelegt — im Tab „Bereiche“ anfangen.";
            return;
        }

        var dialog = new AreaPickerDialog($"{hosts.Count} Host(s) zuordnen:", areas.Roots);
        var result = await dialog.ShowDialog<AreaPickResult?>(owner);
        if (result is null) return;

        await areas.AssignAsync(hosts, result.AreaId);
        vm.StatusMessage = areas.StatusMessage ?? "";
    }

    private async void OnAddHostsToFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm || !vm.CanWrite) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var hosts = GetTargetHostNames();
        if (hosts.Count == 0) return;

        var candidates = vm.Filters.Filters
            .Where(f => f.ExplicitHosts is { Count: > 0 })
            .ToList();

        // Kein passender Favorit vorhanden -> gleich neuen anlegen, damit die
        // Aktion nicht ins Leere laeuft (User hat rechtsgeklickt und erwartet
        // *irgendein* Ergebnis).
        if (candidates.Count == 0)
        {
            OnSaveHostsAsFavoriteClick(sender, e);
            return;
        }

        var dialog = new FavoritePickerDialog(
            $"{hosts.Count} Host(s) zu welchem Favoriten hinzufügen?",
            candidates);
        var chosen = await dialog.ShowDialog<Models.HostFilter?>(owner);
        if (chosen is null) return;

        var before = chosen.ExplicitHosts.Count;
        foreach (var h in hosts)
        {
            if (!chosen.ExplicitHosts.Any(x => string.Equals(x, h, System.StringComparison.OrdinalIgnoreCase)))
                chosen.ExplicitHosts.Add(h);
        }
        var added = chosen.ExplicitHosts.Count - before;
        vm.Filters.Update();
        vm.StatusMessage = $"{added} Host(s) zu Favorit „{chosen.Name}“ hinzugefuegt.";
    }

    private List<string> GetTargetHostNames()
    {
        return GetTargetServices()
            .Select(s => s.HostName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatusViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Status als CSV exportieren",
            SuggestedFileName = $"checkmk-status-{System.DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        if (file is null) return;

        try
        {
            var bytes = CsvExporter.ToCsvBytes(vm.Services);
            await System.IO.File.WriteAllBytesAsync(file.Path.LocalPath, bytes);
            vm.StatusMessage = $"{vm.Services.Count} Zeilen exportiert: {file.Name}";
        }
        catch (System.Exception ex)
        {
            vm.StatusMessage = $"CSV-Export fehlgeschlagen: {ex.Message}";
        }
    }

    private void OnServiceDoubleTapped(object? sender, TappedEventArgs e) => OpenHostDetails();
    private void OnOpenHostDetailsClick(object? sender, RoutedEventArgs e) => OpenHostDetails();

    private void OpenHostDetails()
    {
        if (DataContext is not StatusViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var host = GetTargetHostName();
        if (host is null) return;

        var clients = App.Services!.GetRequiredService<ICheckmkClientProvider>();
        // CanWrite weiterreichen: das Detailfenster zeigt sonst Ack/Downtime/
        // Kommentar-Buttons, obwohl der Status-Tab sie gerade ausblendet.
        var detailVm = new HostDetailViewModel(clients, host, vm.CanWrite);
        new HostDetailWindow(detailVm).Show(owner);
    }

    private async Task ShowActionAsync(ServiceActionMode mode)
    {
        // CanWrite blockt hier zusaetzlich zur ausgeblendeten UI — der Weg ueber
        // Hotkeys und Plugin-Kontextmenues laeuft ebenfalls hier durch.
        if (DataContext is not StatusViewModel vm || !vm.CanWrite) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var selected = GetTargetServices();
        if (selected.Count == 0) return;

        ServiceActionDialogViewModel dialogVm;
        if (selected.Count == 1)
        {
            var svc = selected[0];
            vm.SelectedService = svc; // damit die Single-Service-Methoden das richtige Ziel treffen
            dialogVm = new ServiceActionDialogViewModel(mode, svc.HostName, svc.Description);
        }
        else
        {
            var hosts = selected.Select(s => s.HostName).Distinct().Count();
            var label = hosts == 1
                ? $"{selected.Count} Services auf {selected[0].HostName}"
                : $"{selected.Count} Services auf {hosts} Hosts";
            dialogVm = new ServiceActionDialogViewModel(mode, label);
        }

        var dialog = new ServiceActionDialog(dialogVm);
        var confirmed = await dialog.ShowDialog<bool>(owner);
        if (!confirmed) return;

        if (mode == ServiceActionMode.Acknowledge)
        {
            if (selected.Count == 1)
                await vm.PerformAcknowledgeAsync(dialogVm.Comment);
            else
                await vm.PerformBulkAcknowledgeAsync(selected, dialogVm.Comment);
        }
        else
        {
            var (start, end) = dialogVm.Window();
            if (selected.Count == 1)
                await vm.PerformDowntimeAsync(dialogVm.Comment, start, end);
            else
                await vm.PerformBulkDowntimeAsync(selected, dialogVm.Comment, start, end);
        }
    }

    private IReadOnlyList<ServiceStatus> GetSelectedServices()
    {
        var grid = this.FindControl<DataGrid>("ServiceGrid");
        if (grid is null) return [];
        return grid.SelectedItems.OfType<ServiceStatus>().ToList();
    }

    // --- Ziel-Aufloesung: Tabelle (Grid-Auswahl) oder Baum (SelectedTreeItem) ---

    private IReadOnlyList<ServiceStatus> GetTargetServices()
    {
        if (DataContext is not StatusViewModel vm) return [];
        if (!vm.TreeView) return GetSelectedServices();

        return vm.SelectedTreeItem switch
        {
            ServiceStatus s => [s],
            HostNodeViewModel h => h.Services.ToList(),
            _ => []
        };
    }

    private ServiceStatus? GetTargetService()
    {
        if (DataContext is not StatusViewModel vm) return null;
        if (!vm.TreeView) return vm.SelectedService;

        return vm.SelectedTreeItem switch
        {
            ServiceStatus s => s,
            HostNodeViewModel h => h.Services.FirstOrDefault(),
            _ => null
        };
    }

    private string? GetTargetHostName()
    {
        if (DataContext is not StatusViewModel vm) return null;
        if (!vm.TreeView) return vm.SelectedService?.HostName;

        return vm.SelectedTreeItem switch
        {
            ServiceStatus s => s.HostName,
            HostNodeViewModel h => h.HostName,
            _ => null
        };
    }
}
