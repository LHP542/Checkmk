using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Checkmk.App.Controls;
using Checkmk.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Checkmk.App.Views;

public enum UpdateDialogResult
{
    Later,
    Skip,
    OpenReleasePage,
    Installed
}

public partial class UpdateDialog : ChromeWindow
{
    private readonly UpdateInfo? _info;
    private readonly UpdateInstaller? _installer;

    public UpdateDialog(UpdateInfo info, UpdateInstaller? installer = null)
    {
        AvaloniaXamlLoader.Load(this);
        _info = info;
        // Fallback: wenn der Aufrufer keinen Installer uebergibt (About-Box tat
        // das anfangs vergessen), holen wir ihn selbst aus dem DI-Container.
        // Damit ist "Jetzt installieren" ueberall verfuegbar wo der Dialog
        // aufpoppt — Badge-Klick und "Nach Updates suchen" gleichermassen.
        _installer = installer
            ?? (OperatingSystem.IsWindows()
                ? App.Services?.GetService<UpdateInstaller>()
                : null);

        var current = AppVersion.Display;
        this.FindControl<TextBlock>("VersionText")!.Text = $"Version {info.Version}";
        this.FindControl<TextBlock>("CurrentVersionText")!.Text = $"(installiert: {current})";
        RenderNotes(info.ReleaseNotes);

        // Ohne Windows-ZIP im Release oder ohne Installer: "Jetzt installieren"
        // ausblenden und "Release-Seite oeffnen" als Primary markieren.
        var install = this.FindControl<Button>("InstallButton")!;
        var release = this.FindControl<Button>("ReleaseButton")!;
        var canInstall = _installer is not null
                         && !string.IsNullOrEmpty(info.WindowsZipUrl)
                         && OperatingSystem.IsWindows();
        install.IsVisible = canInstall;
        if (!canInstall)
        {
            release.Background = Avalonia.Media.Brushes.SteelBlue;
            release.IsDefault = true;
        }
    }

