using System.Runtime.Versioning;
using Microsoft.Win32;
using NLog;

namespace Checkmk.App.Services;

/// <summary>
/// Startet das Cockpit beim Anmelden mit — über den <c>Run</c>-Schlüssel des
/// <b>angemeldeten Benutzers</b>.
///
/// <para><b>HKCU, nicht HKLM.</b> Der Autostart ist die Entscheidung dessen, der
/// vor dem Rechner sitzt, nicht die des Administrators. Unter HKCU braucht es
/// keine erhöhten Rechte — ein Häkchen, das nach einer UAC-Abfrage verlangt,
/// setzt niemand.</para>
///
/// <para><b>Registry statt Verknüpfung im Autostart-Ordner.</b> Eine <c>.lnk</c>
/// zu erzeugen geht in .NET nur über COM (<c>IShellLink</c>); der Run-Schlüssel
/// ist zwei Zeilen, lässt sich mit Bordmitteln prüfen
/// (<c>Get-ItemProperty "HKCU:\…\Run"</c>) und ist auf diesen Arbeitsplätzen
/// der eingeführte Weg — der Proxy-Helfer px macht es genauso.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutoStart
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Name des Eintrags. Fest, damit ein zweiter Aufruf den alten
    /// ersetzt statt einen weiteren anzulegen.</summary>
    private const string ValueName = "Checkmk Cockpit";

    /// <summary>
    /// Schalter, mit dem die App beim Autostart hochkommt: sofort ins Tray,
    /// ohne Fenster.
    ///
    /// <b>Muss in <c>Program.KnownSwitches</c> stehen</b>, sonst fängt ihn die
    /// Kurzhilfe ab und die App startet gar nicht — der Autostart wäre dann eine
    /// Konsolenausgabe, die niemand sieht.
    /// </summary>
    public const string TraySwitch = "--tray";

    /// <summary>
    /// Pfad der laufenden Exe. <b>Nicht <c>Assembly.Location</c></b> — der ist im
    /// Single-File-Build leer, und der Autostart-Eintrag zeigte dann ins Nichts.
    /// </summary>
    private static string? ExecutablePath => Environment.ProcessPath;

    /// <summary>Steht ein Eintrag, und zeigt er auf <i>diese</i> Exe?</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string existing
                    && ExecutablePath is { } exe
                    && existing.Contains(exe, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Autostart-Eintrag nicht lesbar.");
                return false;
            }
        }
    }

    /// <summary>
    /// Setzt oder entfernt den Eintrag. Gibt eine Meldung zurück, wenn es nicht
    /// geklappt hat — sonst <c>null</c>.
    ///
    /// Der Pfad wird bei jedem Einschalten <b>neu geschrieben</b>: Nach einem
    /// Update oder einem Umzug des Programmordners zeigte ein alter Eintrag
    /// sonst auf eine Exe, die es nicht mehr gibt.
    /// </summary>
    public static string? Set(bool enabled)
    {
        if (ExecutablePath is not { } exe)
            return "Der Pfad der Anwendung ließ sich nicht ermitteln.";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return "Der Autostart-Schlüssel ließ sich nicht öffnen.";

            if (enabled)
            {
                // Anfuehrungszeichen sind Pflicht: Der Programmordner enthaelt
                // Leerzeichen („C:\Program Files\…"), und Windows startet sonst
                // „C:\Program" mit dem Rest als Argumenten.
                key.SetValue(ValueName, $"\"{exe}\" {TraySwitch}");
                Log.Info("Autostart eingeschaltet: {Exe}", exe);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Autostart ausgeschaltet.");
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Autostart konnte nicht gesetzt werden.");
            return $"Autostart konnte nicht gesetzt werden: {ex.Message}";
        }
    }
}
