using System.Text.Json;

namespace Checkmk.Core.Models;

/// <summary>
/// Beschreibt einen Host-basierten Filter, den der Client als
/// Livestatus-<c>query</c>-Parameter an Checkmk schickt — damit das Filtern
/// serverseitig passiert, statt alle Services zu ziehen. Wichtig bei grossen
/// Installationen (Zehntausende Checks).
/// </summary>
public sealed record LivestatusHostFilter
{
    /// <summary>Case-insensitive Regex auf <c>host_name</c>.</summary>
    public string? HostNameRegex { get; init; }

    /// <summary>
    /// Case-insensitive Regex auf <c>host_alias</c>.
    ///
    /// Eine Standard-Livestatus-Spalte, die ohnehin mit abgefragt wird — das
    /// Filtern nach Alias kostet serverseitig also nichts extra.
    /// </summary>
    public string? HostAliasRegex { get; init; }

    /// <summary>Exakte Hostnamen (OR-Verkettung).</summary>
    public IReadOnlyList<string>? IncludeHosts { get; init; }

    public bool IsEmpty
        => string.IsNullOrWhiteSpace(HostNameRegex)
           && string.IsNullOrWhiteSpace(HostAliasRegex)
           && (IncludeHosts is null || IncludeHosts.Count == 0);

    /// <summary>
    /// Baut den Livestatus-Query-Ausdruck. Include-Liste hat Vorrang vor Regex
    /// (analog zur clientseitigen <c>HostFilter.Matches</c>-Logik).
    /// Rueckgabe: JSON-Ausdruck als Object-Baum, den <see cref="ToJson"/>
    /// serialisiert.
    /// </summary>
    public object? ToQueryObject()
    {
        if (IncludeHosts is { Count: > 0 } list)
        {
            // Mehrere exakte Matches -> OR-Verkettung.
            if (list.Count == 1)
                return new { op = "=", left = "host_name", right = list[0] };

            return new
            {
                op = "or",
                expr = list.Select(h => new
                {
                    op = "=",
                    left = "host_name",
                    right = h
                }).ToArray()
            };
        }

        if (!string.IsNullOrWhiteSpace(HostNameRegex))
        {
            // Livestatus: "~~" == Regex, case-insensitive.
            return new { op = "~~", left = "host_name", right = HostNameRegex };
        }

        if (!string.IsNullOrWhiteSpace(HostAliasRegex))
            return new { op = "~~", left = "host_alias", right = HostAliasRegex };

        return null;
    }

    /// <summary>JSON-Repraesentation des Query-Ausdrucks (oder null wenn leer).</summary>
    public string? ToJson()
    {
        var q = ToQueryObject();
        return q is null ? null : JsonSerializer.Serialize(q);
    }
}
