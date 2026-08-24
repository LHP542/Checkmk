using Checkmk.Data;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Filter-Katalog mit Abonnement — der Nachfolger des Team-Modells.
///
/// Der Unterschied ist nicht die Benennung, sondern das Prinzip:
/// <list type="bullet">
/// <item><b>Teams:</b> Wer im Team ist, sieht die Filter des Teams —
/// jemand muss Mitgliederlisten pflegen.</item>
/// <item><b>Katalog:</b> Wer abonniert, sieht — niemand pflegt etwas.</item>
/// </list>
/// </summary>
public class FilterCatalogTests
{
    private static FachbereichRow Fb(int id, string name) => new(id, name, null);

    private static HostFilterRow Personal(int id, string name, string owner)
        => new() { HostFilterId = id, Name = name, OwnerUserName = owner, Site = "LHP" };

    private static HostFilterRow Published(int id, string name, string owner, int fachbereich)
        => new()
        {
            HostFilterId = id, Name = name, OwnerUserName = owner,
            Site = "LHP", FachbereichId = fachbereich
        };

    /// <summary>
    /// Bildet die Auswahlregel aus <c>FilterStore.LoadAsync</c> nach: eigene
    /// Filter plus abonnierte, und abonniert zählt nur, solange der Filter auch
    /// veröffentlicht ist.
    /// </summary>
    private static IReadOnlyList<string> Visible(
        string user, IReadOnlyList<HostFilterRow> all, params int[] subscribed)
    {
        var subs = subscribed.ToHashSet();
        return [.. all
            .Where(f => f.OwnerUserName.Equals(user, StringComparison.OrdinalIgnoreCase)
                     || (f.FachbereichId is not null && subs.Contains(f.HostFilterId)))
            .Select(f => f.Name)];
    }

    // --- Sichtbarkeit ----------------------------------------------------

    [Fact]
    public void Ein_veroeffentlichter_Filter_erscheint_erst_nach_dem_Abo()
    {
        // Der Kern des Modells. Beim Team-Modell waere er automatisch da
        // gewesen, sobald man im Team ist.
        var all = new[] { Published(1, "Netz-Switche", "wer", 1) };

        Visible("OsteL", all).Should().BeEmpty();
        Visible("OsteL", all, 1).Should().BeEquivalentTo(["Netz-Switche"]);
    }

    [Fact]
    public void Eigene_Filter_sieht_man_immer_ohne_Abo()
    {
        var all = new[]
        {
            Personal(1, "Meine Kisten", "OsteL"),
            Published(2, "Mein geteilter", "OsteL", 1),
        };

        Visible("OsteL", all).Should()
            .BeEquivalentTo(["Meine Kisten", "Mein geteilter"]);
    }

    [Fact]
    public void Fremde_persoenliche_Filter_sieht_niemand()
    {
        // Auch ein Abo darauf waere wirkungslos — persoenlich heisst persoenlich.
        var all = new[] { Personal(1, "Fremdes", "wer") };

        Visible("OsteL", all).Should().BeEmpty();
        Visible("OsteL", all, 1).Should().BeEmpty();
    }

    [Fact]
    public void Ein_zurueckgezogener_Filter_verschwindet_bei_den_Abonnenten()
    {
        // Der Autor nimmt ihn aus dem Katalog (FachbereichId = null). Das Abo
        // steht noch in der Tabelle, darf aber nicht mehr greifen — sonst
        // bekaeme ein Fremder weiter einen Filter zu sehen, der nicht mehr
        // veroeffentlicht ist.
        var zurueckgezogen = Published(1, "War mal geteilt", "wer", 1);
        zurueckgezogen.FachbereichId = null;

        Visible("OsteL", [zurueckgezogen], 1).Should().BeEmpty();
        // Beim Autor bleibt er, als persoenlicher Filter.
        Visible("wer", [zurueckgezogen], 1).Should().BeEquivalentTo(["War mal geteilt"]);
    }

    [Fact]
    public void Wer_nichts_abonniert_hat_eine_kurze_Liste_und_keine_leere()
    {
        // Anders als beim Team-Modell gibt es hier keine „wer in keinem Team
        // ist, sieht alles"-Regel. Die eigenen Filter reichen als Startpunkt;
        // alles Weitere holt man sich aus dem Katalog.
        var all = new[]
        {
            Personal(1, "Meins", "OsteL"),
            Published(2, "Fremd A", "wer", 1),
            Published(3, "Fremd B", "wer", 2),
        };

        Visible("OsteL", all).Should().BeEquivalentTo(["Meins"]);
    }

    // --- Autorschaft -----------------------------------------------------

    [Fact]
    public void Ein_veroeffentlichter_Filter_behaelt_seinen_Autor()
    {
        // Beim Team-Modell schlossen sich TeamId und OwnerUserName aus, ein
        // geteilter Filter war herrenlos. Dann kann niemand einen Tippfehler
        // darin korrigieren.
        var f = Published(1, "Netz-Switche", "wer", 1);

        f.OwnerUserName.Should().Be("wer");
        f.FachbereichId.Should().Be(1);
    }

    [Theory]
    [InlineData("wer", true)]
    [InlineData("WER", true)]     // Anmeldenamen sind case-insensitive
    [InlineData("OsteL", false)]
    public void Aendern_darf_nur_der_Autor(string user, bool expected)
    {
        var f = Published(1, "Netz-Switche", "wer", 1);

        f.OwnerUserName.Equals(user, StringComparison.OrdinalIgnoreCase)
            .Should().Be(expected);
    }

    // --- Fachbereich als Ordnungsbegriff ---------------------------------

    [Fact]
    public void Der_Fachbereich_entscheidet_nicht_ueber_Sichtbarkeit()
    {
        // Genau der Unterschied zum Team: Es gibt keine Mitgliedschaft, die
        // hier geprueft wuerde. Ein Filter aus einem fremden Fachbereich ist
        // genauso abonnierbar wie einer aus dem eigenen.
        var all = new[]
        {
            Published(1, "Aus 5424", "wer", 1),
            Published(2, "Aus 5422", "wer", 2),
        };

        Visible("OsteL", all, 2).Should().BeEquivalentTo(["Aus 5422"]);
    }

    [Fact]
    public void Eine_leere_Adminliste_macht_jeden_zum_Admin()
    {
        // Gilt nur fuer das Verwalten der Fachbereiche. Veroeffentlichen darf
        // ohnehin jeder.
        new FachbereichSnapshot([], []).IsAdmin("irgendwer").Should().BeTrue();
        new FachbereichSnapshot([], ["OsteL"]).IsAdmin("OsteL").Should().BeTrue();
        new FachbereichSnapshot([], ["OsteL"]).IsAdmin("wer").Should().BeFalse();
    }

    [Fact]
    public void Der_Fachbereichsname_kommt_ueber_die_Id_und_nicht_aus_dem_Filter()
    {
        // Sonst zeigt ein umbenannter Fachbereich in alten Filtern weiter den
        // alten Namen.
        var snap = new FachbereichSnapshot([Fb(7, "5424 IT-Basis-Dienste")], []);

        snap.NameOf(7).Should().Be("5424 IT-Basis-Dienste");
        snap.NameOf(99).Should().BeNull();
        snap.NameOf(null).Should().BeNull();
    }
}
