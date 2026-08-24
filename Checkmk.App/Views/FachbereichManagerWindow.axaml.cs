using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Checkmk.App.Controls;
using Checkmk.Data;
using NLog;

namespace Checkmk.App.Views;

/// <summary>
/// Fachbereiche anlegen, umbenennen, löschen — die Gruppen im Filter-Katalog.
///
/// <b>Ordnung, kein Zugriffsschutz.</b> Ein Fachbereich sagt, wo ein Filter im
/// Katalog einsortiert ist, nicht wer ihn sehen darf. Veröffentlichen darf
/// jeder, abonnieren auch. Mitgliederlisten gibt es bewusst nicht — genau die
/// waren die Schwäche des abgelösten Team-Modells.
/// </summary>
public partial class FachbereichManagerWindow : ChromeWindow
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IFachbereichStore? _store;
    private readonly HashSet<int> _deleteConfirmed = [];

    /// <summary>true, wenn etwas geändert wurde — der Aufrufer baut dann seine
    /// Auswahllisten neu.</summary>
    public bool Changed { get; private set; }

    public FachbereichManagerWindow(IFachbereichStore store)
    {
        AvaloniaXamlLoader.Load(this);
        _store = store;

        this.FindControl<ListBox>("FachbereichList")!.SelectionChanged += (_, _) => LoadSelected();
        Reload();
    }

    // Parameterloser ctor fuer XAML-Designer.
    public FachbereichManagerWindow() => AvaloniaXamlLoader.Load(this);

    private ListBox List => this.FindControl<ListBox>("FachbereichList")!;
    private FachbereichRow? Selected => List.SelectedItem as FachbereichRow;

    private void Reload(int? select = null)
    {
        if (_store is null) return;

        var rows = _store.Current.Fachbereiche;
        List.ItemsSource = rows;
        List.SelectedItem = select is { } id
            ? rows.FirstOrDefault(f => f.FachbereichId == id)
            : rows.FirstOrDefault();
        LoadSelected();
    }

    private void LoadSelected()
    {
        var f = Selected;
        this.FindControl<StackPanel>("Editor")!.IsEnabled = f is not null;
        this.FindControl<Button>("DeleteButton")!.IsEnabled = f is not null;

        this.FindControl<TextBox>("NameBox")!.Text = f?.Name ?? "";
        this.FindControl<TextBox>("DescriptionBox")!.Text = f?.Description ?? "";
    }

    private void Status(string message)
        => this.FindControl<TextBlock>("StatusText")!.Text = message;

    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        await Guarded(async () =>
        {
            var name = NextName();
            var id = await _store.CreateAsync(name, null);
            Changed = true;
            Reload(id);
            Status($"„{name}“ angelegt — Namen anpassen und übernehmen.");
        });
    }

    private string NextName()
    {
        var existing = _store?.Current.Fachbereiche.Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var i = 1;
        while (existing.Contains($"Fachbereich {i}")) i++;
        return $"Fachbereich {i}";
    }

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null || Selected is not { } fb) return;

        var name = this.FindControl<TextBox>("NameBox")!.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Status("Ein Fachbereich braucht einen Namen.");
            return;
        }

        await Guarded(async () =>
        {
            await _store.RenameAsync(fb.FachbereichId, name,
                this.FindControl<TextBox>("DescriptionBox")!.Text);
            Changed = true;
            Reload(fb.FachbereichId);
            Status($"„{name}“ gespeichert.");
        });
    }

    /// <summary>
    /// Löschen nimmt die Filter <b>nicht</b> mit — sie fallen an ihre Autoren
    /// zurück und stehen dort weiter als persönliche Filter. Jemandem seine
    /// Arbeit wegzunehmen, weil eine Katalog-Gruppe aufgelöst wird, wäre
    /// unhöflich. Die Abonnements sind danach gegenstandslos und verschwinden.
    /// </summary>
    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null || Selected is not { } fb) return;

        await Guarded(async () =>
        {
            var count = await _store.CountFiltersAsync(fb.FachbereichId);
            if (count > 0 && !_deleteConfirmed.Contains(fb.FachbereichId))
            {
                _deleteConfirmed.Add(fb.FachbereichId);
                Status($"In „{fb.Name}“ stehen {count} Filter. Sie werden nicht gelöscht, "
                     + "sondern gehen an ihre Autoren zurück; die Abos verfallen. "
                     + "Noch einmal „Löschen“ klicken.");
                return;
            }

            await _store.DeleteAsync(fb.FachbereichId);
            _deleteConfirmed.Remove(fb.FachbereichId);
            Changed = true;
            Reload();
            Status($"„{fb.Name}“ gelöscht.");
        });
    }

    /// <summary>
    /// Ein Schreibfehler darf den Dialog nicht beenden. Genau das ist schon
    /// einmal passiert: eine Ausnahme aus einem RelayCommand lief in den
    /// Avalonia-Dispatcher und riss den Prozess mit.
    /// </summary>
    private async Task Guarded(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Log.Warn(ex, "Fachbereichs-Verwaltung: Vorgang fehlgeschlagen.");
            Status($"Fehlgeschlagen: {ex.Message}");
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(Changed);
}
