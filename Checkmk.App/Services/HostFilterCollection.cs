using System.Collections.ObjectModel;
using Checkmk.App.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Checkmk.App.Services;

/// <summary>
/// Zentraler Live-State fuer Host-Filter — Singleton, den beide Tabs (Status + Konfig) beobachten.
/// Filter sind **pro Site** organisiert: beim Site-Wechsel wird die Collection
/// neu geladen. Aenderungen an <see cref="Active"/>, <see cref="Add"/>,
/// <see cref="Remove"/>, <see cref="Update"/> persistieren automatisch in den
/// <see cref="IHostFilterStore"/> unter der aktuellen Site.
/// </summary>
public sealed class HostFilterCollection : ObservableObject
{
    private readonly IHostFilterStore _store;
    private readonly IConnectionSettingsStore _settings;
    private readonly CentralFilterService? _central;
    private readonly bool _viewerMode;
    private string _currentSite;
    private bool _suppressPersist;

    /// <summary>Wurden die Start-Filter für die aktuelle Site schon angelegt?</summary>
    private bool _seeded;

    public ObservableCollection<HostFilter> Filters { get; } = new();

    /// <summary>
    /// Letzter Fehler beim zentralen Speichern, oder <c>null</c>. Der
    /// Filter-Manager zeigt ihn an — sonst verschwände eine abgelehnte Änderung
    /// spurlos, und der Anwender hielte sie für gespeichert.
    /// </summary>
    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    /// <summary>Kommen die Filter aus der zentralen Datenbank?</summary>
    public bool IsCentral => _central is { Origin: FilterOrigin.Central };

    /// <summary>Dürfen Filter überhaupt geändert werden?</summary>
    public bool CanEdit => !_viewerMode && (_central is null || _central.CanWrite);

    /// <summary>Fachbereiche für die Auswahl beim Veröffentlichen; leer ohne Datenbank.</summary>
    public IReadOnlyList<Checkmk.Data.FachbereichRow> Fachbereiche
        => _central?.Fachbereiche ?? [];

    /// <summary>Darf Fachbereiche verwalten. Veröffentlichen darf ohnehin jeder.</summary>
    public bool IsAdmin => _central?.IsAdmin ?? false;

    /// <summary>Anmeldename, gegen den die Autorschaft eines Filters geprüft wird.</summary>
    public string UserName => _central?.UserName ?? Environment.UserName;

    public string StatusHint => _central?.StatusHint ?? "Filter: lokal";

    /// <summary>Der Katalog samt der eigenen Abos — für den Abo-Dialog.</summary>
    public Task<(IReadOnlyList<HostFilter> Catalog, IReadOnlyList<int> Subscribed)> LoadCatalogAsync()
        => _central?.LoadCatalogAsync()
           ?? Task.FromResult<(IReadOnlyList<HostFilter>, IReadOnlyList<int>)>(([], []));

