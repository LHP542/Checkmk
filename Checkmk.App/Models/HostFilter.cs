using System.Text.RegularExpressions;
using Checkmk.Core.Models;

namespace Checkmk.App.Models;

/// <summary>Worauf der Regex eines Filters angewendet wird.</summary>
public enum FilterTarget
{
    /// <summary>Der Hostname — die Vorgabe und das bisherige Verhalten.</summary>
    HostName = 0,

    /// <summary>
    /// Der Host-Alias.
    ///
    /// Bei uns steht dort, wem ein Gerät zugeordnet ist — Aliasse wie
    /// <c>„SchmidtT; WenzelM; SchmidtO; VolkJ; OsteL"</c>. Ein Filter auf den
    /// eigenen Anmeldenamen liefert damit „alle meine Rechner", ohne dass
    /// jemand eine Host-Liste pflegen müsste.
    /// </summary>
    Alias = 1
}

/// <summary>
/// Persistierbarer Host-Filter. Zwei Modi, die sich gegenseitig ausschliessen:
/// <list type="bullet">
///   <item><see cref="ExplicitHosts"/> nicht leer → Include-Liste (exakte Hostnamen).</item>
///   <item>Sonst <see cref="HostNameRegex"/> → Regex-Match auf <see cref="Target"/>.</item>
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

    /// <summary>
    /// Worauf der Regex angewendet wird. Vorgabe <see cref="FilterTarget.HostName"/>
    /// — bestehende Filter verhalten sich damit unverändert.
    ///
    /// <b>Betrifft nur den Regex.</b> Die <see cref="ExplicitHosts"/>-Liste
    /// bleibt immer eine Liste von <i>Hostnamen</i>: Sie entsteht aus
    /// „Auswahl als Favorit…", also aus angeklickten Geräten. Eine Liste
    /// exakter Aliasse zu pflegen ergäbe keinen Sinn — dafür ist der Regex da.
    /// </summary>
    public FilterTarget Target { get; set; } = FilterTarget.HostName;

    /// <summary>Beschriftung für Listen und Dialoge.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string TargetDisplay => Target == FilterTarget.Alias ? "Alias" : "Hostname";

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

    /// <summary>
    /// Passt der Host? <paramref name="alias"/> darf <c>null</c> sein — ein
    /// Alias-Filter trifft dann nichts.
    ///
    /// <b>Ein Host ohne Alias fällt bei <see cref="FilterTarget.Alias"/> heraus</b>,
    /// statt auf den Hostnamen zurückzufallen. Der Rückfall wäre bequem und
    /// falsch: „alle meine Rechner" würde dann stillschweigend Geräte
    /// einsammeln, deren Alias schlicht leer ist.
    /// </summary>
    public bool Matches(string hostName, string? alias = null)
    {
        if (ExplicitHosts.Count > 0)
            return ExplicitHosts.Any(h => string.Equals(h, hostName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(HostNameRegex))
        {
            var subject = Target == FilterTarget.Alias ? alias : hostName;
            if (string.IsNullOrEmpty(subject)) return false;

            try
            {
                return Regex.IsMatch(subject, HostNameRegex, RegexOptions.IgnoreCase, RegexTimeout);
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

        if (string.IsNullOrWhiteSpace(HostNameRegex)) return null;

        // host_alias ist eine Standard-Livestatus-Spalte und wird ohnehin schon
        // mit abgefragt — der Alias-Filter kann deshalb genauso serverseitig
        // laufen wie der auf den Hostnamen. Waere er nur clientseitig, muesste
        // die App fuer „alle meine Rechner" erst alle 33.000 Checks ziehen.
        return Target == FilterTarget.Alias
            ? new LivestatusHostFilter { HostAliasRegex = HostNameRegex }
            : new LivestatusHostFilter { HostNameRegex = HostNameRegex };
    }

    public override string ToString() => Name;
}
