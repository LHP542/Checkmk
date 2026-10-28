using Xunit;

/// <summary>
/// Tests, die echtes Windows voraussetzen: Benutzerprofile unter
/// C:\Users, %APPDATA%, Pfadtrenner. Unter Linux pruefen sie nichts
/// Sinnvolles und wuerden nur rot leuchten.
///
/// <para><b>Uebersprungen, nicht uebergangen.</b> Assert.Skip meldet den
/// Test als "skipped"; ein stilles return liesse ihn als bestanden
/// erscheinen, obwohl nichts geprueft wurde.</para>
/// </summary>
internal static class WindowsOnly
{
    public static void Require() 
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Dieser Test setzt Windows voraus.");
    }
}