    // Parameterloser ctor nur fuer den XAML-Designer.
    public UpdateDialog() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Baut die Release-Notes als Folge gestalteter Absätze auf.
    ///
    /// <para>Die Zerlegung macht <see cref="ReleaseNotesFormatter"/> — hier
    /// steht nur noch, wie ein Absatz aussieht. Das ist die Trennung, die den
    /// Umbau überhaupt testbar macht: Die Formatierung ist eine reine
    /// Funktion, das Zeichnen braucht ein Fenster.</para>
    /// </summary>
    private void RenderNotes(string? notes)
    {
        var panel = this.FindControl<StackPanel>("NotesPanel")!;
        panel.Children.Clear();

        var blocks = ReleaseNotesFormatter.Parse(notes);
        if (blocks.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "(keine Release-Notes hinterlegt)",
                Foreground = Brush.Parse("#888888"),
                FontStyle = FontStyle.Italic
            });
            return;
        }

        foreach (var block in blocks)
            panel.Children.Add(Build(block));
    }

    private static Control Build(NoteBlock block) => block.Kind switch
    {
        NoteBlockKind.Rule => new Border
        {
            Height = 1,
            Background = Brush.Parse("#3A3A3A"),
            Margin = new Thickness(0, 10, 0, 10)
        },

        NoteBlockKind.Heading => Text(block.Text, size: 16, bold: true,
            margin: new Thickness(0, 12, 0, 4)),

        NoteBlockKind.Subheading => Text(block.Text, size: 14, bold: true,
            margin: new Thickness(0, 12, 0, 4), color: "#9CC4E4"),

        // Codeblöcke und Tabellen behalten ihre Zeilen und ihre feste Breite —
        // umgeflossen waeren Spalten und Einrueckung hin. Sie duerfen als
        // einziges waagerecht scrollen.
        NoteBlockKind.Code => new Border
        {
            Background = Brush.Parse("#1B1B1C"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 7),
            Margin = new Thickness(0, 4, 0, 6),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBlock
                {
                    Text = block.Text,
                    FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                    FontSize = 12,
                    Foreground = Brush.Parse("#C8C8C8")
                }
            }
        },

        NoteBlockKind.Quote => new Border
        {
            BorderBrush = Brush.Parse("#4FA3E3"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 4, 0, 6),
            Child = Text(block.Text, size: 13, color: "#B8B8B8")
        },

        // Der Aufzählungspunkt steht in einer eigenen Spalte, damit die
        // Folgezeilen einer langen Zeile darunter buendig bleiben statt unter
        // den Punkt zu rutschen.
        NoteBlockKind.Bullet => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(4, 2, 0, 2),
            Children =
            {
                new TextBlock
                {
                    Text = "•",
                    Foreground = Brush.Parse("#4FA3E3"),
                    Margin = new Thickness(0, 0, 8, 0)
                },
                Build2(Text(block.Text, size: 13))
            }
        },

        _ => Text(block.Text, size: 13, margin: new Thickness(0, 3, 0, 3))
    };

    /// <summary>Setzt ein Control in die zweite Spalte des Aufzählungs-Grids.</summary>
    private static Control Build2(Control c)
    {
        Grid.SetColumn(c, 1);
        return c;
    }

    /// <summary>
    /// Ein Absatz mit Fettungen. <c>**…**</c> wird zu echten fetten Stellen —
    /// die Sternchen stehen zu lassen wäre das, was den Dialog vorher wie eine
    /// rohe Datei aussehen ließ.
    /// </summary>
    private static TextBlock Text(string text, double size, bool bold = false,
        Thickness margin = default, string color = "#DDDDDD")
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            Foreground = Brush.Parse(color),
            Margin = margin
        };

        foreach (var (part, isBold) in ReleaseNotesFormatter.Inline(text))
            tb.Inlines!.Add(new Run(part)
            {
                FontWeight = bold || isBold ? FontWeight.SemiBold : FontWeight.Normal
            });

        return tb;
    }

    private void OnLaterClick(object? sender, RoutedEventArgs e) => Close(UpdateDialogResult.Later);
    private void OnSkipClick(object? sender, RoutedEventArgs e) => Close(UpdateDialogResult.Skip);

    private void OnOpenReleaseClick(object? sender, RoutedEventArgs e)
    {
        if (_info is not null && !string.IsNullOrEmpty(_info.ReleasePageUrl))
            TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(_info.ReleasePageUrl));
        Close(UpdateDialogResult.OpenReleasePage);
    }

    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (_info is null || _installer is null) return;

        // Als Panel, nicht als StackPanel: Die Knopfzeile ist ein WrapPanel,
        // damit lange Beschriftungen umbrechen statt rechts abgeschnitten zu
        // werden. Ein FindControl<StackPanel> gaebe hier still null zurueck.
        var buttonRow = this.FindControl<Panel>("ButtonRow")!;
        var progressPanel = this.FindControl<StackPanel>("ProgressPanel")!;
        var bar = this.FindControl<ProgressBar>("DownloadBar")!;
        var text = this.FindControl<TextBlock>("ProgressText")!;

        foreach (var b in buttonRow.Children.OfType<Button>())
            b.IsEnabled = false;
        progressPanel.IsVisible = true;
        text.Text = "Lade Update herunter…";

        var progress = new Progress<double>(p =>
            Dispatcher.UIThread.Post(() =>
            {
                bar.Value = p;
                text.Text = $"Lade Update herunter… {p * 100:F0} %";
            }));

        bool ok;
        try
        {
            ok = await _installer.DownloadAndApplyAsync(_info, progress);
        }
        catch (Exception ex)
        {
            text.Text = $"Fehler: {ex.Message}";
            foreach (var b in buttonRow.Children.OfType<Button>())
                b.IsEnabled = true;
            return;
        }

        if (!ok)
        {
            // Ein abgelehntes Paket ist kein „hat nicht geklappt", sondern eine
            // Aussage — das muss der Anwender lesen koennen, statt es im Log zu
            // suchen.
            text.Text = _installer.LastVerification is { } why
                ? $"Update abgelehnt: {why}"
                : "Update konnte nicht angewendet werden — siehe Log. Fallback: Release-Seite öffnen.";
            foreach (var b in buttonRow.Children.OfType<Button>())
                b.IsEnabled = true;
            return;
        }

        // Der externe Austausch-Prozess wartet auf das Prozess-Ende dieser
        // App. Wir geben dem Fenster einen Moment zum Schliessen und beenden
        // dann hart — die .bat ersetzt und startet neu.
        text.Text = "Update wird angewendet, App wird beendet…";
        Close(UpdateDialogResult.Installed);
        await Task.Delay(300);
        Environment.Exit(0);
    }
}
