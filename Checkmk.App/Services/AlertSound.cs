using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NLog;

namespace Checkmk.App.Services;

/// <summary>
/// Spielt einen kurzen Warnton, wenn die Kiosk-Ausgabe wegen einer
/// Verschlechterung nach vorn kommt.
///
/// <para><b>Wozu:</b> Ein Bildschirm im Leitstand oder beim Wachschutz steht
/// oft seitlich. Dass das Fenster aufgeht, sieht nur, wer gerade hinschaut —
/// ein Ton holt den Blick dorthin. Genau deshalb hängt er am Aufspringen und
/// nicht am Refresh: Er soll dasselbe melden wie das Fenster, nicht mehr.</para>
///
/// <para><b>Warum P/Invoke statt eines Pakets:</b> <c>System.Media.SoundPlayer</c>
/// käme aus <c>System.Windows.Extensions</c>, und jedes zusätzliche NuGet-Paket
/// ist in diesem Netz teuer (403 auf <c>.nupkg</c>, Offline-Bundle von Hand).
/// <c>PlaySound</c> aus der winmm.dll kann beides — Datei und Systemklang —
/// und kostet nichts. Dasselbe Muster wie beim Tray-Ballon.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AlertSound
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const uint SndAsync = 0x0001;      // nicht auf das Ende warten
    private const uint SndNoDefault = 0x0002;  // bei Fehlschlag NICHT den Standard-Piep
    private const uint SndAlias = 0x00010000;  // Name ist ein Systemklang
    private const uint SndFilename = 0x00020000;

    /// <summary>Systemklang „Hinweis" — folgt dem Klangschema des Rechners,
    /// statt eine eigene Datei mitzuschleppen.</summary>
    private const string SystemAlias = "SystemExclamation";

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    /// <summary>
    /// Spielt <paramref name="wavFile"/>, sonst den Systemklang.
    ///
    /// <para><b>Immer asynchron</b> (<c>SND_ASYNC</c>): Synchron würde der
    /// Aufruf den UI-Thread für die Dauer des Klangs anhalten — ausgerechnet
    /// während das Fenster hochkommt und die betroffene Zeile markiert wird.</para>
    ///
    /// <para><b>Ein Fehlschlag ist kein Fehler.</b> Ein Kiosk-Rechner hat
    /// womöglich gar keine Tonausgabe, und der Ton ist die Zugabe — das
    /// Aufspringen des Fensters bleibt das eigentliche Signal. Deshalb nur
    /// <c>Debug</c> ins Log und weiter.</para>
    /// </summary>
    public static void PlayProblem(string? wavFile = null)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (!string.IsNullOrWhiteSpace(wavFile))
            {
                if (PlaySound(wavFile, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault))
                    return;

                // Datei weg oder kein WAV: lieber der Systemklang als Stille —
                // wer einen Ton eingestellt hat, will einen hoeren.
                Log.Debug("Warnton {File} nicht abspielbar — nutze den Systemklang.", wavFile);
            }

            if (!PlaySound(SystemAlias, IntPtr.Zero, SndAsync | SndAlias | SndNoDefault))
                Log.Debug("Systemklang '{Alias}' nicht abspielbar.", SystemAlias);
        }
        catch (Exception ex)
        {
            // DllNotFound/EntryPointNotFound auf einer abgespeckten Installation.
            Log.Debug(ex, "Warnton konnte nicht abgespielt werden.");
        }
    }
}
