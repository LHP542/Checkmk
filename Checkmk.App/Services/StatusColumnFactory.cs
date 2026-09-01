using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Checkmk.App.Converters;
using Checkmk.Core.Models;

namespace Checkmk.App.Services;

/// <summary>Ein waehlbarer Spaltentyp — Schluessel plus der Text, unter dem er im
/// Spalten-Kontextmenue erscheint.</summary>
public sealed record ColumnChoice(string Key, string Label);

/// <summary>
/// Katalog aller Spalten der Service-Tabelle. Die Schluessel sind bewusst die Namen
/// aus den Checkmk-Sichten (<c>host</c>, <c>service_description</c>,
/// <c>svc_state_age</c> …), damit man eine vorhandene Web-Sicht 1:1 abschreiben kann;
/// dazu kommen ein paar Cockpit-Eigene (<c>state_dot</c>, <c>host_alias</c>).
/// <para>
/// Zwei Nutzer: der Viewer-Modus baut den Satz aus <c>viewer.json</c>, der normale
/// Modus aus der vom Anwender im Header-Kontextmenue gewaehlten und in
/// <c>columns.json</c> gesicherten Auswahl. Beide Wege laufen durch
/// <see cref="Build"/> — es gibt keinen zweiten, im XAML deklarierten Spaltensatz mehr.
/// </para>
/// </summary>
public static class StatusColumnFactory
{
    private sealed record ColumnSpec(string Header, string MenuLabel, Func<DataGridColumn> Create);

