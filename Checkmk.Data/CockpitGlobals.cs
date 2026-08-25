using System.Text.Json;

namespace Checkmk.Data;

/// <summary>
/// Die geteilten Vorgaben, die bisher in <c>bootstrap.json</c> auf dem
/// Fileshare standen. In <c>bootstrap.json</c> bleibt nur noch, wo die
/// Datenbank steht — den Rest holt sich jeder Client hier.
///
/// Nicht enthalten und niemals hier: das Verbindungs-Secret und die
/// SSH-Passwoerter. Die bleiben user-lokal und DPAPI-gebunden.
/// </summary>
/// <summary>
/// Ein auswaehlbarer Kartenhintergrund.
/// </summary>
/// <param name="Layer">WMS-Layername; mehrere kommagetrennt (ALKIS braucht das,
/// dort ist die Karte auf Fachthemen aufgeteilt).</param>
/// <param name="Crs">Koordinatensystem des Dienstes. Vorgabe Web-Mercator wie
/// bei jeder Kachelkarte — <b>aber nicht jeder Dienst kann das</b>. Der
/// Kartenserver der Landeshauptstadt Potsdam etwa spricht nur EPSG:4326 und
/// EPSG:25833; deshalb ist das ein Feld und keine Konstante.</param>
public sealed record MapLayerDefinition(
    string Name,
    string Url,
    string Layer,
    string Crs = "EPSG:3857");

public sealed class CockpitGlobals
{
    public const string KeyHostDefaultDomain   = "HostDefaultDomain";
    public const string KeyUpdateChannelUrl    = "UpdateChannelUrl";
    public const string KeyHostOsAttributeKeys = "HostOsAttributeKeys";
    public const string KeyHostLocationTagKeys = "HostLocationTagKeys";
    public const string KeyShowHostCreation    = "ShowHostCreation";
    public const string KeyShowHostsTab        = "ShowHostsTab";
    public const string KeyMapWmsUrl           = "MapWmsUrl";
    public const string KeyMapWmsLayer         = "MapWmsLayer";
    public const string KeyMapAttribution      = "MapAttribution";
    public const string KeyMapLayers           = "MapLayers";
    public const string KeyMapTileSharePath    = "MapTileSharePath";
    public const string KeyMapTileMaxAgeDays   = "MapTileMaxAgeDays";

    public string HostDefaultDomain { get; init; } = "lhp.intern";

    public string UpdateChannelUrl { get; init; } =
        "https://api.github.com/repos/LHP542/Checkmk/releases/latest";

    /// <summary>Kandidaten-Keys fuer die OS-Familie im Host-Config-Dict, erster
    /// Treffer gewinnt.</summary>
    public IReadOnlyList<string> HostOsAttributeKeys { get; init; } =
    [
        "tag_operation_system",
        "operation_system",
        "operating_system",
        "os_family"
    ];

    /// <summary>
    /// Kandidaten-Keys fuer den Ortstag eines Hosts, erster Treffer gewinnt.
    /// Die Reihenfolge ist damit die Rangfolge: Ein Host mit
    /// <c>tag_location_school</c> <b>und</b> <c>tag_location</c> zaehlt als Schule.
    ///
    /// Vorgabe aus dem Bestand (2026-08-21): Auf der Site <c>schul_it</c> tragen
    /// 553 von 654 Hosts <c>tag_location_school</c> mit Werten wie
    /// <c>schule_46</c>; <c>tag_location_filiale</c> kommt dort auf 5 Hosts vor.
    /// Auf <c>LHP</c> ist <c>tag_location</c> mit 9 von 1438 Hosts praktisch
    /// ungenutzt — dort traegt der Hostname die Information.
    /// </summary>
    public IReadOnlyList<string> HostLocationTagKeys { get; init; } =
    [
        "tag_location_school",
        "tag_location_filiale",
        "tag_location"
    ];

    /// <summary>Blendet das „Host anlegen"-Formular ein. Default false.</summary>
    public bool ShowHostCreation { get; init; }

    /// <summary>
    /// Blendet den Hosts-Tab ein. <b>Default false</b> — die Setup-Handgriffe
    /// laufen zentral, und im Alltag braucht ihn niemand. Wer Service-Discovery
    /// oder „Aenderungen aktivieren" doch benoetigt, setzt die Zeile in
    /// GlobalSetting auf true; ein neues Binary ist dafuer nicht noetig.
    ///
    /// Die Host-Attribute (OS-Familie, Ortstags) laedt seit v1.18.0 der
    /// HostFactsLoader beim Start — sie haengen nicht mehr daran, dass jemand
    /// diesen Tab oeffnet.
    /// </summary>
    public bool ShowHostsTab { get; init; }

