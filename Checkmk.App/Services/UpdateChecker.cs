using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Checkmk.Core;
using NLog;

namespace Checkmk.App.Services;

public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string ReleaseNotes,
    string ReleasePageUrl,
    string? WindowsZipUrl,
    /// <summary>Adresse des signierten Manifests (<c>update.json</c>) im Release,
    /// oder <c>null</c>. Fehlt es bei eingeschalteter Signaturpruefung, wird das
    /// Update abgelehnt.</summary>
    string? ManifestUrl = null);

/// <summary>Ergebnis eines manuell ausgeloesten Update-Checks.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>Eine neuere Version ist verfuegbar (<see cref="UpdateCheckResult.Info"/> gesetzt).</summary>
    UpdateAvailable,
    /// <summary>Die installierte Version ist aktuell.</summary>
    UpToDate,
    /// <summary>Der Check ist fehlgeschlagen (Netz/Proxy/Parsing) — Details im Log.</summary>
    Failed
}

/// <summary>Reichhaltiges Ergebnis fuer die manuelle Pruefung; <see cref="Info"/> nur bei <see cref="UpdateCheckOutcome.UpdateAvailable"/>.</summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateInfo? Info);

public interface IUpdateChecker
{
    /// <summary>
    /// Automatischer Check (Startup): fragt den Update-Kanal an, vergleicht mit der
    /// laufenden Assembly-Version und beruecksichtigt die vom Nutzer uebersprungene
    /// Version. Liefert <c>null</c>, wenn keine neuere Version verfuegbar ist oder der
    /// Check fehlschlaegt (Fehler werden geloggt, nie ins UI durchgereicht).
    /// </summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Manueller Check (About-Box): wie <see cref="CheckAsync"/>, aber
    /// (a) ignoriert eine zuvor uebersprungene Version — wer aktiv prueft, will es wissen —
    /// und (b) unterscheidet die Faelle aktuell / verfuegbar / fehlgeschlagen fuers UI-Feedback.
    /// </summary>
    Task<UpdateCheckResult> CheckManuallyAsync(CancellationToken ct = default);
}

/// <summary>
/// Fragt <c>GET /repos/{owner}/{repo}/releases/latest</c> ab. Draft- und
/// Prerelease-Versionen filtert der Endpunkt selbst; wir vertrauen ihm.
/// Trust-Anker: TLS-Zertifikat des Servers + Repo-Owner in der URL.
///
/// <para>Die Quelle ist seit dem 28.10.2026 <c>git.flexfamily.de</c>; Forgejos
/// Release-API ist feldgleich zu GitHubs. Auf den Dienstrechnern laeuft
/// stattdessen der <see cref="FileShareUpdateChecker"/> -- sie laden nichts
/// aus dem Internet.</para>
/// </summary>
public sealed class GitHubReleasesUpdateChecker : IUpdateChecker
{
    /// <summary>Dateiname des signierten Manifests im Release-Asset-Satz.</summary>
    public const string UpdateManifestFileName = "update.json";

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly IUpdatePreferences _prefs;

    public GitHubReleasesUpdateChecker(HttpClient http, string apiUrl, IUpdatePreferences prefs)
    {
        _http = http;
        _apiUrl = apiUrl;
        _prefs = prefs;

        // GitHub verlangt einen User-Agent, sonst 403.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Checkmk-Cockpit/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // Automatisch: nur ein tatsaechlich verfuegbares (nicht uebersprungenes)
        // Update wird gemeldet; alles andere bleibt still (null).
        var (outcome, info) = await EvaluateAsync(honorSkip: true, ct);
        return outcome == UpdateCheckOutcome.UpdateAvailable ? info : null;
    }

    public async Task<UpdateCheckResult> CheckManuallyAsync(CancellationToken ct = default)
    {
        // Manuell: honorSkip=false — wer aktiv prueft, soll auch eine zuvor
        // uebersprungene Version wieder sehen. Ergebnis wird 1:1 fuers UI-Feedback
        // zurueckgegeben (aktuell / verfuegbar / fehlgeschlagen).
        var (outcome, info) = await EvaluateAsync(honorSkip: false, ct);
        return new UpdateCheckResult(outcome, info);
    }

    /// <summary>
    /// Gemeinsame Kernlogik beider Checks. <paramref name="honorSkip"/> steuert, ob
    /// eine vom Nutzer uebersprungene Version als "nichts anzuzeigen" (UpToDate) gilt.
    /// </summary>
    private async Task<(UpdateCheckOutcome Outcome, UpdateInfo? Info)> EvaluateAsync(
        bool honorSkip, CancellationToken ct)
    {
        // Eine Quelle fuer die laufende Version — nie GetName().Version, das
        // liefert bei MinVer nur Major.0.0.0. Begruendung an AppVersion.Current.
        var current = AppVersion.Current;

        GitHubRelease? release;
        try
        {
            release = await _http.GetFromJsonAsync<GitHubRelease>(_apiUrl, ct);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Update-Check fehlgeschlagen ({Url}).", _apiUrl);
            return (UpdateCheckOutcome.Failed, null);
        }
        if (release is null || string.IsNullOrEmpty(release.TagName))
        {
            Log.Debug("Update-Kanal lieferte kein verwertbares Release.");
            return (UpdateCheckOutcome.Failed, null);
        }

        if (!SemVerTag.TryParse(release.TagName, out var latest))
        {
            Log.Debug("Konnte Release-Tag '{Tag}' nicht als Version parsen.", release.TagName);
            return (UpdateCheckOutcome.Failed, null);
        }

        if (Normalize(latest) <= Normalize(current))
            return (UpdateCheckOutcome.UpToDate, null);

        if (honorSkip)
        {
            var skipped = _prefs.LoadSkippedVersion();
            if (skipped is not null && Normalize(skipped) >= Normalize(latest))
            {
                Log.Debug("Neuere Version {Latest} verfuegbar, aber vom User uebersprungen.", latest);
                return (UpdateCheckOutcome.UpToDate, null);
            }
        }

        var zip = release.Assets?
            .FirstOrDefault(a => a.Name is not null &&
                                 a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
                                 a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?.BrowserDownloadUrl;

        // Das signierte Manifest liegt als eigenes Asset neben dem ZIP.
        var manifest = release.Assets?
            .FirstOrDefault(a => a.Name is not null &&
                                 a.Name.Equals(UpdateManifestFileName, StringComparison.OrdinalIgnoreCase))
            ?.BrowserDownloadUrl;

        var info = new UpdateInfo(
            Version: latest,
            TagName: release.TagName,
            ReleaseNotes: release.Body ?? "",
            ReleasePageUrl: release.HtmlUrl ?? "",
            WindowsZipUrl: zip,
            ManifestUrl: manifest);
        return (UpdateCheckOutcome.UpdateAvailable, info);
    }

    // Version.CompareTo unterscheidet zwischen "1.4.0" (Revision=-1) und "1.4.0.0"
    // (Revision=0). Beide auf 4 Segmente normalisieren, damit der Vergleich zaehlt,
    // was der User sieht (Major.Minor.Patch).
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    // ---- GitHub API DTOs ----
    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
