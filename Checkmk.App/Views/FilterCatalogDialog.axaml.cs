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
/// Was der Katalog-Dialog zurückgibt: die neue Abo-Menge und die Filter, die
/// endgültig aus dem Katalog verschwinden sollen.
/// </summary>
public sealed record CatalogResult(IReadOnlyList<int> Subscribed, IReadOnlyList<int> Deleted);

/// <summary>
/// Ein Eintrag im Katalog. <see cref="IsSubscribed"/> ist bewusst
/// änderbar — das Ankreuzen <b>ist</b> die Bedienung.
/// </summary>
public sealed partial class CatalogEntry : ObservableObject
{
    private readonly HostFilter _filter;
    private readonly string _user;
    private readonly int _matchCount;
    private readonly bool _isAdmin;
    private readonly bool _subscribedInDb;

    public CatalogEntry(HostFilter filter, string user, bool subscribed, int matchCount,
        bool isAdmin)
    {
        _filter = filter;
        _user = user;
        _matchCount = matchCount;
        _isAdmin = isAdmin;
        _subscribedInDb = subscribed;
        _isSubscribed = subscribed;
    }

    public int Id => _filter.Id;
    public string Name => _filter.Name;
    public string Fachbereich => _filter.FachbereichName ?? "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(Meta))]
    private bool _isSubscribed;

    /// <summary>
    /// Zum Löschen vorgemerkt. Das Löschen passiert erst beim „Übernehmen" —
    /// so ist „Abbrechen" die Rücknahme, und es braucht keinen zweiten Dialog
    /// über dem Dialog.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlive))]
    private bool _isDeleted;

    public bool IsAlive => !IsDeleted;

    /// <summary>
    /// <b>Jeder kann jeden Filter abbestellen — auch den eigenen.</b> Vorher
    /// stand das Häkchen beim eigenen fest an, und wer einen Filter für den
    /// Fachbereich baute, den er selbst nicht braucht, bekam ihn nicht aus
    /// seiner Auswahl.
    /// </summary>
    public bool CanUnsubscribe => true;

    /// <summary>
    /// Löschen darf der Autor (und ein Admin) — aber erst, wenn ihn niemand
    /// mehr abonniert hat, das eigene Abo eingerechnet. Ein veröffentlichter
    /// Filter ist geteilte Arbeit; ihn unter Abonnenten wegzuziehen wäre
    /// dasselbe Ärgernis wie ein gelöschter gemeinsamer Ordner.
    /// </summary>
    public bool CanDelete
        => (_filter.IsAuthor(_user) || _isAdmin) && EffectiveSubscribers == 0;

    /// <summary>
    /// Abonnentenzahl <b>inklusive der noch nicht gespeicherten eigenen
    /// Entscheidung</b>. Ohne diese Rechnung müsste man erst übernehmen,
    /// schließen und den Katalog neu öffnen, nur damit „Löschen" angeht.
    /// </summary>
    private int EffectiveSubscribers => _filter.Subscribers
        - (_subscribedInDb && !IsSubscribed ? 1 : 0)
        + (!_subscribedInDb && IsSubscribed ? 1 : 0);

    public string Meta
    {
        get
        {
            var von = _filter.IsAuthor(_user) ? "von dir" : $"von {_filter.Owner}";
            return $"{Fachbereich} · {von} · {EffectiveSubscribers} Abo(s)";
        }
    }

    /// <summary>Was der Filter tatsächlich tut — die erste Frage vor dem Abo.</summary>
    public string Rule => _filter.ExplicitHosts.Count > 0
        ? $"{_filter.ExplicitHosts.Count} feste Hosts: "
          + string.Join(", ", _filter.ExplicitHosts.Take(4))
          + (_filter.ExplicitHosts.Count > 4 ? " …" : "")
        : string.IsNullOrWhiteSpace(_filter.HostNameRegex)
            ? "alle Hosts"
            // Das Ziel gehoert dazu: derselbe Ausdruck bedeutet gegen den Alias
            // etwas voellig anderes als gegen den Hostnamen.
            : $"{_filter.TargetDisplay} ~ {_filter.HostNameRegex}";

    public string Matches => _matchCount >= 0 ? $"trifft gerade {_matchCount} Hosts" : "";

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
///
/// <para>Der Katalog ist zugleich die <b>einzige</b> Stelle, an der ein
/// veröffentlichter Filter endgültig gelöscht werden kann. Das ist kein
/// Zufall: Wer ihn abbestellt hat, findet ihn in keiner Filterliste mehr —
/// nur noch hier.</para>
/// </summary>
public partial class FilterCatalogDialog : ChromeWindow
{
    private readonly List<CatalogEntry> _all = [];

    public FilterCatalogDialog(IReadOnlyList<HostFilter> catalog,
        IReadOnlyList<int> subscribed, string user,
        IReadOnlyList<(string Host, string? Alias)> knownHosts, bool isAdmin = false)
    {
        AvaloniaXamlLoader.Load(this);

        var subs = subscribed.ToHashSet();
        _all = [.. catalog
            .OrderBy(f => f.FachbereichName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new CatalogEntry(f, user, subs.Contains(f.Id),
                knownHosts.Count == 0
                    ? -1
                    : knownHosts.Count(h => f.Matches(h.Host, h.Alias)),
                isAdmin))];

        foreach (var e in _all) e.PropertyChanged += (_, _) => UpdateCount();

        var groups = _all.Select(e => e.Fachbereich).Distinct().Count();
        this.FindControl<TextBlock>("PromptText")!.Text = _all.Count == 0
            ? "Es hat noch niemand einen Filter veröffentlicht. Wer einen guten "
            + "gebaut hat, stellt ihn im Filter-Manager unter „Veröffentlicht in“ "
            + "in einen Fachbereich."
            : $"{_all.Count} veröffentlichte Filter aus {groups} Fachbereich(en). "
            + "Angehakt heißt: erscheint in deiner Filter-Auswahl. Auch deine "
            + "eigenen kannst du abwählen — löschen lässt sich einer erst, wenn "
            + "ihn niemand mehr abonniert hat.";

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
        var n = _all.Count(e => e.IsSubscribed && !e.IsDeleted);
        var d = _all.Count(e => e.IsDeleted);
        this.FindControl<TextBlock>("CountText")!.Text = d == 0
            ? $"{n} von {_all.Count} in deiner Auswahl"
            : $"{n} von {_all.Count} in deiner Auswahl · {d} zum Löschen vorgemerkt";
    }

    /// <summary>
    /// Alles abbestellen — inzwischen wirklich alles, auch die eigenen.
    /// Gedacht zum Aufräumen, wenn das Dropdown zugewachsen ist.
    /// </summary>
    private void OnClearAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var entry in _all) entry.IsSubscribed = false;
        ApplyFilter();
    }

    /// <summary>Merkt einen Eintrag zum Löschen vor. Ausgeführt wird erst beim
    /// „Übernehmen"; bis dahin ist „Abbrechen" die Rücknahme.</summary>
    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CatalogEntry entry })
        {
            entry.IsSubscribed = false;
            entry.IsDeleted = true;
            UpdateCount();
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<int> chosen =
            [.. _all.Where(x => x.IsSubscribed && !x.IsDeleted).Select(x => x.Id)];
        IReadOnlyList<int> doomed = [.. _all.Where(x => x.IsDeleted).Select(x => x.Id)];
        Close(new CatalogResult(chosen, doomed));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
