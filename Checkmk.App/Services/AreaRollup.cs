using Checkmk.Core.Models;
using Checkmk.Data;

namespace Checkmk.App.Services;

/// <summary>
/// Warum der Punkt eines Hosts gelb statt rot ist. Steht als Vermerk in der
/// Host-Zeile des aufgeklappten Bereichs — sonst wäre die Herabstufung
/// unsichtbar.
/// </summary>
public enum HandledReason
{
    /// <summary>Jemand hat das Problem quittiert.</summary>
    Acknowledged,

    /// <summary>Geplantes Wartungsfenster.</summary>
    InDowntime
}

/// <summary>Aggregat eines Bereichs — inklusive seiner Unterbereiche.</summary>
/// <param name="HostCount">Hosts im Bereich und darunter, die auf den aktiven Filter passen.</param>
/// <param name="ProblemCount">Davon mit Status schlechter als OK.</param>
/// <param name="Worst">Schlechtester Status; <c>Ok</c> auch dann, wenn gar kein Host da ist.</param>
/// <param name="HasHosts">false = leerer Bereich. Wird gebraucht, weil ein leerer
/// Bereich nicht gruen aussehen soll wie ein gesunder.</param>
public sealed record AreaAggregate(int HostCount, int ProblemCount, ServiceState Worst, bool HasHosts)
{
    public static readonly AreaAggregate Empty = new(0, 0, ServiceState.Ok, false);
}

/// <summary>
/// Rechnet den Status eines Bereichs aus den Hosts darin hoch: schlechtester
/// gewinnt, von den Blaettern nach oben.
///
/// Bewusst statisch und ohne Seiteneffekte — das ist die Stelle, an der die
/// Farbe der Karte entsteht, und sie soll ohne Datenbank und ohne UI pruefbar
/// sein.
///
/// <b>Die Host-Menge ist bereits die Linse.</b> Der Aufrufer uebergibt nur die
/// Hosts, die auf den aktiven Filter passen — deshalb ist derselbe Serverraum
/// fuer das DB-Team gruen und fuer den Wachschutz rot. Nicht hier zusaetzlich
/// filtern wollen.
/// </summary>
public static class AreaRollup
{
    /// <summary>
    /// Aggregat je Bereichs-Id. Bereiche ohne eigene und ohne geerbte Hosts
    /// kommen mit <see cref="AreaAggregate.Empty"/> zurueck.
    /// </summary>
    public static Dictionary<int, AreaAggregate> Compute(
        IReadOnlyList<AreaRow> areas,
        IReadOnlyDictionary<string, int> hostToArea,
        IReadOnlyDictionary<string, ServiceState> hostWorstState)
    {
        var result = new Dictionary<int, AreaAggregate>();
        if (areas.Count == 0) return result;

        var childrenOf = new Dictionary<int, List<int>>();
        var known = new HashSet<int>();
        foreach (var a in areas)
        {
            known.Add(a.AreaId);
            if (a.ParentAreaId is { } p)
            {
                if (!childrenOf.TryGetValue(p, out var list))
                    childrenOf[p] = list = [];
                list.Add(a.AreaId);
            }
        }

        // Direkt zugeordnete Hosts je Bereich. Zuordnungen auf geloeschte
        // Bereiche werden ignoriert statt zu werfen — die Tabelle kann durch
        // einen Fremdschluessel zwar nicht verwaisen, aber die Momentaufnahme
        // kann aelter sein als der Baum.
        var directHosts = new Dictionary<int, List<string>>();
        foreach (var (host, areaId) in hostToArea)
        {
            if (!known.Contains(areaId)) continue;
            if (!hostWorstState.ContainsKey(host)) continue;   // nicht im Filter
            if (!directHosts.TryGetValue(areaId, out var list))
                directHosts[areaId] = list = [];
            list.Add(host);
        }

        // Wurzeln: kein Elternteil oder ein Elternteil, den es nicht (mehr) gibt.
        var roots = areas
            .Where(a => a.ParentAreaId is not { } p || !known.Contains(p))
            .Select(a => a.AreaId);

        // Der visited-Satz schuetzt gegen einen Zyklus im Baum. Die Datenbank
        // verhindert ihn nicht (ein UPDATE reicht), und ohne Schutz haengt hier
        // die Oberflaeche statt eine falsche Zahl zu zeigen.
        var visited = new HashSet<int>();
        foreach (var root in roots)
            Walk(root);

        // Bereiche, die durch einen Zyklus nicht erreicht wurden, trotzdem melden.
        foreach (var a in areas)
            result.TryAdd(a.AreaId, AreaAggregate.Empty);

        return result;

        AreaAggregate Walk(int areaId)
        {
            if (!visited.Add(areaId))
                return result.GetValueOrDefault(areaId, AreaAggregate.Empty);

            var hostCount = 0;
            var problemCount = 0;
            var worst = ServiceState.Ok;
            var hasHosts = false;

            if (directHosts.TryGetValue(areaId, out var hosts))
            {
                foreach (var h in hosts)
                {
                    var state = hostWorstState[h];
                    hostCount++;
                    hasHosts = true;
                    if (state != ServiceState.Ok) problemCount++;
                    if (Severity(state) > Severity(worst)) worst = state;
                }
            }

            if (childrenOf.TryGetValue(areaId, out var children))
            {
                foreach (var child in children)
                {
                    var sub = Walk(child);
                    hostCount += sub.HostCount;
                    problemCount += sub.ProblemCount;
                    hasHosts |= sub.HasHosts;
                    if (Severity(sub.Worst) > Severity(worst)) worst = sub.Worst;
                }
            }

            var aggregate = new AreaAggregate(hostCount, problemCount, worst, hasHosts);
            result[areaId] = aggregate;
            return aggregate;
        }
    }

