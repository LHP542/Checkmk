using Checkmk.App.Services;
using Checkmk.Data;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Der Abgleich zwischen den Checkmk-Ortstags und den Bereichen.
///
/// Alle Zahlen und Sonderfälle hier sind am Bestand gemessen (2026-08-21,
/// Site <c>schul_it</c>: 654 Hosts, 553 mit <c>tag_location_school</c>,
/// 51 verschiedene Werte) — nicht ausgedacht.
/// </summary>
public class HostTagTests
{
    private static AreaRow Area(int id, string name, string? code = null,
        string? tag = null, string? pattern = null, string? source = "LHP-Schulen")
        => new(id, null, name, 0, null, null, ExternalSource: source, HostPattern: pattern,
               ExternalCode: code, HostTag: tag);

    // --- Zahl aus Tag und Code ------------------------------------------

    [Theory]
    [InlineData("schule_46", "46")]
    [InlineData("schule_01", "1")]      // fuehrende Null nur im Tag
    [InlineData("schule_2526", "2526")]
    [InlineData("filiale_04", "4")]
    [InlineData("47", "47")]            // ohne Praefix
    public void The_number_is_read_from_the_tag_value(string tag, string expected)
        => HostTagMatcher.NumberOfTag(tag).Should().Be(expected);

    [Theory]
    [InlineData("haus1")]
    [InlineData("aussen")]
    [InlineData("schule_osz3")]
    public void A_tag_without_a_number_yields_nothing(string tag)
        => HostTagMatcher.NumberOfTag(tag).Should().BeNull();

    [Fact]
    public void A_combined_code_is_reachable_under_both_spellings()
    {
        // Checkmk fuehrt die 25/26 als schule_2526, die 10/30 aber als
        // schule_10. Beide Schreibweisen muessen den Bereich finden.
        HostTagMatcher.KeysFor("25/26").Should().BeEquivalentTo(["2526", "25", "26"]);
        HostTagMatcher.KeysFor("46").Should().BeEquivalentTo(["46"]);
    }

    [Theory]
    [InlineData("F26")]        // berufliche Schule
    [InlineData("SFT")]        // Schule freier Traeger
    [InlineData("OSZ III")]
    [InlineData("")]
    [InlineData(null)]
    public void Codes_that_are_not_plain_numbers_yield_no_key(string? code)
    {
        // Der Fehler, an dem ein erster Versuch gescheitert ist: Wer die
        // Ziffern aus "F26" herauszieht, bekommt 26 und beansprucht damit die
        // Hosts der Schule 26. Vier Tag-Werte wurden dadurch mehrdeutig.
        HostTagMatcher.KeysFor(code).Should().BeEmpty();
    }

    // --- Abgleich --------------------------------------------------------

    [Fact]
    public void Each_tag_finds_its_area_by_number()
    {
        var areas = new[]
        {
            Area(1, "Steuben-Gesamtschule", "46"),
            Area(2, "Grundschule Bornim", "11"),
        };

        var matches = HostTagMatcher.Match(areas,
            [new HostTagValue("schule_46", 11), new HostTagValue("schule_11", 16)]);

        matches.Should().HaveCount(2);
        matches.Should().ContainSingle(m => m.TagValue == "schule_46" && m.AreaId == 1);
        matches.Should().ContainSingle(m => m.TagValue == "schule_11" && m.AreaId == 2);
        matches.Should().OnlyContain(m => !m.IsAmbiguous);
    }

    [Fact]
    public void Combined_schools_match_under_the_spelling_checkmk_uses()
    {
        var areas = new[]
        {
            Area(1, "Karl-Foerster-Schule", "25/26"),
            Area(2, "Schule am Nuthetal", "10/30"),
        };

        var matches = HostTagMatcher.Match(areas,
            [new HostTagValue("schule_2526", 7), new HostTagValue("schule_10", 13)]);

        matches.Should().ContainSingle(m => m.TagValue == "schule_2526" && m.AreaId == 1);
        matches.Should().ContainSingle(m => m.TagValue == "schule_10" && m.AreaId == 2);
    }

    [Fact]
    public void A_tag_matching_two_areas_is_flagged_not_guessed()
    {
        var areas = new[] { Area(1, "Erste", "46"), Area(2, "Zweite", "46") };

        var m = HostTagMatcher.Match(areas, [new HostTagValue("schule_46", 11)]).Single();

        m.IsAmbiguous.Should().BeTrue();
        m.Conflicts.Should().Contain("Zweite");
        m.Note.Should().Contain("mehrdeutig");
    }

