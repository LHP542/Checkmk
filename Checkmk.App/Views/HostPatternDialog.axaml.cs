using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Checkmk.App.Controls;
using Checkmk.App.Services;

namespace Checkmk.App.Views;

/// <summary>Ergebnis des Dialogs. <c>null</c> = abgebrochen.</summary>
/// <param name="Tag">Checkmk-Ortstag, oder <c>null</c> für „keiner".</param>
/// <param name="Pattern">Hostname-Regex, oder <c>null</c> für „keins".</param>
/// <param name="AssignNow">
/// Der Anwender hat „Speichern und zuordnen" gewählt: Nach dem Sichern der
/// Regel soll gleich der Vorschlagsdialog für diesen Bereich aufgehen.
///
/// <para><b>Warum es diesen Knopf gibt:</b> „Speichern" merkt nur die Regel —
/// zugeordnet wird erst über „Zuordnung vorschlagen…" in der Leiste. Das steht
/// dem Dialog nicht an: Er heißt „Host-Zuordnung", fragt „Welche Hosts gehören
/// zu …?" und zeigt die Treffer. Real passiert: Ein Bereich bekam seinen
/// Ortstag, der Dialog meldete 7 Treffer, und der Bereich blieb leer — was
/// nach einem Fehler aussieht und keiner war.</para>
///
/// <para>Die Bestätigung bleibt trotzdem: Der Knopf überspringt nur das
/// <i>Suchen</i> des zweiten Schritts, nicht den Schritt selbst. „Nur
/// Vorschläge, nie automatisch" gilt weiter.</para>
/// </param>
public sealed record HostAssignmentRule(string? Tag, string? Pattern, bool AssignNow = false);

