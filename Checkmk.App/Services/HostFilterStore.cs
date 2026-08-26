using System.Text.Json;
using System.Text.Json.Serialization;
using Checkmk.App.Models;
using NLog;

namespace Checkmk.App.Services;

public sealed class HostFilterState
{
    public List<HostFilter> Filters { get; set; } = new();
    public string? ActiveFilterName { get; set; }

    /// <summary>
    /// Wurden die beiden Start-Filter für diese Site schon angelegt?
    ///
    /// <b>Merken statt am leeren Bestand erkennen.</b> Sonst kämen „Alle" und
    /// der Filter auf den eigenen Anmeldenamen beim nächsten Start zurück,
    /// sobald jemand sie weggeräumt hat — und ein Filter, der sich nicht
    /// löschen lässt, ist ärgerlicher als einer, der fehlt.
    /// </summary>
    public bool Seeded { get; set; }
}

/// <summary>Root-Dokument: pro Site ein eigenes <see cref="HostFilterState"/>.</summary>
public sealed class HostFilterDoc
{
    /// <summary>Key = Site-Name. Case wird beim Lesen normalisiert (case-insensitive).</summary>
    [JsonPropertyName("Sites")]
    public Dictionary<string, HostFilterState> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IHostFilterStore
{
    HostFilterState Load(string site);
    void Save(string site, HostFilterState state);
    string FilePath { get; }
}

/// <summary>
/// Host-Filter liegen user-lokal (nicht sensibel, deshalb unverschluesselt) unter
/// <c>%APPDATA%\Kroste\Checkmk\filter.json</c>. Pro Site ein eigenes Filter-Set —
/// LHP-Favoriten haben in Schul_IT nichts zu suchen und umgekehrt.
/// </summary>
public sealed class HostFilterStore : IHostFilterStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly IConnectionSettingsStore _settings;

    public string FilePath => _path;

    public HostFilterStore(IConnectionSettingsStore settings)
    {
        _settings = settings;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kroste", "Checkmk");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "filter.json");
    }

    public HostFilterState Load(string site)
    {
        if (string.IsNullOrWhiteSpace(site))
            return new HostFilterState();

        // Immer frisch von disk lesen — kein In-Memory-Cache. Verhindert, dass eine
        // waehrend der Session veraltete _doc-Instanz beim naechsten Site-Wechsel
        // andere Sites "verliert".
        var doc = ReadDocFromDisk();
        return doc.Sites.TryGetValue(site, out var state) ? state : new HostFilterState();
    }

    public void Save(string site, HostFilterState state)
    {
        if (string.IsNullOrWhiteSpace(site))
        {
            Log.Debug("Filter-Save uebersprungen — leerer Site-Name (Cockpit vermutlich noch nicht konfiguriert).");
            return;
        }

        // Read-Modify-Write: die aktuelle Datei laden, nur den Site-Eintrag ersetzen,
        // zurueckschreiben. So bleiben Site-Eintraege, die dieser Prozess in dieser
        // Session gar nicht geladen hat, garantiert erhalten.
        var doc = ReadDocFromDisk();
        doc.Sites[site] = state;

        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(doc, JsonOpts));
            Log.Debug("Filter-Save {Site}: {Count} Filter, Sites in Datei: [{All}]",
                site, state.Filters.Count, string.Join(", ", doc.Sites.Keys));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Host-Filter konnten nicht gespeichert werden: {Path}", _path);
        }
    }

    private HostFilterDoc ReadDocFromDisk()
    {
        if (!File.Exists(_path))
            return NewDoc();

        try
        {
            var raw = File.ReadAllText(_path);
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = jsonDoc.RootElement;

            // Neues Format erkennt man am "Sites"-Objekt.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Sites", out _))
            {
                var deserialized = JsonSerializer.Deserialize<HostFilterDoc>(raw) ?? NewDoc();
                // Der Deserializer wirft den case-insensitive Comparer der Property weg;
                // wir bauen das Dict neu, damit "LHP" == "lhp" beim TryGetValue matcht.
                deserialized.Sites = new Dictionary<string, HostFilterState>(
                    deserialized.Sites, StringComparer.OrdinalIgnoreCase);
                return deserialized;
            }

            // Altformat: die Datei ist selbst der HostFilterState. Als Filter der aktuellen
            // Site interpretieren.
            var legacy = JsonSerializer.Deserialize<HostFilterState>(raw) ?? new HostFilterState();
            var currentSite = _settings.Load().Site;
            var migrated = NewDoc();
            if (!string.IsNullOrWhiteSpace(currentSite))
            {
                migrated.Sites[currentSite] = legacy;
                Log.Info("Host-Filter-Migration: altes Format auf Site '{Site}' abgelegt.", currentSite);
            }
            else
            {
                Log.Warn("Host-Filter-Migration: keine aktive Site — alte Filter bleiben ungenutzt bis zum ersten Site-Save.");
            }
            return migrated;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Host-Filter konnten nicht geladen werden — nutze leere Liste.");
            return NewDoc();
        }
    }

    private static HostFilterDoc NewDoc() => new()
    {
        Sites = new Dictionary<string, HostFilterState>(StringComparer.OrdinalIgnoreCase)
    };
}
