using System.Text;
using System.Text.Json;
using Checkmk.Core;
using NLog;

namespace Checkmk.App.Services;

/// <summary>Persistierbare Verbindungseinstellungen. Secret wird verschluesselt abgelegt.</summary>
public sealed class ConnectionSettings
{
    public string Host { get; set; } = "";
    public string Site { get; set; } = "";
    public string Username { get; set; } = CurrentUser.Name;
    public bool UseHttps { get; set; } = true;
    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>
    /// Windows-/LDAP-Anmeldung (Basic-Auth, empfohlen) vs. klassischer Automation-User
    /// (Bearer). Default ist Bearer aus Backward-Compat mit bestehenden Installs
    /// (deren settings.json das Feld nicht kennt). Bei User-Anmeldung zeigt der
    /// Checkmk-Audit-Log den echten Namen bei Ack/Downtime.
    /// </summary>
    public CheckmkAuthMode AuthMode { get; set; } = CheckmkAuthMode.AutomationBearer;

    /// <summary>
    /// Zusätzliche Sites am selben Checkmk-Server (Host/User/Secret identisch,
    /// nur die Site wechselt). Enthält typischerweise auch die aktuelle
    /// <see cref="Site"/> — wenn nicht, wird sie beim Laden ergänzt. Leer =
    /// kein Umschalter im UI.
    /// </summary>
    public List<string> KnownSites { get; set; } = [];

    /// <summary>Plattformspezifisch verschluesseltes Secret (Base64). Nie im Klartext im JSON.</summary>
    public string? ProtectedSecret { get; set; }

    // AgentShare + AgentUpdateScript wurden ins Plugin ausgegliedert
    // (Checkmk-Plugin-AgentUpdater, seit Cockpit v1.7.0). Alte JSON-Werte
    // werden vom Deserializer ignoriert.

    public CheckmkOptions ToOptions(string plainSecret) => new()
    {
        Host = Host,
        Site = Site,
        Username = Username,
        Secret = plainSecret,
        UseHttps = UseHttps,
        IgnoreCertificateErrors = IgnoreCertificateErrors,
        AuthMode = AuthMode
    };
}

public interface IConnectionSettingsStore
{
    ConnectionSettings Load();
    string? LoadSecret(ConnectionSettings settings);
    void Save(ConnectionSettings settings, string plainSecret);
    bool IsConfigured(ConnectionSettings settings);
    string SettingsFilePath { get; }

    /// <summary>Wechselt nur die aktive Site (Site-Umschalter). Secret bleibt unangetastet.</summary>
    void UpdateActiveSite(string newSite);
}

