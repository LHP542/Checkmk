using System.Text.Json.Serialization;

namespace Checkmk.Core.Models;

/// <summary>
/// Live-Status eines Service (Livestatus) aus
/// /domain-types/service/collections/all mit columns=... .
/// </summary>
public sealed record ServiceStatus
{
    [JsonPropertyName("host_name")]
    public string HostName { get; init; } = string.Empty;

    [JsonPropertyName("host_alias")]
    public string? HostAlias { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Anzeigename des Service (Livestatus <c>display_name</c>). Ist normalerweise
    /// identisch mit <see cref="Description"/>, weicht aber ab, sobald in Checkmk
    /// ein „Alias" fuer den Service gesetzt ist. Kann leer sein, wenn die Site die
    /// Spalte nicht liefert — dafuer gibt es <see cref="DisplayNameOrDescription"/>.
    /// </summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    /// <summary>0 = OK, 1 = WARN, 2 = CRIT, 3 = UNKNOWN.</summary>
    [JsonPropertyName("state")]
    public int State { get; init; }

    [JsonPropertyName("plugin_output")]
    public string? PluginOutput { get; init; }

    [JsonPropertyName("acknowledged")]
    public int Acknowledged { get; init; }

    [JsonPropertyName("scheduled_downtime_depth")]
    public int ScheduledDowntimeDepth { get; init; }

    [JsonPropertyName("last_check")]
    public long LastCheckUnix { get; init; }

    [JsonPropertyName("last_state_change")]
    public long LastStateChangeUnix { get; init; }

    [JsonIgnore]
    public ServiceState ServiceState => State switch
    {
        0 => ServiceState.Ok,
        1 => ServiceState.Warning,
        2 => ServiceState.Critical,
        3 => ServiceState.Unknown,
        _ => ServiceState.Unknown
    };

    /// <summary>
    /// Gesetzt nur auf der Ersatzzeile fuer einen Host, der nicht UP ist
    /// (<see cref="ForHost"/>). Eine solche Zeile steht in der Status-Liste
    /// anstelle seiner Services: Deren Zustand ist veraltet, solange der Host
    /// nicht erreichbar ist, und ein Switch mit „Warning" an einem Inventur-Check
    /// verdeckte sonst, dass er komplett weg ist.
    /// </summary>
    [JsonIgnore]
    public HostState? HostRowState { get; init; }

    [JsonIgnore]
    public bool IsHostRow => HostRowState is not null;

    /// <summary>Text fuer die Status-Spalte: „Down"/„Unreachable" auf einer
    /// Host-Zeile, sonst der Service-Status.</summary>
    [JsonIgnore]
    public string StateLabel => HostRowState?.ToString() ?? ServiceState.ToString();

    /// <summary>Beschreibung der Host-Zeile. Kein echter Service-Name — Aktionen
    /// auf einer Host-Zeile muessen ueber <see cref="IsHostRow"/> auf die
    /// Host-Endpunkte umgelenkt werden.</summary>
    public const string HostRowDescription = "Host";

    /// <summary>Baut die Ersatzzeile fuer einen nicht erreichbaren Host. State 2,
    /// damit Ampel, Sortierung und „Nur Probleme" ihn wie einen CRIT behandeln.</summary>
    public static ServiceStatus ForHost(HostStatus host) => new()
    {
        HostName = host.HostName,
        HostAlias = host.Alias,
        Description = HostRowDescription,
        State = (int)ServiceState.Critical,
        PluginOutput = host.PluginOutput,
        Acknowledged = host.Acknowledged,
        ScheduledDowntimeDepth = host.ScheduledDowntimeDepth,
        LastCheckUnix = host.LastCheckUnix,
        LastStateChangeUnix = host.LastStateChangeUnix,
        HostRowState = host.HostState
    };

    [JsonIgnore]
    public bool IsAcknowledged => Acknowledged != 0;

    [JsonIgnore]
    public bool InDowntime => ScheduledDowntimeDepth > 0;

    [JsonIgnore]
    public DateTimeOffset LastCheck => DateTimeOffset.FromUnixTimeSeconds(LastCheckUnix);

    [JsonIgnore]
    public DateTimeOffset LastStateChange => DateTimeOffset.FromUnixTimeSeconds(LastStateChangeUnix);

    /// <summary>Faellt auf <see cref="Description"/> zurueck, wenn die Site keinen
    /// abweichenden Anzeigenamen liefert. Das ist der Wert, den eine Spalte
    /// „service_display_name" zeigen soll.</summary>
    [JsonIgnore]
    public string DisplayNameOrDescription
        => string.IsNullOrWhiteSpace(DisplayName) ? Description : DisplayName;

    /// <summary>Zeit seit der letzten Statusaenderung, kompakt (z. B. "2 d", "3 h", "15 m").</summary>
    [JsonIgnore]
    public string Age => FormatAge(LastStateChangeUnix);

    /// <summary>Zeit seit dem letzten Check, kompakt — Checkmk-Sicht „svc_check_age".
    /// Ein grosser Wert heisst: der Check laeuft nicht mehr, unabhaengig vom State.</summary>
    [JsonIgnore]
    public string CheckAge => FormatAge(LastCheckUnix);

    /// <summary>Kompakte Altersangabe zu einem Unix-Zeitstempel. 0/negativ =&gt; "-",
    /// weil Livestatus fuer „nie passiert" eine 0 liefert (und nicht null).</summary>
    private static string FormatAge(long unixSeconds)
    {
        if (unixSeconds <= 0)
            return "-";

        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} d {span.Hours} h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} h {span.Minutes} m";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes} m";
        return $"{(int)span.TotalSeconds} s";
    }
}

public enum ServiceState
{
    Ok = 0,
    Warning = 1,
    Critical = 2,
    Unknown = 3
}
