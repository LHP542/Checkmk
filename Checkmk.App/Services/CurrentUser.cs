namespace Checkmk.App.Services;

/// <summary>
/// Der angemeldete Anwender — <b>eine</b> Stelle für die ganze Anwendung.
///
/// <para><b>Warum nicht überall direkt <c>Environment.UserName</c>:</b> Der
/// Anmeldename steht an mehreren Stellen in der Oberfläche (Vorgabe im
/// Verbindungsdialog samt Hinweistext, Name des persönlichen Start-Filters,
/// Autorschaft an Filtern). Das Werkzeug für die Doku-Bilder rendert genau
/// diese Fenster, und die Bilder liegen in einem <b>öffentlichen</b>
/// Repository — im ersten Lauf stand „Default: dein Windows-User (OsteL)" im
/// eingecheckten PNG.</para>
///
/// <para>Auf Windows liest <c>Environment.UserName</c> nicht die
/// Umgebungsvariable, sondern fragt <c>GetUserName</c> — ein
/// <c>SetEnvironmentVariable("USERNAME", …)</c> im Werkzeug würde also nichts
/// bewirken. Deshalb diese Hülle mit einem Schalter, statt der naheliegenden
/// Abkürzung.</para>
///
/// <para>Im Release-Build gibt es den Schalter nicht; dort ist
/// <see cref="Name"/> unverändert <c>Environment.UserName</c>.</para>
/// </summary>
public static class CurrentUser
{
#if DEBUG
    private static string? _demoName;

    /// <summary>Nur für <c>--screenshots</c>: ersetzt den Anmeldenamen
    /// prozesslokal durch einen erfundenen.</summary>
    internal static void UseDemoName(string name) => _demoName = name;

    public static string Name => _demoName ?? Environment.UserName;
#else
    public static string Name => Environment.UserName;
#endif
}