/// <summary>
/// Speichert die Verbindungskonfiguration user-lokal unter
/// <c>%APPDATA%\Kroste\Checkmk\settings.json</c> — verschluesselt mit
/// <see cref="WindowsDpapiProtector"/> (CurrentUser-Scope). Pfad ist per
/// <c>bootstrap.json</c> ueberschreibbar. Frueher zentral auf dem Samba-
/// Share; zurueckverlegt weil die Verbindungsdaten pro Nutzer gehoeren.
/// </summary>
public sealed class ConnectionSettingsStore : IConnectionSettingsStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly ISecretProtector _protector;
    private readonly string _path;

    public string SettingsFilePath => _path;

    public ConnectionSettingsStore(ISecretProtector protector)
    {
        _protector = protector;
        _path = ResolvePath();
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Zielverzeichnis konnte nicht erstellt werden: {Path}", _path);
        }
        Log.Info("Verbindungseinstellungen liegen unter {Path}", _path);
    }

    public ConnectionSettings Load()
    {
        if (!File.Exists(_path))
            return new ConnectionSettings();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<ConnectionSettings>(json) ?? new ConnectionSettings();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Einstellungen konnten nicht geladen werden — nutze Defaults.");
            return new ConnectionSettings();
        }
    }

    public string? LoadSecret(ConnectionSettings settings)
    {
        if (string.IsNullOrEmpty(settings.ProtectedSecret))
            return null;
        try
        {
            var blob = Convert.FromBase64String(settings.ProtectedSecret);
            return Encoding.UTF8.GetString(_protector.Unprotect(blob));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Secret konnte nicht entschluesselt werden (evtl. anderer User/Rechner).");
            return null;
        }
    }

    /// <summary>
    /// Schreibt die Einstellungen. Wirft weiter, wenn das Ziel nicht beschreibbar
    /// ist — der Aufrufer muss das anzeigen, <b>nicht</b> die App sterben lassen
    /// (siehe <c>SettingsViewModel.Save</c>).
    /// </summary>
    public void Save(ConnectionSettings settings, string plainSecret)
    {
        settings.ProtectedSecret = string.IsNullOrEmpty(plainSecret)
            ? null
            : Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(plainSecret)));

        // Verzeichnis hier nochmal anlegen: der Versuch im Ctor kann fehlgeschlagen
        // sein (Pfad kam damals aus einer kaputten bootstrap.json) oder das
        // Verzeichnis wurde zwischenzeitlich geloescht. Ohne das gibt es beim
        // Schreiben eine DirectoryNotFoundException.
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
        Log.Info("Verbindungseinstellungen gespeichert nach {Path}", _path);
    }

    public bool IsConfigured(ConnectionSettings s)
        => !string.IsNullOrWhiteSpace(s.Host)
           && !string.IsNullOrWhiteSpace(s.Site)
           && !string.IsNullOrWhiteSpace(s.Username)
           && !string.IsNullOrEmpty(s.ProtectedSecret);

    public void UpdateActiveSite(string newSite)
    {
        if (string.IsNullOrWhiteSpace(newSite)) return;
        var settings = Load();
        if (string.Equals(settings.Site, newSite, StringComparison.Ordinal)) return;

        settings.Site = newSite;
        // ProtectedSecret bleibt drin — wir serialisieren das Settings-Objekt direkt,
        // *ohne* Save() zu benutzen (das erwartet plainSecret und wuerde die Verschluesselung
        // rotieren).
        var json = JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
        Log.Info("Site auf '{Site}' umgeschaltet.", newSite);
    }

    private static string ResolvePath() => Bootstrap.LoadOrCreate().ResolvedSettingsPath;
}

