using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Checkmk.Core;
using NLog;

namespace Checkmk.App.Services;

/// <summary>
/// Verbindungsdaten aus dem Viewer-Profil.
/// <para>
/// <b>Das Secret ist hier bestenfalls maskiert, nicht geschuetzt.</b>
/// <see cref="SecretBase64"/> haelt Base64 statt Klartext — das verhindert das
/// Mitlesen beim Ueberfliegen der Datei, ist aber trivial umkehrbar und
/// ausdruecklich <b>keine</b> Verschluesselung. Echte Verschluesselung ist hier
/// nicht moeglich: DPAPI ist an den angemeldeten Windows-User gebunden, die Datei
/// wird aber mit der Exe an viele Nutzer verteilt, und ein Key im Binary waere
/// genau der SharedAes-Trick aus CLAUDE.md §8.20.
/// </para>
/// <para>
/// <b>Die einzige echte Grenze ist die Checkmk-Rolle des hier hinterlegten Users.</b>
/// Der Automation-User in dieser Datei muss eine reine Lese-Rolle haben. Die
/// UI-Sperren des Viewer-Modus sind Bedienkomfort, kein Zugriffsschutz.
/// </para>
/// </summary>
public sealed class ViewerConnection
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Strikt: ungueltige UTF-8-Bytes werfen, statt still
    /// U+FFFD-Ersatzzeichen zu liefern. Sonst wuerde ein versehentlich in
    /// <see cref="SecretBase64"/> gepasteter Klartext (der zufaellig gueltiges
    /// Base64 ist) klaglos zu Muell dekodiert und die Anmeldung schluege mit
    /// „401 Wrong credentials" fehl — ohne Hinweis auf die Ursache.</summary>
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string Host { get; set; } = "";
    public string Site { get; set; } = "";
    public string Username { get; set; } = "";

    /// <summary>Klartext-Secret. Funktioniert weiterhin, aber
    /// <see cref="SecretBase64"/> ist die empfohlene Schreibweise.</summary>
    public string Secret { get; set; } = "";

    /// <summary>Base64-kodiertes Secret (UTF-8). Hat Vorrang vor
    /// <see cref="Secret"/>. Erzeugen mit:
    /// <c>[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('DAS-SECRET'))</c></summary>
    public string SecretBase64 { get; set; } = "";

    public bool UseHttps { get; set; } = true;
    public bool IgnoreCertificateErrors { get; set; }
    public CheckmkAuthMode AuthMode { get; set; } = CheckmkAuthMode.AutomationBearer;

    /// <summary>Gesetzt, wenn <see cref="SecretBase64"/> nicht dekodierbar war —
    /// wird als <c>LoadError</c> ans Profil durchgereicht.</summary>
    [JsonIgnore]
    public string? SecretError { get; private set; }

    /// <summary>Das tatsaechlich zu verwendende Secret im Klartext.
    /// Wird von <see cref="ViewerProfile"/> nach dem Laden einmal aufgeloest.</summary>
    [JsonIgnore]
    public string ResolvedSecret { get; private set; } = "";

    [JsonIgnore]
    public bool IsComplete
        => !string.IsNullOrWhiteSpace(Host)
           && !string.IsNullOrWhiteSpace(Site)
           && !string.IsNullOrWhiteSpace(Username)
           && !string.IsNullOrEmpty(ResolvedSecret);

    /// <summary>
    /// Loest <see cref="ResolvedSecret"/> aus Base64 bzw. Klartext auf. Einmal nach
    /// dem Deserialisieren aufrufen — danach fasst niemand mehr die Rohfelder an.
    /// </summary>
    internal void ResolveSecret()
    {
        SecretError = null;

        if (string.IsNullOrWhiteSpace(SecretBase64))
        {
            ResolvedSecret = Secret;
            return;
        }

        if (!string.IsNullOrEmpty(Secret))
            Log.Warn("Viewer-Profil: 'secret' und 'secretBase64' sind beide gesetzt — "
                   + "'secretBase64' gewinnt, 'secret' wird ignoriert.");

        try
        {
            ResolvedSecret = StrictUtf8.GetString(Convert.FromBase64String(SecretBase64.Trim()));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException
                                      or ArgumentException)
        {
            ResolvedSecret = "";
            SecretError = "'secretBase64' ist kein gültiges Base64 eines UTF-8-Textes. "
                        + "Erzeugen mit: "
                        + "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('DAS-SECRET'))";
            // Bewusst ohne den Wert im Log — auch ein kaputtes Secret ist ein Secret.
            Log.Error("Viewer-Profil: {Error}", SecretError);
        }
    }
}

