using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Checkmk.App.Services;
using Checkmk.App.ViewModels;
using Checkmk.App.Views;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Checkmk.App;

public partial class App : Application
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Wird in Program.Main gesetzt, bevor Avalonia startet.</summary>
    public static IServiceProvider Services { get; set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        InstallUiExceptionGuard();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = Services.GetRequiredService<MainWindow>();
            var vm = Services.GetRequiredService<MainWindowViewModel>();

            vm.OpenSettingsRequested += async (_, _) =>
            {
                var settings = Services.GetRequiredService<SettingsWindow>();
                await settings.ShowDialog(window);
                await vm.ReconnectAsync();
            };

            vm.OpenAboutRequested += async (_, _) =>
            {
                var about = Services.GetRequiredService<AboutWindow>();
                await about.ShowDialog(window);
            };

            vm.OpenUpdateRequested += async (_, info) =>
            {
                var installer = OperatingSystem.IsWindows()
                    ? Services.GetService<UpdateInstaller>()
                    : null;
                var dialog = new UpdateDialog(info, installer);
                var result = await dialog.ShowDialog<UpdateDialogResult>(window);
                if (result == UpdateDialogResult.Skip)
                    vm.SkipCurrentUpdate();
            };

            // Dashboard-Kachel-Klick: Filter aktivieren + Tab-Wechsel zu Status.
            vm.Dashboard.TileClicked += (_, filter) =>
            {
                vm.Status.Filters.Active = filter;
                window.SelectMainTab(0);
            };

            window.DataContext = vm;
            window.Opened += async (_, _) => await vm.InitializeAsync();
            desktop.MainWindow = window;

            // Tray-Icon, Minimieren ins Tray, Status-Notifications.
            var status = Services.GetRequiredService<StatusViewModel>();
            var toast = Services.GetRequiredService<IToastNotifier>();
            var viewer = Services.GetRequiredService<ViewerMode>();
            _trayController = new TrayController(this, window, status, toast, viewer);

            // Mit --tray (Autostart) kommt die App direkt ins Tray, ohne Fenster.
            // Ein Werkzeug, das sich beim Anmelden ungefragt vor alles legt, macht
            // sich keine Freunde — im Tray sieht man die Ampel trotzdem.
            if (desktop.Args?.Any(a =>
                    string.Equals(a, AutoStart.TraySwitch, StringComparison.OrdinalIgnoreCase))
                == true)
            {
                window.WindowState = WindowState.Minimized;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Fängt Ausnahmen aus dem UI-Thread ab, statt die Anwendung daran sterben
    /// zu lassen.
    ///
    /// <para><b>Warum das nötig ist:</b> Ein Ereignis-Handler in der
    /// Oberfläche ist fast immer <c>async void</c> — anders lässt sich ein
    /// Click-Handler nicht schreiben. Eine Ausnahme darin kann niemand
    /// zurückgeben; sie läuft durch die Dispatcher-Schleife bis in den
    /// <c>catch</c> in <c>Program.Main</c>, der sie als <c>FATAL</c> loggt —
    /// und danach ist der Prozess weg. Real passiert am 2026-08-27: Ein
    /// Rechtsklick auf einer Karten-Fläche öffnete ein ContextMenu auf dem
    /// falschen Control, und das Cockpit beendete sich. Es gibt rund 45 solcher
    /// Handler; sie einzeln in <c>try</c> zu hüllen ist eine Disziplinfrage,
    /// die man irgendwann verliert.</para>
    ///
    /// <para><b>Der Preis, bewusst bezahlt:</b> Ein Fehler fällt jetzt weniger
    /// auf — er steht im Log, statt die Anwendung zu beenden. Für ein Werkzeug,
    /// das den ganzen Tag im Benachrichtigungsfeld liegt und dessen Aufgabe die
    /// Überwachung <i>anderer</i> Systeme ist, ist ein überlebter Fehlklick
    /// allemal besser als ein stiller Totalausfall der Überwachung. Deshalb
    /// wird auf <c>Error</c> geloggt und nicht auf <c>Warn</c>: Diese Zeilen
    /// sollen auffallen.</para>
    ///
    /// <para>Ausnahmen <b>außerhalb</b> des UI-Threads bleiben davon unberührt
    /// — die fangen weiterhin die Handler in <c>Program.Main</c> ab.</para>
    /// </summary>
    private static void InstallUiExceptionGuard()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unbehandelte Ausnahme im UI-Thread — abgefangen, "
                                 + "die Anwendung laeuft weiter.");
            e.Handled = true;
        };
    }

    // Referenz halten, damit TrayController/TrayIcon nicht vom GC eingesammelt werden.
    private TrayController? _trayController;
}