    /// <summary>
    /// WMS-Basisadresse der Kartenkacheln. Vorgabe sind die Digitalen
    /// Orthophotos 20 cm der LGB Brandenburg (Open Data, dl-de/by-2.0).
    ///
    /// Bewusst der <b>WMS</b>-Endpunkt und nicht WMTS: Das Matrix-Set
    /// <c>grid_3857</c> der LGB hat einen auf Brandenburg beschraenkten
    /// Ursprung und weist globale Slippy-Map-Kachelindizes mit
    /// <c>TileOutOfRange</c> ab. Ueber <c>GetMap</c> gibt der Client die
    /// Bounding-Box selbst vor — die rechnet <c>WebMercator</c> ohnehin aus,
    /// und MapProxy liefert trotzdem aus seinem Kachel-Cache.
    /// </summary>
    public string MapWmsUrl { get; init; } =
        "https://isk.geobasis-bb.de/mapproxy/dop20c/service/wms";

    public string MapWmsLayer { get; init; } = "bebb_dop20c";

    /// <summary>Quellenvermerk. <b>Pflicht</b> nach dl-de/by-2.0 und deshalb
    /// fest im Kartenbild, nicht in einem Menue vergraben.</summary>
    public string MapAttribution { get; init; } = "© GeoBasis-DE/LGB, dl-de/by-2-0";

    /// <summary>
    /// Gemeinsamer Kachelspeicher, z. B. ein Ordner auf dem Fileshare. Wird
    /// <b>vor</b> dem Kartendienst gelesen und, wenn schreibbar, mitgefuellt.
    ///
    /// Sinn: Eine kalte Kachel kostet gut eine Sekunde, aus dem Cache acht
    /// Millisekunden. Ohne gemeinsamen Speicher zahlt jeder der 48 Nutzer
    /// dieselbe Wartezeit noch einmal und laedt dieselben ~200 MB erneut vom
    /// Landesdienst. Mit ihm zahlt der Erste, alle anderen lesen.
    ///
    /// Leer = nur lokaler Cache. Nicht erreichbar = still uebergangen; die
    /// Karte faellt auf den lokalen Cache und den Dienst zurueck. Kacheln sind
    /// Beiwerk, ihr Ausfall darf nichts blockieren.
    /// </summary>
    public string MapTileSharePath { get; init; } = "";

    /// <summary>
    /// Ab welchem Alter eine zwischengespeicherte Kachel im Hintergrund neu
    /// geholt wird. Angezeigt wird immer sofort der vorhandene Stand — der
    /// Anwender wartet nie auf eine Auffrischung.
    ///
    /// 180 Tage als Vorgabe, weil sich die Datengrundlage daran orientiert:
    /// Orthophotos werden jaehrlich beflogen, die Stadtkarte laufend, aber
    /// nicht kachelweise sichtbar. 0 = nie auffrischen.
    /// </summary>
    public int MapTileMaxAgeDays { get; init; } = 180;

    /// <summary>
    /// Auswaehlbare Kartenhintergruende. Alle vier sind gegen den Dienst der LGB
    /// geprueft (2026-08-21) und liefern echte Kacheln fuer Potsdam.
    ///
    /// Warum mehrere: Auf einem Luftbild sind eingefaerbte Flaechen schwer zu
    /// lesen, weil der Untergrund selbst bunt ist. Der Stadtplan zeigt
    /// Strassennamen zum Wiederfinden, die Graustufen-Karte laesst die Ampel am
    /// deutlichsten hervortreten. Welche passt, entscheidet die Aufgabe — also
    /// umschaltbar statt vorgeschrieben.
    /// </summary>
    public IReadOnlyList<MapLayerDefinition> MapLayers { get; init; } =
    [
        new("Luftbild",          "https://isk.geobasis-bb.de/mapproxy/dop20c/service/wms",           "bebb_dop20c"),
        new("Stadtplan",         "https://isk.geobasis-bb.de/mapproxy/basemapde-bebb/service/wms",   "basemapde_farbe"),
        new("Topographisch grau","https://isk.geobasis-bb.de/mapproxy/dtk10grau/service/wms",        "bb_dtk10_grau"),
        new("Luftbild grau",     "https://isk.geobasis-bb.de/mapproxy/dop20g/service/wms",           "bebb_dop20g"),
        // ALKIS ist auf Fachthemen aufgeteilt — die Liegenschaftskarte entsteht
        // erst aus der Kombination. Reihenfolge = Zeichenreihenfolge.
        new("Liegenschaftskarte","https://isk.geobasis-bb.de/ows/alkis_wms",
            "adv_alkis_tatsaechliche_nutzung,adv_alkis_flurstuecke,adv_alkis_gebaeude"),
        // Eigener Kartenserver der Landeshauptstadt: Stadtkarte 1:500 mit
        // Gebäudeumringen, Höfen und Wegen — die detaillierteste Grundlage für
        // die Gebäudeebene. Kann nur EPSG:4326.
        new("Stadtkarte Potsdam",
            "https://geoportal.potsdam.de/server/services/Stadtkarte/MapServer/WMSServer",
            "0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29",
            "EPSG:4326")
    ];