    [Fact]
    public void An_already_correct_tag_is_reported_as_unchanged()
    {
        var areas = new[] { Area(1, "Steuben", "46", tag: "schule_46") };

        var m = HostTagMatcher.Match(areas, [new HostTagValue("schule_46", 11)]).Single();

        m.IsUnchanged.Should().BeTrue();
        m.Note.Should().Be("unverändert");
    }

    [Fact]
    public void A_different_numbering_scheme_never_matches()
    {
        // Am Bestand aufgedeckt: Die fuenf Hosts WLC-25SL-01, 25-SW01,
        // NAS25-01, NAS25-02 und PA25-1 gehoeren zur Karl-Foerster-Schule und
        // tragen tag_location_filiale = filiale_04. Wer nur die Zahl
        // vergleicht, schiebt sie ans Hermann-von-Helmholtz-Gymnasium, das die
        // Schulnummer 4 hat. filiale_ und schule_ sind zwei Nummernkreise.
        var areas = new[] { Area(1, "Hermann-von-Helmholtz-Gymnasium", "4") };

        HostTagMatcher.Match(areas, [new HostTagValue("filiale_04", 5)]).Should().BeEmpty();
    }

    [Fact]
    public void Areas_from_a_source_without_a_known_scheme_stay_out_of_the_match()
    {
        // Die Verwaltungsstandorte haben weder Code noch Ortstag. Sie
        // versehentlich ueber eine Hausnummer zu treffen waere schlimmer als
        // sie von Hand einzutragen.
        var areas = new[] { Area(1, "Stadthaus", "4", source: "LHP-Verwaltungsstandorte") };

        HostTagMatcher.Match(areas, [new HostTagValue("schule_04", 9)]).Should().BeEmpty();
    }

    [Fact]
    public void A_tag_without_a_matching_area_is_simply_absent()
    {
        // schule_61 und schule_63 haben Hosts, aber keine Schule in den
        // offenen Kartendaten. Die traegt man von Hand nach — erfinden waere
        // schlimmer als weglassen.
        var areas = new[] { Area(1, "Steuben", "46") };

        HostTagMatcher.Match(areas, [new HostTagValue("schule_61", 28)]).Should().BeEmpty();
    }

    // --- Vorschlaege: Tag schlaegt Muster --------------------------------

    private static readonly Dictionary<string, int> Nothing =
        new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_tag_wins_over_a_pattern_that_points_elsewhere()
    {
        // Genau der Fall aus dem Bestand: WLC-01SL-01 traegt schule_01, aber
        // die "01" steht im Namen nicht als eigenstaendige Zahl. Ein Muster
        // auf 1 wuerde ihn verfehlen — und ein Muster auf 01 saehe im Namen
        // die 01 von "01SL".
        var areas = new[]
        {
            Area(1, "Schule 1", tag: "schule_01"),
            Area(2, "Sammelbereich", pattern: "WLC"),
        };

        var s = AreaAssignmentSuggester.Suggest(areas, ["WLC-01SL-01"], Nothing,
            _ => "schule_01").Single();

        s.AreaId.Should().Be(1);
        s.Source.Should().Be(SuggestionSource.Tag);
        s.IsAmbiguous.Should().BeFalse();
        s.Note.Should().Contain("Tag");
    }

    [Fact]
    public void Hosts_without_a_tag_still_fall_back_to_the_pattern()
    {
        // Site LHP fuehrt praktisch keine Ortstags — dort ist das Muster der
        // einzige Weg und muss unveraendert funktionieren.
        var areas = new[] { Area(1, "Stadthaus", pattern: "stadthaus") };

        var s = AreaAssignmentSuggester.Suggest(areas, ["SW-STADTHAUS-01"], Nothing,
            _ => null).Single();

        s.AreaId.Should().Be(1);
        s.Source.Should().Be(SuggestionSource.Pattern);
    }

