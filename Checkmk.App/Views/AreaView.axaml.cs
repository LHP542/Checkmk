using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Checkmk.App.Controls;
using Checkmk.App.Services;
using Checkmk.App.ViewModels;
using Checkmk.Core.Models;
using Checkmk.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Checkmk.App.Views;

public partial class AreaView : UserControl
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private MapCanvas? _map;

    public AreaView()
    {
        AvaloniaXamlLoader.Load(this);

        _map = this.FindControl<MapCanvas>("Map");
        if (_map is not null)
        {
            _map.AreaClicked += OnMapAreaClicked;
            _map.AreaRightClicked += OnMapAreaRightClicked;
            _map.DrawingFinished += OnMapDrawingFinished;
            _map.GeometryEdited += OnMapGeometryEdited;
            _map.DrawingModeChanged += UpdateDrawState;

            // GetService wegen des XAML-Previewers (kein DI-Container).
            if (App.Services?.GetService<MapTileLoader>() is { } tiles)
            {
                _map.Attach(tiles);
                SetUpLayerBox(tiles);
            }
        }

        DataContextChanged += (_, _) => BindViewModel();
    }

    private AreaViewModel? Vm => DataContext as AreaViewModel;

    /// <summary>
    /// Füllt den Kartenumschalter und stellt die zuletzt gewählte Ebene wieder
    /// her. Die Vorliebe liegt user-lokal in <c>statusview.json</c> — welchen
    /// Hintergrund jemand mag, ist keine zentrale Vorgabe.
    /// </summary>
    private void SetUpLayerBox(MapTileLoader tiles)
    {
        var box = this.FindControl<ComboBox>("LayerBox");
        if (box is null || tiles.Layers.Count == 0) return;

        foreach (var layer in tiles.Layers) box.Items.Add(layer);

        var store = App.Services?.GetService<IStatusViewStateStore>();
        var wanted = store?.Load().MapLayerName;
        var initial = tiles.Layers.FirstOrDefault(
                          l => string.Equals(l.Name, wanted, StringComparison.OrdinalIgnoreCase))
                      ?? tiles.Layers[0];

        tiles.Active = initial;
        _userLayer = initial;
        box.SelectedItem = initial;

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not MapLayerDefinition chosen) return;
            // Die Wahl in der Toolbar ist die persoenliche Vorliebe und bleibt
            // als Rueckfallebene stehen, wenn ein Bereich mit eigenem
            // Hintergrund markiert und wieder verlassen wird.
            _userLayer = chosen;
            tiles.Active = chosen;
            _map?.InvalidateVisual();

            if (store is null) return;
            var state = store.Load();
            state.MapLayerName = chosen.Name;
            store.Save(state);
        };
    }

    private void BindViewModel()
    {
        if (Vm is not { } vm) return;
        vm.MapChanged += RefreshMap;
        vm.PropertyChanged += OnVmPropertyChanged;
        RefreshMap();
        ApplyViewerMap();
    }

    /// <summary>
    /// Wendet die Kiosk-Vorgaben aus <c>viewer.json</c> an: Startbereich, Zoom,
    /// Hintergrund und ob der Baum überhaupt zu sehen ist.
    ///
    /// Läuft nur einmal. Die Werte sind <b>Startwerte</b>, keine Sperre — wer
    /// vor dem Bildschirm steht, darf verschieben und zoomen; nach einem
    /// Neustart steht wieder die vorgesehene Sicht da.
    /// </summary>
    private void ApplyViewerMap()
    {
        if (_viewerMapApplied) return;
        if (App.Services?.GetService<ViewerMode>()?.Map is not { } map) return;
        if (Vm is not { } vm || _map is null) return;

        _viewerMapApplied = true;

        // Baum ausblenden lassen sich Spalte und Splitter nur zusammen — bleibt
        // einer stehen, klafft links eine leere Spalte.
        if (!map.Tree && this.FindControl<Grid>("SplitGrid") is { } grid)
        {
            grid.ColumnDefinitions[0].Width = new GridLength(0);
            grid.ColumnDefinitions[1].Width = new GridLength(0);
        }

        if (!string.IsNullOrWhiteSpace(map.Layer)
            && App.Services?.GetService<MapTileLoader>() is { } tiles
            && tiles.Layers.FirstOrDefault(
                   l => l.Name.Equals(map.Layer, StringComparison.OrdinalIgnoreCase)) is { } chosen)
        {
            tiles.Active = chosen;
            _userLayer = chosen;
            if (this.FindControl<ComboBox>("LayerBox") is { } box) box.SelectedItem = chosen;
        }

        // Erst den Bereich suchen, dann springen. Ein Name, den es nicht gibt,
        // ist ein Tippfehler im Profil und gehoert ins Log — nicht in eine
        // stumme Gesamtuebersicht, bei der niemand merkt, dass die Vorgabe
        // wirkungslos war.
        if (!string.IsNullOrWhiteSpace(map.Area))
        {
            var node = vm.AllAreas().FirstOrDefault(
                n => n.Name.Equals(map.Area, StringComparison.OrdinalIgnoreCase));
            if (node is null)
                Log.Warn("Viewer-Profil: Startbereich '{Area}' gibt es nicht — "
                       + "Karte bleibt auf der Gesamtübersicht.", map.Area);
            else
                vm.SelectedNode = node;
        }

        // Zoom nach dem Sprung: SelectedNode passt die Ansicht ein, eine
        // ausdrueckliche Angabe soll das ueberstimmen.
        if (map.Zoom > 0) _map.SetZoom(map.Zoom);
    }

    private bool _viewerMapApplied;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AreaViewModel.SelectedNode)) return;
        if (_map is null || Vm is null) return;

        _map.HighlightedAreaId = Vm.SelectedNode is { IsUnassigned: false } n ? n.AreaId : null;
        UpdateDrawState();
        ApplyAreaLayer();

        // Kam die Auswahl vom Klick auf die Karte, bleibt die Ansicht stehen.
        if (_selectionFromMap) { _map.InvalidateVisual(); return; }

        // Auf die Lage springen, wenn es eine gibt — sonst muesste man sie auf
        // einer Stadtkarte erst suchen. Flaeche gewinnt vor Punkt.
        if (Vm.SelectedNode is { IsUnassigned: false } sel)
        {
            if (MapGeometry.Parse(Vm.GeometryOf(sel.AreaId)) is { Count: >= 3 } points)
                _map.FitTo(points);
            else if (Vm.PointOf(sel.AreaId) is { } p)
                _map.CenterOnPoint(new GeoPoint(p.Lon, p.Lat));
            else
                _map.InvalidateVisual();
            return;
        }
        _map.InvalidateVisual();
    }

    /// <summary>
    /// Schaltet auf den Hintergrund, den der markierte Bereich vorgibt — und
    /// zurück auf die Wahl des Anwenders, sobald ein Bereich ohne eigene
    /// Vorgabe markiert wird.
    ///
    /// Der Sinn: Auf der Campus-Ebene ist ein Gebäudeplan brauchbar, auf der
    /// Stadtebene nicht. Die Toolbar-Auswahl bleibt die persönliche Vorliebe;
    /// sie wird hier <b>nicht</b> überschrieben, nur vorübergehend übersteuert.
    /// </summary>
    private void ApplyAreaLayer()
    {
        if (_map is null || Vm is null) return;
        if (App.Services?.GetService<MapTileLoader>() is not { } tiles) return;

        var wanted = Vm.SelectedNode is { IsUnassigned: false } sel
            ? Vm.MapLayerOf(sel.AreaId)
            : null;

        var target = wanted is null
            ? _userLayer
            : tiles.Layers.FirstOrDefault(
                  l => string.Equals(l.Name, wanted, StringComparison.OrdinalIgnoreCase))
              ?? _userLayer;

        if (target is null || ReferenceEquals(tiles.Active, target)) return;

        tiles.Active = target;
        _map.InvalidateVisual();
    }

    /// <summary>Der in der Toolbar gewählte Hintergrund — die persönliche Vorliebe.</summary>
    private MapLayerDefinition? _userLayer;

    /// <summary>
    /// Eigener Kartenhintergrund für den markierten Bereich. „(Vorgabe)"
    /// entfernt ihn wieder.
    /// </summary>
    private async void OnAreaLayerClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (vm.SelectedNode is not { IsUnassigned: false } node) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (App.Services?.GetService<MapTileLoader>() is not { } tiles) return;

        var dialog = new MapLayerDialog(node.Name,
            [.. tiles.Layers.Select(l => l.Name)], vm.MapLayerOf(node.AreaId));
        if (await dialog.ShowDialog<MapLayerChoice?>(owner) is not { } choice) return;

        await vm.SaveMapLayerAsync(node.AreaId, choice.LayerName);
        ApplyAreaLayer();
    }

    /// <summary>Flächen und Farben neu an die Karte geben.</summary>
    private void RefreshMap()
    {
        if (_map is null || Vm is not { } vm) return;

        var shapes = new List<MapShape>();
        foreach (var node in vm.AllAreas())
        {
            var points = MapGeometry.Parse(vm.GeometryOf(node.AreaId));
            var point = vm.PointOf(node.AreaId) is { } p ? new GeoPoint(p.Lon, p.Lat) : (GeoPoint?)null;

            // Weder Flaeche noch Punkt: der Bereich existiert nur im Baum.
            if (points.Count < 3 && point is null) continue;

            shapes.Add(new MapShape(node.AreaId, node.Name, points, ColorFor(node), point));
        }

        _map.Shapes = shapes;
        _map.InvalidateVisual();
    }

    /// <summary>Dieselbe Ampel wie im Baum — grau, wenn keine Hosts drin sind.</summary>
    private static Color ColorFor(AreaNodeViewModel node) => node.IsEmptyOfHosts
        ? Color.FromRgb(0x88, 0x88, 0x88)
        : node.WorstState switch
        {
            ServiceState.Critical => Color.FromRgb(0xEF, 0x53, 0x50),
            ServiceState.Warning => Color.FromRgb(0xFF, 0xCA, 0x28),
            ServiceState.Unknown => Color.FromRgb(0xAB, 0x47, 0xBC),
            _ => Color.FromRgb(0x66, 0xBB, 0x6A)
        };

    /// <summary>
    /// Klick auf der Karte markiert den Bereich im Baum — aber <b>ohne</b> die
    /// Ansicht neu einzupassen. Der Anwender sieht die Fläche ja gerade; sie
    /// unter dem Zeiger wegspringen zu lassen, ist beim Rechtsklick sogar
    /// schädlich, weil das Kontextmenü dann über einer anderen Stelle steht.
    /// </summary>
    private void OnMapAreaClicked(int areaId)
    {
        if (Vm?.NodeOf(areaId) is not { } node) return;

        _selectionFromMap = true;
        try { Vm.SelectedNode = node; }
        finally { _selectionFromMap = false; }
    }

    private bool _selectionFromMap;

    /// <summary>
    /// Doppelklick auf ein Gerät im Bereichsbaum öffnet die Host-Details —
    /// dasselbe Fenster wie im Status-Tab.
    ///
    /// <b>Nur auf Host-Knoten.</b> Ein Doppelklick auf einen Bereich klappt ihn
    /// auf und zu; das ist die erwartete Bedienung eines Baums und darf nicht
    /// nebenbei ein Fenster öffnen.
    /// </summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm?.SelectedTreeItem is not AreaHostViewModel host) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (App.Services?.GetService<ICheckmkClientProvider>() is not { } clients) return;

        // CanWrite weiterreichen: im Viewer-Modus zeigt das Detailfenster sonst
        // Ack/Downtime/Kommentar, obwohl der Rest der Oberflaeche sie ausblendet.
        var detailVm = new HostDetailViewModel(clients, host.HostName, Vm.CanWrite);
        new HostDetailWindow(detailVm).Show(owner);

        e.Handled = true;
    }

    /// <summary>
    /// Rechtsklick auf der Karte öffnet dasselbe Menü wie im Baum. Der Weg
    /// „Fläche sehen → im Baum suchen → Rechtsklick" war der Umweg, den man
    /// bei jedem Zuordnen ging.
    /// </summary>
    private void OnMapAreaRightClicked(int areaId)
    {
        if (Vm is not { CanWrite: true } || _map is null) return;
        if (this.FindControl<ContextMenu>("MapMenu") is not { } menu) return;

        menu.Open(_map);
    }

    private async void OnMapDrawingFinished(IReadOnlyList<GeoPoint> points)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (vm.SelectedNode is not { IsUnassigned: false } node) return;

        await vm.SaveGeometryAsync(node.AreaId, MapGeometry.ToGeoJson(points));
        RefreshMap();
    }

    private async void OnMapGeometryEdited(int areaId, IReadOnlyList<GeoPoint> points)
    {
        if (Vm is not { CanWrite: true } vm) return;

        await vm.SaveGeometryAsync(areaId, MapGeometry.ToGeoJson(points));
        RefreshMap();
    }

    private void UpdateDrawState()
    {
        var drawing = _map?.IsDrawing ?? false;
        var editing = _map?.IsEditing ?? false;

        if (this.FindControl<Button>("DrawButton") is { } b)
            b.Content = drawing ? "Zeichnen abbrechen" : "Fläche zeichnen";
        if (this.FindControl<Button>("EditButton") is { } eb)
        {
            eb.Content = editing ? "Bearbeitung beenden" : "Fläche bearbeiten";
            eb.IsVisible = editing || CanEditSelectedArea();
        }
        if (this.FindControl<Border>("DrawHint") is { } hint)
            hint.IsVisible = drawing;
        if (this.FindControl<Border>("EditHint") is { } ehint)
            ehint.IsVisible = editing;
    }

    /// <summary>Hat der markierte Bereich überhaupt eine Fläche zum Bearbeiten?</summary>
    private bool CanEditSelectedArea()
        => Vm is { CanWrite: true, SelectedNode: { IsUnassigned: false } node }
        && _map?.Shapes.Any(s => s.AreaId == node.AreaId && s.HasArea) == true;

    /// <summary>
    /// Holt die Verwaltungsstandorte der Landeshauptstadt und legt die
    /// ausgewählten als Bereiche an. Ist im Baum ein Bereich markiert, kommen
    /// sie darunter — so lassen sich „Außenstellen" gebündelt einhängen, statt
    /// dreißig Wurzelknoten zu erzeugen.
    /// </summary>
    private async void OnImportPlacesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var importer = App.Services?.GetService<PotsdamPlaceImporter>();
        if (importer is null) return;

        // Welche Liste, und fuer welche Sites? Die Schulen gehoeren zur Site
        // Schul_IT und sind ein eigener Dienst.
        var selected = vm.SelectedNode is { IsUnassigned: false } sel ? sel.Name : null;
        var pick = new PlaceSourceDialog(
            PotsdamPlaceImporter.Sources, KnownSites(), vm.ActiveSite, selected);
        if (await pick.ShowDialog<PlaceSourceChoice?>(owner) is not { } choice) return;

        vm.StatusMessage = $"{choice.Source.Label} werden vom Kartenserver geladen…";
        var places = await importer.LoadAsync(choice.Source);
        if (places.Count == 0)
        {
            vm.StatusMessage =
                $"Keine {choice.Source.Label} erhalten — Kartenserver nicht erreichbar? Siehe Log.";
            return;
        }

        var dialog = new PlaceImportDialog(choice.Source.Label, places);
        var chosen = await dialog.ShowDialog<IReadOnlyList<ExternalPlace>?>(owner);
        if (chosen is null || chosen.Count == 0) return;

        var parent = choice.NestUnderSelection && vm.SelectedNode is { IsUnassigned: false } n
            ? n.AreaId
            : (int?)null;
        await vm.ImportPlacesAsync(choice.Source.Id, chosen, parent, choice.Sites);
        RefreshMap();
    }

    /// <summary>
    /// Verschiebt die gesamte Technik eines Bereichs. Der Alltagsfall: Haus 2
    /// wird aufgelöst, alles wandert in den Container — und irgendwann
    /// vielleicht zurück.
    /// </summary>
    private async void OnMoveHostsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (vm.SelectedNode is not { IsUnassigned: false } node) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var hosts = vm.HostsIn(node.AreaId);
        if (hosts.Count == 0)
        {
            vm.StatusMessage = $"In „{node.Name}“ steht keine Technik.";
            return;
        }

        var dialog = new AreaPickerDialog(
            $"{hosts.Count} Host(s) aus „{node.Name}“ verschieben nach:", vm.Roots);
        var result = await dialog.ShowDialog<AreaPickResult?>(owner);
        if (result is null) return;
        if (result.AreaId == node.AreaId) return;   // Ziel = Quelle

        await vm.MoveHostsAsync(node.AreaId, result.AreaId);
        RefreshMap();
    }

    /// <summary>Bekannte Sites aus den Verbindungseinstellungen.</summary>
    private static IReadOnlyList<string> KnownSites()
    {
        var settings = App.Services?.GetService<IConnectionSettingsStore>()?.Load();
        if (settings?.KnownSites is { Count: > 0 } k) return k;
        return settings?.Site is { Length: > 0 } s ? [s] : [];
    }

    private async void OnEditSitesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (vm.SelectedNode is not { IsUnassigned: false } node) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new SiteSelectDialog(node.Name, KnownSites(), vm.SitesOf(node.AreaId));
        if (await dialog.ShowDialog<IReadOnlyList<string>?>(owner) is not { } chosen) return;

        await vm.SaveSitesAsync(node.AreaId, chosen);
        RefreshMap();
    }

    private async void OnEditPatternClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (vm.SelectedNode is not { IsUnassigned: false } node) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        // Ohne eigenes Muster mit dem aus der Schulnummer abgeleiteten
        // vorbelegen: Das Feld leer zu zeigen, obwohl die Nummer bekannt ist,
        // heisst, den Ausdruck samt Ziffern-Grenzen von Hand zu tippen.
        var dialog = new HostPatternDialog(
            node.Name,
            vm.HostTagOf(node.AreaId),
            vm.SuggestedPatternFor(node.AreaId),
            vm.KnownHosts(),
            vm.KnownTags(),
            vm.TagOfHost);
        if (await dialog.ShowDialog<HostAssignmentRule?>(owner) is not { } rule) return;

        await vm.SaveAssignmentRuleAsync(node.AreaId, rule.Tag, rule.Pattern);
        RefreshMap();
    }

    /// <summary>
    /// Gleicht die Checkmk-Ortstags gegen die Bereiche ab — der Schritt, der
    /// aus 49 Handeingaben eine Sichtprüfung macht. Läuft über die Nummer im
    /// Code der Herkunftsquelle und schreibt nichts ohne Bestätigung.
    /// </summary>
    private async void OnMatchTagsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var matches = vm.SuggestTags();
        if (matches.Count == 0)
        {
            vm.StatusMessage = vm.KnownTags().Count == 0
                ? "Keine Ortstags gelesen — einmal den Hosts-Tab aktualisieren."
                : "Keine Tag-Zuordnung ableitbar. Fehlt den Bereichen der Code aus "
                + "der Herkunftsquelle? Standorte erneut importieren.";
            return;
        }

        var dialog = new TagMatchDialog(matches);
        var accepted = await dialog.ShowDialog<IReadOnlyList<TagMatch>?>(owner);
        if (accepted is null || accepted.Count == 0) return;

        await vm.ApplyTagMatchesAsync(accepted);
        RefreshMap();
    }

    /// <summary>
    /// Sucht anhand von Ortstag und Host-Muster, welche Hosts zu welchem
    /// Bereich gehören. Ordnet nichts selbst zu — 93 Bereiche und über tausend
    /// Hosts von Hand zu verteilen ist keine Option, eine falsche
    /// Massenzuordnung hinterher aufzuräumen aber auch nicht.
    /// </summary>
    private async void OnSuggestClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var suggestions = vm.SuggestAssignments();
        if (suggestions.Count == 0)
        {
            vm.StatusMessage = "Keine Vorschläge — kein Bereich hat einen Ortstag oder "
                             + "ein Host-Muster, oder alles passt bereits. „Tags zuordnen…“ "
                             + "in der Leiste, oder Rechtsklick auf einen Bereich "
                             + "→ Host-Zuordnung…";
            return;
        }

        var dialog = new AssignSuggestionDialog(suggestions);
        var accepted = await dialog.ShowDialog<IReadOnlyList<AssignmentSuggestion>?>(owner);
        if (accepted is null || accepted.Count == 0) return;

        await vm.ApplySuggestionsAsync(accepted);
        RefreshMap();
    }

    private CancellationTokenSource? _prefetchCts;

    /// <summary>
    /// Lädt die Kacheln rund um alle Standorte im Hintergrund. Danach ist der
    /// erste Blick auf einen Standort sofort da statt nach fünf Sekunden — und
    /// die Sicht funktioniert auch ohne Internet.
    /// </summary>
    private async void OnPrefetchClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var tiles = App.Services?.GetService<MapTileLoader>();
        if (tiles is null) return;

        // Zweiter Klick bricht ab — sonst müsste man die App beenden.
        if (_prefetchCts is { } running)
        {
            running.Cancel();
            return;
        }

        var points = vm.PlacePoints();
        if (points.Count == 0)
        {
            vm.StatusMessage = "Keine Standorte mit Lage — erst Bereiche anlegen oder übernehmen.";
            return;
        }

        // Stadtübersicht aus den Standorten selbst ableiten, mit etwas Rand.
        // So passt sie zu dem, was tatsächlich gebraucht wird, statt eine feste
        // Bounding-Box für Potsdam einzubauen.
        var bounds = MapGeometry.Bounds(points) is { } b
            ? (new GeoPoint(b.Min.Lon - 0.02, b.Min.Lat - 0.02),
               new GeoPoint(b.Max.Lon + 0.02, b.Max.Lat + 0.02))
            : ((GeoPoint, GeoPoint)?)null;

        var plan = MapPrefetchPlanner.Plan(points, bounds);

        _prefetchCts = new CancellationTokenSource();
        var button = this.FindControl<Button>("PrefetchButton");
        if (button is not null) button.Content = "Vorladen abbrechen";

        var progress = new Progress<(int Done, int Total)>(p =>
            vm.StatusMessage = $"Karten vorladen: {p.Done}/{p.Total} Kacheln "
                             + $"({tiles.Active.Name})…");

        try
        {
            vm.StatusMessage = $"Karten vorladen: {plan.Count} Kacheln geplant…";
            await tiles.PrefetchAsync(plan, progress, _prefetchCts.Token);
            vm.StatusMessage = _prefetchCts.IsCancellationRequested
                ? "Vorladen abgebrochen — was geladen ist, bleibt im Cache."
                : $"Karten vorgeladen ({tiles.Active.Name}). Die Sicht funktioniert jetzt auch ohne Internet.";
        }
        catch (OperationCanceledException)
        {
            vm.StatusMessage = "Vorladen abgebrochen — was geladen ist, bleibt im Cache.";
        }
        finally
        {
            _prefetchCts.Dispose();
            _prefetchCts = null;
            if (button is not null) button.Content = "Karten vorladen";
            _map?.InvalidateVisual();
        }
    }

    private void OnDrawAreaClick(object? sender, RoutedEventArgs e)
    {
        if (_map is null || Vm is not { CanWrite: true } vm) return;
        if (vm.SelectedNode is not { IsUnassigned: false } node) return;

        if (_map.IsDrawing) { _map.CancelDrawing(); return; }

        vm.StatusMessage = $"Fläche für „{node.Name}“ zeichnen — Punkte klicken, "
                         + "Doppelklick oder Enter schließt ab.";
        _map.BeginDrawing();
    }

    /// <summary>
    /// Nimmt die Fläche des markierten Bereichs in die Bearbeitung. Bis hierher
    /// musste man sie neu zeichnen, wenn eine einzige Ecke daneben lag — bei
    /// einem Campus mit einem Dutzend Ecken dauert das länger als das erste Mal.
    /// </summary>
    private void OnEditAreaClick(object? sender, RoutedEventArgs e)
    {
        if (_map is null || Vm is not { CanWrite: true } vm) return;

        if (_map.IsEditing) { _map.FinishEditing(); return; }
        if (vm.SelectedNode is not { IsUnassigned: false } node) return;

        vm.StatusMessage = $"Fläche von „{node.Name}“ bearbeiten — Punkte ziehen, "
                         + "Kantenmitte klicken fügt ein, Rechtsklick entfernt, "
                         + "Enter übernimmt, Esc verwirft.";
        _map.BeginEditing(node.AreaId);
    }

    private async void OnNewAreaClick(object? sender, RoutedEventArgs e)
        => await CreateAsync(asChild: false);

    private async void OnNewChildAreaClick(object? sender, RoutedEventArgs e)
        => await CreateAsync(asChild: true);

    private async System.Threading.Tasks.Task CreateAsync(bool asChild)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var parent = asChild ? vm.SelectedNode : null;
        if (asChild && parent is not { IsUnassigned: false }) return;

        var prompt = asChild
            ? $"Neuer Bereich unterhalb von „{parent!.Name}“:"
            : "Name des neuen Bereichs:";

        var dialog = new NameInputDialog("Bereich anlegen", prompt, "");
        var name = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrWhiteSpace(name)) return;

        await vm.CreateAsync(name, asChild);
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (vm.SelectedNode is not { IsUnassigned: false } node) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new NameInputDialog("Bereich umbenennen", "Neuer Name:", node.Name);
        var name = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrWhiteSpace(name)) return;

        await vm.RenameAsync(name);
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;

        // Der Store lehnt nicht-leere Bereiche ab und liefert den Grund als Text —
        // der landet in der Statuszeile, damit der Klick nicht wirkungslos wirkt.
        var problem = await vm.DeleteSelectedAsync();
        if (problem is not null) vm.StatusMessage = problem;
    }

    /// <summary>
    /// Weist dem markierten Bereich Hosts zu. Die Quelle ist bewusst die Liste
    /// der noch nicht zugeordneten Hosts: Bei 1105 Geräten ist „was fehlt noch"
    /// die eigentliche Arbeitsliste. Einzelne Hosts weist man umgekehrt aus dem
    /// Status- oder Hosts-Tab zu, wo man sie ohnehin markiert hat.
    /// </summary>
    private async void OnAssignHostsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { CanWrite: true } vm) return;
        if (vm.SelectedNode is not { IsUnassigned: false } node) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var candidates = vm.UnassignedHosts;
        if (candidates.Count == 0)
        {
            vm.StatusMessage = "Im aktiven Filter ist kein Host ohne Bereich übrig.";
            return;
        }

        var dialog = new HostMultiSelectDialog(
            $"Hosts nach „{node.Name}“ zuordnen", candidates);
        var chosen = await dialog.ShowDialog<System.Collections.Generic.IReadOnlyList<string>?>(owner);
        if (chosen is null || chosen.Count == 0) return;

        await vm.AssignAsync(chosen, node.AreaId);
    }
}