/// <summary>
/// Bootstrap-Datei im Userspace — enthaelt nur den Pfad zur zentralen Verbindungsdatei auf dem
/// Windows-Fileshare. Wird beim ersten Start mit dem Default belegt und kann von Hand editiert
/// werden, falls sich der Fileserver-Pfad aendert. Bewusst kein UI dafuer: der Default ist die
/// Konvention, Abweichungen sind Sonderfall.
/// </summary>
internal sealed class Bootstrap
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly string DefaultLocalSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kroste", "Checkmk", "settings.json");

    // Alter Default aus v1.0-v1.4 — wenn der noch in bootstrap.json steht, migrieren
    // wir bei LoadOrCreate() automatisch auf den neuen lokalen Default.
    private const string LegacySambaSettingsPath =
        @"\\Samba01\542$\5424_IT-Basis-Dienste\_Oste\CheckMK\settings.json";

    private const string DefaultSharedHostsPath = @"\\Samba01\542$\5424_IT-Basis-Dienste\_Oste\CheckMK\hosts.json";
    private const string DefaultUpdateChannelUrl =
        "https://api.github.com/repos/LHP542/Checkmk/releases/latest";
    private const string DefaultDomain = "lhp.intern";

    /// <summary>
    /// Pfad zur Verbindungsdatei. <b>Leer = user-lokal</b> (%APPDATA%), und das ist
    /// der Default. Bewusst kein aufgeloester Pfad: die Bootstrap-Datei wird zentral
    /// geteilt, ein absoluter Profilpfad wuerde also allen anderen Nutzern das
    /// Profil eines Einzelnen unterschieben. Genau das ist passiert — in der
    /// zentralen Datei stand <c>C:\Users\OsteL\AppData\Roaming\…</c>, und jeder
    /// andere bekam beim Speichern eine DirectoryNotFoundException.
    /// Umgebungsvariablen (<c>%APPDATA%</c>) werden beim Aufloesen expandiert.
    /// </summary>
    public string SharedSettingsPath { get; set; } = "";

    /// <summary>Der tatsaechlich zu benutzende Pfad — siehe <see cref="SettingsPathResolver"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResolvedSettingsPath => SettingsPathResolver.Resolve(
        SharedSettingsPath,
        DefaultLocalSettingsPath,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Verbindung zur zentralen Datenbank (<c>CheckMK_Copilot</c> auf FOC-SQL01).
    /// Leer = kein zentraler Betrieb; das Cockpit laeuft dann mit lokalem Cache
    /// bzw. eingebauten Vorgaben weiter.
    ///
    /// Zur Ehrlichkeit: Der ausgelieferte String ist <b>Verschleierung, kein
    /// Zugriffsschutz</b> — er liegt auf ~50 Arbeitsplaetzen. Die wirksame
    /// Grenze ist das Recht des Laufzeitkontos (datareader/datawriter, kein
    /// db_owner), siehe db/README.md.
    /// </summary>
    public string DatabaseConnectionString { get; set; } = "";

    /// <summary>Zentrale, unverschluesselte Host-Metadaten-Datei (Domain je Host).
    /// <b>Nur noch fuer die einmalige Uebernahme in die Datenbank</b> — die
    /// Zuordnung lebt seit v1.9 in der Tabelle <c>HostDomain</c>.</summary>
    public string SharedHostsPath { get; set; } = DefaultSharedHostsPath;

    /// <summary>Default-Domain fuer Hosts ohne explizite Zuordnung. Wird an den
    /// Hostnamen angehaengt, wenn Ping/RDP/SSH einen FQDN brauchen.</summary>
    public string HostDefaultDomain { get; set; } = DefaultDomain;

    public string UpdateChannelUrl { get; set; } = DefaultUpdateChannelUrl;

    /// <summary>
    /// Interne Attribut-Keys, unter denen die OS-Familie im Host-Config-Dict
    /// gesucht wird (Custom Host Attribute oder Host-Tag). Erster Treffer gewinnt.
    /// Wenn dein Attribut anders heisst, hier den Key ergaenzen — die App logged
    /// bei jedem Refresh die tatsaechlich gesehenen Keys unter Debug.
    /// </summary>
    public List<string> HostOsAttributeKeys { get; set; } =
    [
        "tag_operation_system",
        "operation_system",
        "operating_system",
        "os_family"
    ];

    /// <summary>
    /// Blendet das „Host anlegen"-Formular im Konfig-Tab ein. Default false —
    /// bewusst versteckt, weil Setup-Handgriffe im Fachbereich zentral erfolgen und
    /// eine Fehlbedienung Config-Aenderungen produziert. Bei Bedarf per JSON auf true
    /// setzen (kein UI-Schalter).
    /// </summary>
    public bool ShowHostCreation { get; set; }

    // App-Konfiguration wird zentral geteilt — im Idealfall ein Wert pro Feld,
    // alle Cockpit-User profitieren. User-Secrets (settings.json, ssh-creds)
    // liegen weiterhin pro Nutzer.
    private const string CentralBootstrapPath =
        @"\\Samba01\542$\5424_IT-Basis-Dienste\_Oste\CheckMK\bootstrap.json";

    private static string LocalBootstrapPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kroste", "Checkmk", "bootstrap.json");

    public static Bootstrap LoadOrCreate()
    {
        // 1) Zentraler Pfad hat Vorrang.
        if (TryLoad(CentralBootstrapPath, out var central))
        {
            NormalizeAndPatchInPlace(central, CentralBootstrapPath);
            return central;
        }

        // 2) Sonst versuchen wir den lokalen Legacy-Pfad — und migrieren einmalig
        //    nach zentral, damit alle User denselben Konfigstand haben.
        if (TryLoad(LocalBootstrapPath, out var local))
        {
            NormalizeAndPatchInPlace(local, LocalBootstrapPath);
            TryMigrateToCentral(local);
            return local;
        }

        // 3) Nichts vorhanden -> Default schreiben (bevorzugt zentral, Fallback lokal).
        var b = new Bootstrap();
        if (!TryWrite(CentralBootstrapPath, b))
            TryWrite(LocalBootstrapPath, b);
        return b;
    }

    private static bool TryLoad(string path, out Bootstrap result)
    {
        result = null!;
        try
        {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<Bootstrap>(json);
            // Bewusst KEINE Pruefung auf einen gesetzten SharedSettingsPath mehr:
            // leer ist der gueltige Normalfall (= user-lokal). Frueher galt die
            // Datei dadurch als kaputt und wurde mit einem aufgeloesten Profilpfad
            // ueberschrieben — genau so kam der fremde Pfad in die zentrale Datei.
            if (loaded is null)
                return false;
            result = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void NormalizeAndPatchInPlace(Bootstrap loaded, string sourcePath)
    {
        var dirty = false;

        // Wer aus v1.0-v1.4 upgradet hat noch den Samba-Pfad in SharedSettingsPath drin.
        // Anmeldedaten gehoeren pro Nutzer -> auf "leer" = user-lokal zuruecksetzen.
        if (string.Equals(loaded.SharedSettingsPath,
                LegacySambaSettingsPath, StringComparison.OrdinalIgnoreCase))
        {
            loaded.SharedSettingsPath = "";
            dirty = true;
        }

        // Selbstheilung: steht in der (zentral geteilten!) Datei der Profilpfad
        // eines anderen Nutzers, ist das fuer alle ausser diesem einen kaputt.
        // Auf "leer" zuruecksetzen, damit jeder wieder sein eigenes %APPDATA% nimmt.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (SettingsPathResolver.PointsIntoForeignUserProfile(loaded.SharedSettingsPath, userProfile))
        {
            Log.Warn("bootstrap.json {Source}: SharedSettingsPath '{Path}' zeigt in ein fremdes "
                   + "Benutzerprofil — wird auf user-lokal zurueckgesetzt.",
                sourcePath, loaded.SharedSettingsPath);
            loaded.SharedSettingsPath = "";
            dirty = true;
        }

        // Ein aufgeloester eigener Profilpfad darf ebenfalls nicht stehenbleiben:
        // sobald die Datei zentral migriert wird, erbt ihn der naechste Nutzer.
        if (string.Equals(loaded.SharedSettingsPath, DefaultLocalSettingsPath,
                StringComparison.OrdinalIgnoreCase))
        {
            loaded.SharedSettingsPath = "";
            dirty = true;
        }

        // Neue Properties nachziehen, falls die Datei aus einer aelteren Version stammt
        // (JSON hatte das Feld nicht -> Deserializer liess es null/leer).
        if (loaded.HostOsAttributeKeys is null || loaded.HostOsAttributeKeys.Count == 0)
        {
            loaded.HostOsAttributeKeys = new Bootstrap().HostOsAttributeKeys;
            dirty = true;
        }

        if (dirty)
        {
            try
            {
                File.WriteAllText(sourcePath, JsonSerializer.Serialize(loaded,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* Best-effort */ }
        }
    }

    private static void TryMigrateToCentral(Bootstrap b)
    {
        try
        {
            var dir = Path.GetDirectoryName(CentralBootstrapPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // Nur schreiben wenn zentral wirklich noch nicht existiert — sonst haben
            // wir vielleicht gerade eine neuere zentrale Version ueberholt.
            if (!File.Exists(CentralBootstrapPath))
            {
                File.WriteAllText(CentralBootstrapPath, JsonSerializer.Serialize(b,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { /* Migration ist best-effort; lokale Datei bleibt Fallback */ }
    }

    private static bool TryWrite(string path, Bootstrap b)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(b,
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