    [Fact]
    public void A_tag_no_area_claims_still_falls_back_to_the_pattern()
    {
        // Naheliegend waere die Gegenrichtung: Der Host sagt, wo er steht, also
        // soll ihn kein Muster woanders hinziehen. Das waere hier aber die
        // schlechtere Regel, weil nicht jeder Tag eine Ortsidentitaet ist —
        // auf LHP steht `tag_location = aussen` auf 8 Hosts und meint eine
        // Kategorie, keinen Standort. Wuerde ein Tag das Muster ausschalten,
        // fielen diese Hosts aus den Vorschlaegen heraus, ohne dass es jemandem
        // auffiele.
        //
        // Ein Muster, das zu weit greift, sieht man dagegen im Dialog: Der
        // Vorschlag steht mit „(Muster)" da und laesst sich abwaehlen. Sichtbar
        // falsch schlaegt unsichtbar fehlend.
        var areas = new[] { Area(1, "Sammelbereich", pattern: "SW") };

        var s = AreaAssignmentSuggester.Suggest(areas, ["61-SW01"], Nothing,
            _ => "schule_61").Single();

        s.Source.Should().Be(SuggestionSource.Pattern);
    }

    [Fact]
    public void Without_a_tag_source_the_behaviour_is_exactly_as_before()
    {
        var areas = new[] { Area(1, "Schule 46", pattern: HostPatternMatcher.FromCode("46")) };

        var s = AreaAssignmentSuggester.Suggest(areas, ["46-SW04"], Nothing).Single();

        s.AreaId.Should().Be(1);
        s.Source.Should().Be(SuggestionSource.Pattern);
    }

    [Fact]
    public void A_host_already_in_the_right_area_produces_no_suggestion()
    {
        var areas = new[] { Area(1, "Schule 46", tag: "schule_46") };
        var assigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["46-SW04"] = 1
        };

        AreaAssignmentSuggester.Suggest(areas, ["46-SW04"], assigned, _ => "schule_46")
            .Should().BeEmpty();
    }

    [Fact]
    public void Moving_a_host_by_tag_is_reported_as_a_move()
    {
        var areas = new[] { Area(1, "Container", tag: "schule_46"), Area(2, "Haus 2") };
        var assigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["46-SW04"] = 2
        };

        var s = AreaAssignmentSuggester.Suggest(areas, ["46-SW04"], assigned,
            _ => "schule_46").Single();

        s.WouldMove.Should().BeTrue();
        s.CurrentAreaName.Should().Be("Haus 2");
        s.Note.Should().Contain("verschiebt").And.Contain("Tag");
    }

    // --- „Speichern und zuordnen": Vorschlaege eines einzelnen Bereichs ----

    /// <summary>
    /// Der Fall, der den Knopf noetig gemacht hat: Ein Bereich bekommt seinen
    /// Ortstag, der Dialog meldet Treffer — und der Bereich bleibt leer, weil
    /// das Sichern der Regel nichts zuordnet. Die Vorschlaege fuer genau
    /// diesen Bereich muessen danach die Treffer enthalten.
    /// </summary>
    [Fact]
    public void Ein_frisch_getaggter_Bereich_hat_Vorschlaege()
    {
        var areas = new[] { Area(52, "Nord Gymnasium", tag: "schule_52") };
        string[] hosts = ["52-SW01", "52-USV", "ESX52-01", "fremd-01"];

        var fuerBereich = AreaAssignmentSuggester
            .Suggest(areas, hosts, Nothing,
                h => h.StartsWith("fremd", StringComparison.Ordinal) ? null : "schule_52")
            .Where(s => s.AreaId == 52)
            .ToList();

        fuerBereich.Select(s => s.HostName)
            .Should().BeEquivalentTo(["52-SW01", "52-USV", "ESX52-01"]);
    }

    /// <summary>
    /// Die Einschraenkung auf einen Bereich darf erst <b>nach</b> dem vollen
    /// Lauf greifen. Sonst fiele die Regel „trifft mehr als ein Bereich,
    /// gewinnt keiner" weg, und ein umstrittener Host wuerde diesem Bereich
    /// still zugeschlagen.
    /// </summary>
    [Fact]
    public void Ein_umstrittener_Host_kommt_mit_Konfliktvermerk_mit()
    {
        var areas = new[]
        {
            Area(52, "Nord Gymnasium", tag: "schule_52"),
            Area(53, "Aussenstelle", tag: "schule_52")
        };

        var alle = AreaAssignmentSuggester.Suggest(areas, ["52-SW01"], Nothing, _ => "schule_52");
        var fuerBereich = alle.Where(s => s.AreaId == 52).ToList();

        fuerBereich.Should().ContainSingle().Which.IsAmbiguous.Should().BeTrue(
            "der zweite Bereich beansprucht denselben Host");
        fuerBereich[0].ConflictingAreas.Should().Contain("Aussenstelle");
    }
}
