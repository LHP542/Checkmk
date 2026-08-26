using System.Diagnostics;
using Avalonia.Threading;
using Checkmk.App.Models;
using Checkmk.App.Services;
using Checkmk.Core;
using Checkmk.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;

namespace Checkmk.App.ViewModels;

public sealed partial class StatusViewModel : ViewModelBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly ICheckmkClientProvider _clients;
    private readonly IHostOsCache _osCache;
    private readonly DispatcherTimer _timer;
    private List<ServiceStatus> _allServices = [];
    private Dictionary<string, OsFamily> _osByHost = [];

    /// <summary>OS-Familie fuer einen Host. Bevorzugt das Custom-Attribute
    /// aus <see cref="IHostOsCache"/> (z. B. "Operation System" vom Folder
    /// vererbt); Fallback ist der Parse aus der Check_MK-Agent-Service-Ausgabe.</summary>
    public OsFamily OsFor(string host)
    {
        var fromCache = _osCache.OsFor(host);
        if (fromCache != OsFamily.Unknown) return fromCache;
        return _osByHost.GetValueOrDefault(host, OsFamily.Unknown);
    }

    public HostFilterCollection Filters { get; }

    /// <summary>Sichtbare Zeilen der Service-Tabelle. <see cref="BulkObservableCollection{T}"/>
    /// statt <c>ObservableCollection</c>, weil hier bei ungefiltertem Blick
    /// zehntausende Zeilen auf einmal ausgetauscht werden.</summary>
    public BulkObservableCollection<ServiceStatus> Services { get; } = [];

    /// <summary>Baum-Ansicht: Hosts als Knoten (OS-Pictogram + Problem-Zaehler), Services als Kinder.</summary>
    public BulkObservableCollection<HostNodeViewModel> HostTree { get; } = [];

    /// <summary>
    /// Alle Services des letzten Refreshs — bereits serverseitig auf den aktiven
    /// Host-Filter beschraenkt, aber vor den Ansichtsfiltern („Nur Probleme",
    /// Freitext). Genau die Menge, die der Bereichs-Rollup als Linse braucht.
    /// </summary>
    public IReadOnlyList<ServiceStatus> AllServices => _allServices;

    /// <summary>false = Tabelle, true = Baum.</summary>
    [ObservableProperty]
    private bool _treeView;

    /// <summary>Aktuell im Baum gewaehlter Knoten (HostNodeViewModel oder ServiceStatus).</summary>
    [ObservableProperty]
    private object? _selectedTreeItem;

    /// <summary>Nach jedem Refresh: Services beschraenkt auf den aktiven Filter + Filtername.
    /// Fuer Tray-Signal und Notifications.</summary>
    public event Action<IReadOnlyList<ServiceStatus>, string?>? Refreshed;

    /// <summary>
    /// Bitte, im Grid auf diesen Service zu scrollen und die Zeile zu markieren.
    /// Wird gefeuert, wenn ein Service seit dem letzten Refresh neu CRIT ist — und
    /// im Viewer-Modus zusaetzlich vom <see cref="TrayController"/>, wenn das Fenster
    /// wegen einer Verschlechterung nach vorn geholt wird (dann auch bei WARN).
    /// </summary>
    public event Action<ServiceStatus>? SpotlightRequested;

    /// <summary>Von aussen anstossbar (Tray/Notification-Weg), damit die Ansicht auf
    /// den gemeldeten Service springt.</summary>
    public void RequestSpotlight(ServiceStatus service) => SpotlightRequested?.Invoke(service);

    private HashSet<string> _previousCrits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Identitaet einer Zeile ueber Refreshs hinweg — die
    /// <see cref="ServiceStatus"/>-Instanzen sind nach jedem Abruf neu.</summary>
    private static string ServiceKey(ServiceStatus s) => s.HostName + "\0" + s.Description;

    [ObservableProperty]
    private ServiceStatus? _selectedService;

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _onlyProblems = true;

    /// <summary>Blendet zusaetzlich Ack'd + in Wartung befindliche Services aus —
    /// zeigt die tatsaechliche Arbeitsliste fuer die Morgen-Runde.</summary>
    [ObservableProperty]
    private bool _onlyOpen;

    [ObservableProperty]
    private bool _autoRefresh;

    [ObservableProperty]
    private int _refreshSeconds = 30;

    [ObservableProperty]
    private int _hostsUp;

    [ObservableProperty]
    private int _hostsDown;

    [ObservableProperty]
    private int _servicesOk;

    [ObservableProperty]
    private int _servicesWarn;

    [ObservableProperty]
    private int _servicesCrit;

    /// <summary>„Filter: DB-Server · 47 Services" / „Filter: — · 33000 Services".
    /// Kurze Sicht in der Statusleiste, damit auf einen Blick klar ist, worauf
    /// sich die Zahlen aktuell beziehen.</summary>
    [ObservableProperty]
    private string _filterInfo = "";

    /// <summary>true wenn der letzte Refresh erfolgreich war. In der Statusleiste
    /// als gruener/roter Punkt sichtbar — schnelle Unterscheidung „Cockpit hakt"
    /// vs. „Checkmk-Backend hakt".</summary>
    [ObservableProperty]
    private bool _isBackendHealthy;

    /// <summary>Fortschritt des laufenden Refreshs, 0..1 — Balken in der Statusleiste.</summary>
    [ObservableProperty]
    private double _refreshProgress;

    /// <summary>true, solange keine Groessen-Schaetzung existiert (allererster
    /// Abruf ohne Vorsitzung): dann laeuft der Balken als Marquee.</summary>
    [ObservableProperty]
    private bool _refreshIndeterminate;

    private readonly IStatusViewStateStore _stateStore;
    private bool _loadingState;
    private readonly bool _isViewerMode;

    // --- Refresh-Zustand (Hintergrund-Lauf) ---
    private CancellationTokenSource? _refreshCts;
    private readonly Stopwatch _refreshClock = new();

    /// <summary>Laufende Nummer des aktuellen Refreshs. Fortschrittsmeldungen
    /// eines abgebrochenen Laufs treffen verzoegert ein und wuerden sonst den
    /// Balken des Nachfolgers verstellen.</summary>
    private int _refreshRun;

    /// <summary>Antwortgroessen des letzten Laufs — Nenner fuer den Balken, weil
    /// Checkmk die grossen Livestatus-Antworten chunked ohne Content-Length liefert.</summary>
    private long _lastHostBytes;
    private long _lastServiceBytes;

    /// <summary>Der Baum wird nur gebaut, wenn er sichtbar ist. Steht die Flagge,
    /// muss beim Umschalten nachgezogen werden.</summary>
    private bool _treeStale = true;

    // Segmentgrenzen des Balkens: Hosts sind eine kleine Antwort, die Services
    // sind der lange Teil, danach kommt nur noch Auswerten/Anzeigen.
    private const double HostSegmentEnd = 0.10;
    private const double ServiceSegmentEnd = 0.80;
    private const double ProjectSegmentEnd = 0.95;

    /// <summary>false im Viewer-Modus — blendet Ack/Downtime/Kommentar aus.
    /// Reiner Bedienschutz; die echte Grenze ist die Checkmk-Rolle des Users.</summary>
    public bool CanWrite { get; }

    public StatusViewModel(ICheckmkClientProvider clients, HostFilterCollection filters,
        IStatusViewStateStore stateStore, IHostOsCache osCache, ViewerMode viewer)
    {
        _clients = clients;
        Filters = filters;
        _stateStore = stateStore;
        _osCache = osCache;
        _isViewerMode = viewer.IsActive;
        CanWrite = viewer.CanWrite;

        // Timer VOR dem State-Load anlegen — sonst greifen die generierten
        // Property-Setter fuer AutoRefresh/RefreshSeconds im OnAutoRefreshChanged/
        // OnRefreshSecondsChanged auf _timer zu, waehrend das Feld noch null ist
        // (NullReferenceException beim Start, wenn statusview.json AutoRefresh=true
        // gespeichert hatte).
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) =>
        {
            // Laeuft der vorige Refresh noch (grosse Installation, kurzes
            // Intervall), wird der Tick verworfen statt ihn abzubrechen —
            // sonst kaeme bei 32.000 Checks nie einer bis zum Ende durch.
            if (IsBusy) return;
            await RefreshAsync();
        };

        // UI-Praeferenzen aus letzter Sitzung wieder herstellen. _loadingState
        // verhindert, dass die Load-Zuweisungen ihrerseits ein Save triggern.
        _loadingState = true;
        var s = _stateStore.Load();
        TreeView = s.TreeView;
        FilterText = s.FilterText;
        OnlyProblems = s.OnlyProblems;
        OnlyOpen = s.OnlyOpen;
        RefreshSeconds = s.RefreshSeconds;   // setzt _timer.Interval
        AutoRefresh = s.AutoRefresh;         // startet/stoppt _timer
        _loadingState = false;

        // Groessen-Schaetzung aus der Vorsitzung: ohne sie liefe ausgerechnet der
        // erste (und langsamste) Refresh nach dem Start nur als Marquee-Balken.
        _lastHostBytes = s.LastHostBytes;
        _lastServiceBytes = s.LastServiceBytes;

        // Viewer-Modus: die Vorgaben aus viewer.json ueberschreiben den zuletzt
        // gespeicherten Zustand. Sie sind Startwerte, keine Sperre — der Anwender
        // darf umschalten, es wird nur nichts zurueckgeschrieben (PersistState()
        // ist im Viewer-Modus ein No-Op).
        if (viewer.Profile is { } profile)
            ApplyViewerPreset(profile);

        Filters.PropertyChanged += async (_, e) =>
        {
            // Filter-Wechsel triggert einen neuen Server-Call — sonst blieben in
            // _allServices die Services der VORHERIGEN Filter-Menge, und die
            // clientseitige ApplyFilter()-Filterung liefe ins Leere.
            if (e.PropertyName == nameof(HostFilterCollection.Active))
                await RefreshAsync();
        };
    }

    /// <summary>
    /// Uebernimmt Sicht-Vorgaben aus dem Viewer-Profil. Laeuft im Ctor, bevor der
    /// Filters-PropertyChanged-Handler haengt — sonst loeste das Setzen des
    /// Vorgabefilters einen Refresh aus, bevor ueberhaupt ein Client konfiguriert ist.
    /// </summary>
    private void ApplyViewerPreset(ViewerProfile profile)
    {
        var v = profile.View;

        _loadingState = true;
        try
        {
            TreeView = v.TreeView;
            FilterText = v.FilterText;
            OnlyProblems = v.OnlyProblems;
            OnlyOpen = v.OnlyOpen;
            RefreshSeconds = v.RefreshSeconds;   // setzt _timer.Interval
            AutoRefresh = v.AutoRefresh;         // startet/stoppt _timer
        }
        finally { _loadingState = false; }

        // Bewusst bedingungslos: ToHostFilter liefert auch ohne Regex/Liste einen
        // Filter (= alle Hosts). Sonst bliebe der zuletzt aktive Filter aus der
        // persoenlichen filter.json stehen und wuerde die Profilvorgabe ueberstimmen.
        var preset = v.ToHostFilter();
        Filters.ApplyPreset(preset);

        // Beim Ausrollen die haeufigste Frage: „welcher Filter greift denn nun?"
        Log.Info("Viewer-Vorgabe aktiv: Filter '{Filter}' ({Target}-Regex={Regex}, {Hosts} Hosts explizit), "
               + "NurProbleme={OnlyProblems}, NurOffen={OnlyOpen}, AutoRefresh={Auto}/{Sec}s.",
            preset.Name, preset.TargetDisplay, preset.HostNameRegex ?? "—", preset.ExplicitHosts.Count,
            OnlyProblems, OnlyOpen, AutoRefresh, RefreshSeconds);
    }

    private void PersistState()
    {
        // Im Viewer-Modus nichts nach statusview.json schreiben: die Vorgabe soll
        // bei jedem Start wieder greifen, auch wenn der Anwender zwischendurch
        // umgeschaltet hat.
        if (_loadingState || _isViewerMode) return;
        _stateStore.Save(new StatusViewState
        {
            TreeView = TreeView,
            FilterText = FilterText,
            OnlyProblems = OnlyProblems,
            OnlyOpen = OnlyOpen,
            AutoRefresh = AutoRefresh,
            RefreshSeconds = RefreshSeconds,
            LastHostBytes = _lastHostBytes,
            LastServiceBytes = _lastServiceBytes
        });
    }

    partial void OnFilterTextChanged(string value) { ApplyFilter(); PersistState(); }
    partial void OnOnlyProblemsChanged(bool value) { ApplyFilter(); PersistState(); }
    partial void OnOnlyOpenChanged(bool value) { ApplyFilter(); PersistState(); }

    partial void OnTreeViewChanged(bool value)
    {
        // Der Baum wird nur gebaut, wenn er auch zu sehen ist (siehe BuildTreeIfVisible).
        // Beim Einschalten also nachziehen.
        if (value && _treeStale) BuildTree();
        PersistState();
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value) _timer.Start();
        else _timer.Stop();
        PersistState();
    }

    partial void OnRefreshSecondsChanged(int value)
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(5, value));
        PersistState();
    }

    /// <summary>
    /// Ergebnis eines Hintergrund-Refreshs. Alles, was der UI-Thread danach nur
    /// noch zuweisen muss — inklusive der fertig gefilterten und sortierten
    /// Zeilenliste. So bleibt auf dem UI-Thread nur der Collection-Austausch.
    /// </summary>
    private sealed record RefreshSnapshot(
        List<ServiceStatus> All,
        List<ServiceStatus> Visible,
        Dictionary<string, OsFamily> OsByHost,
        int HostCount, int HostsUp, int HostsDown,
        int ServicesOk, int ServicesWarn, int ServicesCrit,
        long HostBytes, long ServiceBytes);

    /// <summary>
    /// Holt Host- und Service-Status neu. Der teure Teil — HTTP, JSON-Parse,
    /// Zaehlen, Filtern, Sortieren — laeuft komplett auf dem ThreadPool; auf dem
    /// UI-Thread bleibt nur der Austausch der Collections. Vorher lief das
    /// Deserialisieren nach dem <c>await</c> wieder auf dem UI-Thread und die
    /// 32.000 Einzel-<c>Add</c>s dazu: die App stand mehrere Sekunden.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        var client = _clients.Current;
        if (client is null)
        {
            StatusMessage = "Nicht konfiguriert — bitte Verbindung in den Einstellungen setzen.";
            return;
        }

        // Ein noch laufender Refresh wird abgebrochen, nicht eingereiht: wer neu
        // anstoesst (Button, Filterwechsel), will den aktuellen Stand — und ein
        // abgebrochener Download spart Sekunden. Nur abbrechen, nicht entsorgen:
        // die Quelle gehoert dem alten Lauf, der sie in seinem eigenen finally
        // freigibt — ein Dispose von hier aus faellt dem noch abbauenden
        // HttpClient in die Token-Registrierungen.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _refreshCts, cts)?.Cancel();
        var ct = cts.Token;
        var run = ++_refreshRun;

        IsBusy = true;
        _refreshClock.Restart();
        RefreshProgress = 0;
        RefreshIndeterminate = _lastServiceBytes <= 0;
        StatusMessage = "Aktualisiere…";

        try
        {
            // Serverseitig filtern — bei grossen Installationen (Zehntausende Checks)
            // spart das ein Vielfaches an Netzwerklast. Regex/Include-Liste geht
            // direkt in die Livestatus-Query, Freitext + „Nur Probleme" bleiben
            // clientside (das sind reine Ansichtsfilter, keine Beschraenkung des
            // Datensatzes).
            var livestatusFilter = Filters.Active?.ToLivestatus();

            // Ansichtsfilter als Wert einfrieren, damit die Projektion im
            // Hintergrund laufen kann, ohne auf UI-State zuzugreifen.
            var criteria = CurrentCriteria();

            var hostSegment = new RefreshSegment(this, run, 0.0, HostSegmentEnd, _lastHostBytes);
            var serviceSegment = new RefreshSegment(this, run, HostSegmentEnd, ServiceSegmentEnd, _lastServiceBytes);

            var snapshot = await Task.Run(async () =>
            {
                var hosts = await client.GetHostStatusesAsync(livestatusFilter, ct, hostSegment)
                    .ConfigureAwait(false);
                var services = await client.GetServiceStatusesAsync(livestatusFilter, ct, serviceSegment)
                    .ConfigureAwait(false);

                ReportStage(run, ServiceSegmentEnd, "Werte aus…");

                var all = services.ToList();

                // OS-Familie je Host aus der "Check_MK Agent"-Service-Ausgabe (z. B. "OS: windows").
                var osByHost = all
                    .Where(s => s.Description == "Check_MK Agent")
                    .Select(s => (s.HostName, Os: OsDetection.ParseFamily(s.PluginOutput)))
                    .Where(x => x.Os != OsFamily.Unknown)
                    .GroupBy(x => x.HostName)
                    .ToDictionary(g => g.Key, g => g.First().Os);

                var visible = Project(all, criteria);

                ct.ThrowIfCancellationRequested();
                ReportStage(run, ProjectSegmentEnd, "Zeige an…");

                return new RefreshSnapshot(
                    all, visible, osByHost,
                    hosts.Count,
                    hosts.Count(h => h.HostState == HostState.Up),
                    hosts.Count(h => h.HostState != HostState.Up),
                    all.Count(s => s.ServiceState == ServiceState.Ok),
                    all.Count(s => s.ServiceState == ServiceState.Warning),
                    all.Count(s => s.ServiceState == ServiceState.Critical),
                    hostSegment.BytesRead, serviceSegment.BytesRead);
            }, ct).ConfigureAwait(true);

            ct.ThrowIfCancellationRequested();

            HostsUp = snapshot.HostsUp;
            HostsDown = snapshot.HostsDown;
            ServicesOk = snapshot.ServicesOk;
            ServicesWarn = snapshot.ServicesWarn;
            ServicesCrit = snapshot.ServicesCrit;

            _allServices = snapshot.All;
            _osByHost = snapshot.OsByHost;
            RememberPayloadSizes(snapshot.HostBytes, snapshot.ServiceBytes);

            ApplyVisible(snapshot.Visible);

            // Fuer Tray/Notifications: die Services sind bereits auf den aktiven
            // Filter beschraenkt (serverseitig gefiltert oben) — direkt weiterreichen.
            Refreshed?.Invoke(_allServices, Filters.Active?.Name);

            RefreshProgress = 1;
            StatusMessage = $"Aktualisiert {DateTime.Now:HH:mm:ss} — "
                          + $"{snapshot.All.Count} Services, {snapshot.HostCount} Hosts "
                          + $"in {_refreshClock.Elapsed.TotalSeconds:F1} s.";
            var scope = Filters.Active is { } f ? f.Name : "—";
            FilterInfo = $"Filter: {scope} · {snapshot.All.Count} Services";
            IsBackendHealthy = true;

            SpotlightFreshCrits();
        }
        catch (OperationCanceledException)
        {
            // Abgeloest durch einen neueren Refresh — der besitzt jetzt Balken und
            // Statustext, hier also nichts mehr anfassen.
            Log.Debug("Status-Refresh abgebrochen (Lauf {Run}).", run);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Status-Refresh fehlgeschlagen.");
            if (IsCurrentRun(run))
            {
                StatusMessage = $"Fehler: {ex.Message}";
                IsBackendHealthy = false;
            }
        }
        finally
        {
            // Balken und Busy-Flag nur zuruecksetzen, wenn wir noch der aktuelle
            // Lauf sind — sonst loescht ein abgebrochener Vorgaenger die Anzeige
            // seines Nachfolgers.
            if (IsCurrentRun(run))
            {
                IsBusy = false;
                RefreshIndeterminate = false;
                _refreshClock.Stop();
                Interlocked.CompareExchange(ref _refreshCts, null, cts);
            }
            cts.Dispose();
        }
    }

    private bool IsCurrentRun(int run) => _refreshRun == run;

    /// <summary>Neue CRITs seit dem letzten Refresh erkennen und den juengsten
    /// per Event melden — StatusView scrollt dorthin.</summary>
    private void SpotlightFreshCrits()
    {
        var currentCrits = _allServices
            .Where(s => s.ServiceState == ServiceState.Critical)
            .Select(ServiceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (_previousCrits.Count > 0)
        {
            var freshCrit = _allServices
                .Where(s => s.ServiceState == ServiceState.Critical
                         && !_previousCrits.Contains(ServiceKey(s)))
                .OrderByDescending(s => s.LastStateChangeUnix)
                .FirstOrDefault();
            if (freshCrit is not null)
                SpotlightRequested?.Invoke(freshCrit);
        }
        _previousCrits = currentCrits;
    }

    /// <summary>Merkt sich die Antwortgroessen als Nenner fuer den naechsten
    /// Balken. Geschrieben wird nur bei spuerbarer Abweichung — sonst ginge bei
    /// eingeschaltetem Auto-Refresh alle 30 s eine Datei auf die Platte.</summary>
    private void RememberPayloadSizes(long hostBytes, long serviceBytes)
    {
        var changed = Deviates(_lastHostBytes, hostBytes) || Deviates(_lastServiceBytes, serviceBytes);
        if (hostBytes > 0) _lastHostBytes = hostBytes;
        if (serviceBytes > 0) _lastServiceBytes = serviceBytes;
        if (changed) PersistState();

        static bool Deviates(long old, long fresh)
            => fresh > 0 && (old <= 0 || Math.Abs(fresh - old) > old * 0.1);
    }

    // -----------------------------------------------------------------------
    // Fortschritt
    // -----------------------------------------------------------------------

    /// <summary>
    /// Uebersetzt den Byte-Fortschritt eines Abrufs in ein Segment des
    /// Gesamtbalkens. Checkmk schickt die grossen Livestatus-Antworten chunked,
    /// also ohne <c>Content-Length</c> — Nenner ist dann
    /// <paramref name="estimate"/>, die Groesse aus dem letzten Lauf. Ohne
    /// Schaetzung laeuft der Balken indeterminate und zeigt stattdessen die
    /// geladenen Megabytes.
    /// </summary>
    private sealed class RefreshSegment(
        StatusViewModel owner, int run, double from, double to, long estimate)
        : IProgress<TransferProgress>
    {
        /// <summary>Tatsaechliche Antwortgroesse — Schaetzer fuer den naechsten Lauf.</summary>
        public long BytesRead { get; private set; }

        public void Report(TransferProgress value)
        {
            BytesRead = value.BytesRead;

            var total = value.TotalBytes ?? estimate;
            double? fraction = total > 0
                ? from + (to - from) * Math.Clamp((double)value.BytesRead / total, 0, 1)
                : null;

            // Kommt vom ThreadPool (CountingStream meldet gedrosselt alle 256 KB) —
            // ans UI marshallen. Ein eigenes IProgress statt Progress<T>, weil
            // dessen Context-Capture hier nicht greift.
            var bytes = value.BytesRead;
            Dispatcher.UIThread.Post(() => owner.ShowProgress(run, fraction, bytes));
        }
    }

    /// <summary>Meldet einen Abschnittswechsel ohne Byte-Bezug (Auswerten/Anzeigen).</summary>
    private void ReportStage(int run, double fraction, string label)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrentRun(run)) return;
            RefreshIndeterminate = false;
            RefreshProgress = Math.Max(RefreshProgress, fraction);
            StatusMessage = label;
        });

    /// <summary>Schreibt Balken und Statustext. Laeuft immer auf dem UI-Thread.</summary>
    private void ShowProgress(int run, double? fraction, long bytesRead)
    {
        if (!IsCurrentRun(run)) return;

        if (fraction is not { } f)
        {
            RefreshIndeterminate = true;
            StatusMessage = $"Aktualisiere… {FormatBytes(bytesRead)} geladen";
            return;
        }

        RefreshIndeterminate = false;
        RefreshProgress = Math.Max(RefreshProgress, f);   // nie zurueckspringen
        StatusMessage = "Aktualisiere… " + FormatProgress(RefreshProgress);
    }

    /// <summary>„45 % · noch ca. 3 s". Die Restzeit wird linear aus der bisher
    /// verstrichenen Zeit hochgerechnet — grob, aber genau die Auskunft, die
    /// beim Warten fehlt. Erst ab 5 % und einer halben Sekunde, davor waere die
    /// Hochrechnung reine Zahlenkosmetik.</summary>
    private string FormatProgress(double fraction)
    {
        var text = fraction.ToString("P0");
        var elapsed = _refreshClock.Elapsed;
        if (fraction is >= 0.05 and < 1.0 && elapsed > TimeSpan.FromSeconds(0.5))
        {
            var remaining = elapsed.TotalSeconds * (1 - fraction) / fraction;
            text += remaining < 1 ? " · gleich fertig" : $" · noch ca. {remaining:F0} s";
        }
        return text;
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):F1} MB"
            : $"{bytes / 1024.0:F0} KB";

    /// <summary>Acknowledged das aktuell gewaehlte Service-Problem und aktualisiert.
    /// Die <c>CanWrite</c>-Pruefung ist Absicherung gegen Wege, die an der
    /// ausgeblendeten UI vorbeigehen (Hotkeys, Plugins).</summary>
    public async Task PerformAcknowledgeAsync(string comment)
    {
        var client = _clients.Current;
        var svc = SelectedService;
        if (!CanWrite || client is null || svc is null) return;

        try
        {
            IsBusy = true;
            await client.AcknowledgeServiceProblemAsync(svc.HostName, svc.Description, comment);
            StatusMessage = $"Acknowledged: {svc.HostName} / {svc.Description}.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Acknowledge fehlgeschlagen.");
            StatusMessage = $"Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Setzt eine Downtime auf dem gewaehlten Service und aktualisiert.</summary>
    public async Task PerformDowntimeAsync(string comment, DateTimeOffset start, DateTimeOffset end)
    {
        var client = _clients.Current;
        var svc = SelectedService;
        if (!CanWrite || client is null || svc is null) return;

        try
        {
            IsBusy = true;
            await client.ScheduleServiceDowntimeAsync(svc.HostName, svc.Description, start, end, comment);
            StatusMessage = $"Downtime bis {end:HH:mm} gesetzt: {svc.HostName} / {svc.Description}.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Downtime fehlgeschlagen.");
            StatusMessage = $"Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Legt einen Kommentar auf dem gewaehlten Service an.</summary>
    public async Task PerformAddCommentAsync(string comment, bool persistent)
    {
        var client = _clients.Current;
        var svc = SelectedService;
        if (!CanWrite || client is null || svc is null) return;

        try
        {
            IsBusy = true;
            await client.AddServiceCommentAsync(svc.HostName, svc.Description, comment, persistent);
            StatusMessage = $"Kommentar gespeichert: {svc.HostName} / {svc.Description}.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Kommentar-Anlage fehlgeschlagen.");
            StatusMessage = $"Fehler: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Ack fuer alle uebergebenen Services (Bulk). Fehler werden gesammelt, nicht abgebrochen.</summary>
    public async Task PerformBulkAcknowledgeAsync(IReadOnlyList<ServiceStatus> services, string comment)
    {
        var client = _clients.Current;
        if (!CanWrite || client is null || services.Count == 0) return;

        var errors = 0;
        var done = 0;
        try
        {
            IsBusy = true;
            foreach (var svc in services)
            {
                try
                {
                    done++;
                    StatusMessage = $"Ack {done}/{services.Count}: {svc.HostName} / {svc.Description}";
                    await client.AcknowledgeServiceProblemAsync(svc.HostName, svc.Description, comment);
                }
                catch (Exception ex)
                {
                    errors++;
                    Log.Warn(ex, "Bulk-Ack fehlgeschlagen fuer {Host}/{Service}.", svc.HostName, svc.Description);
                }
            }
            StatusMessage = errors == 0
                ? $"Acknowledged: {done} Services."
                : $"Acknowledged: {done - errors}/{done} — {errors} Fehler (siehe Log).";
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Downtime fuer alle uebergebenen Services (Bulk). Fehler werden gesammelt.</summary>
    public async Task PerformBulkDowntimeAsync(IReadOnlyList<ServiceStatus> services,
        string comment, DateTimeOffset start, DateTimeOffset end)
    {
        var client = _clients.Current;
        if (!CanWrite || client is null || services.Count == 0) return;

        var errors = 0;
        var done = 0;
        try
        {
            IsBusy = true;
            foreach (var svc in services)
            {
                try
                {
                    done++;
                    StatusMessage = $"Downtime {done}/{services.Count}: {svc.HostName} / {svc.Description}";
                    await client.ScheduleServiceDowntimeAsync(svc.HostName, svc.Description, start, end, comment);
                }
                catch (Exception ex)
                {
                    errors++;
                    Log.Warn(ex, "Bulk-Downtime fehlgeschlagen fuer {Host}/{Service}.", svc.HostName, svc.Description);
                }
            }
            StatusMessage = errors == 0
                ? $"Downtime bis {end:HH:mm} gesetzt: {done} Services."
                : $"Downtime: {done - errors}/{done} — {errors} Fehler (siehe Log).";
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Freitext-Treffer auf einem Service. Der <b>Anzeigename</b> muss mitgesucht
    /// werden: bei SNMP-Geraeten (z. B. Rittal CMC III) heisst der Service
    /// „CMCIII-IO3 Input 1", angezeigt wird aber „USV Netzausfall (Input 1)" —
    /// und der Anwender tippt genau das, was er in der Spalte liest.
    /// </summary>
    private static bool MatchesText(ServiceStatus s, string f)
        => s.HostName.Contains(f, StringComparison.OrdinalIgnoreCase)
           || s.Description.Contains(f, StringComparison.OrdinalIgnoreCase)
           || s.DisplayNameOrDescription.Contains(f, StringComparison.OrdinalIgnoreCase)
           || (s.HostAlias?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
           || (s.PluginOutput?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Die clientseitigen Ansichtsfilter als Wert. Kopiert, damit die
    /// Projektion auf einem Hintergrund-Thread laufen kann, ohne UI-State zu lesen.</summary>
    private readonly record struct ViewCriteria(
        HostFilter? Host, bool OnlyProblems, bool OnlyOpen, string Text);

    private ViewCriteria CurrentCriteria()
        => new(Filters.Active, OnlyProblems, OnlyOpen, FilterText?.Trim() ?? "");

    /// <summary>Filtert und sortiert die Rohdaten. Bewusst statisch und ohne
    /// Seiteneffekte — laeuft beim Refresh im Hintergrund und beim Tippen im
    /// Freitextfeld direkt auf dem UI-Thread.</summary>
    private static List<ServiceStatus> Project(IReadOnlyList<ServiceStatus> all, ViewCriteria c)
    {
        IEnumerable<ServiceStatus> q = all;

        if (c.Host is { } activeFilter)
            q = q.Where(s => activeFilter.Matches(s.HostName, s.HostAlias));

        if (c.OnlyProblems)
            q = q.Where(s => s.ServiceState != ServiceState.Ok);

        if (c.OnlyOpen)
            q = q.Where(s => !s.IsAcknowledged && !s.InDowntime);

        if (c.Text.Length > 0)
            q = q.Where(s => MatchesText(s, c.Text));

        return [.. q.OrderByDescending(s => s.State).ThenBy(s => s.HostName)];
    }

    private void ApplyFilter() => ApplyVisible(Project(_allServices, CurrentCriteria()));

    /// <summary>Tauscht die sichtbaren Zeilen aus — ein einziges Reset-Event statt
    /// zehntausender <c>Add</c>s (siehe <see cref="BulkObservableCollection{T}"/>).</summary>
    private void ApplyVisible(List<ServiceStatus> visible)
    {
        // Der Reset raeumt die Grid-Selektion ab, und die ServiceStatus-Instanzen
        // sind nach einem Refresh ohnehin neu — deshalb ueber den Schluessel
        // nachziehen. Sonst verliert ein Auto-Refresh alle 30 s die markierte Zeile.
        var keep = SelectedService is { } sel ? ServiceKey(sel) : null;

        Services.ReplaceAll(visible);

        if (keep is not null)
            SelectedService = visible.FirstOrDefault(s => ServiceKey(s) == keep);

        BuildTreeIfVisible();
    }

    /// <summary>Der Baum kostet ein ViewModel je Host und ist meistens gar nicht
    /// sichtbar. Dann bleibt er ungebaut und wird beim Umschalten nachgezogen
    /// (<see cref="OnTreeViewChanged"/>).</summary>
    private void BuildTreeIfVisible()
    {
        if (!TreeView)
        {
            _treeStale = true;
            return;
        }
        BuildTree();
    }

    /// <summary>
    /// Baut den Host-Baum: oberste Knoten = Hosts (Host-Filter + Freitext), Kinder = deren
    /// Services. "Nur Probleme" filtert auch hier (dann nur Problem-Services + Hosts mit Problemen).
    /// </summary>
    private void BuildTree()
    {
        IEnumerable<ServiceStatus> q = _allServices;

        if (Filters.Active is { } activeFilter)
            q = q.Where(s => activeFilter.Matches(s.HostName, s.HostAlias));

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            q = q.Where(s => MatchesText(s, f));
        }

        var nodes = new List<HostNodeViewModel>();
        foreach (var group in q.GroupBy(s => s.HostName).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var all = group.ToList();

            IEnumerable<ServiceStatus> children = all;
            if (OnlyProblems)
                children = children.Where(s => s.ServiceState != ServiceState.Ok);
            if (OnlyOpen)
                children = children.Where(s => !s.IsAcknowledged && !s.InDowntime);

            var materialized = children.ToList();
            if ((OnlyProblems || OnlyOpen) && materialized.Count == 0)
                continue;

            children = materialized.OrderByDescending(s => s.State).ThenBy(s => s.Description);

            nodes.Add(new HostNodeViewModel(
                group.Key,
                OsFor(group.Key),
                children));
        }

        HostTree.ReplaceAll(nodes);
        _treeStale = false;
    }
}