    /// <summary>
    /// Baut die Vorgaben aus den Schluessel/Wert-Zeilen. Unbekannte Schluessel
    /// werden ignoriert, fehlende behalten ihren Default — ein halb gepflegter
    /// Datenbestand darf die Anwendung nicht lahmlegen.
    /// </summary>
    public static CockpitGlobals FromRows(IReadOnlyDictionary<string, string?> rows)
    {
        var fallback = new CockpitGlobals();

        return new CockpitGlobals
        {
            HostDefaultDomain = Text(KeyHostDefaultDomain) ?? fallback.HostDefaultDomain,
            UpdateChannelUrl  = Text(KeyUpdateChannelUrl)  ?? fallback.UpdateChannelUrl,
            HostOsAttributeKeys = StringList(KeyHostOsAttributeKeys) ?? fallback.HostOsAttributeKeys,
            HostLocationTagKeys = StringList(KeyHostLocationTagKeys) ?? fallback.HostLocationTagKeys,
            ShowHostCreation  = Bool(KeyShowHostCreation) ?? fallback.ShowHostCreation,
            ShowHostsTab      = Bool(KeyShowHostsTab) ?? fallback.ShowHostsTab,
            MapWmsUrl         = Text(KeyMapWmsUrl)        ?? fallback.MapWmsUrl,
            MapWmsLayer       = Text(KeyMapWmsLayer)      ?? fallback.MapWmsLayer,
            MapAttribution    = Text(KeyMapAttribution)   ?? fallback.MapAttribution,
            MapLayers         = LayerList(KeyMapLayers)   ?? fallback.MapLayers,
            MapTileSharePath  = Text(KeyMapTileSharePath) ?? fallback.MapTileSharePath,
            MapTileMaxAgeDays = Int(KeyMapTileMaxAgeDays) ?? fallback.MapTileMaxAgeDays
        };

        string? Text(string key)
            => rows.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        bool? Bool(string key)
            => Text(key) is { } s && bool.TryParse(s, out var b) ? b : null;

        // Negative Werte werden verworfen statt uebernommen — ein Tippfehler
        // soll auf den Default fallen, nicht in eine Sonderbedeutung kippen.
        int? Int(string key)
            => Text(key) is { } s && int.TryParse(s, out var n) && n >= 0 ? n : null;

        IReadOnlyList<MapLayerDefinition>? LayerList(string key)
        {
            if (Text(key) is not { } s) return null;
            try
            {
                var parsed = JsonSerializer.Deserialize<List<MapLayerDefinition>>(
                    s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                // Eintraege ohne Adresse oder Layer waeren stumme Fehlkacheln —
                // lieber aussortieren als eine leere Karte zeigen.
                var usable = parsed?
                    .Where(l => !string.IsNullOrWhiteSpace(l.Name)
                             && !string.IsNullOrWhiteSpace(l.Url)
                             && !string.IsNullOrWhiteSpace(l.Layer))
                    .ToList();
                return usable is { Count: > 0 } ? usable : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        IReadOnlyList<string>? StringList(string key)
        {
            if (Text(key) is not { } s) return null;
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(s);
                return parsed is { Count: > 0 } ? parsed : null;
            }
            catch (JsonException)
            {
                // Kaputte Liste = Default nehmen. Wer sie verstellt hat, sieht es
                // daran, dass sich nichts aendert; die Alternative waere eine
                // Anwendung, die wegen eines Kommas nicht startet.
                return null;
            }
        }
    }

    public IReadOnlyDictionary<string, string?> ToRows() => new Dictionary<string, string?>
    {
        [KeyHostDefaultDomain]   = HostDefaultDomain,
        [KeyUpdateChannelUrl]    = UpdateChannelUrl,
        [KeyHostOsAttributeKeys] = JsonSerializer.Serialize(HostOsAttributeKeys),
        [KeyHostLocationTagKeys] = JsonSerializer.Serialize(HostLocationTagKeys),
        [KeyShowHostCreation]    = ShowHostCreation.ToString(),
        [KeyShowHostsTab]        = ShowHostsTab.ToString(),
        [KeyMapWmsUrl]           = MapWmsUrl,
        [KeyMapWmsLayer]         = MapWmsLayer,
        [KeyMapAttribution]      = MapAttribution,
        [KeyMapLayers]           = JsonSerializer.Serialize(MapLayers),
        [KeyMapTileSharePath]    = MapTileSharePath,
        [KeyMapTileMaxAgeDays]   = MapTileMaxAgeDays.ToString()
    };
}
