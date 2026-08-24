using Avalonia;
using Checkmk.App.Services;
using Checkmk.App.Services.Plugins;
using Checkmk.App.ViewModels;
using Checkmk.App.Views;
using Checkmk.Data;
using Checkmk.PluginContracts;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace Checkmk.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var logger = LogManager.Setup()
            .SetupExtensions(e => e.RegisterLayoutRenderer<Checkmk.App.Services.MaskedLayoutRenderer>("masked"))
            .LoadConfigurationFromFile("nlog.config", optional: true)
            .GetCurrentClassLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.Error(e.ExceptionObject as Exception, "Unbehandelte Ausnahme (AppDomain).");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.Error(e.Exception, "Unbeobachtete Task-Ausnahme.");
            e.SetObserved();
        };

        try
        {
            // Werkzeug-Modus vor dem UI: erzeugt database.json neben der Exe.
            if (TryRunProtectDb(args)) return;
            if (TryRunUpdateKey(args)) return;
            if (TryRunSignUpdate(args)) return;
            if (TryShowUsage(args)) return;

            App.Services = BuildServiceProvider();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.Fatal(ex, "App wurde durch eine Ausnahme beendet.");
        }
        finally
        {
            LogManager.Shutdown();
        }
    }

    /// <summary>
    /// <c>Checkmk.App.exe --protect-db "&lt;Verbindungsstring&gt;" [Zielpfad]</c>
    ///
    /// Schreibt <c>database.json</c> mit verschleiertem Wert neben die Exe. Ohne
    /// diesen Schalter käme man an den Wert nicht heran — von Hand ist er nicht
    /// zu erzeugen.
    ///
    /// Die Ausgabe geht bewusst auf eine Konsole, die eine WinExe normalerweise
    /// nicht hat: <c>AttachConsole</c> hängt sich an die aufrufende cmd/PowerShell,
    /// sonst sähe der Anwender gar nichts und wüsste nicht, ob es geklappt hat.
    /// </summary>
    private static bool TryRunProtectDb(string[] args)
    {
        var i = Array.FindIndex(args, a =>
            string.Equals(a, "--protect-db", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;

        AttachConsole(AttachParentProcess);

        if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
        {
            Console.Error.WriteLine(
                "Aufruf: Checkmk.App.exe --protect-db \"<Verbindungsstring>\" [Zielpfad]");
            return true;
        }

        var target = i + 2 < args.Length && !args[i + 2].StartsWith('-')
            ? args[i + 2]
            : DatabaseConnection.DeployedConfigPath;

        try
        {
            DatabaseConnection.WriteDeployedConfig(target, args[i + 1]);
            Console.WriteLine($"Geschrieben: {target}");
            Console.WriteLine(
                "Hinweis: Der Wert ist verschleiert, nicht geschuetzt — der Schluessel steckt "
              + "im Binary daneben. Wirksam ist allein das Datenbankrecht des Laufzeitkontos.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fehlgeschlagen: {ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// <c>Checkmk.App.exe --make-update-key</c>
    ///
    /// Erzeugt ein ECDSA-P-256-Schlüsselpaar für die Update-Signatur. Der
    /// <b>öffentliche</b> Teil wird als Konstante ins Binary eingetragen
    /// (<c>UpdateSignature.PublicKeyBase64</c>), der <b>private</b> gehört in ein
    /// GitHub-Secret und sonst nirgendwohin.
    ///
    /// Solange die Konstante leer ist, prüft das Cockpit keine Signaturen —
    /// bestehende Releases bleiben installierbar. Ab dem ersten eingetragenen
    /// Schlüssel ist ein gültiges Manifest Pflicht.
    /// </summary>
    private static bool TryRunUpdateKey(string[] args)
    {
        if (!args.Any(a => string.Equals(a, "--make-update-key", StringComparison.OrdinalIgnoreCase)))
            return false;

        AttachConsole(AttachParentProcess);

        using var key = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        Console.WriteLine();
        Console.WriteLine("--- OEFFENTLICH: als UpdateSignature.PublicKeyBase64 ins Binary ---");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Console.WriteLine();
        Console.WriteLine("--- PRIVAT: als GitHub-Secret UPDATE_SIGNING_KEY hinterlegen ---");
        Console.WriteLine(Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        Console.WriteLine();
        Console.WriteLine("Der private Schluessel wird hier NICHT gespeichert. Geht er verloren,");
        Console.WriteLine("erzeugt man ein neues Paar — dann muessen aber alle Clients den neuen");
        Console.WriteLine("oeffentlichen Schluessel bekommen, bevor sie wieder updaten koennen.");
        return true;
    }

    /// <summary>
    /// <c>Checkmk.App.exe --sign-update &lt;paket.zip&gt; &lt;version&gt; &lt;privkey-base64&gt; [ziel]</c>
    ///
    /// Erzeugt das signierte <c>update.json</c> zu einem Release-ZIP. Läuft im
    /// Release-Workflow, kann aber auch von Hand aufgerufen werden.
    /// </summary>
    private static bool TryRunSignUpdate(string[] args)
    {
        var i = Array.FindIndex(args, a =>
            string.Equals(a, "--sign-update", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;

        AttachConsole(AttachParentProcess);

        if (i + 3 >= args.Length)
        {
            Console.Error.WriteLine(
                "Aufruf: Checkmk.App.exe --sign-update <paket.zip> <version> <privkey-base64> [ziel]");
            return true;
        }

        var zip = args[i + 1];
        var version = args[i + 2];
        var privateKey = args[i + 3];
        var target = i + 4 < args.Length && !args[i + 4].StartsWith('-')
            ? args[i + 4]
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zip)) ?? ".", "update.json");

        try
        {
            var manifest = new UpdateManifest(
                Version: version.TrimStart('v', 'V'),
                File: Path.GetFileName(zip),
                Sha256: UpdateSignature.HashFile(zip),
                Size: new FileInfo(zip).Length);

            var signed = new SignedUpdateManifest
            {
                Version = manifest.Version,
                File = manifest.File,
                Sha256 = manifest.Sha256,
                Size = manifest.Size,
                Signature = UpdateSignature.Sign(manifest, privateKey)
            };

            File.WriteAllText(target, UpdateSignature.ToJson(signed));
            Console.WriteLine($"Geschrieben: {target}");
            Console.WriteLine($"  Version {signed.Version}, {signed.Size} Bytes, SHA-256 {signed.Sha256}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fehlgeschlagen: {ex.Message}");
        }
        return true;
    }

    /// <summary>Werkzeug-Schalter, die diese Exe kennt.</summary>
    private static readonly string[] KnownSwitches =
        ["--protect-db", "--make-update-key", "--sign-update", "--help"];

    /// <summary>
    /// Fängt unbekannte <c>--</c>-Schalter ab und zeigt die Kurzhilfe.
    ///
    /// <b>Warum das nötig ist:</b> Ohne diesen Griff startet ein Vertipper —
    /// oder ein veraltetes Binary, das den Schalter noch nicht kennt —
    /// wortlos die Oberfläche. Man sieht dann <i>gar nichts</i> im Terminal und
    /// sucht den Fehler beim Werkzeug, während in Wahrheit ein weiteres Cockpit
    /// im Hintergrund läuft. Genau so ist es einmal passiert.
    ///
    /// Nur <c>--</c>-Argumente werden geprüft; alles andere reicht die Methode
    /// unangetastet an Avalonia weiter.
    /// </summary>
    private static bool TryShowUsage(string[] args)
    {
        var unknown = args.FirstOrDefault(a =>
            a.StartsWith("--", StringComparison.Ordinal)
            && !KnownSwitches.Contains(a, StringComparer.OrdinalIgnoreCase));

        var wantsHelp = args.Any(a =>
            string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase));

        if (unknown is null && !wantsHelp) return false;

        AttachConsole(AttachParentProcess);

        if (unknown is not null)
            Console.Error.WriteLine($"Unbekannter Schalter: {unknown}");

        Console.WriteLine();
        Console.WriteLine("Checkmk Cockpit — Werkzeug-Modus");
        Console.WriteLine();
        Console.WriteLine("  --protect-db \"<Verbindungsstring>\" [Zielpfad]");
        Console.WriteLine("      Schreibt database.json mit verschleiertem Wert neben die Exe.");
        Console.WriteLine();
        Console.WriteLine("  --make-update-key");
        Console.WriteLine("      Erzeugt ein Schluesselpaar fuer die Update-Signatur.");
        Console.WriteLine();
        Console.WriteLine("  --sign-update <paket.zip> <version> <privkey-base64> [ziel]");
        Console.WriteLine("      Schreibt das signierte update.json zu einem Paket.");
        Console.WriteLine();
        Console.WriteLine("Ohne Schalter startet die Oberflaeche.");
        return true;
    }

    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        // Viewer-Profil: optionale viewer.json NEBEN der Exe. Liegt sie da, kommt die
        // Verbindung aus ihr (statt %APPDATA%\...\settings.json) und die App laeuft
        // im reinen Guck-Modus. Fehlt sie, aendert sich nichts am bisherigen Verhalten.
        var viewerProfile = ViewerProfile.LoadOrNull();
        services.AddSingleton(new ViewerMode(viewerProfile));

        services.AddSingleton<ISecretProtector>(_ => SecretProtectorFactory.Create());
        if (viewerProfile is not null)
            services.AddSingleton<IConnectionSettingsStore>(
                new ViewerConnectionSettingsStore(viewerProfile));
        else
            services.AddSingleton<IConnectionSettingsStore>(_ =>
                new ConnectionSettingsStore(SecretProtectorFactory.Create()));
        services.AddSingleton<ICheckmkClientProvider, CheckmkClientProvider>();
        services.AddSingleton<IToastNotifier, WindowsToastNotifier>();
        services.AddSingleton<IHostFilterStore, HostFilterStore>();
        // Explizite Fabrik statt AddSingleton<T>(): Der Standardcontainer fuellt
        // optionale Konstruktorparameter nicht mit ihrem Default, sondern wirft,
        // wenn er sie nicht aufloesen kann — und ohne Datenbank ist
        // CentralFilterService gar nicht registriert.
        services.AddSingleton(sp => new HostFilterCollection(
            sp.GetRequiredService<IHostFilterStore>(),
            sp.GetRequiredService<IConnectionSettingsStore>(),
            sp.GetRequiredService<ViewerMode>(),
            sp.GetService<CentralFilterService>()));
        services.AddSingleton<IStatusViewStateStore, StatusViewStateStore>();
        services.AddSingleton<IColumnLayoutStore, ColumnLayoutStore>();
        services.AddSingleton<CheckmkWebLinker>();

        // ---- Zentrale Datenbank (CheckMK_Copilot auf FOC-SQL01) ----
        // Optional: ohne Verbindungsangabe laeuft das Cockpit weiter, dann aus
        // dem lokalen Ausfall-Cache bzw. mit eingebauten Vorgaben. Die
        // Verfuegbarkeit des Fileshares war der Grund fuer den Umzug — die
        // Datenbank darf nicht der naechste Engpass werden.
        var connectionString = DatabaseConnection.Resolve(
            Bootstrap.LoadOrCreate().DatabaseConnectionString);
        var cockpitDb = connectionString is null ? null : new CockpitDatabase(connectionString);
        if (cockpitDb is not null) services.AddSingleton(cockpitDb);

        services.AddSingleton<IGlobalSettingsProvider>(_ =>
            new GlobalSettingsProvider(cockpitDb, DatabaseConnection.CachePath));

        if (cockpitDb is not null)
        {
            // Die Datei-Variante bleibt als Quelle fuer die einmalige Uebernahme
            // registriert — danach ist die Tabelle die Wahrheit.
            services.AddSingleton<DbHostDomainStore>(sp =>
                new DbHostDomainStore(cockpitDb, new HostDomainStore()));
            services.AddSingleton<IHostDomainStore>(sp => sp.GetRequiredService<DbHostDomainStore>());
            services.AddSingleton<IAreaStore>(_ => new AreaStore(cockpitDb));
            services.AddSingleton<IFachbereichStore>(_ => new FachbereichStore(cockpitDb));
            services.AddSingleton<IFilterStore>(_ => new FilterStore(cockpitDb));
            services.AddSingleton(sp => new CentralFilterService(
                sp.GetRequiredService<IFilterStore>(),
                sp.GetRequiredService<IFachbereichStore>(),
                DatabaseConnection.FilterCachePath,
                Environment.UserName));
            services.AddSingleton<AreaViewModel>();
            services.AddSingleton<MapTileLoader>();
            services.AddSingleton<PotsdamPlaceImporter>();
        }
        else
        {
            services.AddSingleton<IHostDomainStore, HostDomainStore>();
        }

        services.AddSingleton<IHostOsCache, HostOsCache>();
        services.AddSingleton<IHostLocationTags, HostLocationTagCache>();
        services.AddSingleton<HostContext>();
        services.AddSingleton<ISshCredentialStore, SshCredentialStore>();
        services.AddSingleton<RemoteTools>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<IUpdatePreferences, UpdatePreferences>();
        services.AddSingleton<IUpdateChecker>(sp =>
        {
            var url = sp.GetRequiredService<IGlobalSettingsProvider>().Current.UpdateChannelUrl;
            var prefs = sp.GetRequiredService<IUpdatePreferences>();

            // Der Kanal darf ein Ordner sein (Fileshare) statt einer Adresse.
            // Erkannt wird das an der Schreibweise, nicht an einer zweiten
            // Einstellung — ein Pfad und eine URL sind nicht zu verwechseln,
            // und ein Schalter mehr waere ein Schalter, den jemand falsch setzt.
            if (FileShareUpdateChecker.LooksLikeFolder(url))
                return new FileShareUpdateChecker(url, prefs);

            // Update-Check laeuft ins Internet -> ueber den Firmen-Proxy. Ohne
            // Proxy-Credentials gibt der FortiProxy 407. DefaultCredentials nutzt
            // den angemeldeten Windows-User (Negotiate/NTLM).
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials
            };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            return new GitHubReleasesUpdateChecker(http, url, prefs);
        });

        // Self-Update-Installer: nur unter Windows registriert (Cockpit ist WinExe).
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<UpdateInstaller>();
            services.AddSingleton<PluginUpdateInstaller>();
        }
        services.AddSingleton<PluginUpdateService>();

        services.AddSingleton<StatusViewModel>();
        services.AddSingleton<ConfigViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<SettingsViewModel>();

        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<AboutWindow>();

        // ---- Plugins entdecken und registrieren ----
        // Ordner "plugins/" neben der Exe. Plugins koennen im Register eigene
        // Services im DI-Container registrieren und Contributions (IContextMenu-,
        // ITabContribution) beisteuern. Auf Cockpit-Services zugreifen sollten
        // sie erst zur Laufzeit ueber IPluginContext.Services — der Late-Bind-
        // Wrapper macht den DI-Container nach BuildServiceProvider verfuegbar.
        var appDir = AppContext.BaseDirectory;
        var pluginsDir = Path.Combine(appDir, "plugins");
        var pluginDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kroste", "Checkmk", "plugins");

        var lateBound = new LateBoundServiceProvider();
        var contextFactory = (IPlugin plugin) =>
        {
            var dataDir = Path.Combine(pluginDataRoot, plugin.Metadata.Id);
            Directory.CreateDirectory(dataDir);
            return (IPluginContext)new PluginContext(
                lateBound,
                Services.AppVersion.Display,
                dataDir);
        };

        // Im Viewer-Modus werden Plugins bewusst NICHT geladen: sie steuern eigene
        // Tabs und Kontextmenue-Aktionen bei (z. B. der AgentUpdater mit Admin-
        // Credentials). Das wuerde den Lockdown an der Oberflaeche vorbei aushebeln.
        IReadOnlyList<LoadedPlugin> loadedPlugins = [];
        if (viewerProfile is null)
            loadedPlugins = PluginLoader.DiscoverAndRegister(pluginsDir, services, contextFactory);
        else
            LogManager.GetCurrentClassLogger()
                .Info("Viewer-Modus: Plugin-Ordner {Dir} wird nicht geladen.", pluginsDir);
        services.AddSingleton(loadedPlugins);

        var provider = services.BuildServiceProvider();
        lateBound.SetInner(provider);
        return provider;
    }

    /// <summary>
    /// Provider-Wrapper, den der <c>PluginContext</c> beim Register bekommt.
    /// Der echte DI-Container existiert erst NACH <c>BuildServiceProvider</c>;
    /// bis dahin blockt <c>GetService</c>-Aufruf durch Plugins mit NRE (was ein
    /// Bug im Plugin waere — Register darf keine anderen Services aufloesen).
    /// </summary>
    private sealed class LateBoundServiceProvider : IServiceProvider
    {
        private IServiceProvider? _inner;
        public void SetInner(IServiceProvider inner) => _inner = inner;
        public object? GetService(Type serviceType)
        {
            if (_inner is null)
                throw new InvalidOperationException(
                    "Plugin greift auf Cockpit-Services zu, bevor der DI-Container fertig gebaut wurde. " +
                    "IPluginContext.Services darf im Register(...) nicht benutzt werden.");
            return _inner.GetService(serviceType);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
