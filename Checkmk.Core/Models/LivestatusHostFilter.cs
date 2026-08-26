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
    /// <param name="hostTable">
    /// <b>Die Spalten heissen je Tabelle anders.</b> Auf dem host-Endpunkt sind
    /// es <c>name</c> und <c>alias</c>, auf dem service-Endpunkt
    /// <c>host_name</c> und <c>host_alias</c>. Wer das verwechselt, bekommt von
    /// Checkmk ein nichtssagendes <c>400 „These fields have problems: query"</c>
    /// — nicht etwa null Treffer, sondern einen Fehlschlag des ganzen Refreshs.
    /// </param>
    public object? ToQueryObject(bool hostTable = false)
    {
        var nameCol = hostTable ? "name" : "host_name";

        if (IncludeHosts is { Count: > 0 } list)
        {
            // Mehrere exakte Matches -> OR-Verkettung.
            if (list.Count == 1)
                return new { op = "=", left = nameCol, right = list[0] };

            return new
            {
                op = "or",
                expr = list.Select(h => new
                {
                    op = "=",
                    left = nameCol,
                    right = h
                }).ToArray()
            };
        }

        if (!string.IsNullOrWhiteSpace(HostNameRegex))
        {
            // Livestatus: "~~" == Regex, case-insensitive.
            return new { op = "~~", left = nameCol, right = HostNameRegex };
        }

        if (!string.IsNullOrWhiteSpace(HostAliasRegex))
            return new { op = "~~", left = hostTable ? "alias" : "host_alias", right = HostAliasRegex };

        return null;
    }

    /// <summary>JSON-Repraesentation des Query-Ausdrucks (oder null wenn leer).</summary>
    /// <param name="hostTable">siehe <see cref="ToQueryObject"/>.</param>
    public string? ToJson(bool hostTable = false)
    {
        var q = ToQueryObject(hostTable);
        return q is null ? null : JsonSerializer.Serialize(q);
    }
}
