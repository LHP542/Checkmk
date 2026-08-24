using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Checkmk.App.Controls;
using Checkmk.App.Services;
using Checkmk.App.ViewModels;
using Checkmk.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Checkmk.App.Views;

public partial class FilterManagerWindow : ChromeWindow
{
    private readonly HostFilterCollection? _filters;

    public FilterManagerWindow(HostFilterCollection filters)
    {
        AvaloniaXamlLoader.Load(this);
        _filters = filters;
        DataContext = new FilterManagerViewModel(filters);
    }

    // Parameterloser ctor nur fuer den XAML-Designer.
    public FilterManagerWindow() => AvaloniaXamlLoader.Load(this);

    private void OnDismissClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Der Filter-Katalog: alles, was Fachbereiche veröffentlicht haben, zum
    /// Ankreuzen. Die Hostnamen kommen aus dem Status-Tab, damit zu jedem
    /// Eintrag steht, wie viele Hosts er gerade trifft — das beantwortet die
    /// Frage vor dem Abonnieren besser als jeder Name.
    /// </summary>
    private async void OnCatalogClick(object? sender, RoutedEventArgs e)
    {
        if (_filters is null || DataContext is not FilterManagerViewModel vm) return;

        var (catalog, subscribed) = await _filters.LoadCatalogAsync();

        IReadOnlyList<string> hosts = App.Services?.GetService<StatusViewModel>() is { } status
            ? [.. System.Linq.Enumerable.Distinct(
                System.Linq.Enumerable.Select(status.AllServices, s => s.HostName),
                System.StringComparer.OrdinalIgnoreCase)]
            : [];

        var dialog = new FilterCatalogDialog(catalog, subscribed, _filters.UserName, hosts);
        if (await dialog.ShowDialog<IReadOnlyList<int>?>(this) is not { } chosen) return;

        await vm.ApplySubscriptionsAsync(chosen);
    }

    private async void OnManageFachbereicheClick(object? sender, RoutedEventArgs e)
    {
        // Ohne Datenbank gibt es keine Fachbereiche — der Knopf ist dann gar
        // nicht sichtbar, aber der Guard bleibt: der Container liefert hier null.
        if (App.Services?.GetService<IFachbereichStore>() is not { } store) return;

        var dialog = new FachbereichManagerWindow(store);
        var changed = await dialog.ShowDialog<bool>(this);

        // Ein umbenannter oder neuer Fachbereich muss sofort in der Auswahl
        // stehen — sonst muesste man den Filter-Manager schliessen und neu
        // oeffnen, nur um einen Filter dort veroeffentlichen zu koennen.
        if (changed && DataContext is FilterManagerViewModel vm)
            vm.RefreshScopes();
    }
}
