namespace Checkmk.App.Services;

/// <summary>Was für ein Absatz das ist — bestimmt die Darstellung im Dialog.</summary>
public enum NoteBlockKind
{
    Heading,
    Subheading,
    Paragraph,
    Bullet,
    /// <summary>Codeblock oder Tabellenzeile: <b>nicht</b> umfließen lassen.</summary>
    Code,
    Quote,
    Rule
}

/// <param name="Text">Bei <see cref="NoteBlockKind.Code"/> mehrzeilig, sonst
/// ein zusammenhängender Absatz.</param>
public sealed record NoteBlock(NoteBlockKind Kind, string Text);

/// <summary>
/// Zerlegt Release-Notes in darstellbare Absätze.
///
/// <para><b>Warum überhaupt:</b> Die Notes sind Markdown-Dateien, im Repo auf
/// rund 78 Zeichen hart umbrochen. Roh in ein <c>TextBlock</c> mit
/// <c>TextWrapping</c> gekippt bricht der Text ein <i>zweites</i> Mal — das
/// Ergebnis franst mitten im Satz aus („Kein ⏎ Datenbank-Eingriff"). Deshalb
/// werden die harten Umbrüche <b>innerhalb eines Absatzes wieder
/// zusammengefügt</b> und erst vom Fenster neu umbrochen.</para>
///
/// <para>Bewusst <b>kein</b> vollständiger Markdown-Renderer: Gebraucht wird,
/// was in unseren Notes vorkommt — Überschriften, Absätze, Aufzählungen,
/// Codeblöcke, Tabellen und fette Stellen. Ein Paket dafür wäre in diesem Netz
/// teuer (siehe NuGet-Falle in §5) und für sechs Zeilenformate nicht zu
/// rechtfertigen.</para>
///
/// <para>Reine Funktion ohne Avalonia-Bezug — deshalb testbar, und deshalb
/// liegt sie hier und nicht im Dialog.</para>
/// </summary>
public static class ReleaseNotesFormatter
{
    public static IReadOnlyList<NoteBlock> Parse(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Sammler fuer den laufenden Absatz. Erst beim naechsten Blockwechsel
        // wird er abgelegt — nur so lassen sich hart umbrochene Zeilen wieder
        // zusammenfuegen.
        var buffer = new List<string>();
        var kind = NoteBlockKind.Paragraph;
        var inFence = false;

        void Flush()
        {
            if (buffer.Count == 0) return;
            // Codeblöcke behalten ihre Zeilen, alles andere fließt zusammen.
            var text = kind == NoteBlockKind.Code
                ? string.Join("\n", buffer).Trim('\n')
                : Clean(string.Join(" ", buffer.Select(l => l.Trim())));
            blocks.Add(new NoteBlock(kind, text));
            buffer.Clear();
            kind = NoteBlockKind.Paragraph;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            // Zaun auf/zu. Der Zaun selbst wird nicht mit angezeigt.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                inFence = !inFence;
                if (inFence) kind = NoteBlockKind.Code;
                continue;
            }

            if (inFence)
            {
                kind = NoteBlockKind.Code;
                buffer.Add(line);
                continue;
            }

            if (trimmed.Length == 0) { Flush(); continue; }

            // Tabellenzeilen bleiben Zeilen — umgeflossen waeren die Spalten hin.
            if (trimmed.StartsWith('|'))
            {
                if (kind != NoteBlockKind.Code) { Flush(); kind = NoteBlockKind.Code; }
                buffer.Add(trimmed);
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                Flush();
                blocks.Add(new NoteBlock(NoteBlockKind.Rule, ""));
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new NoteBlock(NoteBlockKind.Heading, Clean(trimmed[2..])));
                continue;
            }

            if (trimmed.StartsWith("## ", StringComparison.Ordinal)
                || trimmed.StartsWith("### ", StringComparison.Ordinal)
                || trimmed.StartsWith("#### ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new NoteBlock(NoteBlockKind.Subheading,
                    Clean(trimmed.TrimStart('#').TrimStart())));
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                if (kind != NoteBlockKind.Quote) { Flush(); kind = NoteBlockKind.Quote; }
                buffer.Add(trimmed.Length > 1 ? trimmed[2..] : "");
                continue;
            }

            if (IsBulletStart(trimmed))
            {
                Flush();
                kind = NoteBlockKind.Bullet;
                buffer.Add(StripBullet(trimmed));
                continue;
            }

            // Fortsetzungszeile: gehoert zum laufenden Absatz — genau hier
            // entsteht das Zusammenfuegen der harten Umbrueche.
            buffer.Add(trimmed);
        }

        Flush();
        return blocks;
    }

    /// <summary>
    /// Zerlegt eine Zeile in Stücke mit und ohne Fettung (<c>**…**</c>).
    /// Ungerade Anzahl an Markierungen: der Rest bleibt normal, statt den
    /// halben Absatz fett zu setzen.
    /// </summary>
    public static IReadOnlyList<(string Text, bool Bold)> Inline(string text)
    {
        var parts = new List<(string, bool)>();
        var segments = text.Split("**");

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0) continue;
            // Nur bei paarweise geschlossenen Markierungen fetten.
            var bold = i % 2 == 1 && segments.Length % 2 == 1;
            parts.Add((segments[i], bold));
        }

        return parts.Count == 0 ? [(text, false)] : parts;
    }

    private static bool IsBulletStart(string s)
        => s.StartsWith("- ", StringComparison.Ordinal)
        || s.StartsWith("* ", StringComparison.Ordinal)
        || (s.Length > 2 && char.IsDigit(s[0]) && s[1] == '.' && s[2] == ' ');

    private static string StripBullet(string s)
        => Clean(s.StartsWith("- ", StringComparison.Ordinal)
              || s.StartsWith("* ", StringComparison.Ordinal)
            ? s[2..]
            : s[(s.IndexOf('.') + 1)..].TrimStart());

    /// <summary>Backticks raus — Inline-Code als eigene Schrift zu setzen wäre
    /// in einem Absatz mehr Unruhe als Gewinn.</summary>
    private static string Clean(string s) => s.Replace("`", "").Trim();
}