/// <summary>
/// Voreingestellte Sicht. Die Werte sind <b>Startwerte</b>, keine Sperre — der
/// Nutzer darf umschalten. Sie werden bewusst nicht nach
/// <c>%APPDATA%\Kroste\Checkmk\statusview.json</c> zurueckgeschrieben, damit jeder
/// Start wieder mit der vorgesehenen Sicht beginnt.
/// </summary>
public sealed class ViewerView
{
    /// <summary>Host-Regex fuer den vorgewaehlten Favoriten (case-insensitive).
    /// Wird ignoriert, wenn <see cref="IncludeHosts"/> gefuellt ist.</summary>
    public string? HostRegex { get; set; }

    /// <summary>Explizite Host-Liste fuer den vorgewaehlten Favoriten. Hat Vorrang
    /// vor <see cref="HostRegex"/> — gleiche Regel wie bei den normalen Favoriten.</summary>
    public List<string> IncludeHosts { get; set; } = [];

    /// <summary>
    /// <see cref="HostRegex"/> gegen den Host-<b>Alias</b> pruefen statt gegen
    /// den Hostnamen. Default false — bestehende Profile verhalten sich damit
    /// unveraendert.
    /// </summary>
    public bool MatchAlias { get; set; }

    /// <summary>Name, unter dem der vorgewaehlte Filter in der ComboBox erscheint.</summary>
    public string FilterName { get; set; } = "Vorgabe";

    public string FilterText { get; set; } = "";
    public bool OnlyProblems { get; set; } = true;
    public bool OnlyOpen { get; set; }
    public bool AutoRefresh { get; set; } = true;
    public int RefreshSeconds { get; set; } = 60;
    public bool TreeView { get; set; }

    /// <summary>
    /// Baut den vorgegebenen Host-Filter. Liefert <b>immer</b> einen Filter, auch
    /// wenn weder Regex noch Liste gesetzt sind — dann matcht er alle Hosts. Genau
    /// so ist es gewollt: im Viewer-Modus muss immer ein Filter aus dem Profil aktiv
    /// sein, sonst bliebe der zuletzt aktive aus der persoenlichen <c>filter.json</c>
    /// stehen und wuerde die Vorgabe ueberstimmen.
    /// </summary>
    public Models.HostFilter ToHostFilter() => new()
    {
        Name = string.IsNullOrWhiteSpace(FilterName) ? "Vorgabe" : FilterName.Trim(),
        // Leerstring -> null: ein leerer Regex ist kein Filter, sondern „alles".
        HostNameRegex = string.IsNullOrWhiteSpace(HostRegex) ? null : HostRegex.Trim(),
        Target = MatchAlias ? Models.FilterTarget.Alias : Models.FilterTarget.HostName,
        ExplicitHosts = [.. IncludeHosts.Where(h => !string.IsNullOrWhiteSpace(h))
                                        .Select(h => h.Trim())]
    };
}

/// <summary>
/// Kiosk-Karte. Steht <see cref="Show"/> auf true, bleibt der Bereiche-Tab im
/// Viewer-Modus erhalten — <b>lesend</b>: Alle Schreibknöpfe hängen ohnehin an
/// <c>CanWrite</c> und sind damit weg.
///
/// Gedacht für den Bildschirm im Leitstand oder beim Wachschutz: eine Stadtkarte,
/// auf der ein Standort grün, gelb oder rot ist. Welche Hosts dabei zählen,
/// entscheidet weiterhin allein der Filter aus <c>view</c> — derselbe Raum ist für
/// das DB-Team grün und für den Wachschutz rot.
/// </summary>
public sealed class ViewerMap
{
    /// <summary>Bereiche-Tab im Viewer-Modus zeigen. Default false — ohne
    /// ausdrückliche Angabe bleibt der Kiosk beim reinen Status-Tab.</summary>
    public bool Show { get; set; }

