using Avalonia.Controls;
using NLog;

namespace Checkmk.App.Services;

/// <summary>
/// Bindeglied zwischen <see cref="IColumnLayoutStore"/> und dem Service-Grid:
/// baut die Spalten aus dem gespeicherten Layout, liest die aktuelle Anordnung
/// wieder aus dem Grid zurueck und kennt die Regeln fuers Zusammenfuehren mit
/// dem Katalog.
/// <para>
/// Bewusst UI-nah, aber ohne View-Wissen — dadurch testbar (siehe
/// <c>StatusGridColumnsTests</c>) und aus dem Code-Behind heraus duenn benutzbar.
/// </para>
/// </summary>
public static class StatusGridColumns
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Schluessel fuer die Service-Tabelle im Status-Tab.</summary>
    public const string StatusViewId = "status";

    /// <summary>
    /// Fuehrt ein gespeichertes Layout mit dem aktuellen Katalog zusammen.
    /// <list type="bullet">
    ///   <item>Unbekannte Schluessel fliegen raus (Spalte wurde umbenannt/entfernt).</item>
    ///   <item>Neu hinzugekommene Katalog-Spalten werden <b>ausgeblendet</b> angehaengt —
    ///         ein Update darf die gewohnte Ansicht nicht von selbst umbauen, die neue
    ///         Spalte steht aber sofort im Kontextmenue bereit.</item>
    ///   <item>Leeres/kaputtes Layout faellt auf <see cref="StatusColumnFactory.DefaultLayout"/> zurueck.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<ColumnSetting> Merge(ColumnLayout stored)
    {
        var result = new List<ColumnSetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in stored.Columns)
        {
            if (setting is null || string.IsNullOrWhiteSpace(setting.Key)) continue;
            if (!StatusColumnFactory.IsKnown(setting.Key)) continue;
            if (!seen.Add(setting.Key)) continue;
            result.Add(setting);
        }

        if (result.Count == 0)
        {
            // Nichts Brauchbares gespeichert -> Vorgabe, und zwar sichtbar.
            foreach (var key in StatusColumnFactory.DefaultLayout)
            {
                if (seen.Add(key))
                    result.Add(new ColumnSetting { Key = key, Visible = true });
            }
        }

        foreach (var choice in StatusColumnFactory.Catalog)
        {
            if (seen.Add(choice.Key))
                result.Add(new ColumnSetting { Key = choice.Key, Visible = false });
        }

        return result;
    }

    /// <summary>
    /// Baut die Spalten und haengt sie in der gespeicherten Reihenfolge ins Grid.
    /// Ausgeblendete Spalten kommen mit <c>IsVisible = false</c> trotzdem hinein —
    /// so bleibt ihre Position erhalten, wenn man sie spaeter wieder einschaltet.
    /// </summary>
    public static void Apply(DataGrid grid, IReadOnlyList<ColumnSetting> settings)
    {
        var columns = StatusColumnFactory.Build(settings.Select(s => s.Key));
        var byKey = settings.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        grid.Columns.Clear();
        foreach (var column in columns)
        {
            if (column.Tag is string key && byKey.TryGetValue(key, out var setting))
            {
                column.IsVisible = setting.Visible;
                if (setting.Width is > 0)
                {
                    // Nie unter die Untergrenze der Spalte. Der gespeicherte
                    // Wert stammt aus einer frueheren Version und kann zu
                    // schmal sein — bei „Ack"/„DT" standen so lange 50 px in
                    // columns.json, dass vom Kopf nur „A" bzw. „D" uebrigblieb.
                    // Eine neue Vorgabe in der Factory allein erreicht diese
                    // Anwender nicht, weil genau diese Zeile sie ueberschreibt.
                    column.Width = new DataGridLength(
                        Math.Max(setting.Width.Value, column.MinWidth));
                }
            }
            grid.Columns.Add(column);
        }

        Log.Debug("Spalten angewandt: {Visible} von {Total} sichtbar.",
            settings.Count(s => s.Visible), settings.Count);
    }

    /// <summary>
    /// Liest die aktuelle Anordnung aus dem Grid — in <see cref="DataGridColumn.DisplayIndex"/>-
    /// Reihenfolge, damit vom Anwender per Drag verschobene Spalten so wieder kommen.
    /// <para>
    /// Die Breite kommt aus <see cref="DataGridColumn.Width"/> und ausdruecklich
    /// <b>nicht</b> aus <c>ActualWidth</c>: Spalten, die gerade rechts aus dem
    /// sichtbaren Bereich ragen, sind nicht gemessen und liefern dort Unsinn (20 px
    /// fuer eine 110-px-Spalte) — gespeichert wuerde die Tabelle bei jedem Start
    /// weiter zusammenschrumpfen. <c>Width</c> ist der gesetzte Wert und wird von
    /// Avalonia beim Ziehen des Trenners aktualisiert.
    /// </para>
    /// Stern-Breiten werden als <c>null</c> gesichert, sonst friert die Ausgabe-Spalte
    /// nach dem ersten Speichern auf einer festen Pixelbreite ein.
    /// </summary>
    public static ColumnLayout Capture(DataGrid grid)
    {
        var layout = new ColumnLayout();
        foreach (var column in grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            if (column.Tag is not string key) continue;
            layout.Columns.Add(new ColumnSetting
            {
                Key = key,
                Visible = column.IsVisible,
                Width = column.Width.IsAbsolute && column.Width.Value > 0
                    ? column.Width.Value
                    : null
            });
        }
        return layout;
    }
}
