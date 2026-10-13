using System.Text.Json.Serialization;

namespace Checkmk.Core.Models;

/// <summary>
/// Ein einzelner Checkmk-Comment (Host oder Service).
/// Envelope: <c>CheckmkObject&lt;CommentExtensions&gt;</c> — <see cref="Id"/>
/// kommt aus dem Root, alles andere aus <c>extensions</c>.
/// </summary>
public sealed record CommentExtensions
{
    [JsonPropertyName("host_name")]
    public string? HostName { get; init; }

    /// <summary>Bei Host-Kommentaren null/leer.</summary>
    [JsonPropertyName("service_description")]
    public string? ServiceDescription { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("persistent")]
    public bool Persistent { get; init; }

    /// <summary>
    /// Site, auf der der Kommentar liegt. Pflichtangabe beim Löschen.
    ///
    /// <para>Kann fehlen — ältere Checkmk-Stände liefern das Feld nicht. Dann
    /// gilt die Site der Verbindung. In einer verteilten Installation
    /// antwortet die Zentrale auch für Remote-Sites, und dort wäre das die
    /// falsche; deshalb wird der Wert bevorzugt, wenn er dabei ist.</para>
    /// </summary>
    [JsonPropertyName("site_id")]
    public string? SiteId { get; init; }

    /// <summary>Wird bei Reboot verworfen wenn Persistent=false.</summary>
    [JsonPropertyName("entry_time")]
    public DateTimeOffset? EntryTime { get; init; }

    [JsonIgnore]
    public bool IsService => !string.IsNullOrEmpty(ServiceDescription);
}
