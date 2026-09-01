#if DEBUG
using Checkmk.App.Models;
using Checkmk.App.Services;
using Checkmk.App.ViewModels;
using Checkmk.Core.Models;

namespace Checkmk.App.Tools;

/// <summary>
/// Erfundene Daten für die Doku-Bilder.
///
/// <para><b>Jeder Wert hier ist ausgedacht.</b> Das Repository ist öffentlich;
/// ein Bild aus dem laufenden Betrieb zeigte Hostnamen, die interne Domäne und
/// — über den Host-Alias — die Anmeldenamen von Kollegen. „Musterstadt" statt
/// des echten Ortes, `beispiel.intern` statt der echten Domäne, erfundene
/// Kürzel statt echter Personen.</para>
///
/// <para>Trotzdem gilt: <b>das erzeugte Bild ansehen</b>, nicht nur prüfen, ob
/// die Datei da ist. In einem anderen Projekt standen nach dem ersten Lauf zwei
/// echte Werte im Bild, obwohl der Code eine Attrappe benutzte — beide kamen
/// aus Stellen, die sich selbst nachluden.</para>
/// </summary>
internal static class DemoData
{
    private static long Ago(TimeSpan span) => DateTimeOffset.UtcNow.Subtract(span).ToUnixTimeSeconds();

    // --- Status-Tab -------------------------------------------------------

    private static ServiceStatus Svc(string host, string alias, string desc, int state,
        string output, TimeSpan age, int ack = 0, int downtime = 0) => new()
        {
            HostName = host,
            HostAlias = alias,
            Description = desc,
            State = state,
            PluginOutput = output,
            Acknowledged = ack,
            ScheduledDowntimeDepth = downtime,
            LastCheckUnix = Ago(TimeSpan.FromMinutes(1)),
            LastStateChangeUnix = Ago(age)
        };

    internal static List<ServiceStatus> Services() =>
    [
        Svc("SRV-DB01", "MeierS; KruegerT", "CPU load", 2,
            "CRIT - 15 min load 24.80 at 8 cores (310.0%)", TimeSpan.FromMinutes(18)),
        Svc("SRV-DB01", "MeierS; KruegerT", "Filesystem /var", 1,
            "WARN - 87.4% used (218 GB of 250 GB)", TimeSpan.FromHours(6)),
        Svc("SRV-DB01", "MeierS; KruegerT", "MSSQL Instanz FACHDB", 0,
            "OK - Instanz läuft, 42 Datenbanken online", TimeSpan.FromDays(12)),
        Svc("SRV-DB02", "MeierS", "Backup FACHDB", 2,
            "CRIT - letztes vollständiges Backup vor 3 d 4 h", TimeSpan.FromHours(3)),
        Svc("SRV-DB02", "MeierS", "Memory", 0,
            "OK - 41.2% genutzt (26.4 GB von 64.0 GB)", TimeSpan.FromDays(30)),
        Svc("SW-RATHAUS-01", "NowakP", "Interface Uplink", 1,
            "WARN - 812 Mbit/s von 1000 Mbit/s (81.2%)", TimeSpan.FromMinutes(47)),
        Svc("SW-RATHAUS-01", "NowakP", "Temperatur Gehäuse", 0,
            "OK - 34.0 °C", TimeSpan.FromDays(64)),
        Svc("USV-RATHAUS", "NowakP; MeierS", "Batteriezustand", 1,
            "WARN - Batterie in 14 Tagen fällig", TimeSpan.FromDays(2), ack: 1),
        Svc("ESX-HAUS2-01", "SchulzA", "Hardware Sensoren", 0,
            "OK - alle 42 Sensoren im grünen Bereich", TimeSpan.FromDays(9)),
        Svc("ESX-HAUS2-01", "SchulzA", "Datastore ds-schnell", 1,
            "WARN - 91.0% belegt (1.82 TB von 2.00 TB)", TimeSpan.FromHours(11)),
        Svc("NAS-HAUS2", "SchulzA", "RAID-Status", 2,
            "CRIT - Platte 6 ausgefallen, Rebuild läuft", TimeSpan.FromMinutes(9)),
        Svc("NAS-HAUS2", "SchulzA", "Filesystem /volume1", 0,
            "OK - 58.1% used (5.8 TB of 10.0 TB)", TimeSpan.FromDays(21)),
        Svc("PC-4711", "MeierS", "Check_MK Agent", 0,
            "OK - Version 2.5.0p3, keine Fehler", TimeSpan.FromDays(3)),
        Svc("PC-4711", "MeierS", "Windows Update", 1,
            "WARN - 4 wichtige Updates ausstehend", TimeSpan.FromDays(1)),
        Svc("DC-01", "NowakP", "AD Replikation", 0,
            "OK - alle Partner synchron", TimeSpan.FromDays(45)),
        Svc("DC-01", "NowakP", "Zeitsynchronisation", 0,
            "OK - Abweichung 0.004 s", TimeSpan.FromDays(45)),
        Svc("SRV-FILE01", "KruegerT", "Freigabe Fachbereich", 0,
            "OK - erreichbar, 1.204 offene Dateien", TimeSpan.FromDays(7)),
        Svc("SRV-FILE01", "KruegerT", "Filesystem /daten", 1,
            "WARN - 84.9% used (4.2 TB of 5.0 TB)", TimeSpan.FromHours(30)),
        Svc("PRT-HAUS2-03", "SchulzA", "Tonerstand", 1,
            "WARN - Schwarz bei 8%", TimeSpan.FromDays(4), downtime: 1),
        Svc("WLC-01", "NowakP", "Access Points", 0,
            "OK - 84 von 84 verbunden", TimeSpan.FromDays(15))
    ];

