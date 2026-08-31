using Checkmk.App.Services;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Die Release-Notes sind Markdown, im Repo auf rund 78 Zeichen hart
/// umbrochen. Roh in ein umbrechendes TextBlock gekippt bricht der Text ein
/// zweites Mal — mitten im Satz. Hier wird geprüft, dass die harten Umbrüche
/// innerhalb eines Absatzes wieder verschwinden und alles andere seine Form
/// behält.
/// </summary>
public class ReleaseNotesFormatterTests
{
    [Fact]
    public void Harte_Umbrueche_im_Absatz_werden_zusammengefuegt()
    {
        var blocks = ReleaseNotesFormatter.Parse(
            "Robustheit und zwei Korrekturen aus einer Durchsicht des\n" +
            "Codes. Kein\nDatenbank-Eingriff nötig.");

        blocks.Should().ContainSingle();
        blocks[0].Kind.Should().Be(NoteBlockKind.Paragraph);
        blocks[0].Text.Should().Be(
            "Robustheit und zwei Korrekturen aus einer Durchsicht des Codes. "
            + "Kein Datenbank-Eingriff nötig.");
    }

    [Fact]
    public void Eine_Leerzeile_trennt_zwei_Absaetze()
    {
        var blocks = ReleaseNotesFormatter.Parse("Erster Satz.\n\nZweiter Satz.");

        blocks.Should().HaveCount(2);
        blocks.Should().AllSatisfy(b => b.Kind.Should().Be(NoteBlockKind.Paragraph));
    }

    [Theory]
    [InlineData("# Titel", NoteBlockKind.Heading)]
    [InlineData("## Abschnitt", NoteBlockKind.Subheading)]
    [InlineData("### Tiefer", NoteBlockKind.Subheading)]
    public void Ueberschriften_werden_erkannt(string line, NoteBlockKind expected)
    {
        var block = ReleaseNotesFormatter.Parse(line).Should().ContainSingle().Subject;

        block.Kind.Should().Be(expected);
        block.Text.Should().NotStartWith("#");
    }

    [Fact]
    public void Aufzaehlungen_werden_je_Punkt_zu_einem_Block()
    {
        var blocks = ReleaseNotesFormatter.Parse("- Erster Punkt\n- Zweiter Punkt");

        blocks.Should().HaveCount(2);
        blocks.Should().AllSatisfy(b => b.Kind.Should().Be(NoteBlockKind.Bullet));
        blocks[0].Text.Should().Be("Erster Punkt");
    }

    [Fact]
    public void Nummerierte_Punkte_verlieren_ihre_Nummer_nicht_den_Text()
        => ReleaseNotesFormatter.Parse("1. Erster Punkt")
            .Should().ContainSingle().Subject.Text.Should().Be("Erster Punkt");

    /// <summary>
    /// Ein Codeblock behaelt seine Zeilen. Wuerde er umflossen, waere aus einer
    /// Log-Zeile ein Fliesstext — und genau die will man abtippen koennen.
    /// </summary>
    [Fact]
    public void Codebloecke_behalten_ihre_Zeilen()
    {
        var blocks = ReleaseNotesFormatter.Parse(
            "Im Log steht dann\n\n```\nZeile eins\nZeile zwei\n```\n\ndanach weiter.");

        var code = blocks.Should().ContainSingle(b => b.Kind == NoteBlockKind.Code).Subject;
        code.Text.Should().Be("Zeile eins\nZeile zwei");
        blocks.Should().HaveCount(3, "Absatz, Code, Absatz");
    }

    /// <summary>Tabellen sind der zweite Fall, in dem Zeilen Zeilen bleiben
    /// muessen — umgeflossen waeren die Spalten hin.</summary>
    [Fact]
    public void Tabellenzeilen_bleiben_Zeilen()
    {
        var blocks = ReleaseNotesFormatter.Parse(
            "| Name | Zweck |\n|---|---|\n| Alle | alles |");

        var table = blocks.Should().ContainSingle().Subject;
        table.Kind.Should().Be(NoteBlockKind.Code);
        table.Text.Split('\n').Should().HaveCount(3);
    }

    [Fact]
    public void Zitate_werden_als_solche_erkannt()
    {
        var block = ReleaseNotesFormatter.Parse(
            "> Für Administratoren: Schema 9\n> vorher einspielen.")
            .Should().ContainSingle().Subject;

        block.Kind.Should().Be(NoteBlockKind.Quote);
        block.Text.Should().Be("Für Administratoren: Schema 9 vorher einspielen.");
    }

    [Fact]
    public void Trennlinien_werden_zu_eigenen_Bloecken()
        => ReleaseNotesFormatter.Parse("Oben\n\n---\n\nUnten")
            .Should().Contain(b => b.Kind == NoteBlockKind.Rule);

    [Fact]
    public void Backticks_verschwinden_aus_dem_Fliesstext()
        => ReleaseNotesFormatter.Parse("Die Datei `viewer.json` daneben.")
            .Should().ContainSingle().Subject.Text.Should().Be("Die Datei viewer.json daneben.");

    [Fact]
    public void Leere_Notes_ergeben_keine_Bloecke()
    {
        ReleaseNotesFormatter.Parse(null).Should().BeEmpty();
        ReleaseNotesFormatter.Parse("   \n \n").Should().BeEmpty();
    }

    // --- Fettung ---------------------------------------------------------

    [Fact]
    public void Fette_Stellen_werden_als_solche_zurueckgegeben()
    {
        var parts = ReleaseNotesFormatter.Inline("Das ist **wichtig** hier.");

        parts.Should().HaveCount(3);
        parts[1].Should().Be(("wichtig", true));
        parts[0].Bold.Should().BeFalse();
        parts[2].Bold.Should().BeFalse();
    }

    /// <summary>
    /// Ein einzelnes Sternchenpaar, das nie geschlossen wird, darf nicht den
    /// halben Absatz fett setzen — lieber gar keine Fettung als eine falsche.
    /// </summary>
    [Fact]
    public void Eine_offene_Markierung_faerbt_nicht_den_Rest_ein()
        => ReleaseNotesFormatter.Inline("Ein **offener Anfang ohne Ende")
            .Should().AllSatisfy(p => p.Bold.Should().BeFalse());

    [Fact]
    public void Text_ohne_Markierung_bleibt_ein_Stueck()
        => ReleaseNotesFormatter.Inline("Ganz normaler Satz.")
            .Should().ContainSingle().Subject.Should().Be(("Ganz normaler Satz.", false));

    /// <summary>Gegen die echten Notes: Der Dialog soll daraus etwas
    /// Lesbares machen, nicht eine Wand.</summary>
    [Fact]
    public void Die_echten_Notes_zerfallen_in_sinnvolle_Bloecke()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "RELEASE_NOTES", "v1.20.3.md");
        if (!File.Exists(path)) return;   // im Release-ZIP nicht dabei

        var blocks = ReleaseNotesFormatter.Parse(File.ReadAllText(path));

        blocks.Should().Contain(b => b.Kind == NoteBlockKind.Subheading);
        blocks.Should().Contain(b => b.Kind == NoteBlockKind.Code);
        blocks.Should().AllSatisfy(b =>
            b.Text.Should().NotStartWith("#", "Markierungen gehoeren nicht in die Anzeige"));
    }
}