    /// <summary>
    /// Name des Startbereichs. Die Karte springt beim Start dorthin. Leer =
    /// Gesamtübersicht.
    ///
    /// Bewusst der <b>Name</b> und keine Id: Das Profil wird von Hand
    /// geschrieben, und eine Id steht nirgends, wo ein Mensch sie ablesen könnte.
    /// </summary>
    public string? Area { get; set; }

    /// <summary>Zoomstufe für den Start. 0 = automatisch (Fläche einpassen bzw.
    /// Stadtübersicht).</summary>
    public double Zoom { get; set; }

    /// <summary>Kartenhintergrund per Name aus <c>GlobalSetting.MapLayers</c>.
    /// Leer = erster Eintrag.</summary>
    public string? Layer { get; set; }

    /// <summary>Bereichsbaum links zeigen. Für eine reine Kartenwand auf false —
    /// dann bleibt die volle Breite für die Karte.</summary>
    public bool Tree { get; set; } = true;
}

/// <summary>
/// Optionales Profil in <c>viewer.json</c> <b>neben der Exe</b>. Liegt die Datei da,
/// laeuft das Cockpit im Viewer-Modus: Verbindung, Spalten und Start-Filter kommen
/// aus der Datei, und alles Schreibende ist aus der Oberflaeche entfernt (nur der
/// Status-Tab bleibt). Fehlt die Datei, verhaelt sich die App unveraendert wie bisher.
/// </summary>
public sealed class ViewerProfile
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public const string FileName = "viewer.json";

    /// <summary>Spaltenschluessel, die <see cref="StatusColumnFactory"/> kennt.
    /// Reihenfolge in der Config = Reihenfolge im Grid.</summary>
    public static readonly string[] DefaultColumns =
    [
        "state_dot", "host", "service_display_name", "service_description",
        "service_state", "svc_check_age", "svc_state_age"
    ];

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Fenstertitel. Damit sieht der Anwender, welche Sicht er vor sich hat.</summary>
    public string Title { get; set; } = "Checkmk Cockpit";

    public ViewerConnection Connection { get; set; } = new();

    /// <summary>Sichtbare Spalten in Reihenfolge. Leer => <see cref="DefaultColumns"/>.</summary>
    public List<string> Columns { get; set; } = [];

    public ViewerView View { get; set; } = new();

    /// <summary>Kiosk-Karte. Ohne Angabe bleibt der Bereiche-Tab weg.</summary>
    public ViewerMap Map { get; set; } = new();

    /// <summary>
    /// Holt das Fenster bei einer Verschlechterung nach vorn (maximiert) und springt
    /// auf den betroffenen Service — zusaetzlich zur Toast-Benachrichtigung. Gedacht
    /// fuer Ausgaben, die dauerhaft auf einem Bildschirm laufen (Wachschutz, Leitstand)
    /// und wo eine Meldung nicht uebersehen werden darf.
    /// <para>
    /// Reine Recoveries loesen das <b>nicht</b> aus — sonst springt das Fenster auch
    /// dann auf, wenn sich gerade etwas erholt. Ein aktiver Snooze unterdrueckt es
    /// wie den Toast auch.
    /// </para>
    /// </summary>
    public bool PopUpOnProblem { get; set; } = true;

    /// <summary>Voller Pfad der geladenen Datei (fuer Meldungen und die Über-Box).</summary>
    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    /// <summary>Gesetzt, wenn die Datei zwar da, aber nicht lesbar/parsebar war.
    /// Der Viewer-Modus bleibt trotzdem aktiv — siehe <see cref="LoadOrNull"/>.</summary>
    [JsonIgnore]
    public string? LoadError { get; private set; }

    /// <summary>
    /// Laedt <c>viewer.json</c> neben der Exe. Gibt <c>null</c> zurueck, wenn die Datei
    /// <b>nicht existiert</b> — nur dann laeuft die App im normalen Vollmodus.
    /// <para>
    /// Ist die Datei vorhanden aber kaputt, kommt ein Profil mit gesetztem
    /// <see cref="LoadError"/> zurueck. Das ist Absicht: ein Tippfehler im JSON darf
    /// niemals dazu fuehren, dass ein Nutzer, der nur gucken soll, ploetzlich die
    /// volle Oberflaeche samt Einstellungen und Schreibaktionen bekommt.
    /// </para>
    /// </summary>
    public static ViewerProfile? LoadOrNull()
        => LoadFrom(Path.Combine(AppContext.BaseDirectory, FileName));

    /// <summary>Wie <see cref="LoadOrNull"/>, aber mit explizitem Pfad — fuer Tests
    /// und fuer den Fall, dass die Datei mal woanders liegen soll.</summary>
    public static ViewerProfile? LoadFrom(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var profile = JsonSerializer.Deserialize<ViewerProfile>(File.ReadAllText(path), Opts)
                          ?? new ViewerProfile();
            profile.FilePath = path;
            profile.NormalizeColumns();
            profile.Connection.ResolveSecret();

            if (profile.Connection.SecretError is { } secretError)
            {
                // Eigene Meldung, weil „unvollstaendig" hier in die Irre fuehrt:
                // das Secret IST da, es ist nur falsch kodiert.
                profile.LoadError = secretError;
                Log.Warn("Viewer-Profil {Path}: {Error}", path, secretError);
            }
            else if (!profile.Connection.IsComplete)
            {
                profile.LoadError = "Verbindungsdaten unvollstaendig (host, site, username "
                                  + "und secret bzw. secretBase64 muessen gesetzt sein).";
                Log.Warn("Viewer-Profil {Path}: {Error}", path, profile.LoadError);
            }
            else
            {
                Log.Info("Viewer-Modus aktiv — Profil {Path}, {Count} Spalten, Site {Site}.",
                    path, profile.Columns.Count, profile.Connection.Site);
            }

            return profile;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Viewer-Profil {Path} konnte nicht gelesen werden — "
                        + "Viewer-Modus bleibt aktiv, aber ohne Verbindung.", path);
            return new ViewerProfile
            {
                FilePath = path,
                LoadError = $"{FileName} konnte nicht gelesen werden: {ex.Message}"
            };
        }
    }

    /// <summary>Unbekannte Spaltenschluessel aussortieren (mit Log, damit ein Tippfehler
    /// auffindbar ist) und auf die Defaults zurueckfallen, wenn nichts uebrig bleibt.</summary>
    private void NormalizeColumns()
    {
        var known = new List<string>();
        foreach (var key in Columns)
        {
            var trimmed = key?.Trim() ?? "";
            if (trimmed.Length == 0)
                continue;
            if (StatusColumnFactory.IsKnown(trimmed))
                known.Add(trimmed);
            else
                Log.Warn("Viewer-Profil: unbekannte Spalte '{Key}' — ignoriert. Bekannt: {Known}",
                    trimmed, string.Join(", ", StatusColumnFactory.KnownKeys));
        }

        Columns = known.Count > 0 ? known : [.. DefaultColumns];
    }
}

/// <summary>
/// Prozessweiter Schalter „laeuft das Cockpit im Viewer-Modus?". Wird immer
/// registriert (auch ohne Profil), damit ViewModels und Views einfach dagegen
/// binden koennen, statt ueberall auf null zu pruefen.
/// </summary>
public sealed class ViewerMode
{
    public ViewerMode(ViewerProfile? profile) => Profile = profile;

    public ViewerProfile? Profile { get; }

    /// <summary>true, wenn <c>viewer.json</c> neben der Exe liegt.</summary>
    public bool IsActive => Profile is not null;

    /// <summary>false im Viewer-Modus — steuert die Sichtbarkeit aller Aktionen,
    /// die in Checkmk schreiben (Ack, Downtime, Kommentar, Discovery, Host anlegen).</summary>
    public bool CanWrite => Profile is null;

    /// <summary>
    /// Bleibt der Bereiche-Tab im Viewer-Modus erhalten? Nur, wenn das Profil es
    /// ausdrücklich verlangt — sonst bekäme jede bestehende Kiosk-Ausgabe beim
    /// Update ungefragt einen neuen Tab.
    /// </summary>
    public ViewerMap? Map => Profile is { Map.Show: true } p ? p.Map : null;
}
