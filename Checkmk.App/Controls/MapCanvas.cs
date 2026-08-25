using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Checkmk.App.Services;

namespace Checkmk.App.Controls;

/// <summary>Ein Bereich, wie ihn die Karte zeichnet.</summary>
/// <param name="AreaId">Kennung für den Rückweg (Klick → Bereich).</param>
/// <param name="Outline">Farbe — kommt aus dem Status-Rollup.</param>
/// <param name="Points">Fläche, oder leer.</param>
/// <param name="Point">Punktlage, oder <c>null</c>. Hat ein Bereich beides,
/// gewinnt die Fläche — der Punkt bleibt aber als Sprungziel gültig.</param>
public sealed record MapShape(
    int AreaId,
    string Name,
    IReadOnlyList<GeoPoint> Points,
    Color Outline,
    GeoPoint? Point = null)
{
    public bool HasArea => Points.Count >= 3;
}

/// <summary>
/// Kachelkarte mit Polygon-Overlay. Bewusst ein eigenes Control statt einer
/// WebView: Was hier gebraucht wird — schieben, zoomen, Flächen zeichnen,
/// Treffer erkennen — ist überschaubar, und eine eingebettete Browser-Engine
/// in einem self-contained Single-File-EXE wäre der teurere Weg.
/// </summary>
public sealed class MapCanvas : Control
{
    // Potsdam, Alter Markt — sinnvoller Startpunkt für diese Stadtverwaltung.
    private static readonly GeoPoint DefaultCenter = new(13.0645, 52.3958);

    private MapTileLoader? _tiles;
    private GeoPoint _center = DefaultCenter;
    private double _zoom = 14;

    private bool _dragging;
    private Point _dragFrom;
    private GeoPoint _dragCenterAtStart;

    private readonly List<GeoPoint> _draft = [];
    private GeoPoint? _draftCursor;

    // Bearbeiten einer bestehenden Flaeche: Punkte ziehen, einfuegen, entfernen.
    private int? _editAreaId;
    private readonly List<GeoPoint> _edit = [];
    private int _dragVertex = -1;
    private int _hoverVertex = -1;
    private int _hoverMidpoint = -1;