/// <summary>
/// Wie ein Bereich zu seinen Hosts kommt — <b>Checkmk-Ortstag</b> oder
/// Hostname-Muster, mit <b>Live-Vorschau der Treffer</b>.
///
/// Die Vorschau ist der Punkt: Ein regulärer Ausdruck ist für die meisten
/// unlesbar, aber „diese 7 Hosts würden zugeordnet" versteht jeder sofort.
/// Ohne sie müsste man speichern, Vorschläge erzeugen und wieder zurückgehen,
/// um zu sehen, ob es stimmt.
///
/// Beide Wege stehen nebeneinander, weil beide gebraucht werden: Auf
/// <c>schul_it</c> trägt fast jeder Host ein <c>tag_location_school</c>, auf
/// <c>LHP</c> so gut wie keiner. Der Tag gewinnt, wo er da ist — deshalb sagt
/// die Vorschau bei gesetztem Tag ausdrücklich, dass das Muster nur noch für
/// Hosts <i>ohne</i> Tag greift, statt eine Trefferzahl zu zeigen, die im
/// Betrieb gar nicht zustande käme.
/// </summary>
public partial class HostPatternDialog : ChromeWindow
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xCA, 0x28));
    private static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));

    private readonly IReadOnlyList<string> _hosts = [];
    private readonly Func<string, string?> _tagFor = _ => null;

    public HostPatternDialog(
        string areaName,
        string? tag,
        string? pattern,
        IReadOnlyList<string> knownHosts,
        IReadOnlyList<HostTagValue> tagValues,
        Func<string, string?>? tagFor = null)
    {
        AvaloniaXamlLoader.Load(this);

        _hosts = knownHosts;
        if (tagFor is not null) _tagFor = tagFor;

        Title = $"Host-Zuordnung: {areaName}";
        this.FindControl<TitleBar>("DialogTitleBar")!.Title = Title;
        this.FindControl<TextBlock>("PromptText")!.Text =
            $"Welche Hosts gehören zu „{areaName}“?";

        var tagBox = this.FindControl<ComboBox>("TagBox")!;
        // Ein bereits gesetzter Tag, der in der aktuellen Sicht nicht vorkommt,
        // muss trotzdem in der Liste stehen — sonst faellt er beim Speichern
        // still weg, nur weil gerade eine andere Site aktiv ist.
        var items = tagValues.ToList();
        if (!string.IsNullOrWhiteSpace(tag)
            && !items.Any(v => v.Value.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            items.Insert(0, new HostTagValue(tag, 0));

        tagBox.ItemsSource = items;
        tagBox.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(HostTagValue.Display));
        tagBox.SelectedItem = items.FirstOrDefault(
            v => v.Value.Equals(tag, StringComparison.OrdinalIgnoreCase));
        tagBox.SelectionChanged += (_, _) => UpdatePreview();

        var box = this.FindControl<TextBox>("PatternBox")!;
        box.Text = pattern ?? "";
        box.TextChanged += (_, _) => UpdatePreview();

        UpdatePreview();
    }

    // Parameterloser ctor fuer XAML-Designer.
    public HostPatternDialog() => AvaloniaXamlLoader.Load(this);

    private string? SelectedTag
        => (this.FindControl<ComboBox>("TagBox")!.SelectedItem as HostTagValue)?.Value;

    private void UpdatePreview()
    {
        var tag = SelectedTag;
        var pattern = this.FindControl<TextBox>("PatternBox")!.Text ?? "";

        var byTag = string.IsNullOrWhiteSpace(tag)
            ? []
            : _hosts.Where(h => string.Equals(_tagFor(h), tag, StringComparison.OrdinalIgnoreCase))
                    .ToList();

        UpdateTagState(tag, byTag.Count);

        // Nur Hosts ohne passenden Tag koennen ueberhaupt ueber das Muster
        // kommen — genau so entscheidet auch der Suggester.
        var patternCandidates = _hosts.Where(h => _tagFor(h) is null).ToList();
        var byPattern = UpdatePatternState(pattern, patternCandidates);

        var all = byTag.Concat(byPattern)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();

        this.FindControl<TextBlock>("MatchHeader")!.Text = all.Count == 0
            ? "Treffer:"
            : $"Treffer ({all.Count}):";
        this.FindControl<ListBox>("MatchList")!.ItemsSource = all;
    }

    private void UpdateTagState(string? tag, int hits)
    {
        var state = this.FindControl<TextBlock>("TagStateText")!;

        if (string.IsNullOrWhiteSpace(tag))
        {
            state.Text = "Kein Tag gewählt — Zuordnung nur über das Muster unten.";
            state.Foreground = Muted;
            return;
        }

        // Null Treffer sind kein Fehler: Der Tag kann zu einer anderen Site
        // gehoeren als der gerade angezeigten. Deshalb gelb, nicht rot.
        state.Text = hits == 0
            ? $"„{tag}“ trifft in der aktuellen Sicht keinen Host — evtl. eine andere Site."
            : $"„{tag}“ trifft {hits} von {_hosts.Count} Hosts.";
        state.Foreground = hits == 0 ? Warn : Good;
    }

    private List<string> UpdatePatternState(string pattern, IReadOnlyList<string> candidates)
    {
        var state = this.FindControl<TextBlock>("StateText")!;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            state.Text = "Kein Muster.";
            state.Foreground = Muted;
            return [];
        }

        if (!HostPatternMatcher.IsValid(pattern))
        {
            // Klare Rueckmeldung statt stiller Wirkungslosigkeit: Ein kaputtes
            // Muster trifft sonst einfach nichts und man sucht den Fehler beim
            // Hostnamen.
            state.Text = "Ungültiger Ausdruck — trifft nichts.";
            state.Foreground = Bad;
            return [];
        }

        var matches = candidates
            .Where(h => HostPatternMatcher.Matches(pattern, h))
            .ToList();

        var skipped = _hosts.Count - candidates.Count;
        var suffix = skipped > 0 ? $" ({skipped} Hosts sind über ihren Tag zugeordnet)" : "";

        state.Text = matches.Count == 0
            ? $"Kein Treffer unter {candidates.Count} Hosts ohne Tag.{suffix}"
            : $"{matches.Count} zusätzliche Hosts über das Muster.{suffix}";
        state.Foreground = matches.Count == 0 ? Warn : Good;
        return matches;
    }

    private void OnTagClearClick(object? sender, RoutedEventArgs e)
    {
        this.FindControl<ComboBox>("TagBox")!.SelectedItem = null;
        UpdatePreview();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        this.FindControl<TextBox>("PatternBox")!.Text = "";
        UpdatePreview();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => CloseWithRule(assignNow: false);

    private void OnOkAndAssignClick(object? sender, RoutedEventArgs e)
        => CloseWithRule(assignNow: true);

    private void CloseWithRule(bool assignNow)
    {
        var pattern = this.FindControl<TextBox>("PatternBox")!.Text?.Trim();
        Close(new HostAssignmentRule(
            SelectedTag,
            string.IsNullOrWhiteSpace(pattern) ? null : pattern,
            assignNow));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
