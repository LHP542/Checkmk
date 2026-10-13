using Checkmk.App.Services;
using Checkmk.Core.Models;
using Checkmk.Data;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Der Rollup ist die Stelle, an der die Farbe eines Bereichs entsteht — und
/// spaeter die der Karte. Deshalb ohne Datenbank und ohne UI geprueft.
/// </summary>
public class AreaRollupTests
{
    // Campus
    //  ├─ Serverraum 3
    //  └─ Serverraum 4
    // Stadthaus
    private static readonly AreaRow Campus  = new(1, null, "Campus", 0, null, null);
    private static readonly AreaRow Raum3   = new(2, 1, "Serverraum 3", 0, null, null);
    private static readonly AreaRow Raum4   = new(3, 1, "Serverraum 4", 1, null, null);
    private static readonly AreaRow Stadthaus = new(4, null, "Stadthaus", 1, null, null);

    private static readonly List<AreaRow> Tree = [Campus, Raum3, Raum4, Stadthaus];

    private static Dictionary<string, int> Assign(params (string Host, int Area)[] pairs)
        => pairs.ToDictionary(p => p.Host, p => p.Area, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, ServiceState> States(
        params (string Host, ServiceState State)[] pairs)
        => pairs.ToDictionary(p => p.Host, p => p.State, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Worst_state_rolls_up_to_the_parent()
    {
        var result = AreaRollup.Compute(
            Tree,
            Assign(("sql01", 2), ("sw01", 3)),
            States(("sql01", ServiceState.Ok), ("sw01", ServiceState.Critical)));

        result[2].Worst.Should().Be(ServiceState.Ok);
        result[3].Worst.Should().Be(ServiceState.Critical);
        result[1].Worst.Should().Be(ServiceState.Critical, "der Campus erbt das Schlimmste");
        result[1].HostCount.Should().Be(2);
        result[1].ProblemCount.Should().Be(1);
    }

    [Fact]
    public void Critical_beats_warning_beats_unknown_beats_ok()
    {
        var result = AreaRollup.Compute(
            Tree,
            Assign(("a", 2), ("b", 2), ("c", 2)),
            States(("a", ServiceState.Ok), ("b", ServiceState.Unknown), ("c", ServiceState.Warning)));

        result[2].Worst.Should().Be(ServiceState.Warning);

        var withCrit = AreaRollup.Compute(
            Tree,
            Assign(("a", 2), ("b", 2)),
            States(("a", ServiceState.Warning), ("b", ServiceState.Critical)));

        withCrit[2].Worst.Should().Be(ServiceState.Critical);
    }

    [Fact]
    public void Empty_area_is_marked_as_empty_not_as_healthy()
    {
        // Ein leerer Bereich darf nicht gruen aussehen wie ein gesunder —
        // sonst haelt man 0 zugewiesene Hosts fuer „alles in Ordnung".
        var result = AreaRollup.Compute(Tree, Assign(), States());

        result[4].HasHosts.Should().BeFalse();
        result[4].HostCount.Should().Be(0);
        result[4].Worst.Should().Be(ServiceState.Ok);
    }

    [Fact]
    public void Hosts_outside_the_filter_do_not_count()
    {
        // Die Host-Menge IST die Linse: was der Aufrufer nicht mitgibt, gehoert
        // nicht zu dieser Sicht. Derselbe Raum ist damit fuer ein Team gruen und
        // fuer ein anderes rot.
        var result = AreaRollup.Compute(
            Tree,
            Assign(("sql01", 2), ("usv01", 2)),
            States(("sql01", ServiceState.Ok)));   // usv01 nicht im Filter

        result[2].HostCount.Should().Be(1);
        result[2].Worst.Should().Be(ServiceState.Ok);
    }

    [Fact]
    public void Assignment_to_an_unknown_area_is_ignored()
    {
        var result = AreaRollup.Compute(
            Tree,
            Assign(("geist", 999)),
            States(("geist", ServiceState.Critical)));

        result.Values.Should().AllSatisfy(a => a.HostCount.Should().Be(0));
    }

    [Fact]
    public void Cycle_in_the_tree_does_not_hang()
    {
        // Die Datenbank verhindert einen Zyklus nicht — ein UPDATE reicht.
        // Ohne Schutz haengt hier die Oberflaeche, statt eine falsche Zahl zu zeigen.
        var cyclic = new List<AreaRow>
        {
            new(1, 2, "A", 0, null, null),
            new(2, 1, "B", 0, null, null)
        };

        var act = () => AreaRollup.Compute(cyclic, Assign(("h", 1)),
            States(("h", ServiceState.Critical)));

        act.Should().NotThrow();
        act().Should().HaveCount(2);
    }

    [Fact]
    public void Area_whose_parent_was_deleted_is_treated_as_a_root()
    {
        var orphan = new List<AreaRow> { new(7, 999, "Verwaist", 0, null, null) };

        var result = AreaRollup.Compute(orphan, Assign(("h", 7)),
            States(("h", ServiceState.Warning)));

        result[7].Worst.Should().Be(ServiceState.Warning);
        result[7].HostCount.Should().Be(1);
    }

    [Fact]
    public void Worst_state_per_host_picks_the_most_severe_service()
    {
        var services = new[]
        {
            new ServiceStatus { HostName = "sql01", Description = "CPU",  State = 0 },
            new ServiceStatus { HostName = "sql01", Description = "Disk", State = 2 },
            new ServiceStatus { HostName = "sql01", Description = "Mem",  State = 1 },
            new ServiceStatus { HostName = "sw01",  Description = "Port", State = 1 }
        };

        var worst = AreaRollup.WorstStatePerHost(services);

        worst["sql01"].Should().Be(ServiceState.Critical);
        worst["sw01"].Should().Be(ServiceState.Warning);
    }

    [Fact]
    public void Worst_state_per_host_is_case_insensitive()
    {
        // Checkmk liefert Hostnamen konsistent, die Zuordnungstabelle wird aber
        // von Menschen gefuellt.
        var worst = AreaRollup.WorstStatePerHost(
        [
            new ServiceStatus { HostName = "SQL01", Description = "a", State = 2 }
        ]);

        worst.ContainsKey("sql01").Should().BeTrue();
    }

    // --- Quittierte Probleme ---------------------------------------------

    private static ServiceStatus Svc(string host, int state, bool ack = false, string desc = "x")
        => new() { HostName = host, Description = desc, State = state, Acknowledged = ack ? 1 : 0 };

    /// <summary>
    /// Der Kern: Die Bereichsfarbe beantwortet „liegt hier etwas, um das sich
    /// noch niemand kuemmert". Ein Acknowledge heisst genau: jemand ist dran.
    /// Bliebe der Standort rot, waere die Karte nach dem ersten schlechten Tag
    /// durchgehend rot.
    /// </summary>
    [Fact]
    public void Ein_quittierter_CRIT_zaehlt_als_WARN()
        => AreaRollup.WorstStatePerHost([Svc("sql01", 2, ack: true)])["sql01"]
            .Should().Be(ServiceState.Warning);

    /// <summary>Nur bis WARN, nicht bis OK — das Problem besteht weiter.</summary>
    [Fact]
    public void Ein_quittierter_CRIT_wird_nicht_gruen()
        => AreaRollup.WorstStatePerHost([Svc("sql01", 2, ack: true)])["sql01"]
            .Should().NotBe(ServiceState.Ok);

    [Fact]
    public void Ein_offener_CRIT_bleibt_rot()
        => AreaRollup.WorstStatePerHost([Svc("sql01", 2)])["sql01"]
            .Should().Be(ServiceState.Critical);

    /// <summary>Ein quittierter WARN bleibt WARN — ihn auf OK zu ziehen hiesse,
    /// ihn verschwinden zu lassen.</summary>
    [Fact]
    public void Ein_quittierter_WARN_bleibt_WARN()
        => AreaRollup.WorstStatePerHost([Svc("sw01", 1, ack: true)])["sw01"]
            .Should().Be(ServiceState.Warning);

    /// <summary>
    /// Ein offener CRIT neben einem quittierten haelt den Host rot — sonst
    /// verdeckte ein einziges Acknowledge den noch unbearbeiteten Ausfall.
    /// </summary>
    [Fact]
    public void Ein_offener_CRIT_neben_einem_quittierten_bleibt_rot()
    {
        var worst = AreaRollup.WorstStatePerHost(
        [
            Svc("sql01", 2, ack: true, desc: "Backup"),
            Svc("sql01", 2, desc: "Disk")
        ]);

        worst["sql01"].Should().Be(ServiceState.Critical);
    }

    [Fact]
    public void Der_Bereich_wird_gelb_statt_rot_wenn_alles_quittiert_ist()
    {
        var result = AreaRollup.Compute(Tree, Assign(("h", 2)),
            AreaRollup.WorstStatePerHost([Svc("h", 2, ack: true)]));

        result[2].Worst.Should().Be(ServiceState.Warning);
        result[1].Worst.Should().Be(ServiceState.Warning, "der Elternteil erbt den Status");
        result[2].ProblemCount.Should().Be(1, "ein Problem ist es weiterhin");
    }

    // --- Der Vermerk „quittiert" -----------------------------------------

    [Fact]
    public void Quittierte_Hosts_werden_benannt()
        => AreaRollup.HandledHosts([Svc("sql01", 2, ack: true)])
            .Should().BeEquivalentTo(["sql01"]);

    /// <summary>
    /// Ein Host mit einem quittierten UND einem offenen CRIT ist nicht
    /// „quittiert" — sein Punkt ist rot, und der Vermerk waere irrefuehrend.
    /// </summary>
    [Fact]
    public void Ein_Host_mit_offenem_CRIT_gilt_nicht_als_quittiert()
        => AreaRollup.HandledHosts(
            [Svc("sql01", 2, ack: true, desc: "a"), Svc("sql01", 2, desc: "b")])
            .Should().BeEmpty();

    [Fact]
    public void Ein_gesunder_Host_ist_nicht_quittiert()
        => AreaRollup.HandledHosts([Svc("sql01", 0), Svc("sw01", 1, ack: true)])
            .Should().BeEmpty("nur herabgestufte CRITs bekommen den Vermerk");
}