    /// <summary>Property-Name, unter dem der Spaltenschluessel in
    /// <see cref="DataGridColumn.Tag"/> steckt — so findet das Speichern spaeter
    /// zurueck vom Control zum Schluessel.</summary>
    private static readonly Dictionary<string, ColumnSpec> Specs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["state_dot"] = new("", "Status-Punkt (Ampel)", () => DotColumn()),
        ["host"] = new("Host", "Host", () => Text("Host", nameof(ServiceStatus.HostName), 160)),
        ["host_alias"] = new("Alias", "Host-Alias",
            () => Text("Alias", nameof(ServiceStatus.HostAlias), 180)),
        ["service_display_name"] = new("Anzeigename", "Anzeigename",
            () => Text("Anzeigename", nameof(ServiceStatus.DisplayNameOrDescription), 200)),
        ["service_description"] = new("Service", "Service-Beschreibung",
            () => Text("Service", nameof(ServiceStatus.Description), 200)),
        ["service_state"] = new("Status", "Status (OK/WARN/CRIT)",
            () => Text("Status", nameof(ServiceStatus.ServiceState), 90)),
        ["service_plugin_output"] = new("Ausgabe", "Ausgabe der Prüfung",
            () => Star("Ausgabe", nameof(ServiceStatus.PluginOutput))),
        ["svc_acknowledged"] = new("Ack", "Acknowledged",
            () => Check("Ack", nameof(ServiceStatus.IsAcknowledged))),
        ["svc_in_downtime"] = new("DT", "In Wartung",
            () => Check("DT", nameof(ServiceStatus.InDowntime))),
        // Bewusst OHNE Alters-Einfaerbung: bei svc_check_age ist "frisch" gut und
        // "alt" schlecht — genau umgekehrt zu svc_state_age, wofuer AgeToBrush
        // gebaut ist. Rot fuer einen Check, der gerade eben lief, waere irrefuehrend.
        ["svc_check_age"] = new("Letzter Check", "Zeit seit letztem Check",
            () => Text("Letzter Check", nameof(ServiceStatus.CheckAge), 110,
                nameof(ServiceStatus.LastCheckUnix))),
        ["svc_state_age"] = new("Alter Status", "Zeit seit Statuswechsel",
            () => AgeColumn("Alter Status", nameof(ServiceStatus.Age),
                nameof(ServiceStatus.LastStateChange), nameof(ServiceStatus.LastStateChangeUnix)))
    };

    /// <summary>
    /// Spaltensatz, mit dem der normale Modus startet, solange der Anwender nichts
    /// eigenes gewaehlt hat. Entspricht dem, was die Tabelle vor der
    /// Spaltenkonfiguration fest im XAML hatte — ein Update darf niemandem die
    /// gewohnte Ansicht umbauen.
    /// </summary>
    public static readonly string[] DefaultLayout =
    [
        "state_dot", "host", "host_alias", "service_description", "service_state",
        "service_plugin_output", "svc_acknowledged", "svc_in_downtime", "svc_state_age"
    ];

    /// <summary>Alle waehlbaren Spalten in Menue-Reihenfolge (= Definitionsreihenfolge oben).</summary>
    public static IReadOnlyList<ColumnChoice> Catalog { get; } =
        [.. Specs.Select(kv => new ColumnChoice(kv.Key, kv.Value.MenuLabel))];

    /// <summary>Alle unterstuetzten Schluessel — fuer Logmeldungen und Doku.</summary>
    public static IReadOnlyCollection<string> KnownKeys { get; } = [.. Specs.Keys];

    public static bool IsKnown(string key) => Specs.ContainsKey(key);

    /// <summary>Menue-Beschriftung zu einem Schluessel; faellt auf den Schluessel zurueck.</summary>
    public static string LabelFor(string key)
        => Specs.TryGetValue(key, out var spec) ? spec.MenuLabel : key;

    /// <summary>
    /// Erzeugt die Spalten in der Reihenfolge der uebergebenen Schluessel. Jede Spalte
    /// traegt ihren Schluessel in <see cref="DataGridColumn.Tag"/>, damit das Speichern
    /// der Anordnung vom Control auf den Schluessel zurueckschliessen kann.
    /// Unbekannte Schluessel werden still uebersprungen.
    /// </summary>
    public static IReadOnlyList<DataGridColumn> Build(IEnumerable<string> keys)
    {
        var columns = new List<DataGridColumn>();
        foreach (var key in keys)
        {
            if (!Specs.TryGetValue(key, out var spec))
                continue;
            var column = spec.Create();
            column.Tag = key;
            columns.Add(column);
        }
        return columns;
    }

    // --- Spaltentypen ----------------------------------------------------

    /// <summary><paramref name="sortPath"/> setzen, wenn der angezeigte Text nicht
    /// in seiner eigenen Reihenfolge sortiert werden darf (z. B. "3 h" vs. "5 m").</summary>
    private static DataGridTextColumn Text(string header, string path, double width,
        string? sortPath = null) => new()
    {
        Header = header,
        Binding = new Binding(path),
        Width = new DataGridLength(width),
        SortMemberPath = sortPath ?? path
    };

    private static DataGridTextColumn Star(string header, string path) => new()
    {
        Header = header,
        Binding = new Binding(path),
        Width = new DataGridLength(1, DataGridLengthUnitType.Star)
    };

    /// <summary>
    /// Ja/Nein-Spalte (Ack, Downtime).
    ///
    /// <para>66 px und nicht 50: Neben dem Text sitzen noch das Sortierdreieck
    /// und die Kopfzeilen-Polsterung. Bei 50 px blieb von „Ack" ein „A" und von
    /// „DT" ein „D" übrig — aufgefallen erst auf dem ersten Doku-Bild, weil man
    /// im Alltag weiß, was in der Spalte steht.</para>
    /// </summary>
    private static DataGridCheckBoxColumn Check(string header, string path) => new()
    {
        Header = header,
        Binding = new Binding(path),
        Width = new DataGridLength(66)
    };

    /// <summary>Ampelpunkt wie im XAML-Standardgrid.</summary>
    private static DataGridTemplateColumn DotColumn() => new()
    {
        Header = "",
        Width = new DataGridLength(34),
        CanUserSort = true,
        SortMemberPath = nameof(ServiceStatus.State),
        CellTemplate = new FuncDataTemplate<ServiceStatus>((_, _) => new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [!Border.BackgroundProperty] = new Binding(nameof(ServiceStatus.ServiceState))
            {
                Converter = StateToBrushConverter.Instance
            }
        })
    };

    /// <summary>
    /// Alters-Spalte: kompakter Text ("3 h 12 m") eingefaerbt nach Frische.
    /// <paramref name="sortPath"/> zeigt auf den Unix-Zeitstempel — sonst wuerde
    /// die Tabelle den formatierten String alphabetisch sortieren ("3 h" &lt; "5 m").
    /// </summary>
    private static DataGridTemplateColumn AgeColumn(
        string header, string textPath, string brushPath, string sortPath) => new()
    {
        Header = header,
        Width = new DataGridLength(110),
        CanUserSort = true,
        SortMemberPath = sortPath,
        CellTemplate = new FuncDataTemplate<ServiceStatus>((_, _) => new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0),
            [!TextBlock.TextProperty] = new Binding(textPath),
            [!TextBlock.ForegroundProperty] = new Binding(brushPath)
            {
                Converter = AgeToBrushConverter.Instance
            }
        })
    };
}
