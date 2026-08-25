using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Checkmk.App.Controls;
using Checkmk.App.Services;
using Checkmk.App.ViewModels;
using Checkmk.Data;
using Checkmk.PluginContracts;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Checkmk.App.Views;

public partial class MainWindow : ChromeWindow
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Plugin-Tabs duerfen nur EINMAL angehaengt werden. Das Opened-Event feuert
    // aber bei jedem Window.Show() erneut — u. a. wenn der TrayController die App
    // aus dem Tray zurueckholt (Minimieren -> Hide, Wiederherstellen -> Show).
    // Ohne diesen Guard bekaeme jeder Minimieren/Wiederherstellen-Zyklus einen
    // weiteren Satz Plugin-Tabs (z. B. dreifaches "vSphere Baseimages").
    private bool _pluginTabsAdded;

    // Im Viewer-Modus (viewer.json neben der Exe) faellt alles Schreibende weg —
    // gecacht, weil OnKeyDown das bei jedem Tastendruck braucht.
    private readonly bool _canWrite;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // GetService statt GetRequiredService: der XAML-Previewer instanziiert das
        // Fenster ohne DI-Container. Ohne Container gilt der normale Vollmodus.
        var viewer = App.Services?.GetService<ViewerMode>();
        _canWrite = viewer?.CanWrite ?? true;
        if (viewer?.IsActive == true)
        {
            RemoveNonViewerTabs();
        }
        else
        {
            SetUpAreasTab();
            SetUpHostsTab();
        }

        Opened += (_, _) => AddPluginTabs();

        // Zentrale Filter erst nach dem Oeffnen nachziehen: Der Konstruktor darf
        // nicht auf Netz-I/O warten, sonst haengt das Fenster, bevor es zu sehen
        // ist. Bis dahin stehen die lokalen Filter da — genau der Bestand, der
        // danach einmalig uebernommen wird.
        Opened += (_, _) =>
        {
            if (App.Services?.GetService<HostFilterCollection>() is { } filters)
                _ = filters.InitializeAsync();

            // Host-Attribute (OS-Familie, Ortstags) einmal beim Start holen.
            // Frueher passierte das nur, wenn jemand den Hosts-Tab oeffnete —
            // seit der weg ist, waeren OS-Symbole und „Tags zuordnen…" sonst
            // dauerhaft leer, ohne dass es eine Fehlermeldung gaebe.
            if (App.Services?.GetService<HostFactsLoader>() is { } facts)
                _ = facts.RefreshAsync();
        };

        // Spaltenbreiten einfangen: Avalonias DataGrid meldet das Ende eines
        // Spalten-Resize nicht, Reihenfolge und Sichtbarkeit speichern sich dagegen
        // sofort. Beim Schliessen holen wir deshalb den kompletten Stand nach.
        Closing += (_, _) => this.FindDescendantOfType<StatusView>()?.SaveColumnLayout();
    }

    /// <summary>
    /// Entfernt Hosts- und Dashboard-Tab. Der Hosts-Tab kann Config schreiben
    /// (Discovery, Host anlegen, Aenderungen aktivieren), das Dashboard haengt
    /// an Filtern, die es im Viewer-Modus so nicht gibt.
    ///
    /// Der Bereiche-Tab bleibt <b>nur</b>, wenn das Profil ihn ausdruecklich
    /// verlangt (<c>map.show</c>) — sonst bekaeme jede bestehende
    /// Kiosk-Ausgabe beim Update ungefragt einen neuen Tab. Er ist dann rein
    /// lesend: Saemtliche Schreibknoepfe haengen an <c>CanWrite</c>, das im
    /// Viewer-Modus false ist.
    /// </summary>
    private void RemoveNonViewerTabs()
    {
        var tabs = this.FindControl<TabControl>("MainTabs");
        if (tabs is null) return;

        var keepAreas = App.Services?.GetService<ViewerMode>()?.Map is not null;

        foreach (var name in new[] { "HostsTab", "AreasTab", "DashboardTab" })
        {
            if (name == "AreasTab" && keepAreas) continue;
            if (this.FindControl<TabItem>(name) is { } tab)
                tabs.Items.Remove(tab);
        }

        if (keepAreas) SetUpAreasTab();
        tabs.SelectedIndex = 0;
    }

    /// <summary>
    /// Entfernt den Hosts-Tab, solange <c>GlobalSetting.ShowHostsTab</c> nicht
    /// auf true steht (Vorgabe: false).
    ///
    /// <b>Entfernt, nicht per <c>IsVisible</c> versteckt</b> — dieselbe Regel wie
    /// im Viewer-Modus: Ein nur ausgeblendeter Tab bleibt per Ctrl+Tab
    /// erreichbar.
    ///
    /// Die Host-Attribute, die dieser Tab früher nebenbei geladen hat, holt
    /// jetzt <see cref="HostFactsLoader"/> beim Start. Ohne diese Verlagerung
    /// hätte das Ausblenden OS-Symbole und Ortstag-Zuordnung still
    /// mitabgeschaltet.
    /// </summary>
    private void SetUpHostsTab()
    {
        var globals = App.Services?.GetService<IGlobalSettingsProvider>();
        if (globals?.Current.ShowHostsTab == true) return;

        if (this.FindControl<TabControl>("MainTabs") is not { } tabs) return;
        if (this.FindControl<TabItem>("HostsTab") is { } tab) tabs.Items.Remove(tab);
    }

    /// <summary>
    /// Haengt den Bereiche-Tab an sein ViewModel — oder entfernt ihn, wenn keine
    /// zentrale Datenbank konfiguriert ist. Bereiche leben ausschliesslich dort;
    /// ein leerer Tab, der nur „nicht verfuegbar" sagt, waere schlechter als kein Tab.
    /// </summary>
    private void SetUpAreasTab()
    {
        var tabs = this.FindControl<TabControl>("MainTabs");
        var tab = this.FindControl<TabItem>("AreasTab");
        if (tabs is null || tab is null) return;

        var vm = App.Services?.GetService<AreaViewModel>();
        if (vm is null)
        {
            tabs.Items.Remove(tab);
            return;
        }

        this.FindControl<AreaView>("AreasView")!.DataContext = vm;
        _ = vm.InitializeAsync();
    }

    /// <summary>Fuegt Tabs, die von Plugins beigesteuert werden, rechts von den
    /// eingebauten Tabs (Status/Hosts/Dashboard) ein. Sortierung: Cockpit-Tabs
    /// liegen bei 0-999 (XAML-Reihenfolge), Plugin-Tabs ab Order 1000.</summary>
    private void AddPluginTabs()
    {
        if (_pluginTabsAdded) return;

        var tabs = this.FindControl<TabControl>("MainTabs");
        if (tabs is null) return;

        _pluginTabsAdded = true;

        // ITabContribution-Instanzen einzeln aufloesen und in try/catch iterieren —
        // wenn ein Plugin einen kaputten Ctor hat (z. B. IPluginContext als DI-
        // Dependency erwartet), wirft nur DIESES Plugin und die anderen laufen
        // trotzdem. Ohne den Try-Wrapper wuerde ein einziger Fehler in der
        // GetServices-Enumeration die ganze Kette killen -> Cockpit-Absturz.
        var contribs = new List<ITabContribution>();
        try
        {
            var descriptors = App.Services!.GetServices<ITabContribution>();
            foreach (var c in descriptors)
            {
                try { contribs.Add(c); }
                catch (Exception ex) { Log.Warn(ex, "Plugin-Tab-Instanz konnte nicht aufgeloest werden."); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Plugin-Tab-Enumeration fehlgeschlagen — kein Plugin-Tab wird geladen.");
            return;
        }
        contribs.Sort((a, b) => a.Order.CompareTo(b.Order));

        foreach (var contrib in contribs)
        {
            try
            {
                if (contrib.CreateView() is not Control view)
                {
                    Log.Warn("Plugin-Tab '{Header}' hat kein Avalonia-Control als View geliefert — uebersprungen.",
                        contrib.Header);
                    continue;
                }
                tabs.Items.Add(new TabItem { Header = contrib.Header, Content = view });
                Log.Info("Plugin-Tab hinzugefuegt: {Header} (Order {Order})", contrib.Header, contrib.Order);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Plugin-Tab '{Header}' konnte nicht erstellt werden.", contrib.Header);
            }
        }
    }

    /// <summary>Wird von aussen (App.axaml.cs) genutzt, um beim Dashboard-Klick
    /// zurueck in den Status-Tab zu springen.</summary>
    public void SelectMainTab(int index)
    {
        var tabs = this.FindControl<TabControl>("MainTabs");
        if (tabs is not null) tabs.SelectedIndex = index;
    }

    /// <summary>
    /// Alltags-Hotkeys. Tunnel-Routing damit sie die aktive TextBox nicht ueberschreiben —
    /// wir prueft ExplicitHandled-Flag und lassen die TextBox ihre eigene Tastaturbelegung
    /// behalten (nur Esc wird auch aus der Textbox verwendet, weil wir es zum Leeren nutzen).
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var focus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var focusIsTextInput = focus is TextBox;

        switch (e.Key)
        {
            case Key.F5:
                _ = vm.Status.RefreshCommand.ExecuteAsync(null);
                e.Handled = true;
                return;

            case Key.F when ctrl:
                // Fokus in die Freitext-Filter-Textbox des Status-Tabs.
                var box = this.FindDescendantOfType<TextBox>("FilterTextBox");
                if (box is not null)
                {
                    box.Focus();
                    box.SelectAll();
                    e.Handled = true;
                }
                return;

            case Key.Escape when focusIsTextInput && focus is TextBox tb
                              && tb.Name == "FilterTextBox":
                // Nur die Freitext-Filter-Box leeren; andere TextBoxen (Kommentar-Dialog etc.)
                // sollen ihr Escape-Verhalten behalten.
                tb.Text = "";
                e.Handled = true;
                return;
        }

        // Modifier-Hotkeys (Ctrl+K/D/A) sollen nicht in TextBoxen greifen — und im
        // Viewer-Modus gar nicht, sonst gaebe es einen Tastenweg an der
        // ausgeblendeten Aktions-UI vorbei.
        if (focusIsTextInput || !_canWrite) return;

        switch (e.Key)
        {
            case Key.K when ctrl:
                RequestServiceAction(ServiceHotkeyAction.Comment);
                e.Handled = true;
                return;
            case Key.D when ctrl:
                RequestServiceAction(ServiceHotkeyAction.Downtime);
                e.Handled = true;
                return;
            case Key.A when ctrl:
                RequestServiceAction(ServiceHotkeyAction.Acknowledge);
                e.Handled = true;
                return;
        }
    }

    private void RequestServiceAction(ServiceHotkeyAction action)
    {
        // Delegiert an den StatusView-Code-Behind — der weiss, wie Kommentar-/
        // Downtime-/Ack-Dialog auf den markierten Services aufgeht.
        var status = this.FindDescendantOfType<StatusView>();
        status?.TriggerHotkeyAction(action);
    }
}

public enum ServiceHotkeyAction
{
    Comment,
    Downtime,
    Acknowledge
}

/// <summary>Kleine Findhelper — Avalonia hat kein direktes FindName ueber die Tree-Hierarchie.</summary>
internal static class ControlTreeExtensions
{
    public static T? FindDescendantOfType<T>(this Control root, string? name = null) where T : Control
    {
        foreach (var child in root.GetVisualDescendants())
        {
            if (child is T typed && (name is null || typed.Name == name))
                return typed;
        }
        return null;
    }
}
