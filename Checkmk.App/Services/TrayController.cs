using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Checkmk.App.ViewModels;
using Checkmk.Core.Models;
using NLog;

namespace Checkmk.App.Services;

/// <summary>
/// Verwaltet das Tray-Icon: Minimieren ins Tray, Ampelfarbe + Tooltip fuer den
/// aktiven Filter, und Toast-Benachrichtigung bei Statusaenderungen (nur wenn
/// ins Tray minimiert). Aktiviert beim Minimieren automatisch die Auto-Aktualisierung.
/// </summary>
public sealed class TrayController
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Application _app;
    private readonly Window _window;
    private readonly StatusViewModel _status;
    private readonly IToastNotifier _toast;
    private readonly StatusChangeMonitor _monitor = new();

    private readonly WindowIcon _iconOk;
    private readonly WindowIcon _iconWarn;
    private readonly WindowIcon _iconCrit;
    private readonly WindowIcon _iconUnknown;

    private TrayIcon _trayIcon = null!;
    private NativeMenuItem _snoozeStatusItem = null!;
    private string? _lastFilterName;
    private bool _restoreInProgress;

    public bool IsMinimizedToTray { get; private set; }

    /// <summary>Wenn gesetzt: bis dahin keine Notifications ausgeben. Aendert nichts
    /// am Tray-Icon (der Ampelstatus bleibt sichtbar).</summary>
    public DateTimeOffset? SnoozedUntil { get; private set; }

    /// <summary>
    /// Nur im Viewer-Modus und nur wenn das Profil es nicht abschaltet: bei einer
    /// Verschlechterung das Fenster maximiert nach vorn holen. Fuer Ausgaben, die
    /// dauerhaft auf einem Bildschirm laufen und wo ein Toast allein zu leise ist.
    /// </summary>
    private readonly bool _popUpOnProblem;

    /// <summary>Warnton beim Aufspringen; leerer Dateiname = Systemklang.</summary>
    private readonly bool _popUpSound;
    private readonly string _popUpSoundFile;

    public TrayController(Application app, Window window, StatusViewModel status,
        IToastNotifier toast, ViewerMode viewer)
    {
        _app = app;
        _window = window;
        _status = status;
        _toast = toast;
        _popUpOnProblem = viewer.Profile?.PopUpOnProblem ?? false;
        _popUpSound = viewer.Profile?.PopUpSound ?? false;
        _popUpSoundFile = viewer.Profile?.PopUpSoundFile ?? "";

        // Tray-Icons zur Laufzeit rendern: App-Icon + farbiger Status-Dot unten
        // rechts. Damit bleibt im Tray erkennbar dass das der Checkmk Cockpit
        // ist, und der Status ist trotzdem auf einen Blick sichtbar.
        _iconOk = TrayIconFactory.Create(TrayIconFactory.OkGreen);
        _iconWarn = TrayIconFactory.Create(TrayIconFactory.WarnYellow);
        _iconCrit = TrayIconFactory.Create(TrayIconFactory.CritRed);
        _iconUnknown = TrayIconFactory.Create(TrayIconFactory.UnknownGrey);

        BuildTray();

        _status.Refreshed += OnStatusRefreshed;
        _window.PropertyChanged += OnWindowPropertyChanged;
    }

    private void BuildTray()
    {
        _trayIcon = new TrayIcon
        {
            Icon = _iconOk,
            ToolTipText = "Checkmk Cockpit",
            IsVisible = true,
            Menu = new NativeMenu()
        };

        var show = new NativeMenuItem("Anzeigen");
        show.Click += (_, _) => Restore();
        var test = new NativeMenuItem("Test-Benachrichtigung");
        test.Click += (_, _) =>
        {
            Log.Info("Test-Benachrichtigung ausgeloest ueber Tray-Menue.");
            _toast.Notify("Checkmk Cockpit — Test",
                "Wenn du diese Nachricht siehst, funktionieren Toasts. Zeitpunkt: "
                + DateTime.Now.ToString("HH:mm:ss"));
        };
        var snooze30 = new NativeMenuItem("Snooze 30 Min");
        snooze30.Click += (_, _) => Snooze(TimeSpan.FromMinutes(30));
        var snooze2h = new NativeMenuItem("Snooze 2 Std");
        snooze2h.Click += (_, _) => Snooze(TimeSpan.FromHours(2));
        var snoozeMorning = new NativeMenuItem("Snooze bis morgen 06:00");
        snoozeMorning.Click += (_, _) => Snooze(NextMorningSix() - DateTimeOffset.Now);
        _snoozeStatusItem = new NativeMenuItem("Snooze aufheben") { IsVisible = false };
        _snoozeStatusItem.Click += (_, _) => CancelSnooze();

        var exit = new NativeMenuItem("Beenden");
        exit.Click += (_, _) => (_app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        _trayIcon.Menu.Items.Add(show);
        _trayIcon.Menu.Items.Add(test);
        _trayIcon.Menu.Items.Add(new NativeMenuItemSeparator());
        _trayIcon.Menu.Items.Add(snooze30);
        _trayIcon.Menu.Items.Add(snooze2h);
        _trayIcon.Menu.Items.Add(snoozeMorning);
        _trayIcon.Menu.Items.Add(_snoozeStatusItem);
        _trayIcon.Menu.Items.Add(new NativeMenuItemSeparator());
        _trayIcon.Menu.Items.Add(exit);
        _trayIcon.Clicked += (_, _) => Restore();

        TrayIcon.SetIcons(_app, new TrayIcons { _trayIcon });
    }

    private void Snooze(TimeSpan duration)
    {
        SnoozedUntil = DateTimeOffset.Now.Add(duration);
        _snoozeStatusItem.Header = $"Snooze aufheben (aktiv bis {SnoozedUntil:HH:mm})";
        _snoozeStatusItem.IsVisible = true;
        Log.Info("Notifications ge-snoozed bis {Until}.", SnoozedUntil);
    }

    private void CancelSnooze()
    {
        SnoozedUntil = null;
        _snoozeStatusItem.IsVisible = false;
        Log.Info("Snooze manuell aufgehoben.");
    }

    private static DateTimeOffset NextMorningSix()
    {
        var now = DateTimeOffset.Now;
        var six = new DateTimeOffset(now.Year, now.Month, now.Day, 6, 0, 0, now.Offset);
        return now.Hour < 6 ? six : six.AddDays(1);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty || _restoreInProgress)
            return;

        if ((WindowState)e.NewValue! == WindowState.Minimized)
            MinimizeToTray();
    }

    private void MinimizeToTray()
    {
        IsMinimizedToTray = true;
        _window.Hide();
        // Auto-Refresh muss laufen, sonst gibt es keine Aenderungen zu melden.
        _status.AutoRefresh = true;
        Log.Debug("Ins Tray minimiert, Auto-Refresh aktiviert.");
    }

    private void Restore()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _restoreInProgress = true;
            _window.Show();
            _window.WindowState = WindowState.Normal;
            _window.Activate();
            IsMinimizedToTray = false;
            _restoreInProgress = false;
        });
    }

    /// <summary>
    /// Holt das Fenster maximiert nach vorn und springt auf den betroffenen Service.
    /// Greift auch, wenn das Fenster gar nicht im Tray liegt, sondern nur hinter
    /// anderen Fenstern — „nicht uebersehen" ist der ganze Zweck.
    /// </summary>
    private void PopUpForProblem(ServiceStatus? problem)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Vor dem Hochholen, nicht danach: PlaySound kehrt sofort zurueck
            // (asynchron), und so faellt der Ton mit dem Aufspringen zusammen
            // statt erst nach dem Maximieren und dem Scrollen zu kommen.
            if (_popUpSound && OperatingSystem.IsWindows())
                AlertSound.PlayProblem(string.IsNullOrWhiteSpace(_popUpSoundFile)
                    ? null
                    : _popUpSoundFile);

            _restoreInProgress = true;
            try
            {
                _window.Show();
                _window.WindowState = WindowState.Maximized;

                // Activate() allein holt unter Windows nicht zuverlaessig den
                // Vordergrund, wenn eine andere Anwendung den Fokus hat. Der
                // Topmost-Toggle erzwingt es, ohne das Fenster dauerhaft ueber
                // alles andere zu nageln.
                _window.Topmost = true;
                _window.Activate();
                _window.Topmost = false;

                IsMinimizedToTray = false;
            }
            finally { _restoreInProgress = false; }

            // Nach dem Hochholen die betroffene Zeile markieren und dorthin scrollen.
            // Getrennt gepostet, damit das Grid das Layout nach dem Maximieren fertig hat.
            if (problem is not null)
                Dispatcher.UIThread.Post(() => _status.RequestSpotlight(problem),
                    DispatcherPriority.Background);
        });
    }

    private void OnStatusRefreshed(IReadOnlyList<ServiceStatus> services, string? filterName)
    {
        // Ampelfarbe + Tooltip
        var crit = services.Count(s => s.ServiceState == ServiceState.Critical);
        var warn = services.Count(s => s.ServiceState == ServiceState.Warning);
        var unknown = services.Count(s => s.ServiceState == ServiceState.Unknown);
        var ok = services.Count(s => s.ServiceState == ServiceState.Ok);

        _trayIcon.Icon = crit > 0 ? _iconCrit
            : warn > 0 ? _iconWarn
            : unknown > 0 ? _iconUnknown
            : _iconOk;

        var scope = string.IsNullOrWhiteSpace(filterName) ? "Alle Hosts" : filterName;
        _trayIcon.ToolTipText = $"Checkmk Cockpit — {scope}\nCRIT {crit} · WARN {warn} · OK {ok}";

        // Bei Filterwechsel Monitor zuruecksetzen (kein Fehlalarm durch anderen Datensatz).
        if (filterName != _lastFilterName)
        {
            _monitor.Reset();
            _lastFilterName = filterName;
        }

        // Snooze abgelaufen -> stillschweigend aufraeumen.
        if (SnoozedUntil is { } until && until <= DateTimeOffset.Now)
            CancelSnooze();

        var change = _monitor.Diff(services);

        // Immer mitschreiben: „warum kam kein Toast/kein Popup?" ist die haeufigste
        // Rueckfrage, und ohne diese Zeile sieht man im Log nur Stille.
        Log.Debug("Refresh-Diff: {Services} Services (CRIT {Crit}/WARN {Warn}/UNK {Unk}/OK {Ok}), "
                + "Aenderungen={Changes} ({Text}), ImTray={Tray}, Snooze={Snooze}, PopUp={PopUp}.",
            services.Count, crit, warn, unknown, ok,
            change.Total, change.HasChanges ? change.ToText() : "keine",
            IsMinimizedToTray, SnoozedUntil?.ToString("HH:mm") ?? "aus", _popUpOnProblem);

        if (change.HasChanges)
        {
            if (SnoozedUntil is not null)
            {
                Log.Debug("Statusaenderung erkannt — aber Snooze aktiv bis {Until}, kein Toast.", SnoozedUntil);
            }
            else if (IsMinimizedToTray)
            {
                Log.Info("Statusaenderung erkannt (CRIT {C}, WARN {W}, OK {O}, UNK {U}) — sende Toast.",
                    change.NewProblems, change.OtherChanges, change.Recoveries, 0);
                var title = $"Checkmk: {scope}";
                var body = change.ToText();
                if (change.FirstExample is { } ex)
                    body += $"\n{ex}";
                _toast.Notify(title, body);
            }
            else
            {
                Log.Debug("Statusaenderung erkannt — aber Fenster ist nicht ins Tray minimiert, kein Toast.");
            }

            // Viewer-Modus: zusaetzlich zum Toast das Fenster hochholen. Bewusst
            // NICHT bei reinen Recoveries (HasWorsened) und nicht bei aktivem
            // Snooze — sonst springt die Ausgabe auch dann auf, wenn sich gerade
            // etwas erholt oder der Nutzer ausdruecklich Ruhe haben wollte.
            if (_popUpOnProblem && change.HasWorsened && SnoozedUntil is null)
            {
                Log.Info("Viewer-Modus: Verschlechterung erkannt ({Text}) — hole Fenster nach vorn{Target}{Sound}.",
                    change.ToText(),
                    change.WorstNewProblem is { } p ? $" und springe auf {p.HostName}/{p.Description}" : "",
                    _popUpSound
                        ? $", mit Ton ({(string.IsNullOrWhiteSpace(_popUpSoundFile) ? "Systemklang" : _popUpSoundFile)})"
                        : "");
                PopUpForProblem(change.WorstNewProblem);
            }
        }
    }
}
