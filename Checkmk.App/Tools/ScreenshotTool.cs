#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Checkmk.App.Controls;
using Checkmk.App.Models;
using Checkmk.App.Services;
using Checkmk.App.ViewModels;
using Checkmk.App.Views;
using Checkmk.Core.Models;
using NLog;

namespace Checkmk.App.Tools;

/// <summary>
/// Werkzeugmodus <c>--screenshots &lt;ordner&gt;</c>: baut die Fenster mit
/// <b>erfundenen</b> Daten, rendert sie in PNG-Dateien und beendet sich.
///
/// <para><b>Warum nicht die App fernsteuern:</b> Von außen klicken
/// (<c>SetForegroundWindow</c>, <c>mouse_event</c>, <c>PrintWindow</c>) ist aus
/// Sicht eines verhaltensbasierten Virenscanners das Muster eines RATs und wird
/// auf dem Arbeitslaptop blockiert. Hier passiert alles im eigenen Prozess —
/// kein Port, kein Token, keine zusätzliche Angriffsfläche.</para>
///
/// <para><b>Warum erfundene Daten Pflicht sind:</b> Das Repository ist
/// öffentlich. Ein Screenshot gegen die echte Site zeigt Hostnamen, die interne
/// Domäne und — über den Host-Alias — die Anmeldenamen von Kollegen. Deshalb
/// hängt hier <i>kein</i> Client dran; die Ansichten bekommen ihre Daten
/// direkt eingesetzt.</para>
///
/// <para>Der ganze Modus steht hinter <c>#if DEBUG</c> und ist im
/// Release-Binary nicht enthalten.</para>
/// </summary>
internal static class ScreenshotTool
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    internal const string Switch = "--screenshots";

    private static string? _outputDir;

    /// <summary>true, sobald <see cref="TryParse"/> den Schalter gefunden hat —
    /// <c>App</c> baut dann kein Hauptfenster auf.</summary>
    internal static bool IsActive { get; private set; }

    /// <summary>Erkennt den Schalter und merkt sich den Zielordner.</summary>
    internal static bool TryParse(string[] args)
    {
        var i = Array.FindIndex(args, a =>
            string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;

        _outputDir = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[i + 1]
            : "docs";
        IsActive = true;
        return true;
    }

    /// <summary>Läuft, sobald Avalonia steht — rendert alles und beendet den Prozess.</summary>
    internal static async void RunAsync()
    {
        var dir = Path.GetFullPath(_outputDir ?? "docs");
        Directory.CreateDirectory(dir);
        Console.WriteLine($"Screenshots nach {dir}");

        // Je Bild einzeln abfangen: Ein Fenster, das sich verschluckt, darf
        // nicht die uebrigen vier verhindern — und der Grund gehoert sichtbar
        // ins Terminal, nicht ins Log. (Der UI-Ausnahme-Waechter aus App
        // verschluckt sonst genau diese Meldung.)
        var failed = 0;
        foreach (var (file, factory, w, h) in Shots())
        {
            try
            {
                await ShootAsync(dir, file, factory(), w, h);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  {file}  FEHLGESCHLAGEN: {ex.Message}");
                Log.Error(ex, "Screenshot {File} fehlgeschlagen.", file);
            }
        }

        Console.WriteLine(failed == 0 ? "Fertig." : $"Fertig, {failed} fehlgeschlagen.");
        Environment.Exit(failed == 0 ? 0 : 1);
    }

    private static IEnumerable<(string File, Func<Window> Factory, int W, int H)> Shots()
    {
        yield return ("status.png", DemoStatusWindow, 1280, 760);
        yield return ("hostdetail.png", DemoHostDetailWindow, 1000, 640);
        yield return ("filter-manager.png", DemoFilterManagerWindow, 780, 560);
        yield return ("filter-katalog.png", DemoCatalogDialog, 780, 600);
        yield return ("update.png", DemoUpdateDialog, 680, 620);
    }

    /// <summary>
    /// Zeigt ein Fenster weit außerhalb des Bildschirms, lässt den Dispatcher
    /// mehrfach laufen und rendert es.
    ///
    /// <para>Beide Kunstgriffe sind nötig: <b>Ohne <c>Show()</c> bleibt die
    /// Bitmap leer</b> (kein Layout, keine Größe), und <b>ohne die
    /// Dispatcher-Runden landet ein halb aufgebautes Fenster im Bild</b> —
    /// Bindings und Messungen brauchen mehr als einen Durchlauf.</para>
    /// </summary>
    private static async Task ShootAsync(string dir, string file, Window window,
        int width, int height)
    {
        window.Width = width;
        window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-4000, -4000);
        window.ShowInTaskbar = false;
        window.Show();

        for (var i = 0; i < 10; i++)
        {
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(120);
        }

        var size = new PixelSize(
            Math.Max(1, (int)window.Bounds.Width),
            Math.Max(1, (int)window.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);

        var path = Path.Combine(dir, file);
        using (var stream = File.Create(path))
            bitmap.Save(stream, new PngBitmapEncoderOptions());

        window.Close();
        Console.WriteLine($"  {file}  {size.Width}x{size.Height}");
    }

    // --- Fensterattrappen -------------------------------------------------

    /// <summary>
    /// Hüllfenster für eine Ansicht, die sonst in einem Reiter des Hauptfensters
    /// sitzt. Titelleiste und Palette sind dieselben wie im echten Fenster —
    /// das Bild zeigt also die tatsächliche Oberfläche, nur ohne den
    /// DI-Baum des Hauptfensters aufzuziehen.
    /// </summary>
    private sealed class DemoShell : ChromeWindow
    {
        public DemoShell(string title, Control content, string statusLine, string right)
        {
            Title = title;
            Content = new Border
            {
                BorderBrush = Avalonia.Media.Brush.Parse("#3A3A3A"),
                BorderThickness = new Thickness(1),
                Background = Avalonia.Media.Brush.Parse("#1A1D21"),
                Child = new Grid
                {
                    RowDefinitions = new RowDefinitions("40,*,26"),
                    Children =
                    {
                        new TitleBar
                        {
                            Title = title,
                            Extras = new StackPanel
                            {
                                Orientation = Avalonia.Layout.Orientation.Horizontal,
                                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                                Children =
                                {
                                    Badge("2.5 · v1", "#123E6B"),
                                    new Button { Content = "Einstellungen", Background = Avalonia.Media.Brushes.Transparent, Padding = new Thickness(12, 0) },
                                    new Button { Content = "Über", Background = Avalonia.Media.Brushes.Transparent, Padding = new Thickness(12, 0) }
                                }
                            }
                        },
                        Row(new Border { Margin = new Thickness(8), Child = content }, 1),
                        Row(StatusBar(statusLine, right), 2)
                    }
                }
            };
        }

        private static Border Badge(string text, string color) => new()
        {
            Background = Avalonia.Media.Brush.Parse(color),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11 }
        };

        private static Border StatusBar(string left, string right) => new()
        {
            Background = Avalonia.Media.Brush.Parse("#123E6B"),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(10, 0),
                Children =
                {
                    new Border
                    {
                        Width = 10, Height = 10,
                        CornerRadius = new CornerRadius(5),
                        Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Background = Avalonia.Media.Brush.Parse("#4CAF50")
                    },
                    Col(new TextBlock
                    {
                        Text = left, FontSize = 12,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    }, 1),
                    Col(new TextBlock
                    {
                        Text = right, FontSize = 12,
                        Foreground = Avalonia.Media.Brush.Parse("#B5D0EA"),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    }, 2)
                }
            }
        };

        private static Control Row(Control c, int row) { Grid.SetRow(c, row); return c; }
        private static Control Col(Control c, int col) { Grid.SetColumn(c, col); return c; }
    }

    private static Window DemoStatusWindow()
    {
        var vm = DemoData.StatusViewModel();
        return new DemoShell("Checkmk Cockpit — Musterstadt",
            new StatusView { DataContext = vm },
            vm.StatusMessage ?? "",
            "https://cmk.beispiel.intern/musterstadt");
    }

    private static Window DemoHostDetailWindow()
    {
        var vm = DemoData.HostDetailViewModel();
        return new HostDetailWindow(vm);
    }

    private static Window DemoFilterManagerWindow()
        => new FilterManagerWindow(DemoData.FilterCollection());

    private static Window DemoCatalogDialog()
        => new FilterCatalogDialog(DemoData.Catalog(), [201], "MeierS",
            DemoData.KnownHosts(), isAdmin: false);

    private static Window DemoUpdateDialog()
        => new UpdateDialog(DemoData.UpdateInfo());
}
#endif
