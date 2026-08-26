using Checkmk.App.Models;
using Checkmk.App.Services;
using Checkmk.Core.Models;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Ein Filter kann seinen Regex gegen den Hostnamen oder gegen den
/// Host-<b>Alias</b> pruefen.
///
/// Hintergrund aus dem Betrieb: Im Alias steht bei uns, wem ein Geraet
/// zugeordnet ist — Werte wie „SchmidtT; WenzelM; OsteL". Ein Filter auf den
/// eigenen Anmeldenamen liefert damit „alle meine Rechner", ohne dass jemand
/// eine Host-Liste pflegen muesste.
/// </summary>
public class FilterAliasTests
{
    private static HostFilter Alias(string regex) => new()
    {
        Name = "Meine Geräte",
        Target = FilterTarget.Alias,
        HostNameRegex = regex
    };

    [Fact]
    public void Alias_filter_matches_the_alias_not_the_host_name()
    {
        var f = Alias("OsteL");

        f.Matches("PC-4711", "SchmidtT; WenzelM; OsteL").Should().BeTrue();
        f.Matches("OsteL-PC", "MuellerK").Should().BeFalse(
            "der Hostname zaehlt bei einem Alias-Filter nicht mit");
    }

    /// <summary>
    /// <b>Kein Rueckfall auf den Hostnamen, wenn der Alias leer ist.</b> Der
    /// Rueckfall waere bequem und falsch: „alle meine Geraete" wuerde dann
    /// stillschweigend jedes Geraet einsammeln, dessen Alias schlicht nicht
    /// gepflegt ist.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_host_without_an_alias_falls_out(string? alias)
        => Alias(".*").Matches("PC-4711", alias).Should().BeFalse();

    [Fact]
    public void Host_name_filters_ignore_the_alias()
    {
        var f = new HostFilter { Name = "DB", HostNameRegex = "^db" };

        f.Matches("DBSQL01", "OsteL").Should().BeTrue();
        f.Matches("PC-4711", "db-team").Should().BeFalse();
    }

    /// <summary>
    /// Die Include-Liste bleibt immer eine Liste von <i>Hostnamen</i> — sie
    /// entsteht aus „Auswahl als Favorit…", also aus angeklickten Geraeten.
    /// </summary>
    [Fact]
    public void The_include_list_stays_host_names_even_for_an_alias_filter()
    {
        var f = new HostFilter
        {
            Name = "Fest",
            Target = FilterTarget.Alias,
            HostNameRegex = "OsteL",
            ExplicitHosts = ["DBSQL01"]
        };

        f.Matches("DBSQL01", "MuellerK").Should().BeTrue();
        f.Matches("PC-4711", "OsteL").Should().BeFalse();
    }

    // --- serverseitig, nicht clientseitig --------------------------------

    /// <summary>
    /// <c>host_alias</c> ist eine Standard-Livestatus-Spalte. Liefe der
    /// Alias-Filter nur clientseitig, muesste die App fuer „alle meine
    /// Rechner" erst alle 33.000 Checks ziehen.
    /// </summary>
    [Fact]
    public void Alias_filters_are_pushed_down_to_livestatus()
    {
        var q = Alias("OsteL").ToLivestatus();

        q.Should().NotBeNull();
        q!.HostAliasRegex.Should().Be("OsteL");
        q.HostNameRegex.Should().BeNull();
        q.ToJson().Should().Contain("host_alias").And.Contain("~~");
    }

    [Fact]
    public void Host_name_filters_still_query_host_name()
    {
        var q = new HostFilter { HostNameRegex = "^db" }.ToLivestatus();

        q!.HostNameRegex.Should().Be("^db");
        q.HostAliasRegex.Should().BeNull();
        q.ToJson().Should().Contain("host_name");
    }

    [Fact]
    public void An_alias_only_filter_is_not_empty()
        => new LivestatusHostFilter { HostAliasRegex = "OsteL" }.IsEmpty.Should().BeFalse();

    /// <summary>Include-Liste schlaegt weiterhin jeden Regex — auch den auf den
    /// Alias, sonst wuerde eine gepflegte Auswahl ueberstimmt.</summary>
    [Fact]
    public void The_include_list_wins_in_the_livestatus_query_too()
    {
        var q = new HostFilter
        {
            Target = FilterTarget.Alias,
            HostNameRegex = "OsteL",
            ExplicitHosts = ["DBSQL01"]
        }.ToLivestatus();

        q!.IncludeHosts.Should().Equal("DBSQL01");
        q.HostAliasRegex.Should().BeNull();
    }

    [Fact]
    public void The_target_is_shown_in_plain_words()
    {
        Alias("x").TargetDisplay.Should().Be("Alias");
        new HostFilter().TargetDisplay.Should().Be("Hostname");
    }

    /// <summary>Auch der Kiosk kann nach Alias filtern (<c>matchAlias</c> in
    /// <c>viewer.json</c>); Default bleibt der Hostname.</summary>
    [Fact]
    public void The_viewer_profile_can_ask_for_the_alias()
    {
        new ViewerView { HostRegex = "OsteL" }.ToHostFilter()
            .Target.Should().Be(FilterTarget.HostName);

        new ViewerView { HostRegex = "OsteL", MatchAlias = true }.ToHostFilter()
            .Target.Should().Be(FilterTarget.Alias);
    }
}
