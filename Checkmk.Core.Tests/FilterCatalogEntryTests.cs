using Checkmk.App.Models;
using Checkmk.App.Views;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Die Zeile im Filter-Katalog. Hier entscheidet sich, ob der Löschen-Knopf
/// überhaupt erscheint — und Löschen ist die einzige Aktion im Katalog, die
/// sich nicht zurücknehmen lässt.
/// </summary>
public class FilterCatalogEntryTests
{
    private const string Me = "OsteL";

    private static HostFilter Published(string owner, int subscribers) => new()
    {
        Id = 11,
        Name = "CTX",
        FachbereichId = 1,
        FachbereichName = "Netzwerk",
        Owner = owner,
        Subscribers = subscribers,
        HostNameRegex = ".*ctx.*"
    };

    private static CatalogEntry Entry(HostFilter f, bool subscribed, bool isAdmin = false)
        => new(f, Me, subscribed, matchCount: 7, isAdmin);

    /// <summary>
    /// Der Autor ist beim Veröffentlichen selbst abonniert. Solange er das
    /// Häkchen stehen lässt, hat der Filter einen Abonnenten — und wird nicht
    /// gelöscht.
    /// </summary>
    [Fact]
    public void Der_eigene_abonnierte_Filter_ist_nicht_loeschbar()
        => Entry(Published(Me, subscribers: 1), subscribed: true)
            .CanDelete.Should().BeFalse();

    /// <summary>
    /// Das Häkchen wegzunehmen muss den Knopf <b>sofort</b> freigeben. Sonst
    /// müsste man übernehmen, schließen und den Katalog neu öffnen, nur damit
    /// „Löschen" angeht.
    /// </summary>
    [Fact]
    public void Das_eigene_Abo_wegzunehmen_gibt_das_Loeschen_sofort_frei()
    {
        var entry = Entry(Published(Me, subscribers: 1), subscribed: true);

        entry.IsSubscribed = false;

        entry.CanDelete.Should().BeTrue();
        entry.Meta.Should().Contain("0 Abo");
    }

    /// <summary>Ein Fremder abonniert noch — dann bleibt der Filter stehen,
    /// auch wenn ich der Autor bin.</summary>
    [Fact]
    public void Solange_ein_anderer_abonniert_bleibt_der_Filter()
    {
        var entry = Entry(Published(Me, subscribers: 2), subscribed: true);

        entry.IsSubscribed = false;

        entry.CanDelete.Should().BeFalse("ein Fremder hat ihn noch in seiner Auswahl");
    }

    [Fact]
    public void Fremde_Filter_darf_man_nicht_loeschen()
        => Entry(Published("GuentherJ", subscribers: 0), subscribed: false)
            .CanDelete.Should().BeFalse();

    /// <summary>Aufräumen muss möglich bleiben, wenn der Autor das Haus
    /// verlassen hat.</summary>
    [Fact]
    public void Ein_Admin_darf_einen_verwaisten_Filter_loeschen()
        => Entry(Published("GuentherJ", subscribers: 0), subscribed: false, isAdmin: true)
            .CanDelete.Should().BeTrue();

    /// <summary>
    /// <b>Jeder Filter ist abbestellbar, auch der eigene.</b> Vorher stand das
    /// Häkchen beim eigenen fest an — wer einen Filter für den Fachbereich
    /// baute, den er selbst nicht braucht, bekam ihn nicht aus seiner Auswahl.
    /// </summary>
    [Fact]
    public void Auch_der_eigene_Filter_laesst_sich_abbestellen()
        => Entry(Published(Me, subscribers: 1), subscribed: true)
            .CanUnsubscribe.Should().BeTrue();

    /// <summary>Das Vormerken muss an der Zeile sichtbar werden — sonst ist die
    /// einzige Rueckmeldung fuer eine unumkehrbare Aktion der kleine Zaehler
    /// in der Fusszeile.</summary>
    [Fact]
    public void Vorgemerktes_Loeschen_schlaegt_auf_die_Zeile_durch()
    {
        var entry = Entry(Published(Me, subscribers: 1), subscribed: true);
        var geaendert = new List<string>();
        entry.PropertyChanged += (_, e) => geaendert.Add(e.PropertyName ?? "");

        entry.IsDeleted = true;

        entry.IsAlive.Should().BeFalse();
        geaendert.Should().Contain(nameof(CatalogEntry.IsAlive));
    }

    /// <summary>Das Ziel gehoert in die Anzeige: derselbe Ausdruck bedeutet
    /// gegen den Alias etwas voellig anderes als gegen den Hostnamen.</summary>
    [Fact]
    public void Die_Regel_nennt_das_Ziel_des_Regex()
    {
        var byName = Published(Me, 0);
        Entry(byName, false).Rule.Should().StartWith("Hostname ~");

        var byAlias = Published(Me, 0);
        byAlias.Target = FilterTarget.Alias;
        Entry(byAlias, false).Rule.Should().StartWith("Alias ~");
    }
}
