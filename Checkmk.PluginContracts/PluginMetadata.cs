namespace Checkmk.PluginContracts;

/// <summary>
/// Metadaten eines Plugins — vom Cockpit fuer About-Anzeige, Log,
/// Konflikt-Detection und Auto-Update genutzt.
/// </summary>
/// <param name="UpdateChannelUrl">
/// Optional. Releases-API-URL fuer den Auto-Update-Check, z. B.
/// <c>https://git.flexfamily.de/api/v1/repos/LHP542/Checkmk-Plugin-AgentUpdater/releases/latest</c>.
/// Fehlt der Wert, wird das Plugin vom Cockpit-Plugin-Update-Check uebersprungen.
/// Konvention fuer das Release-Asset: <c>&lt;RepoName&gt;-X.Y.Z.zip</c>, mit
/// genau einer <c>CheckmkPlugin.&lt;Name&gt;.dll</c> drin.
/// </param>
public sealed record PluginMetadata(
    string Id,
    string Name,
    string Version,
    string Description = "",
    string Author = "",
    string? UpdateChannelUrl = null);