    /// <summary>
    /// Ist dieser Service „in Bearbeitung" — quittiert oder in einem geplanten
    /// Wartungsfenster?
    ///
    /// <para>Beides zählt gleich, weil beides dieselbe Frage beantwortet:
    /// Weiß schon jemand davon? Ein Acknowledge sagt „gesehen, jemand ist
    /// dran", eine Downtime sagt „das ist so gewollt, und zwar bis dann".</para>
    /// </summary>
    public static bool IsHandled(ServiceStatus service)
        => service.IsAcknowledged || service.InDowntime;

    /// <summary>
    /// Status eines Service, <b>wie er für die Bereichsfarbe zählt</b>.
    ///
    /// <para><b>Ein quittierter CRIT — oder einer in Wartung — zählt als
    /// WARN.</b> Die Farbe eines Bereichs beantwortet nicht „welchen Status hat
    /// der Check", sondern „liegt hier etwas, um das sich noch niemand
    /// kümmert". Ein Acknowledge heißt genau: gesehen, jemand ist dran; eine
    /// Downtime heißt: das ist so geplant. Bleibt der Standort in beiden Fällen
    /// rot, ist die Karte nach dem ersten schlechten Tag durchgehend rot und
    /// sagt nichts mehr — und Quittieren wie Wartungsfenster, die in Checkmk
    /// genau dafür da sind, hätten sichtbar keine Wirkung.</para>
    ///
    /// <para>Herabgestuft wird nur bis WARN, <b>nicht bis OK</b>: Das Problem
    /// besteht weiter, es ist nur bekannt. Grün wäre eine Lüge — ein Server in
    /// Wartung ist tatsächlich nicht verfügbar.</para>
    ///
    /// <para><b>Nur CRIT wird herabgestuft.</b> Ein quittierter WARN bleibt
    /// WARN — ihn auf OK zu ziehen hieße, ihn verschwinden zu lassen, und
    /// damit wäre der Bereich grün, obwohl etwas offen ist.</para>
    ///
    /// <para>Die <b>Liste</b> im Status-Tab zeigt weiterhin den echten Status;
    /// dort stehen Quittierung und Wartung in eigenen Spalten, und „CRIT ist
    /// CRIT" ist die richtige Antwort auf die Frage, die eine Statusliste
    /// stellt.</para>
    /// </summary>
    public static ServiceState EffectiveState(ServiceStatus service)
        => service.ServiceState == ServiceState.Critical && IsHandled(service)
            ? ServiceState.Warning
            : service.ServiceState;

    /// <summary>
    /// Schlechtester Service-Status je Host — nach <see cref="EffectiveState"/>,
    /// also mit herabgestuften quittierten CRITs. CRIT &gt; WARN &gt; UNKNOWN &gt; OK,
    /// dieselbe Reihenfolge wie im Host-Baum des Status-Tabs.
    /// </summary>
    public static Dictionary<string, ServiceState> WorstStatePerHost(
        IEnumerable<ServiceStatus> services)
    {
        var worst = new Dictionary<string, ServiceState>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in services)
        {
            var state = EffectiveState(s);
            if (worst.TryGetValue(s.HostName, out var current)
                && Severity(current) >= Severity(state))
                continue;
            worst[s.HostName] = state;
        }
        return worst;
    }

    /// <summary>
    /// Hosts, deren Punkt nur deshalb nicht rot ist, weil der CRIT bekannt ist
    /// — samt dem Grund, der in der Host-Zeile steht.
    ///
    /// <para>Ohne diese Angabe wäre die Herabstufung eine stille: Ein
    /// quittierter Ausfall sähe in der Bereichsliste genauso aus wie eine echte
    /// Warnung.</para>
    ///
    /// <para><b>Wartung sticht Quittierung</b>, wenn beides zutrifft: „in
    /// Wartung" erklärt die Lage vollständig (es ist geplant und endet zu einer
    /// bekannten Zeit), „quittiert" sagt nur, dass jemand hingesehen hat.</para>
    /// </summary>
    public static Dictionary<string, HandledReason> HandledHosts(
        IEnumerable<ServiceStatus> services)
    {
        var handled = new Dictionary<string, HandledReason>(StringComparer.OrdinalIgnoreCase);
        var unhandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in services)
        {
            if (s.ServiceState != ServiceState.Critical) continue;

            if (!IsHandled(s)) { unhandled.Add(s.HostName); continue; }

            var reason = s.InDowntime ? HandledReason.InDowntime : HandledReason.Acknowledged;
            if (!handled.TryGetValue(s.HostName, out var existing)
                || reason == HandledReason.InDowntime)
                handled[s.HostName] = reason;
            else
                handled[s.HostName] = existing;
        }

        // Ein Host mit einem bekannten UND einem offenen CRIT gilt nicht als
        // bearbeitet — sein Punkt ist weiterhin rot, und der Vermerk waere
        // dann irrefuehrend.
        foreach (var host in unhandled) handled.Remove(host);
        return handled;
    }

    /// <summary>UNKNOWN zaehlt schwerer als OK, aber leichter als WARN — ein
    /// Check, der nichts sagt, ist ein Hinweis und kein Alarm.</summary>
    private static int Severity(ServiceState state) => state switch
    {
        ServiceState.Critical => 3,
        ServiceState.Warning => 2,
        ServiceState.Unknown => 1,
        _ => 0
    };
}