    internal static IReadOnlyList<(string Host, string? Alias)> KnownHosts() =>
        [.. Services()
            .DistinctBy(s => s.HostName, StringComparer.OrdinalIgnoreCase)
            .Select(s => (s.HostName, (string?)s.HostAlias))];

    internal static StatusViewModel StatusViewModel()
    {
        var vm = new StatusViewModel(new NoClient(), FilterCollection(),
            new FixedState(), new NoOsCache(), new ViewerMode(null));

        var services = Services();
        vm.LoadDemoData(services, hostsUp: 11, hostsDown: 0, osByHost: new Dictionary<string, OsFamily>
        {
            ["SRV-DB01"] = OsFamily.Windows,
            ["SRV-DB02"] = OsFamily.Windows,
            ["DC-01"] = OsFamily.Windows,
            ["PC-4711"] = OsFamily.Windows,
            ["SRV-FILE01"] = OsFamily.Windows,
            ["ESX-HAUS2-01"] = OsFamily.Linux,
            ["NAS-HAUS2"] = OsFamily.Linux
        });
        return vm;
    }

    // --- Host-Detail ------------------------------------------------------

    internal static HostDetailViewModel HostDetailViewModel()
    {
        var vm = new HostDetailViewModel(new NoClient(), "SRV-DB01")
        {
            // Ohne das ueberschreibt der Selbst-Load beim Oeffnen alles hier.
            AutoLoad = false,
            HostStatus = new HostStatus
            {
                HostName = "SRV-DB01",
                State = 0,
                PluginOutput = "OK - Paket erhalten von 10.20.30.41",
                Acknowledged = 0,
                ScheduledDowntimeDepth = 0
            },
            HostConfig = new CheckmkObject<HostConfigExtensions>
            {
                Id = "SRV-DB01",
                Extensions = new HostConfigExtensions
                {
                    Folder = "/datenbanken/mssql",
                    Attributes = new HostAttributes
                    {
                        IpAddress = "10.20.30.41",
                        Alias = "MeierS; KruegerT"
                    }
                }
            },
            DisplayIp = "10.20.30.41"
        };

        foreach (var s in Services().Where(s => s.HostName == "SRV-DB01"))
            vm.Services.Add(s);

        vm.ServicesOk = vm.Services.Count(s => s.ServiceState == ServiceState.Ok);
        vm.ServicesWarn = vm.Services.Count(s => s.ServiceState == ServiceState.Warning);
        vm.ServicesCrit = vm.Services.Count(s => s.ServiceState == ServiceState.Critical);
        vm.StatusMessage = $"{vm.Services.Count} Services geladen.";
        return vm;
    }

    // --- Filter -----------------------------------------------------------

    internal static HostFilterCollection FilterCollection()
    {
        var store = new FixedFilters(new HostFilterState
        {
            Seeded = true,
            // Der Alias-Filter ist vorgewählt — so zeigt das Bild, was der
            // Umschalter „Regex vergleichen mit" tut.
            ActiveFilterName = "MeierS",
            Filters =
            [
                // Owner bleibt leer: `IsAuthor` fällt dann auf true zurück, der
                // Editor ist also bedienbar — ohne dass der echte Anmeldename
                // des Rechners ins Bild gerät.
                new HostFilter { Id = 1, Name = "Alle" },
                new HostFilter
                {
                    Id = 2, Name = "MeierS",
                    Target = FilterTarget.Alias, HostNameRegex = "MeierS"
                },
                new HostFilter
                {
                    Id = 3, Name = "Datenbanken",
                    HostNameRegex = "^SRV-DB"
                },
                new HostFilter
                {
                    Id = 201, Name = "Netzwerk aktiv", Owner = "NowakP",
                    FachbereichId = 7, FachbereichName = "Netzwerk",
                    HostNameRegex = "^(SW|WLC)-", Subscribers = 9
                }
            ]
        });

        return new HostFilterCollection(store, new FixedSettings(), new ViewerMode(null));
    }