    /// <summary>Anfassradius für Griffe. Kleiner, und man trifft sie mit der
    /// Maus nicht zuverlässig; größer, und benachbarte Punkte überlappen.</summary>
    private const double HandleRadius = 6.0;
    private const double GrabRadius = 10.0;

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;   // fuer Esc/Enter im Zeichenmodus
    }

    /// <summary>Bereiche mit Fläche. Wird bei jedem Rollup neu gesetzt.</summary>
    public IReadOnlyList<MapShape> Shapes { get; set; } = [];

    /// <summary>Hervorgehobener Bereich (Auswahl im Baum).</summary>
    public int? HighlightedAreaId { get; set; }

    /// <summary>true, solange der Anwender eine Fläche zeichnet.</summary>
    public bool IsDrawing { get; private set; }

    /// <summary>true, solange eine bestehende Fläche bearbeitet wird.</summary>
    public bool IsEditing => _editAreaId is not null;

    /// <summary>Zeichnen oder Bearbeiten — für Toolbar-Zustände.</summary>
    public bool IsBusy => IsDrawing || IsEditing;

    /// <summary>Klick auf eine Fläche (im Normalmodus).</summary>
    public event Action<int>? AreaClicked;

    /// <summary>Rechtsklick auf eine Fläche oder einen Marker.</summary>
    public event Action<int>? AreaRightClicked;

    /// <summary>Zeichnen abgeschlossen — liefert das fertige Polygon.</summary>
    public event Action<IReadOnlyList<GeoPoint>>? DrawingFinished;

    /// <summary>Bearbeiten abgeschlossen — Bereichs-Id und das geänderte Polygon.</summary>
    public event Action<int, IReadOnlyList<GeoPoint>>? GeometryEdited;

    /// <summary>Zeichen- oder Bearbeitungsmodus verlassen, für die Toolbar.</summary>
    public event Action? DrawingModeChanged;

    public void Attach(MapTileLoader tiles)
    {
        _tiles = tiles;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Ansicht
    // ------------------------------------------------------------------

    public void CenterOn(GeoPoint center, double zoom)
    {
        _center = center;
        _zoom = Math.Clamp(zoom, 2, 20);
        InvalidateVisual();
    }

    /// <summary>Zoomstufe setzen, Mittelpunkt behalten — für die Kiosk-Vorgabe,
    /// die das automatische Einpassen überstimmen soll.</summary>
    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 2, 20);
        InvalidateVisual();
    }

    /// <summary>Ansicht auf ein Polygon einpassen, mit etwas Luft am Rand.</summary>
    public void FitTo(IReadOnlyList<GeoPoint> points)
    {
        if (MapGeometry.Bounds(points) is not { } b) return;

        var zoom = WebMercator.FitZoom(b.Min, b.Max,
            Math.Max(32, Bounds.Width * 0.85), Math.Max(32, Bounds.Height * 0.85));
        CenterOn(new GeoPoint((b.Min.Lon + b.Max.Lon) / 2, (b.Min.Lat + b.Max.Lat) / 2), zoom);
    }

    // ------------------------------------------------------------------
    // Zeichenmodus
    // ------------------------------------------------------------------

    public void BeginDrawing()
    {
        _draft.Clear();
        _draftCursor = null;
        IsDrawing = true;
        Focus();
        DrawingModeChanged?.Invoke();
        InvalidateVisual();
    }

    public void CancelDrawing()
    {
        if (!IsDrawing) return;
        IsDrawing = false;
        _draft.Clear();
        _draftCursor = null;
        DrawingModeChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Schließt das Polygon ab. Unter drei Punkten passiert nichts —
    /// zwei Punkte sind eine Linie, keine Fläche.</summary>
    public void FinishDrawing()
    {
        if (!IsDrawing) return;

        var points = _draft.ToList();
        IsDrawing = false;
        _draft.Clear();
        _draftCursor = null;
        DrawingModeChanged?.Invoke();
        InvalidateVisual();

        if (points.Count >= 3) DrawingFinished?.Invoke(points);
    }

    /// <summary>Letzten gesetzten Punkt zurücknehmen.</summary>
    public void UndoLastPoint()
    {
        if (!IsDrawing || _draft.Count == 0) return;
        _draft.RemoveAt(_draft.Count - 1);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Bearbeitungsmodus
    //
    // Eine Fläche neu zeichnen zu müssen, weil ein Punkt daneben liegt, ist
    // der ärgerlichste Handgriff an der Karte: Ein Campus hat schnell ein
    // Dutzend Ecken, und alle wieder zu setzen dauert länger als das erste Mal.
    // ------------------------------------------------------------------

    /// <summary>
    /// Nimmt die Fläche eines Bereichs in die Bearbeitung. Gearbeitet wird auf
    /// einer <b>Kopie</b> — Esc muss zum unveränderten Ausgangszustand
    /// zurückführen, und der steht in <see cref="Shapes"/>.
    /// </summary>
    public void BeginEditing(int areaId)
    {
        var shape = Shapes.FirstOrDefault(s => s.AreaId == areaId && s.HasArea);
        if (shape is null) return;

        CancelDrawing();
        _editAreaId = areaId;
        _edit.Clear();
        _edit.AddRange(shape.Points);
        _dragVertex = -1;
        Focus();
        DrawingModeChanged?.Invoke();
        InvalidateVisual();
    }

    public void CancelEditing()
    {
        if (!IsEditing) return;
        _editAreaId = null;
        _edit.Clear();
        _dragVertex = _hoverVertex = _hoverMidpoint = -1;
        DrawingModeChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Übernimmt die Änderung. Unter drei Punkten ist es keine Fläche mehr.</summary>
    public void FinishEditing()
    {
        if (_editAreaId is not { } areaId) return;

        var points = _edit.ToList();
        _editAreaId = null;
        _edit.Clear();
        _dragVertex = _hoverVertex = _hoverMidpoint = -1;
        DrawingModeChanged?.Invoke();
        InvalidateVisual();

        if (points.Count >= MapGeometry.MinimumVertices)
            GeometryEdited?.Invoke(areaId, points);
    }

    /// <summary>
    /// Entfernt den Punkt unter dem Zeiger. <b>Nicht unter drei</b> — sonst
    /// bliebe eine Linie stehen, die als Fläche gespeichert würde.
    /// </summary>
    private bool RemoveVertexAt(Point position)
    {
        if (!IsEditing) return false;
        var i = VertexAt(position);
        if (i < 0) return false;
        return RemoveVertex(i);
    }

    /// <summary>Entfernt eine Ecke, wenn dabei noch eine Fläche übrig bleibt.</summary>
    private bool RemoveVertex(int index)
    {
        var reduced = MapGeometry.RemoveVertex(_edit, index);
        if (reduced.Count == _edit.Count) return false;   // Untergrenze erreicht

        _edit.Clear();
        _edit.AddRange(reduced);
        _hoverVertex = -1;
        InvalidateVisual();
        return true;
    }

    /// <summary>Index des Griffs unter dem Zeiger, oder -1.</summary>
    private int VertexAt(Point position)
    {
        for (var i = 0; i < _edit.Count; i++)
        {
            var s = ToScreen(_edit[i]);
            if (Math.Abs(s.X - position.X) <= GrabRadius && Math.Abs(s.Y - position.Y) <= GrabRadius)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Index des Mittelpunkt-Griffs unter dem Zeiger, oder -1. Ein Klick darauf
    /// fügt dort eine neue Ecke ein — der übliche Griff aus Kartenwerkzeugen,
    /// und deutlich schneller, als die Fläche für eine zusätzliche Ecke neu zu
    /// zeichnen.
    /// </summary>
    private int MidpointAt(Point position)
    {
        for (var i = 0; i < _edit.Count; i++)
        {
            var m = MidpointScreen(i);
            if (Math.Abs(m.X - position.X) <= GrabRadius && Math.Abs(m.Y - position.Y) <= GrabRadius)
                return i;
        }
        return -1;
    }

    private Point MidpointScreen(int i)
    {
        var a = ToScreen(_edit[i]);
        var b = ToScreen(_edit[(i + 1) % _edit.Count]);
        return new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    }

    // ------------------------------------------------------------------
    // Eingabe
    // ------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);

        // Rechtsklick: im Bearbeitungsmodus nimmt er eine Ecke weg, sonst
        // oeffnet er das Kontextmenue des getroffenen Bereichs.
        if (p.Properties.IsRightButtonPressed)
        {
            Focus();
            if (IsEditing)
            {
                if (RemoveVertexAt(p.Position)) e.Handled = true;
                return;
            }
            if (HitTest(p.Position) is { } hit)
            {
                AreaClicked?.Invoke(hit);        // erst markieren …
                AreaRightClicked?.Invoke(hit);   // … dann das Menue oeffnen
            }
            return;
        }

        if (!p.Properties.IsLeftButtonPressed) return;

        Focus();

        if (IsEditing)
        {
            // Mittelpunkt zuerst pruefen: Er liegt nie auf einer Ecke, aber die
            // Reihenfolge macht das Verhalten vorhersagbar.
            var mid = MidpointAt(p.Position);
            if (mid >= 0)
            {
                var grown = MapGeometry.InsertMidpoint(_edit, mid);
                _edit.Clear();
                _edit.AddRange(grown);
                _dragVertex = mid + 1;           // gleich weiterziehen koennen
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            var vertex = VertexAt(p.Position);
            if (vertex >= 0)
            {
                _dragVertex = vertex;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            // Daneben getroffen: Die Karte laesst sich weiter schieben, ohne
            // die Bearbeitung zu verlassen.
            _dragging = true;
            _dragFrom = p.Position;
            _dragCenterAtStart = _center;
            e.Pointer.Capture(this);
            return;
        }

        if (IsDrawing)
        {
            // Doppelklick schliesst die Flaeche — derselbe Griff wie in jedem
            // Zeichenprogramm.
            if (e.ClickCount >= 2) { FinishDrawing(); e.Handled = true; return; }

            _draft.Add(ToGeo(p.Position));
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragFrom = p.Position;
        _dragCenterAtStart = _center;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (IsEditing)
        {
            if (_dragVertex >= 0 && _dragVertex < _edit.Count)
            {
                _edit[_dragVertex] = ToGeo(pos);
                InvalidateVisual();
                return;
            }

            if (!_dragging)
            {
                // Griffe unter dem Zeiger hervorheben — ohne diese Rueckmeldung
                // raet man, ob man den Punkt trifft.
                var v = VertexAt(pos);
                var m = v >= 0 ? -1 : MidpointAt(pos);
                if (v != _hoverVertex || m != _hoverMidpoint)
                {
                    _hoverVertex = v;
                    _hoverMidpoint = m;
                    Cursor = v >= 0 || m >= 0
                        ? new Cursor(StandardCursorType.Hand)
                        : Cursor.Default;
                    InvalidateVisual();
                }
                return;
            }
        }
        else if (IsDrawing)
        {
            _draftCursor = ToGeo(pos);   // Gummiband zum Mauszeiger
            InvalidateVisual();
            return;
        }

        if (!_dragging) return;

        // Verschieben in Weltpixeln statt in Grad: In Mercator sind Grad je
        // nach Breite unterschiedlich breit, die Karte wuerde am Bildschirm
        // "rutschen" statt dem Zeiger zu folgen.
        var (cx, cy) = WebMercator.ToWorld(_dragCenterAtStart, _zoom);
        _center = WebMercator.ToGeo(cx - (pos.X - _dragFrom.X), cy - (pos.Y - _dragFrom.Y), _zoom);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragVertex >= 0)
        {
            _dragVertex = -1;
            e.Pointer.Capture(null);
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);

        // Im Bearbeitungsmodus ist ein Klick ins Leere kein Wechsel des
        // Bereichs — sonst waere die halbfertige Aenderung weg.
        if (IsEditing) return;

        // Kaum bewegt? Dann war es ein Klick auf eine Flaeche, kein Schieben.
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragFrom.X) < 3 && Math.Abs(pos.Y - _dragFrom.Y) < 3)
        {
            if (HitTest(pos) is { } areaId) AreaClicked?.Invoke(areaId);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;

        // Auf den Mauszeiger zoomen: der Punkt unter dem Zeiger bleibt stehen.
        // Ohne das rutscht bei jedem Rad-Schritt das Ziel aus dem Bild.
        var pos = e.GetPosition(this);
        var before = ToGeo(pos);

        _zoom = Math.Clamp(_zoom + (e.Delta.Y > 0 ? 1 : -1), 2, 20);

        var after = ToGeo(pos);
        _center = new GeoPoint(
            _center.Lon + (before.Lon - after.Lon),
            _center.Lat + (before.Lat - after.Lat));

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (IsEditing)
        {
            switch (e.Key)
            {
                case Key.Escape: CancelEditing(); e.Handled = true; break;
                case Key.Enter: FinishEditing(); e.Handled = true; break;
                case Key.Delete:
                    // Entf auf dem Griff unter dem Zeiger — dasselbe wie der
                    // Rechtsklick, nur fuer die, die zur Tastatur greifen.
                    if (_hoverVertex >= 0 && RemoveVertex(_hoverVertex)) e.Handled = true;
                    break;
            }
            return;
        }

        if (!IsDrawing) return;

        switch (e.Key)
        {
            case Key.Escape: CancelDrawing(); e.Handled = true; break;
            case Key.Enter: FinishDrawing(); e.Handled = true; break;
            case Key.Back: UndoLastPoint(); e.Handled = true; break;
        }
    }

    /// <summary>
    /// Welcher Bereich liegt unter dem Punkt?
    ///
    /// <b>Marker gewinnen vor Flächen</b>: Ein Standort-Marker ist ein paar
    /// Pixel groß und liegt oft innerhalb eines größeren Bereichs — würde die
    /// Fläche gewinnen, wäre der Marker nicht anklickbar. Unter den Flächen
    /// gewinnt die kleinste, damit ein Serverraum nicht vom Campus verdeckt wird.
    /// </summary>
    private int? HitTest(Point position)
    {
        const double MarkerRadius = 14;

        int? nearest = null;
        var nearestDistance = double.MaxValue;

        foreach (var s in Shapes)
        {
            if (s.HasArea || s.Point is not { } point) continue;
            var p = ToScreen(point);
            var d = Math.Sqrt(Math.Pow(p.X - position.X, 2) + Math.Pow(p.Y - position.Y, 2));
            if (d > MarkerRadius || d >= nearestDistance) continue;
            nearestDistance = d;
            nearest = s.AreaId;
        }
        if (nearest is not null) return nearest;

        var geo = ToGeo(position);
        int? best = null;
        var bestSize = double.MaxValue;

        foreach (var s in Shapes)
        {
            if (!s.HasArea) continue;
            if (!MapGeometry.Contains(s.Points, geo)) continue;
            if (MapGeometry.Bounds(s.Points) is not { } b) continue;

            var size = (b.Max.Lon - b.Min.Lon) * (b.Max.Lat - b.Min.Lat);
            if (size >= bestSize) continue;
            bestSize = size;
            best = s.AreaId;
        }
        return best;
    }

    /// <summary>Ansicht auf einen einzelnen Punkt zentrieren, ohne den Zoom zu
    /// verlieren — für den Sprung auf einen Standort-Marker.</summary>
    public void CenterOnPoint(GeoPoint point)
    {
        _center = point;
        if (_zoom < 15) _zoom = 16;   // aus der Stadtsicht sinnvoll heranholen
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Zeichnen
    // ------------------------------------------------------------------

    private (double X, double Y) TopLeftWorld()
    {
        var (cx, cy) = WebMercator.ToWorld(_center, _zoom);
        return (cx - Bounds.Width / 2, cy - Bounds.Height / 2);
    }

    private Point ToScreen(GeoPoint p)
    {
        var (wx, wy) = WebMercator.ToWorld(p, _zoom);
        var (ox, oy) = TopLeftWorld();
        return new Point(wx - ox, wy - oy);
    }

    private GeoPoint ToGeo(Point screen)
    {
        var (ox, oy) = TopLeftWorld();
        return WebMercator.ToGeo(ox + screen.X, oy + screen.Y, _zoom);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Grundfarbe: ohne sie blitzt beim Schieben der Fensterhintergrund durch.
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), Bounds);

        DrawTiles(context);
        DrawShapes(context);
        DrawDraft(context);
        DrawEdit(context);
        DrawAttribution(context);
    }

    private void DrawTiles(DrawingContext context)
    {
        if (_tiles is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var zoom = (int)Math.Round(_zoom);
        var (ox, oy) = TopLeftWorld();

        // Der Kachelindex gilt fuer ganzzahlige Zoomstufen; bei krummem _zoom
        // wird das Bild skaliert gezeichnet statt eine falsche Stufe zu laden.
        var scale = Math.Pow(2, _zoom - zoom);
        var tileOnScreen = WebMercator.TileSize * scale;

        var firstX = (int)Math.Floor(ox / tileOnScreen);
        var firstY = (int)Math.Floor(oy / tileOnScreen);
        var countX = (int)Math.Ceiling(Bounds.Width / tileOnScreen) + 1;
        var countY = (int)Math.Ceiling(Bounds.Height / tileOnScreen) + 1;

        var max = 1 << zoom;

        for (var dx = 0; dx < countX; dx++)
        for (var dy = 0; dy < countY; dy++)
        {
            var tx = firstX + dx;
            var ty = firstY + dy;
            if (tx < 0 || ty < 0 || tx >= max || ty >= max) continue;   // ausserhalb der Welt

            var key = new TileKey(zoom, tx, ty);
            var rect = new Rect(
                tx * tileOnScreen - ox, ty * tileOnScreen - oy,
                tileOnScreen + 0.5, tileOnScreen + 0.5);   // halbes Pixel gegen Fugen

            if (_tiles.Peek(key) is { } bitmap)
                context.DrawImage(bitmap, rect);
            else
                _tiles.Request(key, () => Dispatcher.UIThread.Post(InvalidateVisual));
        }
    }

    private void DrawShapes(DrawingContext context)
    {
        // Erst die Flaechen, dann die Marker: ein Punkt in einem Bereich soll
        // nicht unter dessen Fuellung verschwinden.
        foreach (var shape in Shapes)
        {
            // Die Flaeche in Bearbeitung wird von DrawEdit gezeichnet — sonst
            // laege der alte Umriss ueber dem neuen und man saehe nicht, was
            // man gerade tut.
            if (shape.AreaId == _editAreaId) continue;
            DrawArea(context, shape);
        }
        foreach (var shape in Shapes) DrawMarker(context, shape);
    }

    /// <summary>
    /// Die Fläche in Bearbeitung: durchgezogener Umriss, Griffe auf den Ecken
    /// und kleinere Griffe auf den Kantenmitten zum Einfügen.
    /// </summary>
    private void DrawEdit(DrawingContext context)
    {
        if (!IsEditing || _edit.Count < 2) return;

        var accent = Color.FromRgb(0x4F, 0xC3, 0xF7);
        context.DrawGeometry(
            new SolidColorBrush(accent, 0.22),
            new Pen(new SolidColorBrush(accent), 2.5),
            BuildGeometry(_edit, close: true));

        // Kantenmitten zuerst: Sie liegen tiefer als die Ecken und sollen von
        // einem benachbarten Eckgriff verdeckt werden, nicht umgekehrt.
        for (var i = 0; i < _edit.Count; i++)
        {
            var m = MidpointScreen(i);
            var hot = _hoverMidpoint == i;
            context.DrawEllipse(
                new SolidColorBrush(accent, hot ? 0.95 : 0.5),
                new Pen(Brushes.Black, 1),
                m, hot ? HandleRadius : HandleRadius - 2, hot ? HandleRadius : HandleRadius - 2);
        }

        for (var i = 0; i < _edit.Count; i++)
        {
            var s = ToScreen(_edit[i]);
            var hot = _hoverVertex == i || _dragVertex == i;
            context.DrawEllipse(
                hot ? new SolidColorBrush(accent) : Brushes.White,
                new Pen(Brushes.Black, 1.5),
                s, hot ? HandleRadius + 2 : HandleRadius, hot ? HandleRadius + 2 : HandleRadius);
        }
    }

    /// <summary>
    /// Marker für Bereiche ohne Fläche — der Normalfall bei importierten
    /// Standorten. Ein Kreis mit Fähnchenspitze auf der Position, damit die
    /// Spitze und nicht die Kreismitte den Ort bezeichnet.
    /// </summary>
    private void DrawMarker(DrawingContext context, MapShape shape)
    {
        if (shape.HasArea || shape.Point is not { } point) return;

        var p = ToScreen(point);
        var highlighted = HighlightedAreaId == shape.AreaId;

        // Groesser als frueher (7/9 px): Ein Punkt dieser Groesse geht auf einem
        // Luftbild zwischen Autos, Dachfenstern und Baumkronen unter.
        var r = highlighted ? 11.0 : 9.0;
        var tip = 9.0;

        var brush = new SolidColorBrush(shape.Outline);
        // Kraeftigerer schwarzer Rand als Kante gegen jeden Untergrund —
        // derselbe Grund wie beim doppelten Umriss der Flaechen.
        var pen = new Pen(Brushes.Black, highlighted ? 2.5 : 2.0);

        // Spitze als Dreieck unter dem Kreis.
        var flag = new StreamGeometry();
        using (var ctx = flag.Open())
        {
            ctx.BeginFigure(new Point(p.X, p.Y), isFilled: true);
            ctx.LineTo(new Point(p.X - r * 0.7, p.Y - tip));
            ctx.LineTo(new Point(p.X + r * 0.7, p.Y - tip));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(brush, pen, flag);

        var centre = new Point(p.X, p.Y - tip - r * 0.6);
        context.DrawEllipse(brush, pen, centre, r, r);

        // Heller Kern: Der Marker bleibt als Ampel lesbar, auch wenn die
        // Sättigung auf buntem Untergrund verwaschen wirkt.
        context.DrawEllipse(new SolidColorBrush(Colors.White, 0.85), null,
            centre, r * 0.28, r * 0.28);

        var text = new FormattedText(shape.Name, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
        var box = new Rect(p.X - text.Width / 2 - 4, p.Y + 3, text.Width + 8, text.Height + 4);
        context.FillRectangle(new SolidColorBrush(Colors.Black, 0.75), box, 3);
        context.DrawText(text, new Point(box.X + 4, box.Y + 2));
    }

    private void DrawArea(DrawingContext context, MapShape shape)
    {
        {
            if (!shape.HasArea) return;

            var geometry = BuildGeometry(shape.Points, close: true);
            var highlighted = HighlightedAreaId == shape.AreaId;

            // Deckkraft deutlich hoeher als frueher (0.25): Auf einem Luftbild
            // schlaegt der bunte Untergrund durch eine zarte Fuellung durch, und
            // die Flaeche war kaum als eingefaerbt zu erkennen.
            var fill = new SolidColorBrush(shape.Outline, highlighted ? 0.62 : 0.42);
            var width = highlighted ? 3.5 : 2.5;

            // Zweifacher Umriss („casing"): erst eine dunkle, breitere Linie,
            // darauf die farbige. Ein einzelner farbiger Strich verschwindet
            // gegen hellen Beton genauso wie gegen dunkles Laub — die dunkle
            // Unterlage gibt ihm auf jedem Untergrund eine Kante.
            context.DrawGeometry(fill,
                new Pen(new SolidColorBrush(Colors.Black, 0.65), width + 2.5), geometry);
            context.DrawGeometry(null,
                new Pen(new SolidColorBrush(shape.Outline), width), geometry);

            // Beschriftung in die Mitte des umschliessenden Rechtecks.
            if (MapGeometry.Bounds(shape.Points) is not { } b) return;
            var mid = ToScreen(new GeoPoint((b.Min.Lon + b.Max.Lon) / 2, (b.Min.Lat + b.Max.Lat) / 2));
            var text = new FormattedText(shape.Name, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.White);

            // Dunkler Kasten hinter der Schrift: auf einem Luftbild ist weisser
            // Text ueber hellen Flaechen sonst unlesbar.
            var box = new Rect(mid.X - text.Width / 2 - 4, mid.Y - text.Height / 2 - 2,
                text.Width + 8, text.Height + 4);
            context.FillRectangle(new SolidColorBrush(Colors.Black, 0.75), box, 3);
            context.DrawText(text, new Point(box.X + 4, box.Y + 2));
        }
    }

    private void DrawDraft(DrawingContext context)
    {
        if (!IsDrawing || _draft.Count == 0) return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), 2,
            new DashStyle([4, 3], 0));

        var points = _draft.ToList();
        if (_draftCursor is { } cursor) points.Add(cursor);

        context.DrawGeometry(
            new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7), 0.2),
            pen,
            BuildGeometry(points, close: points.Count > 2));

        // Griffe auf den gesetzten Punkten — zeigt, was zaehlt und was nur
        // Gummiband zum Zeiger ist.
        foreach (var p in _draft)
        {
            var s = ToScreen(p);
            context.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1), s, 4, 4);
        }
    }

    private StreamGeometry BuildGeometry(IReadOnlyList<GeoPoint> points, bool close)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ToScreen(points[0]), isFilled: true);
            for (var i = 1; i < points.Count; i++)
                ctx.LineTo(ToScreen(points[i]));
            ctx.EndFigure(close);
        }
        return geometry;
    }

    /// <summary>
    /// Quellenvermerk. Pflicht nach dl-de/by-2.0 — deshalb fest im Bild und
    /// nicht in einem Menü, das niemand öffnet.
    /// </summary>
    private void DrawAttribution(DrawingContext context)
    {
        var label = _tiles?.Attribution;
        if (string.IsNullOrWhiteSpace(label)) return;

        var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11,
            new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));

        var box = new Rect(Bounds.Width - text.Width - 10, Bounds.Height - text.Height - 6,
            text.Width + 8, text.Height + 4);
        context.FillRectangle(new SolidColorBrush(Colors.Black, 0.55), box, 3);
        context.DrawText(text, new Point(box.X + 4, box.Y + 2));
    }
}
