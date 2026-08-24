using System.Text.RegularExpressions;
using Checkmk.Core.Models;

namespace Checkmk.App.Models;

/// <summary>
/// Persistierbarer Host-Filter. Zwei Modi, die sich gegenseitig ausschliessen:
/// <list type="bullet">
///   <item><see cref="ExplicitHosts"/> nicht leer → Include-Liste (exakte Hostnamen).</item>
///   <item>Sonst <see cref="HostNameRegex"/> → Regex-Match auf den Hostnamen.</item>
///   <item>Beides leer → matcht alle Hosts (Standard).</item>
/// </list>
/// </summary>
public sealed class HostFilter
{
    // Harter Cap gegen catastrophic backtracking bei bloed geschriebenen Regexes
    // ( ".*.*", "(a+)+", ...). 100 ms sind viel fuer einen einzelnen Hostnamen,
    // aber schuetzen zuverlaessig vor UI-Freezes.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public string Name { get; set; } = "";
    public string? HostNameRegex { get; set; }
    public List<string> ExplicitHosts { get; set; } = new();

    /// <summary>Id in der zentralen Datenbank; 0 = nur lokal (kein Datenbankzugang).</summary>
    public int Id { get; set; }

    /// <summary>Gesetzt = im Katalog veroeffentlicht, sonst persoenlich.</summary>
    public int? FachbereichId { get; set; }

    /// <summary>Fachbereichs-Name zur Anzeige. Nur Laufzeit.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FachbereichName { get; set; }

    /// <summary>Autor. Wer nicht der Autor ist, darf nur abonnieren.</summary>
    public string Owner { get; set; } = "";

    /// <summary>Wie viele diesen Filter abonniert haben — nur Anzeige.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Subscribers { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPublished => FachbereichId is not null;

    /// <summary>Darf dieser Anwender den Filter aendern? Nur der Autor.</summary>
    public bool IsAuthor(string user)
        => string.IsNullOrEmpty(Owner)
        || Owner.Equals(user, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// „Herkunft" fuer die Liste im Filter-Manager. Ohne diese Angabe sieht man
    /// einem veroeffentlichten Filter nicht an, dass eine Aenderung daran alle
    /// Abonnenten trifft.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string OriginDisplay => FachbereichId is null
        ? "persönlich"
        : FachbereichName ?? $"Fachbereich {FachbereichId}";

    /// <summary>
    /// Nur zur Laufzeit vorhanden, nie in <c>filter.json</c>. Gesetzt fuer den aus
    /// <c>viewer.json</c> vorgegebenen Filter: er soll in der ComboBox auswaehlbar
    /// sein, aber nicht in die Favoritenbibliothek des Anwenders einsickern —
    /// sonst bliebe er dort stehen, nachdem der Admin das Profil geaendert hat.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTransient { get; set; }

    public bool Matches(string hostName)
    {
        if (ExplicitHosts.Count > 0)
            return ExplicitHosts.Any(h => string.Equals(h, hostName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(HostNameRegex))
        {
            try
            {
                return Regex.IsMatch(hostName, HostNameRegex, RegexOptions.IgnoreCase, RegexTimeout);
            }
            catch (ArgumentException)
            {
                // ungueltiges Regex → matched nichts, damit der Anwender es visuell sofort merkt
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                // Der Regex ist zwar syntaktisch gueltig, hat aber catastrophic backtracking.
                // Wir behandeln ihn wie "matched nichts", damit die App nicht einfriert.
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Bildet den Filter auf einen <see cref="LivestatusHostFilter"/> ab, den der
    /// Client als serverseitigen Livestatus-Query verschickt. Rueckgabe <c>null</c>
    /// wenn der Filter effektiv „alle Hosts" bedeutet (kein Regex, keine Include-Liste).
    /// </summary>
    public LivestatusHostFilter? ToLivestatus()
    {
        if (ExplicitHosts.Count > 0)
            return new LivestatusHostFilter { IncludeHosts = ExplicitHosts };
        if (!string.IsNullOrWhiteSpace(HostNameRegex))
            return new LivestatusHostFilter { HostNameRegex = HostNameRegex };
        return null;
    }

    public override string ToString() => Name;
}
