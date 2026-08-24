using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Checkmk.App.Models;
using Checkmk.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Checkmk.App.ViewModels;

/// <summary>
/// Eintrag der Auswahl „Veröffentlicht in". <c>FachbereichId = null</c> heißt
/// „nur für mich".
/// </summary>
public sealed record FilterScope(int? FachbereichId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Dialog-VM zum Verwalten der Host-Filter (Anlegen/Bearbeiten/Loeschen).</summary>
public sealed partial class FilterManagerViewModel : ObservableObject
{
    private readonly HostFilterCollection _collection;

    public ObservableCollection<HostFilter> Filters => _collection.Filters;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    [NotifyPropertyChangedFor(nameof(SelectionHint))]
    private HostFilter? _selected;

    // Editor-Felder (auf Selected gemappt beim Selection-Change)
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editRegex = "";
    [ObservableProperty] private string _editExplicitHosts = "";
    [ObservableProperty] private FilterScope? _editScope;

    /// <summary>Fehlermeldung fuer den Editor (v. a. Regex-Validierung).</summary>
    [ObservableProperty] private string _validationMessage = "";

    /// <summary>Woher die Filter kommen, plus Fehler aus dem zentralen Speichern.</summary>
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>Auswahl „Veröffentlicht in": nur für mich, oder ein Fachbereich.</summary>
    public ObservableCollection<FilterScope> Scopes { get; } = [];

    public bool IsCentral => _collection.IsCentral;
    public bool IsAdmin => _collection.IsAdmin;

    /// <summary>Insgesamt änderbar? false im Ausfall-Betrieb und im Viewer-Modus.</summary>
    public bool CanEdit => _collection.CanEdit;

    /// <summary>
    /// Ein abonnierter Filter gehört seinem Autor. Er steht in meiner Liste,
    /// aber ändern darf ihn nur der, der ihn gebaut hat — eine Änderung ginge
    /// sonst alle Abonnenten an, ohne dass sie es merken.
    /// </summary>
    public bool CanEditSelected
        => CanEdit && Selected is { } s && s.IsAuthor(_collection.UserName);

    /// <summary>Transiente Vorgabe-Filter gehören dem Profil und lassen sich nicht löschen.</summary>
    public bool CanDeleteSelected => CanEditSelected && Selected is { IsTransient: false };

    /// <summary>Hinweis unter der Liste, wenn ein fremder Filter markiert ist.</summary>
    public string SelectionHint => Selected switch
    {
        null => "",
        { } s when !s.IsAuthor(_collection.UserName)
            => $"„{s.Name}“ stammt von {s.Owner} — abonniert, nicht bearbeitbar. "
             + "Zum Abbestellen: Katalog…",
        { IsPublished: true } s
            => $"Veröffentlicht in „{s.FachbereichName}“ — {s.Subscribers} Abonnent(en). "
             + "Änderungen sehen alle.",
        _ => ""
    };

    public FilterManagerViewModel(HostFilterCollection collection)
    {
        _collection = collection;
        BuildScopes();
        _selected = _collection.Active ?? _collection.Filters.FirstOrDefault();
        LoadFromSelected();
        UpdateStatus();

        // Der zentrale Ladevorgang laeuft asynchron weiter, waehrend der Dialog
        // schon offen sein kann. Ohne dieses Nachziehen zeigt er dann den Stand
        // von vor dem Laden.
        _collection.PropertyChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(HostFilterCollection.LastError):
            case nameof(HostFilterCollection.StatusHint):
                UpdateStatus();
                break;
            case nameof(HostFilterCollection.CanEdit):
            case nameof(HostFilterCollection.IsCentral):
                BuildScopes();
                OnPropertyChanged(nameof(IsCentral));
                OnPropertyChanged(nameof(IsAdmin));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CanEditSelected));
                OnPropertyChanged(nameof(CanDeleteSelected));
                UpdateStatus();
                break;
        }
    }

    /// <summary>Nach dem Verwalten von Fachbereichen: Auswahlliste neu aufbauen.</summary>
    public void RefreshScopes()
    {
        BuildScopes();
        LoadFromSelected();
        OnPropertyChanged(nameof(IsAdmin));
    }

    private void BuildScopes()
    {
        Scopes.Clear();
        Scopes.Add(new FilterScope(null, "nur für mich"));
        foreach (var f in _collection.Fachbereiche)
            Scopes.Add(new FilterScope(f.FachbereichId, f.Name));
    }

    private void UpdateStatus()
        => StatusMessage = _collection.LastError ?? _collection.StatusHint;

    partial void OnSelectedChanged(HostFilter? value) => LoadFromSelected();

    [RelayCommand]
    private void New()
    {
        var f = new HostFilter { Name = NextName() };
        _collection.Add(f);
        Selected = f;
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is null) return;
        var toRemove = Selected;
        var idx = Filters.IndexOf(toRemove);
        _collection.Remove(toRemove);
        Selected = Filters.Count == 0
            ? null
            : Filters[Math.Min(idx, Filters.Count - 1)];
        UpdateStatus();
    }

    [RelayCommand]
    private void Apply()
    {
        if (Selected is null) return;

        var regex = string.IsNullOrWhiteSpace(EditRegex) ? null : EditRegex.Trim();

        // Regex VOR dem Speichern validieren — ein kaputter Ausdruck wuerde sonst
        // erst zur Refresh-Zeit auffallen (und ist dann persistent gespeichert,
        // wodurch jeder Auto-Refresh die Ausnahme wiederholt).
        if (regex is not null)
        {
            try
            {
                _ = new Regex(regex, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                ValidationMessage = $"Regex ungültig: {ex.Message}";
                return;
            }
        }
        ValidationMessage = "";

        // Referenz sichern: Das RemoveAt unten leert die two-way-gebundene ListBox-Auswahl
        // und schreibt Selected=null zurueck. Ohne diese lokale Kopie wuerde danach ein
        // null in die Filter-Liste eingefuegt (-> NRE beim naechsten Laden/Matchen).
        var item = Selected;

        item.Name = string.IsNullOrWhiteSpace(EditName) ? "unbenannt" : EditName.Trim();
        item.HostNameRegex = regex;
        item.ExplicitHosts = EditExplicitHosts
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (IsCentral)
        {
            item.FachbereichId = EditScope?.FachbereichId;
            item.FachbereichName = _collection.Fachbereiche
                .FirstOrDefault(t => t.FachbereichId == item.FachbereichId)?.Name;
        }

        _collection.Update();
        UpdateStatus();

        // ObservableCollection benachrichtigt bei Property-Aenderungen auf Items nicht — Re-Insert
        // erzwingt das Neu-Rendern des Eintrags.
        var idx = Filters.IndexOf(item);
        if (idx >= 0)
        {
            Filters.RemoveAt(idx);
            Filters.Insert(idx, item);
            Selected = item;
        }
    }

    /// <summary>
    /// Übernimmt eine geänderte Abo-Auswahl aus dem Katalog-Dialog.
    /// </summary>
    public async Task ApplySubscriptionsAsync(IReadOnlyList<int> filterIds)
    {
        await _collection.SubscribeAsync(filterIds);
        Selected = Filters.FirstOrDefault();
        UpdateStatus();
    }

    [RelayCommand]
    private void ActivateSelected()
    {
        _collection.Active = Selected;
    }

    [RelayCommand]
    private void ClearActive()
    {
        _collection.Active = null;
    }

    private void LoadFromSelected()
    {
        ValidationMessage = "";
        if (Selected is null)
        {
            EditName = "";
            EditRegex = "";
            EditExplicitHosts = "";
            EditScope = Scopes.FirstOrDefault();
            return;
        }
        EditName = Selected.Name;
        EditRegex = Selected.HostNameRegex ?? "";
        EditExplicitHosts = string.Join(Environment.NewLine, Selected.ExplicitHosts);
        EditScope = Scopes.FirstOrDefault(o => o.FachbereichId == Selected.FachbereichId)
                    ?? Scopes.FirstOrDefault();
    }

    private string NextName()
    {
        var i = 1;
        while (Filters.Any(f => f.Name == $"Filter {i}")) i++;
        return $"Filter {i}";
    }
}
