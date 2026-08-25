using System.Collections.ObjectModel;
using Checkmk.App.Services;
using Checkmk.Core.Models;
using Checkmk.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;

namespace Checkmk.App.ViewModels;

/// <summary>
/// Bereichsbaum mit Status-Rollup. Bewusst <b>vor</b> der Karte gebaut: Der
/// Nutzen steckt im Rollup („welcher Standort hat gerade ein Problem"), die
/// Karte ist die Hülle darum. Wer Bereiche hier pflegt, kann sie später
/// zeichnen, ohne die Zuordnung noch einmal anzufassen.
/// </summary>
public sealed partial class AreaViewModel : ViewModelBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IAreaStore _areas;
    private readonly StatusViewModel _status;
    private readonly IHostLocationTags _locationTags;

    /// <summary>Knoten je Bereichs-Id — damit der Refresh die Aggregate in place
    /// setzen kann, statt den Baum neu zu bauen.</summary>
    private readonly Dictionary<int, AreaNodeViewModel> _byId = [];

    /// <summary>Signatur des zuletzt gebauten Baums (Id + Name + Elternteil).
    /// Ändert sie sich nicht, bleibt der Baum stehen und behält seinen
    /// Aufklapp-Zustand.</summary>
    private string _builtFrom = "";

    private readonly AreaNodeViewModel _unassigned =
        new(AreaNodeViewModel.UnassignedId, "Ohne Bereich", isUnassigned: true);

    public ObservableCollection<AreaNodeViewModel> Roots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanEditSelection))]
    private AreaNodeViewModel? _selectedNode;

    /// <summary>
    /// Was im Baum markiert ist — ein Bereich <b>oder</b> ein Host.
    ///
    /// Der TreeView bindet hierauf statt auf <see cref="SelectedNode"/>, weil
    /// unter einem Bereich jetzt auch Host-Knoten hängen. Ohne diese
    /// Zwischenstufe bekäme die typisierte Bereichs-Auswahl bei jedem Klick auf
    /// einen Host ein Objekt des falschen Typs.
    ///
    /// <b>Ein Klick auf einen Host lässt die Bereichsauswahl stehen.</b> Das ist
    /// auch die richtige Bedienung: Wer einen Host unter „Container" anklickt,
    /// meint weiterhin den Container — Kontextmenü und Karte sollen sich
    /// darauf beziehen.
    /// </summary>
    [ObservableProperty]
    private object? _selectedTreeItem;

    partial void OnSelectedTreeItemChanged(object? value)
    {
        if (value is AreaNodeViewModel node) SelectedNode = node;
    }

    partial void OnSelectedNodeChanged(AreaNodeViewModel? value)
    {
        // Programmatisch gesetzte Auswahl (Karte, Neuanlage) im Baum nachziehen.
        if (value is not null && !ReferenceEquals(SelectedTreeItem, value))
            SelectedTreeItem = value;
    }

    public bool HasSelection => SelectedNode is not null;

    /// <summary>Der Sammelknoten „Ohne Bereich" ist kein Datensatz — umbenennen
    /// und löschen gehen dort nicht.</summary>
    public bool CanEditSelection => SelectedNode is { IsUnassigned: false };

    /// <summary>false im Viewer-Modus.</summary>
    public bool CanWrite { get; }

    /// <summary>Hostnamen ohne Bereich im aktuellen Filter — die Arbeitsliste
    /// beim Zuordnen.</summary>
    public IReadOnlyList<string> UnassignedHosts { get; private set; } = [];

    /// <summary>
    /// Aktive Checkmk-Site. Bereiche, die ausdrücklich einer anderen Site
    /// zugeordnet sind, bleiben ausgeblendet — sonst stünden nach dem
    /// Schul-Import 82 graue Marker in der LHP-Sicht. Bereiche ohne
    /// Site-Zuordnung sind überall sichtbar.
    /// </summary>
    public string? ActiveSite
    {
        get => _activeSite;
        set
        {
            if (string.Equals(_activeSite, value, StringComparison.OrdinalIgnoreCase)) return;
            _activeSite = value;
            _builtFrom = "";              // Baum neu aufbauen, die Menge ändert sich
            Recompute(_status.AllServices);
        }
    }
    private string? _activeSite;

    public AreaViewModel(IAreaStore areas, StatusViewModel status, ViewerMode viewer,
        IHostLocationTags locationTags)
    {
        _areas = areas;
        _status = status;
        _locationTags = locationTags;
        CanWrite = viewer.CanWrite;

        // Der Status-Tab liefert die Hosts, die auf den aktiven Filter passen —
        // genau die Linse, die den Rollup ausmacht.
        _status.Refreshed += (services, _) => Recompute(services);
    }

    /// <summary>Baum aus der Datenbank holen und mit dem letzten Statusstand füllen.</summary>
    public async Task InitializeAsync()
    {
        await _areas.RefreshAsync();
        Recompute(_status.AllServices);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        try
        {
            IsBusy = true;
            await _areas.RefreshAsync();
            Recompute(_status.AllServices);
            StatusMessage = $"{_byId.Count} Bereiche geladen.";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Baut den Baum (falls nötig) und setzt die Aggregate. Läuft nach jedem
    /// Status-Refresh, also alle paar Sekunden — deshalb erst die günstige
    /// Signaturprüfung, bevor irgendetwas neu entsteht.
    /// </summary>
    public void Recompute(IReadOnlyList<ServiceStatus> services)
    {
        var snapshot = _areas.Current;
        RebuildIfChanged(snapshot);

        var worstPerHost = AreaRollup.WorstStatePerHost(services);
        var aggregates = AreaRollup.Compute(VisibleAreas(snapshot), snapshot.HostToArea, worstPerHost);

        // Erst die Host-Listen, dann die Aggregate: Apply() vergleicht die Zahl
        // der zugeordneten Hosts mit der gefilterten, um „(3 zugeordnet)" zu
        // setzen — dafuer muss die Liste schon stehen.
        FillAssignedHosts(snapshot, worstPerHost);

        foreach (var (areaId, node) in _byId)
            node.Apply(aggregates.GetValueOrDefault(areaId, AreaAggregate.Empty));

        ApplyUnassigned(snapshot, worstPerHost);
        MapChanged?.Invoke();
    }

    /// <summary>
    /// Füllt je Bereich die Liste seiner zugeordneten Hosts — die Menge, die
    /// beim Aufklappen erscheint.
    ///
    /// <b>Bewusst ungefiltert.</b> Der Ampelpunkt und die Zahl am Bereich
    /// zeigen die Linse des aktiven Filters; diese Liste zeigt den
    /// tatsächlichen Bestand. Genau die Differenz war die Verwirrung: „Der
    /// Container hat drei Geräte, warum steht da 1?" Hosts außerhalb des
    /// Filters stehen mit dem Vermerk „nicht im Filter" dabei, statt zu fehlen.
    /// </summary>
    private void FillAssignedHosts(AreaSnapshot snapshot,
        IReadOnlyDictionary<string, ServiceState> worstPerHost)
    {
        var perArea = new Dictionary<int, List<AreaHostViewModel>>();

        foreach (var (host, areaId) in snapshot.HostToArea
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!_byId.ContainsKey(areaId)) continue;

            var inFilter = worstPerHost.TryGetValue(host, out var state);
            (perArea.TryGetValue(areaId, out var list) ? list : perArea[areaId] = [])
                .Add(new AreaHostViewModel(host, inFilter ? state : ServiceState.Unknown, inFilter));
        }

        foreach (var (areaId, node) in _byId)
            node.SetHosts(perArea.GetValueOrDefault(areaId, []));
    }

    /// <summary>
    /// Hosts im Filter, die keinem Bereich zugeordnet sind. Ohne diese Anzeige
    /// wäre bei 1105 Hosts nicht erkennbar, wie weit die Zuordnung gediehen ist —
    /// und ein vergessener Host fiele niemandem auf, weil er schlicht nirgends
    /// auftaucht.
    /// </summary>
    private void ApplyUnassigned(AreaSnapshot snapshot,
        IReadOnlyDictionary<string, ServiceState> worstPerHost)
    {
        // Nur sichtbare Bereiche zaehlen: Ein Host in einem Bereich einer
        // anderen Site steht hier zu Recht unter "Ohne Bereich" — in DIESER
        // Sicht hat er keinen.
        var known = VisibleAreas(snapshot).Select(a => a.AreaId).ToHashSet();

        var hosts = new List<string>();
        var problems = 0;
        var worst = ServiceState.Ok;

        foreach (var (host, state) in worstPerHost)
        {
            // Eine Zuordnung auf einen Bereich, den es nicht mehr gibt, zählt
            // ebenfalls als „ohne Bereich" — sonst verschwände der Host ganz.
            if (snapshot.HostToArea.TryGetValue(host, out var areaId) && known.Contains(areaId))
                continue;

            hosts.Add(host);
            if (state != ServiceState.Ok) problems++;
            if (Rank(state) > Rank(worst)) worst = state;
        }

        UnassignedHosts = hosts;
        _unassigned.Apply(new AreaAggregate(hosts.Count, problems, worst, hosts.Count > 0));

        static int Rank(ServiceState s) => s switch
        {
            ServiceState.Critical => 3,
            ServiceState.Warning => 2,
            ServiceState.Unknown => 1,
            _ => 0
        };
    }

    /// <summary>Bereiche, die in der aktiven Site sichtbar sind.</summary>
    private List<AreaRow> VisibleAreas(AreaSnapshot snapshot)
        => [.. snapshot.Areas.Where(a => snapshot.IsVisibleIn(a.AreaId, ActiveSite))];

    private void RebuildIfChanged(AreaSnapshot snapshot)
    {
        var visible = VisibleAreas(snapshot);
        var signature = ActiveSite + "#" + string.Join('|', visible
            .OrderBy(a => a.AreaId)
            .Select(a => $"{a.AreaId}:{a.ParentAreaId}:{a.Name}"));
        if (signature == _builtFrom && Roots.Count > 0) return;
        _builtFrom = signature;

        var selectedId = SelectedNode?.AreaId;

        _byId.Clear();
        Roots.Clear();

        foreach (var a in visible)
            _byId[a.AreaId] = new AreaNodeViewModel(a.AreaId, a.Name);

        foreach (var a in visible.OrderBy(a => a.SortOrder).ThenBy(a => a.Name))
        {
            var node = _byId[a.AreaId];
            if (a.ParentAreaId is { } p && _byId.TryGetValue(p, out var parent))
                parent.Children.Add(node);
            else
                Roots.Add(node);   // auch verwaiste Bereiche bleiben sichtbar
        }

        Roots.Add(_unassigned);

        // Unterbereiche stehen jetzt; die Host-Knoten haengt FillAssignedHosts
        // gleich darunter.
        foreach (var node in _byId.Values) node.RebuildTreeChildren();

        // Auswahl über die Id nachziehen, sonst springt sie beim Anlegen weg.
        if (selectedId is { } id)
            SelectedNode = id == AreaNodeViewModel.UnassignedId
                ? _unassigned
                : _byId.GetValueOrDefault(id);
    }

    // -----------------------------------------------------------------------
    // Pflege
    // -----------------------------------------------------------------------

    /// <summary>Legt einen Bereich an — unterhalb der Auswahl, sonst als Wurzel.</summary>
    public async Task<bool> CreateAsync(string name, bool asChildOfSelection)
    {
        if (!CanWrite || string.IsNullOrWhiteSpace(name)) return false;

        var parent = asChildOfSelection && SelectedNode is { IsUnassigned: false } s
            ? s.AreaId
            : (int?)null;

        try
        {
            IsBusy = true;

            // Neue Bereiche gehören der Site, in der man gerade arbeitet.
            // Sonst tauchen sie in JEDER Site auf — genau das ist passiert:
            // „Container" und „Haus 2" standen mit in der Schul-Sicht.
            // Bestehende Bereiche ohne Zuordnung bleiben davon unberührt,
            // das ist der Migrationsfall.
            var sites = string.IsNullOrWhiteSpace(ActiveSite)
                ? Array.Empty<string>()
                : [ActiveSite];

            await _areas.CreateAsync(name, parent, sites);
            Recompute(_status.AllServices);
            StatusMessage = sites.Length > 0
                ? $"Bereich „{name.Trim()}“ angelegt — sichtbar in {ActiveSite}."
                : $"Bereich „{name.Trim()}“ angelegt.";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Bereich konnte nicht angelegt werden.");
            StatusMessage = $"Anlegen fehlgeschlagen: {ex.Message}";
            return false;
        }
        finally { IsBusy = false; }
    }

    public async Task<bool> RenameAsync(string name)
    {
        if (!CanWrite || SelectedNode is not { IsUnassigned: false } node) return false;
        if (string.IsNullOrWhiteSpace(name)) return false;

        try
        {
            IsBusy = true;
            await _areas.RenameAsync(node.AreaId, name);
            Recompute(_status.AllServices);
            StatusMessage = $"Bereich umbenannt in „{name.Trim()}“.";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Bereich konnte nicht umbenannt werden.");
            StatusMessage = $"Umbenennen fehlgeschlagen: {ex.Message}";
            return false;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Löscht den gewählten Bereich. Gibt eine Klartext-Begründung
    /// zurück, wenn er noch nicht leer ist — sonst bliebe es beim wirkungslosen
    /// Klick.</summary>
    public async Task<string?> DeleteSelectedAsync()
    {
        if (!CanWrite || SelectedNode is not { IsUnassigned: false } node) return null;

        try
        {
            IsBusy = true;
            var result = await _areas.DeleteAsync(node.AreaId);
            if (!result.Deleted)
            {
                var parts = new List<string>();
                if (result.ChildCount > 0) parts.Add($"{result.ChildCount} Unterbereich(e)");
                if (result.HostCount > 0) parts.Add($"{result.HostCount} zugeordnete(n) Host(s)");
                return $"„{node.Name}“ enthält noch {string.Join(" und ", parts)} — "
                     + "erst leeren, dann löschen.";
            }

            SelectedNode = null;
            Recompute(_status.AllServices);
            StatusMessage = $"Bereich „{node.Name}“ gelöscht.";
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Bereich konnte nicht geloescht werden.");
            return $"Löschen fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Ordnet Hosts einem Bereich zu (null = Zuordnung entfernen).</summary>
    public async Task AssignAsync(IReadOnlyList<string> hosts, int? areaId)
    {
        if (!CanWrite || hosts.Count == 0) return;

        try
        {
            IsBusy = true;
            await _areas.AssignAsync(hosts, areaId);
            Recompute(_status.AllServices);

            var target = areaId is { } id
                ? _byId.GetValueOrDefault(id)?.Name ?? id.ToString()
                : "(Zuordnung entfernt)";
            StatusMessage = $"{hosts.Count} Host(s) → {target}.";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Bereichszuordnung fehlgeschlagen.");
            StatusMessage = $"Zuordnung fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Alle echten Bereiche flach, für Auswahldialoge.</summary>
    public IReadOnlyList<AreaNodeViewModel> AllAreas()
        => [.. Roots.Where(r => !r.IsUnassigned).SelectMany(r => r.Flatten())];

    /// <summary>Hostnamen im Bereich — die „Technik", die dort steht.</summary>
    public IReadOnlyList<string> HostsIn(int areaId) => _areas.HostsIn(areaId);

    /// <summary>Sites, in denen ein Bereich sichtbar ist. Leer = überall.</summary>
    public IReadOnlyList<string> SitesOf(int areaId) => _areas.SitesOf(areaId);

    /// <summary>Host-Namensmuster eines Bereichs, oder <c>null</c>.</summary>
    public string? HostPatternOf(int areaId)
        => _areas.Current.Areas.FirstOrDefault(a => a.AreaId == areaId)?.HostPattern;

    /// <summary>Checkmk-Ortstag eines Bereichs, oder <c>null</c>.</summary>
    public string? HostTagOf(int areaId)
        => _areas.Current.Areas.FirstOrDefault(a => a.AreaId == areaId)?.HostTag;

    /// <summary>Eigener Kartenhintergrund eines Bereichs, oder <c>null</c>.</summary>
    public string? MapLayerOf(int areaId)
        => _areas.Current.Areas.FirstOrDefault(a => a.AreaId == areaId)?.MapLayerKey;

    public async Task SaveMapLayerAsync(int areaId, string? layerKey)
    {
        if (!CanWrite) return;
        try
        {
            IsBusy = true;
            await _areas.SaveMapLayerAsync(areaId, layerKey);
            StatusMessage = layerKey is null
                ? $"{NodeOf(areaId)?.Name}: Kartenhintergrund auf Vorgabe zurückgesetzt."
                : $"{NodeOf(areaId)?.Name}: Kartenhintergrund „{layerKey}“.";
            MapChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Kartenhintergrund konnte nicht gespeichert werden.");
            StatusMessage = $"Speichern fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Muster-Vorschlag für einen Bereich, der noch keins hat — aus dem Code
    /// der Herkunftsquelle. Damit steht im Dialog etwas Sinnvolles, statt dass
    /// jemand den Ausdruck mit den Ziffern-Grenzen von Hand tippt.
    /// </summary>
    public string? SuggestedPatternFor(int areaId)
    {
        var area = _areas.Current.Areas.FirstOrDefault(a => a.AreaId == areaId);
        if (area is null) return null;
        if (!string.IsNullOrWhiteSpace(area.HostPattern)) return area.HostPattern;
        return PotsdamPlaceImporter.PatternFor(area.ExternalCode);
    }

    /// <summary>Alle in Checkmk vorkommenden Ortstags — für die Auswahlliste.</summary>
    public IReadOnlyList<HostTagValue> KnownTags() => _locationTags.Values;

    /// <summary>Ortstag eines Hosts, oder <c>null</c>.</summary>
    public string? TagOfHost(string hostName) => _locationTags.TagFor(hostName);

    /// <summary>Speichert beide Zuordnungswege eines Bereichs in einem Vorgang.</summary>
    public async Task SaveAssignmentRuleAsync(int areaId, string? tag, string? pattern)
    {
        if (!CanWrite) return;
        try
        {
            IsBusy = true;

            var before = _areas.Current.Areas.FirstOrDefault(a => a.AreaId == areaId);
            var tagChanged = !string.Equals(before?.HostTag, tag, StringComparison.Ordinal);
            var patternChanged = !string.Equals(before?.HostPattern, pattern, StringComparison.Ordinal);

            if (tagChanged) await _areas.SaveHostTagAsync(areaId, tag);
            if (patternChanged) await _areas.SaveHostPatternAsync(areaId, pattern);

            var name = NodeOf(areaId)?.Name;
            StatusMessage = (tagChanged, patternChanged) switch
            {
                (false, false) => "Nichts geändert.",
                _ => $"{name}: Tag {Describe(tag)}, Muster {Describe(pattern)}."
            };
        }
        catch (Exception ex)
        {
            // Der eindeutige Index auf HostTag schlaegt hier zu, wenn ein
            // anderer Bereich denselben Tag schon traegt. Die Meldung des
            // Servers ist unlesbar, die Ursache aber immer dieselbe.
            Log.Warn(ex, "Zuordnungsregel konnte nicht gespeichert werden.");
            StatusMessage = ex.ToString().Contains("UX_Area_HostTag", StringComparison.Ordinal)
                ? $"Der Tag „{tag}“ gehört bereits zu einem anderen Bereich."
                : $"Speichern fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }

        static string Describe(string? v) => string.IsNullOrWhiteSpace(v) ? "entfernt" : $"→ {v}";
    }

    /// <summary>
    /// Schlägt vor, welcher Checkmk-Ortstag zu welchem Bereich gehört — über
    /// die Nummer im Code der Herkunftsquelle. Nur die sichtbaren Bereiche,
    /// damit ein Abgleich auf <c>schul_it</c> keine LHP-Bereiche anfasst.
    /// </summary>
    public IReadOnlyList<TagMatch> SuggestTags()
    {
        var snapshot = _areas.Current;
        var visible = snapshot.Areas
            .Where(a => snapshot.IsVisibleIn(a.AreaId, ActiveSite))
            .ToList();
        return HostTagMatcher.Match(visible, _locationTags.Values);
    }

    /// <summary>Übernimmt bestätigte Tag-Zuordnungen in einem Vorgang.</summary>
    public async Task ApplyTagMatchesAsync(IReadOnlyList<TagMatch> accepted)
    {
        if (!CanWrite || accepted.Count == 0) return;

        try
        {
            IsBusy = true;
            await _areas.SaveHostTagsAsync(
                accepted.ToDictionary(m => m.AreaId, m => (string?)m.TagValue));
            Recompute(_status.AllServices);
            StatusMessage = $"{accepted.Count} Tag-Zuordnung(en) gespeichert.";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Tag-Zuordnungen konnten nicht gespeichert werden.");
            StatusMessage = $"Speichern fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Alle Hostnamen der aktuellen Sicht. Aus den Services abgeleitet, weil
    /// der Status-Tab ohnehin serverseitig auf den aktiven Filter beschränkt
    /// hat — Vorschläge sollen nur Hosts betreffen, die man auch sieht.
    /// </summary>
    public IReadOnlyList<string> KnownHosts()
        => [.. _status.AllServices.Select(s => s.HostName)
                                  .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Zuordnungsvorschläge aus den Mustern der sichtbaren Bereiche.</summary>
    public IReadOnlyList<AssignmentSuggestion> SuggestAssignments()
    {
        var snapshot = _areas.Current;
        var visible = snapshot.Areas
            .Where(a => snapshot.IsVisibleIn(a.AreaId, ActiveSite))
            .ToList();
        return AreaAssignmentSuggester.Suggest(visible, KnownHosts(), snapshot.HostToArea,
            _locationTags.TagFor);
    }

    /// <summary>Übernimmt bestätigte Vorschläge.</summary>
    public async Task ApplySuggestionsAsync(IReadOnlyList<AssignmentSuggestion> accepted)
    {
        if (!CanWrite || accepted.Count == 0) return;

        try
        {
            IsBusy = true;
            // Nach Zielbereich buendeln: ein Schreibvorgang je Bereich statt
            // je Host — bei tausend Vorschlaegen macht das den Unterschied.
            foreach (var group in accepted.GroupBy(s => s.AreaId))
                await _areas.AssignAsync([.. group.Select(s => s.HostName)], group.Key);

            Recompute(_status.AllServices);
            MapChanged?.Invoke();
            StatusMessage = $"{accepted.Count} Host(s) zugeordnet "
                          + $"({accepted.Select(s => s.AreaId).Distinct().Count()} Bereiche).";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Zuordnung der Vorschlaege fehlgeschlagen.");
            StatusMessage = $"Zuordnen fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Setzt die Sichtbarkeit eines Bereichs. Leer = überall.</summary>
    public async Task SaveSitesAsync(int areaId, IReadOnlyList<string> sites)
    {
        if (!CanWrite) return;
        try
        {
            IsBusy = true;
            await _areas.SaveSitesAsync(areaId, sites);
            _builtFrom = "";                     // Sichtbarkeit ändert die Menge
            Recompute(_status.AllServices);
            MapChanged?.Invoke();

            var name = NodeOf(areaId)?.Name ?? areaId.ToString();
            StatusMessage = sites.Count == 0
                ? $"„{name}“ ist jetzt in allen Sites sichtbar."
                : $"„{name}“ ist jetzt sichtbar in: {string.Join(", ", sites)}.";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Site-Sichtbarkeit konnte nicht gespeichert werden.");
            StatusMessage = $"Speichern fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Lagen aller sichtbaren Bereiche — Mittelpunkt der Fläche oder
    /// der Punkt. Grundlage für das Vorabladen der Kacheln.</summary>
    public IReadOnlyList<GeoPoint> PlacePoints()
    {
        var points = new List<GeoPoint>();
        foreach (var node in AllAreas())
        {
            if (MapGeometry.Parse(GeometryOf(node.AreaId)) is { Count: >= 3 } shape
                && MapGeometry.Bounds(shape) is { } b)
            {
                points.Add(new GeoPoint((b.Min.Lon + b.Max.Lon) / 2, (b.Min.Lat + b.Max.Lat) / 2));
            }
            else if (PointOf(node.AreaId) is { } p)
            {
                points.Add(new GeoPoint(p.Lon, p.Lat));
            }
        }
        return points;
    }

    /// <summary>
    /// Verschiebt die gesamte Technik eines Bereichs in einen anderen. Der
    /// Alltagsfall: Ein Haus wird aufgelöst, alles wandert in den Container —
    /// und später vielleicht zurück.
    /// </summary>
    public async Task<int> MoveHostsAsync(int fromAreaId, int? toAreaId)
    {
        if (!CanWrite) return 0;
        try
        {
            IsBusy = true;
            var moved = await _areas.MoveHostsAsync(fromAreaId, toAreaId);
            Recompute(_status.AllServices);
            MapChanged?.Invoke();

            var target = toAreaId is { } id ? NodeOf(id)?.Name ?? id.ToString() : "(ohne Bereich)";
            StatusMessage = moved == 0
                ? "Dort steht keine Technik — nichts zu verschieben."
                : $"{moved} Host(s) verschoben nach {target}.";
            return moved;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Verschieben fehlgeschlagen.");
            StatusMessage = $"Verschieben fehlgeschlagen: {ex.Message}";
            return 0;
        }
        finally { IsBusy = false; }
    }

    // -----------------------------------------------------------------------
    // Karte
    // -----------------------------------------------------------------------

    /// <summary>Wird gefeuert, wenn sich Flächen oder Farben geändert haben —
    /// die Karte zeichnet daraufhin neu.</summary>
    public event Action? MapChanged;

    /// <summary>Rohe Fläche eines Bereichs (GeoJSON), oder <c>null</c>.</summary>
    public string? GeometryOf(int areaId)
        => _areas.Current.Areas.FirstOrDefault(a => a.AreaId == areaId)?.GeometryJson;

    /// <summary>Punktlage eines Bereichs, oder <c>null</c>.</summary>
    public (double Lat, double Lon)? PointOf(int areaId)
    {
        var row = _areas.Current.Areas.FirstOrDefault(a => a.AreaId == areaId);
        return row is { Lat: { } lat, Lon: { } lon } ? (lat, lon) : null;
    }

    public string? AddressOf(int areaId)
        => _areas.Current.Areas.FirstOrDefault(a => a.AreaId == areaId)?.Address;

    /// <summary>Setzt die Punktlage (null entfernt sie).</summary>
    public async Task SavePointAsync(int areaId, double? lat, double? lon)
    {
        if (!CanWrite) return;
        try
        {
            IsBusy = true;
            await _areas.SavePointAsync(areaId, lat, lon);
            Recompute(_status.AllServices);
            MapChanged?.Invoke();
            StatusMessage = lat is null
                ? $"Position entfernt: {NodeOf(areaId)?.Name}."
                : $"Position gesetzt: {NodeOf(areaId)?.Name}.";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Position konnte nicht gespeichert werden.");
            StatusMessage = $"Position speichern fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Übernimmt ausgewählte Standorte als Bereiche.</summary>
    public async Task<ImportResult?> ImportPlacesAsync(string source,
        IReadOnlyList<ExternalPlace> places, int? parentAreaId, IReadOnlyList<string> sites)
    {
        if (!CanWrite || places.Count == 0) return null;
        try
        {
            IsBusy = true;
            // Muster gleich mit ableiten: Bei Schulen steckt die Nummer aus
            // SCHULNUM im Hostnamen (46-SW04, NAS46-01, iRMC-46). Der Importer
            // kennt dabei die Potsdamer Eigenheit der zusammengelegten Schulen.
            var result = await _areas.ImportPlacesAsync(source, places, parentAreaId, sites,
                PotsdamPlaceImporter.PatternFor);
            Recompute(_status.AllServices);
            MapChanged?.Invoke();
            StatusMessage = $"Standorte übernommen: {result.Created} neu, "
                          + $"{result.Updated} aktualisiert, {result.Unchanged} unverändert.";
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Standort-Import fehlgeschlagen.");
            StatusMessage = $"Import fehlgeschlagen: {ex.Message}";
            return null;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Aggregat eines Bereichs für die Einfärbung auf der Karte.</summary>
    public AreaNodeViewModel? NodeOf(int areaId) => _byId.GetValueOrDefault(areaId);

    /// <summary>Speichert die gezeichnete Fläche. <c>null</c> entfernt sie.</summary>
    public async Task SaveGeometryAsync(int areaId, string? geoJson)
    {
        if (!CanWrite) return;

        try
        {
            IsBusy = true;
            await _areas.SaveGeometryAsync(areaId, geoJson);
            Recompute(_status.AllServices);
            MapChanged?.Invoke();
            StatusMessage = geoJson is null
                ? $"Fläche entfernt: {NodeOf(areaId)?.Name}."
                : $"Fläche gespeichert: {NodeOf(areaId)?.Name}.";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Flaeche konnte nicht gespeichert werden.");
            StatusMessage = $"Speichern der Fläche fehlgeschlagen: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}