    internal static IReadOnlyList<HostFilter> Catalog() =>
    [
        new()
        {
            Id = 201, Name = "Netzwerk aktiv", Owner = "NowakP",
            FachbereichId = 7, FachbereichName = "Netzwerk",
            HostNameRegex = "^(SW|WLC)-", Subscribers = 9
        },
        new()
        {
            Id = 202, Name = "USV und Klima", Owner = "NowakP",
            FachbereichId = 7, FachbereichName = "Netzwerk",
            HostNameRegex = "^(USV|KLIMA)-", Subscribers = 4
        },
        new()
        {
            Id = 203, Name = "Datenbankserver", Owner = "MeierS",
            FachbereichId = 3, FachbereichName = "Datenbanken",
            HostNameRegex = "^SRV-DB", Subscribers = 6
        },
        new()
        {
            Id = 204, Name = "Meine Geräte (Vorlage)", Owner = "KruegerT",
            FachbereichId = 3, FachbereichName = "Datenbanken",
            Target = FilterTarget.Alias, HostNameRegex = "KruegerT", Subscribers = 1
        },
        new()
        {
            Id = 205, Name = "Virtualisierung", Owner = "SchulzA",
            FachbereichId = 5, FachbereichName = "Serverbetrieb",
            HostNameRegex = "^(ESX|NAS)-", Subscribers = 12
        },
        new()
        {
            Id = 206, Name = "Rathaus (alle Geräte)", Owner = "SchulzA",
            FachbereichId = 5, FachbereichName = "Serverbetrieb",
            ExplicitHosts = ["SW-RATHAUS-01", "USV-RATHAUS", "DC-01"], Subscribers = 3
        }
    ];

    // --- Update -----------------------------------------------------------

    internal static UpdateInfo UpdateInfo() => new(
        new Version(1, 21, 0),
        "v1.21.0",
        ReadNotesOrFallback(),
        "https://github.com/LHP542/Checkmk/releases/tag/v1.21.0",
        "https://example.invalid/Checkmk-1.21.0-win-x64.zip");

    /// <summary>Nimmt echte Release-Notes aus dem Repo, wenn sie danebenliegen —
    /// das Bild soll zeigen, wie der Dialog mit einem <i>realen</i> Text umgeht,
    /// nicht mit drei Musterzeilen.</summary>
    private static string ReadNotesOrFallback()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "RELEASE_NOTES", "v1.20.4.md");
        try
        {
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        catch (IOException) { /* Fallback unten */ }

        return "Der Update-Dialog ist lesbar geworden.\n\n"
             + "## Was jetzt passiert\n\n"
             + "Die Notes werden in Absätze zerlegt und gesetzt: Überschriften als\n"
             + "Überschriften, Aufzählungen mit Punkt, **fette Stellen** fett.\n";
    }

    // --- Attrappen --------------------------------------------------------

    /// <summary>Kein Client — es wird nichts abgefragt, die Daten stehen oben.</summary>
    private sealed class NoClient : ICheckmkClientProvider
    {
        public Checkmk.Core.CheckmkClient? Current => null;
        public bool IsReady => false;
        public void Configure(ConnectionSettings settings, string plainSecret) { }
    }

    private sealed class NoOsCache : IHostOsCache
    {
        public void ApplyFromHostConfigs(IEnumerable<CheckmkObject<HostConfigExtensions>> hosts) { }
        public OsFamily OsFor(string hostName) => OsFamily.Unknown;
        public bool IsEmpty => true;
    }

    /// <summary>Feste Ansichtsvorgaben; Speichern ist ein No-Op, damit das
    /// Werkzeug die persönliche <c>statusview.json</c> nicht anfasst.</summary>
    private sealed class FixedState : IStatusViewStateStore
    {
        public StatusViewState Load() => new()
        {
            OnlyProblems = false,
            AutoRefresh = false,
            RefreshSeconds = 60
        };
        public void Save(StatusViewState state) { }
    }

    private sealed class FixedFilters(HostFilterState state) : IHostFilterStore
    {
        public string FilePath => "(demo)";
        public HostFilterState Load(string site) => state;
        public void Save(string site, HostFilterState s) { }
    }

    private sealed class FixedSettings : IConnectionSettingsStore
    {
        private readonly ConnectionSettings _s = new()
        {
            Site = "Musterstadt",
            Host = "cmk.beispiel.intern",
            Username = "cockpit"
        };
        public string SettingsFilePath => "(demo)";
        public ConnectionSettings Load() => _s;
        public string? LoadSecret(ConnectionSettings settings) => null;
        public void Save(ConnectionSettings settings, string plainSecret) { }
        public bool IsConfigured(ConnectionSettings settings) => true;
        public void UpdateActiveSite(string newSite) { }
    }
}
#endif
