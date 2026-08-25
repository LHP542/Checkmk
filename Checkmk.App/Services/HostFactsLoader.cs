using Checkmk.Core.Models;
using NLog;

namespace Checkmk.App.Services;

/// <summary>
/// Holt die Host-Konfiguration aus Checkmk und füllt daraus die beiden Caches,
/// an denen andere Ansichten hängen: <see cref="IHostOsCache"/> (OS-Pictogramme
/// im Statusbaum) und <see cref="IHostLocationTags"/> (Ortstags für die
/// Bereichszuordnung).
///
/// <para><b>Warum ein eigener Dienst.</b> Das lief bisher als Nebenwirkung des
/// Hosts-Tabs: Wer ihn nie öffnete, hatte keine OS-Symbole und bekam bei
/// „Tags zuordnen…" keine Vorschläge. Mit dem Ausblenden des Tabs wäre daraus
/// ein stiller Totalausfall geworden — die Funktionen hätten weiter existiert
/// und einfach nichts mehr gefunden.</para>
///
/// <para><b>Einmal beim Start, nicht bei jedem Refresh.</b> Die Abfrage holt
/// mit <c>effective_attributes=true</c> die vererbten Attribute aller ~1400
/// Hosts; das ist keine Antwort, die man alle 30 Sekunden braucht. OS und
/// Ortstag ändern sich im Setup, nicht im Betrieb.</para>
/// </summary>
public sealed class HostFactsLoader(
    ICheckmkClientProvider clients,
    IHostOsCache osCache,
    IHostLocationTags locationTags)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Wann zuletzt erfolgreich geladen — für die Anzeige und um
    /// doppelte Läufe beim Site-Wechsel zu erkennen.</summary>
    public DateTime? LastLoadedUtc { get; private set; }

    public bool HasData => LastLoadedUtc is not null;

    /// <summary>
    /// Lädt und verteilt. Wirft nicht — ein Fehlschlag darf den Start nicht
    /// aufhalten; die abhängigen Funktionen melden sich dann von selbst
    /// („keine Ortstags gelesen — einmal aktualisieren").
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var client = clients.Current;
        if (client is null)
        {
            Log.Debug("Host-Attribute nicht geladen — keine Verbindung konfiguriert.");
            return false;
        }

        try
        {
            // effective_attributes=true: sonst kommen vererbte Custom Attributes
            // (z. B. „Operation System" auf Folder-Ebene gesetzt) nicht durch.
            var hosts = await client.GetHostConfigsAsync(effectiveAttributes: true, ct)
                .ConfigureAwait(false);

            osCache.ApplyFromHostConfigs(hosts);
            locationTags.ApplyFromHostConfigs(hosts);

            LastLoadedUtc = DateTime.UtcNow;
            Log.Info("Host-Attribute geladen: {Count} Hosts.", hosts.Count);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Host-Attribute konnten nicht geladen werden.");
            return false;
        }
    }

    /// <summary>Wie <see cref="RefreshAsync"/>, gibt aber zusätzlich die
    /// Host-Liste zurück — für Aufrufer, die sie selbst anzeigen wollen.</summary>
    public async Task<IReadOnlyList<CheckmkObject<HostConfigExtensions>>> LoadAsync(
        CancellationToken ct = default)
    {
        var client = clients.Current;
        if (client is null) return [];

        var hosts = await client.GetHostConfigsAsync(effectiveAttributes: true, ct)
            .ConfigureAwait(false);

        osCache.ApplyFromHostConfigs(hosts);
        locationTags.ApplyFromHostConfigs(hosts);
        LastLoadedUtc = DateTime.UtcNow;
        return hosts;
    }
}
