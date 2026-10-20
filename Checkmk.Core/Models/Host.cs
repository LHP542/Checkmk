using System.Text.Json.Serialization;

namespace Checkmk.Core.Models;

/// <summary>
/// Attribute eines konfigurierten Hosts (Setup-Seite), aus
/// /domain-types/host_config/collections/all -> extensions.
/// </summary>
public sealed record HostConfigExtensions
{
    [JsonPropertyName("folder")]
    public string? Folder { get; init; }

    [JsonPropertyName("attributes")]
    public HostAttributes? Attributes { get; init; }

    [JsonPropertyName("is_cluster")]
    public bool IsCluster { get; init; }

    [JsonPropertyName("is_offline")]
    public bool IsOffline { get; init; }
}

public sealed record HostAttributes
{
    [JsonPropertyName("ipaddress")]
    public string? IpAddress { get; init; }

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    /// <summary>Frei belegbare Labels (tag_* etc.).</summary>
    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>
    /// Catch-all fuer nicht explizit gemappte Attribute — Host-Tag-Gruppen
    /// (<c>tag_*</c>), Custom Host Attributes, etc. Nur befuellt, wenn die
    /// Antwort mit <c>effective_attributes=true</c> geholt wurde.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalProperties { get; init; }
}

/// <summary>
/// Live-Status eines Hosts (Monitoring-Seite / Livestatus) aus
/// /domain-types/host/collections/all mit columns=... .
/// </summary>
public sealed record HostStatus
{
    /// <summary>Auf der host-Tabelle heisst die Spalte <c>name</c>, nicht
    /// <c>host_name</c> — mit dem alten Namen blieb das Feld in der Host-Liste
    /// stillschweigend leer.</summary>
    [JsonPropertyName("name")]
    public string HostName { get; init; } = string.Empty;

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    /// <summary>0 = UP, 1 = DOWN, 2 = UNREACHABLE.</summary>
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
    public HostState HostState => State switch
    {
        0 => HostState.Up,
        1 => HostState.Down,
        2 => HostState.Unreachable,
        _ => HostState.Unknown
    };

    [JsonIgnore]
    public bool IsAcknowledged => Acknowledged != 0;

    [JsonIgnore]
    public bool InDowntime => ScheduledDowntimeDepth > 0;
}

public enum HostState
{
    Up = 0,
    Down = 1,
    Unreachable = 2,
    Unknown = -1
}
