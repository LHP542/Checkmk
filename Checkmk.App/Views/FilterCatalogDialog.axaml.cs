using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Checkmk.App.Controls;
using Checkmk.App.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Checkmk.App.Views;

/// <summary>
/// Ein Eintrag im Katalog. <see cref="IsSubscribed"/> ist bewusst
/// änderbar — das Ankreuzen <b>ist</b> die Bedienung.
/// </summary>
public sealed partial class CatalogEntry : ObservableObject
{
    private readonly HostFilter _filter;
    private readonly string _user;
    private readonly int _matchCount;

    public CatalogEntry(HostFilter filter, string user, bool subscribed, int matchCount)
    {
        _filter = filter;
        _user = user;
        _matchCount = matchCount;
        _isSubscribed = subscribed;
    }

    public int Id => _filter.Id;
    public string Name => _filter.Name;
    public string Fachbereich => _filter.FachbereichName ?? "—";

    [ObservableProperty]
    private bool _isSubscribed;

    /// <summary>
    /// Den eigenen Filter kann man nicht abbestellen — er gehört einem, und er
    /// verschwände sonst aus der eigenen Auswahl, ohne dass man ihn wiederfände.
    /// Das Häkchen steht deshalb fest an.
    /// </summary>
    public bool CanUnsubscribe => !_filter.IsAuthor(_user);

    public string Meta => _filter.IsAuthor(_user)
        ? $"{Fachbereich} · von dir · {_filter.Subscribers} Abo(s)"
        : $"{Fachbereich} · von {_filter.Owner} · {_filter.Subscribers} Abo(s)";

    /// <summary>Was der Filter tatsächlich tut — die erste Frage vor dem Abo.</summary>
    public string Rule => _filter.ExplicitHosts.Count > 0
        ? $"{_filter.ExplicitHosts.Count} feste Hosts: "
          + string.Join(", ", _filter.ExplicitHosts.Take(4))
          + (_filter.ExplicitHosts.Count > 4 ? " …" : "")
        : string.IsNullOrWhiteSpace(_filter.HostNameRegex)
            ? "alle Hosts"
            : _filter.HostNameRegex;

    public string Matches => _matchCount >= 0
        ? $"trifft gerade {_matchCount} Hosts"
        : "";

    public bool Contains(string needle)
        => Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || Fachbereich.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || _filter.Owner.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Der Filter-Katalog: alles, was Fachbereiche veröffentlicht haben, zum
/// Ankreuzen.
///
/// Das ist der Kern des Modells — nicht die Zugehörigkeit zu einer Gruppe
/// entscheidet, was jemand in seiner Auswahl hat, sondern seine eigene
/// Entscheidung. Damit entfällt jede Mitgliederpflege.
///
/// Zu jedem Eintrag steht, <b>was er tut</b> (Regex bzw. Host-Liste) und wie
/// viele Hosts er gerade trifft. „DB-Server" allein sagt nichts; „trifft gerade
/// 34 Hosts" beantwortet die Frage, die man vor dem Abonnieren wirklich hat.
/// </summary>
public partial class FilterCatalogDialog : ChromeWindow
{
    private readonly List<CatalogEntry> _all = [];

    public FilterCatalogDialog(IReadOnlyList<HostFilter> catalog,
        IReadOnlyList<int> subscribed, string user, IReadOnlyList<string> knownHosts)
    {
        AvaloniaXamlLoader.Load(this);

        var subs = subscribed.ToHashSet();
        _all = [.. catalog
            .OrderBy(f => f.FachbereichName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new CatalogEntry(f, user,
                subs.Contains(f.Id) || f.IsAuthor(user),
                knownHosts.Count == 0 ? -1 : knownHosts.Count(f.Matches)))];

        foreach (var e in _all) e.PropertyChanged += (_, _) => UpdateCount();

        var groups = _all.Select(e => e.Fachbereich).Distinct().Count();
        this.FindControl<TextBlock>("PromptText")!.Text = _all.Count == 0
            ? "Es hat noch niemand einen Filter veröffentlicht. Wer einen guten "
            + "gebaut hat, stellt ihn im Filter-Manager unter „Veröffentlicht in“ "
            + "in einen Fachbereich."
            : $"{_all.Count} veröffentlichte Filter aus {groups} Fachbereich(en). "
            + "Angehakt heißt: erscheint in deiner Filter-Auswahl. Deine eigenen "
            + "sind immer dabei.";

        this.FindControl<TextBox>("FilterBox")!.TextChanged += (_, _) => ApplyFilter();
        this.FindControl<CheckBox>("OnlySubscribedBox")!.IsCheckedChanged += (_, _) => ApplyFilter();

        ApplyFilter();
    }

    // Parameterloser ctor fuer XAML-Designer.
    public FilterCatalogDialog() => AvaloniaXamlLoader.Load(this);

    private void ApplyFilter()
    {
        var needle = this.FindControl<TextBox>("FilterBox")!.Text?.Trim();
        var onlySubscribed = this.FindControl<CheckBox>("OnlySubscribedBox")!.IsChecked == true;

        this.FindControl<ListBox>("CatalogList")!.ItemsSource = _all
            .Where(e => !onlySubscribed || e.IsSubscribed)
            .Where(e => string.IsNullOrWhiteSpace(needle) || e.Contains(needle))
            .ToList();

        UpdateCount();
    }

    private void UpdateCount()
    {
        var n = _all.Count(e => e.IsSubscribed);
        this.FindControl<TextBlock>("CountText")!.Text =
            $"{n} von {_all.Count} in deiner Auswahl";
    }

    /// <summary>
    /// Alles abbestellen — außer den eigenen, die kann man nicht loswerden.
    /// Gedacht zum Aufräumen, wenn das Dropdown zugewachsen ist.
    /// </summary>
    private void OnClearAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var entry in _all.Where(x => x.CanUnsubscribe))
            entry.IsSubscribed = false;
        ApplyFilter();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        // Eigene Filter tauchen ohnehin in der Auswahl auf; sie zusaetzlich zu
        // abonnieren waere eine Zeile ohne Wirkung in der Datenbank.
        IReadOnlyList<int> chosen =
            [.. _all.Where(x => x.IsSubscribed && x.CanUnsubscribe).Select(x => x.Id)];
        Close(chosen);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