    /// <summary>
    /// Übernimmt eine geänderte Abo-Auswahl und baut die Liste neu auf.
    /// Bewusst ein eigener Weg neben <see cref="Persist"/>: Ein Abo ist keine
    /// Änderung am Filter, sondern an meiner Sicht darauf.
    /// </summary>
    public async Task SubscribeAsync(IReadOnlyList<int> filterIds)
    {
        if (_central is null) return;

        LastError = await _central.SetSubscriptionsAsync(filterIds).ConfigureAwait(true);
        if (LastError is not null) return;

        var activeName = _active?.Name;
        _suppressPersist = true;
        try
        {
            Filters.Clear();
            foreach (var f in _central.Current) Filters.Add(f);
            _active = string.IsNullOrEmpty(activeName)
                ? null
                : Filters.FirstOrDefault(f => f.Name == activeName);
        }
        finally { _suppressPersist = false; }

        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(StatusHint));
    }

    private HostFilter? _active;
    public HostFilter? Active
    {
        get => _active;
        set
        {
            // Beim Laden setzt die two-way-gebundene ComboBox waehrend Filters.Clear()
            // Active=null zurueck. Ohne diesen Guard wuerde der Setter dann Persist()
            // mit LEERER Filterliste ausloesen und die Site auf Platte loeschen.
            if (SetProperty(ref _active, value) && !_suppressPersist)
                Persist();
        }
    }

    public HostFilterCollection(IHostFilterStore store, IConnectionSettingsStore settings,
        ViewerMode viewer, CentralFilterService? central = null)
    {
        _store = store;
        _settings = settings;
        // Im Viewer-Modus bleibt auch die zentrale Quelle draussen: Der
        // Filterzustand kommt dort ausschliesslich aus viewer.json, und ein
        // Team-Filter im Dropdown waere derselbe Fehler wie die persoenlichen
        // Favoriten des Admins, die frueher mit ausgeliefert wurden.
        _central = viewer.IsActive ? null : central;
        _viewerMode = viewer.IsActive;
        _currentSite = _settings.Load().Site;

        // Im Viewer-Modus bleibt die persoenliche filter.json komplett aussen vor:
        // sie wird weder geladen noch geschrieben. Der Filterzustand kommt allein
        // aus viewer.json (StatusViewModel ruft gleich ApplyPreset). Sonst haengen
        // am Cockpit des Admins dessen eigene Favoriten mit drin — inklusive des
        // dort zuletzt aktiven Filters, der die Vorgabe aus dem Profil ueberstimmt.
        if (!_viewerMode)
            LoadFiltersForCurrentSite();
    }

    private void LoadFiltersForCurrentSite()
    {
        _suppressPersist = true;
        try
        {
            var s = _store.Load(_currentSite);
            _seeded = s.Seeded;
            Filters.Clear();
            foreach (var f in s.Filters)
            {
                // Defensiv: alte filter.json kann einen null-Eintrag enthalten.
                if (f is not null)
                    Filters.Add(f);
            }
            _active = string.IsNullOrEmpty(s.ActiveFilterName)
                ? null
                : Filters.FirstOrDefault(f => f.Name == s.ActiveFilterName);
        }
        finally { _suppressPersist = false; }
        OnPropertyChanged(nameof(Active));

        // Ohne Datenbank steht der Bestand hier schon fest. Mit Datenbank nicht —
        // dann saet erst LoadCentralAsync, sonst legten wir zwei Filter an, die
        // gleich darauf vom zentralen Stand ueberschrieben wuerden.
        if (_central is null) SeedStarterFilters();
    }

    /// <summary>Name des Filters, der alle Hosts zeigt.</summary>
    public const string AllHostsFilterName = "Alle Hosts";

    /// <summary>Name des Filters auf den eigenen Anmeldenamen im Host-Alias.</summary>
    public const string MyDevicesFilterName = "Meine Geräte";

    /// <summary>
    /// Legt auf einem frischen Rechner zwei Filter an: „Alle Hosts" und
    /// „Meine Geräte" (Anmeldename gegen den Host-Alias), und stellt den
    /// zweiten scharf.
    ///
    /// <para>Der Grund ist der Erstkontakt: Wer das Cockpit zum ersten Mal
    /// startet, sieht sonst alle 33.000 Checks der Stadt und muss sich erst
    /// einen Filter bauen, um seine eigenen Geräte zu finden. Beide Filter sind
    /// ganz normale persönliche Filter — umbenennbar, änderbar, löschbar.</para>
    ///
    /// <para><b>Genau einmal je Site</b> (<see cref="HostFilterState.Seeded"/>):
    /// Wer sie wegräumt, soll sie nicht beim nächsten Start wiederhaben.</para>
    /// </summary>
    private void SeedStarterFilters()
    {
        if (_viewerMode || _seeded) return;
        if (string.IsNullOrWhiteSpace(_currentSite)) return;
        if (!CanEdit) return;

        // Schon Filter da? Dann ist das kein frischer Rechner, sondern ein
        // Bestand aus der Zeit vor dieser Funktion — der bleibt unangetastet.
        if (Filters.Any(f => !f.IsTransient)) { MarkSeeded(); return; }

        var user = UserName;
        if (string.IsNullOrWhiteSpace(user)) return;

        _suppressPersist = true;
        try
        {
            Filters.Add(new HostFilter { Name = AllHostsFilterName, Owner = user });

            // Regex.Escape, weil der Anmeldename in den Ausdruck wandert:
            // ein Punkt in „max.mustermann" ist sonst ein Platzhalter.
            var mine = new HostFilter
            {
                Name = MyDevicesFilterName,
                Owner = user,
                Target = FilterTarget.Alias,
                HostNameRegex = System.Text.RegularExpressions.Regex.Escape(user)
            };
            Filters.Add(mine);
            _active = mine;
        }
        finally { _suppressPersist = false; }

        _seeded = true;
        OnPropertyChanged(nameof(Active));
        Persist();
    }

    /// <summary>Merkt „schon gesät", ohne etwas anzulegen — für Bestandsrechner.</summary>
    private void MarkSeeded()
    {
        _seeded = true;
        Persist();
    }

    /// <summary>
    /// Holt die Filter aus der zentralen Datenbank nach. Läuft nach dem
    /// Start — der Konstruktor darf nicht auf Netz-I/O warten, sonst hängt das
    /// Fenster, bevor es zu sehen ist.
    ///
    /// Bis das durch ist, stehen die lokalen Filter da; das ist genau der
    /// Bestand, der danach einmalig übernommen wird.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_central is null || _viewerMode) return;
        await LoadCentralAsync().ConfigureAwait(true);
    }

    private async Task LoadCentralAsync()
    {
        if (_central is null || string.IsNullOrWhiteSpace(_currentSite)) return;

        var legacy = _store.Load(_currentSite).Filters.Where(f => f is not null).ToList();
        var central = await _central.LoadAsync(_currentSite, legacy).ConfigureAwait(true);

        // Der zuletzt aktive Filter ist persoenliche Oberflaechen-Vorliebe und
        // bleibt lokal — er gehoert niemandem im Team.
        var activeName = _store.Load(_currentSite).ActiveFilterName;

        _suppressPersist = true;
        try
        {
            Filters.Clear();
            foreach (var f in central) Filters.Add(f);
            _active = string.IsNullOrEmpty(activeName)
                ? null
                : Filters.FirstOrDefault(f => f.Name == activeName);
        }
        finally { _suppressPersist = false; }

        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(IsCentral));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(StatusHint));

        // Erst jetzt: der zentrale Stand ist der massgebliche. Bei Ausfall
        // (Origin = Cache, CanEdit false) wird nicht gesaet — zwei Filter, die
        // nur im Cache stehen, waeren beim naechsten erfolgreichen Laden weg.
        SeedStarterFilters();
    }

    /// <summary>Wechselt das Filter-Set auf die neue Site. Persistiert erst die aktuelle
    /// Site, laedt dann die neue.</summary>
    public void SwitchSite(string newSite)
    {
        if (string.Equals(_currentSite, newSite, StringComparison.OrdinalIgnoreCase))
            return;
        Persist();
        _currentSite = newSite;

        if (_central is not null)
        {
            LoadFiltersForCurrentSite();   // sofort etwas zeigen
            _ = LoadCentralAsync();         // und gleich zentral nachziehen
        }
        else
        {
            LoadFiltersForCurrentSite();
        }
    }

    public void Add(HostFilter f)
    {
        Filters.Add(f);
        Persist();
    }

    /// <summary>
    /// Ids, die ausdrücklich gelöscht werden sollen.
    ///
    /// <b>Warum eine eigene Liste:</b> Vorher erschloss der Diff die Löschungen
    /// daraus, dass ein Filter nicht mehr in der Collection stand. Das ist
    /// gefährlich, weil die Collection bei jedem Neuaufbau kurzzeitig
    /// unvollständig ist — und ein währenddessen ausgelöstes Persist hat dann
    /// einen völlig unbeteiligten Filter aus der Datenbank gelöscht. Real
    /// passiert: „XMS" verschwand im selben Moment, in dem er veröffentlicht
    /// wurde. Gelöscht wird jetzt nur noch, was hier drinsteht.
    /// </summary>
    private readonly List<int> _deleted = [];

    public void Remove(HostFilter f)
    {
        if (f.Id > 0) _deleted.Add(f.Id);

        Filters.Remove(f);
        if (ReferenceEquals(_active, f))
            Active = null;
        else
            Persist();
    }

    /// <summary>Nach externer Bearbeitung eines Filters aufrufen, um den Store zu aktualisieren.</summary>
    public void Update() => Persist();

    /// <summary>
    /// Setzt den aus <c>viewer.json</c> vorgegebenen Filter und aktiviert ihn —
    /// <b>ohne</b> zu persistieren.
    /// <para>
    /// Wird im Viewer-Modus <b>immer</b> aufgerufen, auch wenn das Profil gar keinen
    /// Host-Bezug vorgibt (dann matcht der Filter alle Hosts). Genau das ist der
    /// Punkt: es darf keinen Pfad geben, auf dem stattdessen ein Filter aus der
    /// persoenlichen <c>filter.json</c> aktiv wird.
    /// </para>
    /// </summary>
    public void ApplyPreset(HostFilter preset)
    {
        preset.IsTransient = true;
        _suppressPersist = true;
        try
        {
            // Gleichnamigen Bestandsfilter entfernen, damit die ComboBox keine
            // zwei optisch identischen Eintraege zeigt.
            var clash = Filters.FirstOrDefault(f =>
                string.Equals(f.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
                Filters.Remove(clash);

            Filters.Insert(0, preset);
            _active = preset;
        }
        finally { _suppressPersist = false; }
        OnPropertyChanged(nameof(Active));
    }

    private void Persist()
    {
        // Viewer-Modus schreibt grundsaetzlich nicht in die persoenliche filter.json.
        if (_viewerMode) return;

        var state = new HostFilterState
        {
            // Transiente Vorgabe-Filter bleiben draussen — sie gehoeren dem Profil,
            // nicht der Favoritenbibliothek des Anwenders.
            Filters = Filters.Where(f => !f.IsTransient).ToList(),
            ActiveFilterName = _active is { IsTransient: false } ? _active.Name : null,
            Seeded = _seeded
        };

        if (_central is null)
        {
            _store.Save(_currentSite, state);
            return;
        }

        // Mit Datenbank wandern die Filter dorthin; lokal bleibt allein der
        // zuletzt aktive Name. Ihn zentral abzulegen hiesse, dass der Wechsel
        // des einen die Ansicht aller anderen im Team umstellt.
        _store.Save(_currentSite, new HostFilterState
        {
            Filters = state.Filters,          // zugleich der Ausfall-Lesestand
            ActiveFilterName = state.ActiveFilterName,
            Seeded = state.Seeded
        });

        var deleted = _deleted.ToList();
        _deleted.Clear();
        QueuePersist(state.Filters, deleted);
    }

    /// <summary>
    /// Läuft nacheinander, nie parallel. <see cref="Persist"/> wird bei jeder
    /// Kleinigkeit ausgelöst und startete den Schreibvorgang bisher als
    /// Feuer-und-vergessen — zwei Aufrufe in derselben Millisekunde
    /// überholten sich dann gegenseitig, jeder mit einem anderen Stand der
    /// Collection. Genau so ist ein Filter verlorengegangen.
    /// </summary>
    private Task _persistChain = Task.CompletedTask;

    private void QueuePersist(List<HostFilter> current, IReadOnlyList<int> deleted)
    {
        if (_central is null) return;

        _persistChain = _persistChain.ContinueWith(async _ =>
        {
            LastError = await _central.PersistAsync(current, deleted).ConfigureAwait(true);
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(StatusHint));
        }, TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
    }
}
