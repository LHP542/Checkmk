using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Checkmk.Core.Exceptions;
using Checkmk.Core.Models;
using Microsoft.Extensions.Options;
using NLog;

namespace Checkmk.Core;

/// <summary>
/// Typisierter Client fuer die Checkmk REST-API (Version v1, Checkmk 2.5.x).
///
/// Auth: Bearer-Header im Checkmk-Format "Bearer {user} {secret}"
/// (Username und Secret durch ein Leerzeichen getrennt).
///
/// Wichtig: Der HTTP-Statuscode bestaetigt nur die Uebertragung, nicht die
/// fachliche Ausfuehrung. Kommandos (Downtime/Ack) werden serverseitig via
/// Livestatus verarbeitet -> bei Bedarf danach den Zustand erneut abfragen.
/// </summary>
public sealed class CheckmkClient
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // WhenWritingNull: Checkmk lehnt null-Werte im "attributes"-Block ab
    // ("These fields have problems: attributes"). Nicht gesetzte Attribute
    // muessen weggelassen, nicht als null gesendet werden.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    /// <summary>Site der Verbindung — Pflichtfeld einiger Kommando-Endpunkte
    /// (z. B. Kommentar löschen).</summary>
    private readonly string _site;

    public CheckmkClient(HttpClient http, IOptions<CheckmkOptions> options)
        : this(http, options.Value) { }

    public CheckmkClient(HttpClient http, CheckmkOptions options)
    {
        _http = http;
        _site = options.Site;
        _http.BaseAddress = options.BaseUri;
        _http.Timeout = options.Timeout;
        _http.DefaultRequestHeaders.Authorization = BuildAuthHeader(options);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Secret niemals im Klartext loggen.
        Log.Debug("CheckmkClient initialisiert fuer {BaseUri} (User={User}, AuthMode={Mode}, Secret={Secret})",
            options.BaseUri, options.Username, options.AuthMode, Mask(options.Secret));
    }

    private static AuthenticationHeaderValue BuildAuthHeader(CheckmkOptions options) =>
        options.AuthMode switch
        {
            CheckmkAuthMode.UserBasic => new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{options.Username}:{options.Secret}"))),
            _ => new AuthenticationHeaderValue("Bearer", $"{options.Username} {options.Secret}")
        };

    // ---------------------------------------------------------------------
    // Read: Version / Setup / Livestatus
    // ---------------------------------------------------------------------

    /// <summary>GET /version — praktisch zum Verbindungstest und Editions-Check.</summary>
    public Task<CheckmkVersionInfo> GetVersionAsync(CancellationToken ct = default)
        => GetAsync<CheckmkVersionInfo>("version", ct);

    /// <summary>Alle konfigurierten Hosts (Setup-Seite).</summary>
    public async Task<IReadOnlyList<CheckmkObject<HostConfigExtensions>>> GetHostConfigsAsync(
        bool effectiveAttributes = false, CancellationToken ct = default)
    {
        var url = $"domain-types/host_config/collections/all?effective_attributes={effectiveAttributes.ToString().ToLowerInvariant()}";
        var result = await GetAsync<CheckmkCollection<CheckmkObject<HostConfigExtensions>>>(url, ct);
        return result.Value;
    }

    /// <summary>
    /// Ein einzelner konfigurierter Host inkl. Attribute. Praktisch fuer die
    /// Host-Detailansicht — vermeidet den Bulk-Abruf ueber alle Hosts.
    /// </summary>
    public Task<CheckmkObject<HostConfigExtensions>> GetHostConfigAsync(string hostName,
        bool effectiveAttributes = true, CancellationToken ct = default)
        => GetAsync<CheckmkObject<HostConfigExtensions>>(
            $"objects/host_config/{Uri.EscapeDataString(hostName)}"
            + $"?effective_attributes={effectiveAttributes.ToString().ToLowerInvariant()}", ct);

    /// <summary>Live-Status aller Hosts (Monitoring/Livestatus), optional
    /// serverseitig gefiltert (Regex oder Include-Liste).
    /// <paramref name="progress"/> meldet den Download-Fortschritt in Bytes.</summary>
    public async Task<IReadOnlyList<HostStatus>> GetHostStatusesAsync(
        LivestatusHostFilter? filter = null, CancellationToken ct = default,
        IProgress<TransferProgress>? progress = null)
    {
        var cols = new[]
        {
            "name", "alias", "state", "plugin_output", "acknowledged", "scheduled_downtime_depth",
            "last_check", "last_state_change"
        };
        var url = "domain-types/host/collections/all?" + ColumnsQuery(cols, hostNameCol: "name");

        // Livestatus-Query auf dem host-Endpunkt: die Spalten heissen hier
        // "name" und "alias", nicht "host_name"/"host_alias". Frueher stand
        // hier ein string.Replace auf dem fertigen JSON — das traf den
        // Alias-Filter nicht und quittierte jeden Refresh mit
        // „400 These fields have problems: query". Die Namen entstehen deshalb
        // jetzt an der Quelle.
        if (filter?.ToJson(hostTable: true) is { } queryJson)
            url += "&query=" + Uri.EscapeDataString(queryJson);

        var result = await GetAsync<CheckmkCollection<HostStatusEnvelope>>(url, ct, progress)
            .ConfigureAwait(false);
        return result.Value.Select(v => v.Extensions).ToList();
    }

    /// <summary>
    /// Live-Status eines einzelnen Hosts (Livestatus-Query filtert serverseitig
    /// ueber <c>name</c>). Liefert <c>null</c>, wenn der Host nicht ueberwacht wird.
    /// </summary>
    public async Task<HostStatus?> GetHostStatusAsync(string hostName, CancellationToken ct = default)
    {
        var cols = new[] { "name", "state", "plugin_output", "acknowledged", "scheduled_downtime_depth" };
        var query = JsonSerializer.Serialize(new { op = "=", left = "name", right = hostName });
        var url = "domain-types/host/collections/all?" + ColumnsQuery(cols)
                + "&query=" + Uri.EscapeDataString(query);

        var result = await GetAsync<CheckmkCollection<HostStatusEnvelope>>(url, ct);
        return result.Value.Select(v => v.Extensions).FirstOrDefault();
    }

    /// <summary>
    /// Live-Status von Services. Optional auf einen einzelnen Host gefiltert
    /// (Livestatus-Query ueber host_name = X).
    /// </summary>
    public Task<IReadOnlyList<ServiceStatus>> GetServiceStatusesAsync(
        string? hostName = null, CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(hostName)
            ? null
            : new LivestatusHostFilter { IncludeHosts = new[] { hostName } };
        return GetServiceStatusesAsync(filter, ct);
    }

    /// <summary>
    /// Live-Status von Services mit serverseitig-filternder Livestatus-Query
    /// (Regex oder Include-Liste auf host_name). Reduziert bei grossen
    /// Installationen die Antwortgroesse drastisch.
    /// <paramref name="progress"/> meldet den Download-Fortschritt in Bytes —
    /// das ist der Abruf, der bei ungefiltertem Blick auf zehntausende Checks
    /// mehrere Sekunden laeuft.
    /// </summary>
    public async Task<IReadOnlyList<ServiceStatus>> GetServiceStatusesAsync(
        LivestatusHostFilter? filter, CancellationToken ct = default,
        IProgress<TransferProgress>? progress = null)
    {
        // display_name ist eine Standard-Livestatus-Spalte (Service-Alias, sonst
        // identisch mit description) — sie kostet nichts und die Viewer-Sicht
        // „service_display_name" braucht sie.
        var cols = new[]
        {
            "host_name", "host_alias", "description", "display_name", "state", "plugin_output",
            "acknowledged", "scheduled_downtime_depth", "last_check", "last_state_change"
        };
        var url = "domain-types/service/collections/all?" + ColumnsQuery(cols);

        if (filter?.ToJson() is { } queryJson)
            url += "&query=" + Uri.EscapeDataString(queryJson);

        var result = await GetAsync<CheckmkCollection<ServiceStatusEnvelope>>(url, ct, progress)
            .ConfigureAwait(false);
        return result.Value.Select(v => v.Extensions).ToList();
    }

    // ---------------------------------------------------------------------
    // Write: Host anlegen / Ack / Downtime / Changes aktivieren
    // ---------------------------------------------------------------------

    /// <summary>Legt einen Host an (Setup). Vergiss nicht ActivateChangesAsync danach.</summary>
    public async Task CreateHostAsync(string hostName, string folder = "/",
        HostAttributes? attributes = null, CancellationToken ct = default)
    {
        var payload = new
        {
            folder,
            host_name = hostName,
            attributes = attributes ?? new HostAttributes()
        };
        using var resp = await _http.PostAsJsonAsync(
            "domain-types/host_config/collections/all", payload, JsonOpts, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>Acknowledged ein Host-Problem.</summary>
    public async Task AcknowledgeHostProblemAsync(string hostName, string comment,
        bool sticky = true, bool notify = true, bool persistent = false,
        CancellationToken ct = default)
    {
        var payload = new
        {
            acknowledge_type = "host",
            host_name = hostName,
            sticky,
            notify,
            persistent,
            comment
        };
        using var resp = await _http.PostAsJsonAsync(
            "domain-types/acknowledge/collections/host", payload, JsonOpts, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>Acknowledged ein einzelnes Service-Problem.</summary>
    public async Task AcknowledgeServiceProblemAsync(string hostName, string serviceDescription,
        string comment, bool sticky = true, bool notify = true, bool persistent = false,
        CancellationToken ct = default)
    {
        var payload = new
        {
            acknowledge_type = "service",
            host_name = hostName,
            service_description = serviceDescription,
            sticky,
            notify,
            persistent,
            comment
        };
        using var resp = await _http.PostAsJsonAsync(
            "domain-types/acknowledge/collections/service", payload, JsonOpts, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>
    /// Nimmt die Quittierung eines Host-Problems zurück.
    ///
    /// <para><b>Nicht zu verwechseln mit dem Löschen des Kommentars</b>, den
    /// das Quittieren angelegt hat: Der Kommentar ist nur die Notiz dazu, die
    /// Quittierung hängt am Host. Ihn zu löschen lässt das Häkchen stehen.</para>
    /// </summary>
    public async Task RemoveHostAcknowledgementAsync(string hostName,
        CancellationToken ct = default)
    {
        var payload = new { acknowledge_type = "host", host_name = hostName };

        using var resp = await _http.PostAsJsonAsync(
            "domain-types/acknowledge/actions/delete/invoke", payload, JsonOpts, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>
    /// Nimmt die Quittierung einzelner Service-Probleme zurück.
    ///
    /// <para><b>Immer ausdrücklich benannt, nie über eine Abfrage.</b> Checkmk
    /// kennt für diesen Endpunkt auch <c>*_by_query</c>-Varianten; die bleiben
    /// hier bewusst draußen. Eine Abfrage, die einen Tick zu weit greift,
    /// entquittiert stillschweigend fremde Probleme — und das fällt niemandem
    /// auf, weil nichts rot wird, was vorher grün war. Mit Hostname und
    /// Service-Liste kann der Aufruf per Konstruktion nur treffen, was hier
    /// steht.</para>
    ///
    /// <para><b>Zur Feldschreibweise:</b> Beim Anlegen heißt es
    /// <c>service_description</c> (Einzahl), beim Löschen erwartet Checkmk eine
    /// Liste. Welche Schreibweise ein Stand genau will, steht nur in der
    /// Spezifikation der jeweiligen Site — deshalb wird bei einer
    /// Feldbeanstandung einmal die andere Form versucht. Beide sind gleich eng
    /// gefasst; es ist eine Frage der Schreibweise, nicht des Umfangs.</para>
    /// </summary>
    public async Task RemoveServiceAcknowledgementAsync(string hostName,
        IReadOnlyList<string> serviceDescriptions, CancellationToken ct = default)
    {
        if (serviceDescriptions.Count == 0) return;

        var plural = new
        {
            acknowledge_type = "service",
            host_name = hostName,
            service_descriptions = serviceDescriptions
        };

        using (var resp = await _http.PostAsJsonAsync(
            "domain-types/acknowledge/actions/delete/invoke", plural, JsonOpts, ct))
        {
            if (resp.IsSuccessStatusCode) return;

            // Nur bei einer Feldbeanstandung die andere Schreibweise probieren.
            // Ein 403 (Rechte) oder 404 (Endpunkt fehlt) ist keine.
            if (resp.StatusCode != HttpStatusCode.BadRequest)
            {
                await EnsureSuccessAsync(resp, ct);
                return;
            }

            Log.Debug("Ack-Entfernen mit service_descriptions abgelehnt — "
                    + "versuche die Einzahl-Schreibweise.");
        }

        // Einzahl-Variante: je Service ein Aufruf, damit der Umfang derselbe
        // bleibt. Der erste Fehlschlag bricht ab und wird gemeldet — sonst
        // staende am Ende „fertig", obwohl die Haelfte noch quittiert ist.
        foreach (var service in serviceDescriptions)
        {
            var single = new
            {
                acknowledge_type = "service",
                host_name = hostName,
                service_description = service
            };
            using var resp = await _http.PostAsJsonAsync(
                "domain-types/acknowledge/actions/delete/invoke", single, JsonOpts, ct);
            await EnsureSuccessAsync(resp, ct);
        }
    }

    /// <summary>Plant eine Host-Downtime.</summary>
    public async Task ScheduleHostDowntimeAsync(string hostName, DateTimeOffset start,
        DateTimeOffset end, string comment, CancellationToken ct = default)
    {
        var payload = new
        {
            downtime_type = "host",
            host_name = hostName,
            start_time = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            end_time = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            recur = "fixed",
            duration = 0,
            comment
        };
        using var resp = await _http.PostAsJsonAsync(
            "domain-types/downtime/collections/host", payload, JsonOpts, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>Plant eine Downtime fuer einen einzelnen Service.</summary>
    public async Task ScheduleServiceDowntimeAsync(string hostName, string serviceDescription,
        DateTimeOffset start, DateTimeOffset end, string comment, CancellationToken ct = default)
    {
        var payload = new
        {
            downtime_type = "service",
            host_name = hostName,
            service_descriptions = new[] { serviceDescription },
            start_time = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            end_time = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            recur = "fixed",
            duration = 0,
            comment
        };
        using var resp = await _http.PostAsJsonAsync(
            "domain-types/downtime/collections/service", payload, JsonOpts, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    // ---------------------------------------------------------------------
    // Kommentare
    // ---------------------------------------------------------------------

    /// <summary>Alle Kommentare (Host + Service) auf dem gegebenen Host.</summary>
    public async Task<IReadOnlyList<CheckmkObject<CommentExtensions>>> GetCommentsForHostAsync(
        string hostName, CancellationToken ct = default)
    {
        var query = JsonSerializer.Serialize(new { op = "=", left = "host_name", right = hostName });
        var url = "domain-types/comment/collections/all?query=" + Uri.EscapeDataString(query);
        var result = await GetAsync<CheckmkCollection<CheckmkObject<CommentExtensions>>>(url, ct);
        return result.Value;
    }

    /// <summary>Legt einen Host-Kommentar an.</summary>
    public async Task AddHostCommentAsync(string hostName, string comment,
        bool persistent = false, CancellationToken ct = default)
    {
        var payload = new
        {
            comment_type = "host",
            host_name = hostName,
            comment,
            persistent
        };
        using var resp = await _http.PostAsJsonAsync(
            "domain-types/comment/collections/host", payload, JsonOpts, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>Legt einen Kommentar auf einem einzelnen Service an.</summary>
    public async Task AddServiceCommentAsync(string hostName, string serviceDescription,
        string comment, bool persistent = false, CancellationToken ct = default)
    {
        var payload = new
        {
            comment_type = "service",
            host_name = hostName,
            service_description = serviceDescription,
            comment,
            persistent
        };
        using var resp = await _http.PostAsJsonAsync(
            "domain-types/comment/collections/service", payload, JsonOpts, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>
    /// Loescht einen Kommentar. Checkmk 2.4/2.5 haben zwei konkurrierende Varianten;
    /// wir probieren beide in der Reihenfolge Doku-Empfehlung → REST-Konvention:
    /// zuerst <c>POST /domain-types/comment/actions/delete/invoke</c> mit
    /// <c>{delete_type:"by_id", comment_id:&lt;zahl&gt;, site_id:"…"}</c>, bei
    /// 404/405 Fallback auf <c>DELETE /objects/comment/{id}</c>. Andere 4xx/5xx
    /// werden direkt hochgereicht.
    /// </summary>
    /// <param name="siteId">Site des Kommentars. Leer = die Site der Verbindung.</param>
    public async Task DeleteCommentAsync(string commentId, string? siteId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commentId))
            throw new ArgumentException("commentId darf nicht leer sein", nameof(commentId));

        // Variante A: POST domain-types/comment/actions/delete/invoke
        //
        // comment_id ist eine ZAHL, nicht ein Array von Zeichenketten, und
        // site_id ist Pflicht. Beides hat Checkmk 2.5 gemeldet:
        // „These fields have problems: comment_id, site_id" — die Nutzlast
        // davor schickte ["5"] und gar keine Site.
        //
        // Die Site kommt vom Kommentar, wenn sie dabei steht, sonst von der
        // Verbindung. In einer verteilten Installation beantwortet die
        // Zentrale auch fuer Remote-Sites, und dann ist die Site des Kommentars
        // die richtige — nicht die, mit der wir sprechen.
        var payload = new
        {
            delete_type = "by_id",
            comment_id = long.TryParse(commentId, out var numeric) ? numeric : default(long?),
            site_id = string.IsNullOrWhiteSpace(siteId) ? _site : siteId
        };
        using (var resp = await _http.PostAsJsonAsync(
            "domain-types/comment/actions/delete/invoke", payload, JsonOpts, ct))
        {
            if (resp.IsSuccessStatusCode)
                return;

            // Nur bei "Endpoint gibt's nicht" auf DELETE zurueckfallen.
            // 400 (falsche Payload) oder 403 (Rechte) sind echte Fehler.
            var status = (int)resp.StatusCode;
            if (status != 404 && status != 405)
            {
                await EnsureSuccessAsync(resp, ct);
                return;
            }

            Log.Debug("POST comment/actions/delete/invoke gab {Status} zurueck — versuche DELETE-Fallback", status);
        }

        // Variante B: DELETE objects/comment/{id} — REST-konventioneller Fallback.
        using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"objects/comment/{Uri.EscapeDataString(commentId)}");
        delReq.Headers.TryAddWithoutValidation("If-Match", "*");
        using var delResp = await _http.SendAsync(delReq, ct);
        await EnsureSuccessAsync(delResp, ct);
    }

    // ---------------------------------------------------------------------
    // Service Discovery
    // ---------------------------------------------------------------------

    /// <summary>
    /// Startet einen Service-Discovery-Run auf dem gegebenen Host. Laeuft als
    /// Hintergrund-Task auf dem Server — mit <see cref="WaitForServiceDiscoveryAsync"/>
    /// pollen bis fertig, danach <see cref="ActivateChangesAsync"/> aufrufen.
    /// </summary>
    public async Task StartServiceDiscoveryAsync(string hostName,
        string mode = ServiceDiscoveryMode.FixAll, CancellationToken ct = default)
    {
        var payload = new { host_name = hostName, mode };
        using var resp = await _http.PostAsJsonAsync(
            "domain-types/service_discovery_run/actions/start/invoke", payload, JsonOpts, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>Aktueller Status eines laufenden Discovery-Runs.</summary>
    public async Task<ServiceDiscoveryRunState> GetServiceDiscoveryRunAsync(string hostName,
        CancellationToken ct = default)
    {
        var envelope = await GetAsync<CheckmkObject<ServiceDiscoveryRunState>>(
            $"objects/service_discovery_run/{Uri.EscapeDataString(hostName)}", ct);
        return envelope.Extensions ?? new ServiceDiscoveryRunState();
    }

    /// <summary>
    /// Pollt <see cref="GetServiceDiscoveryRunAsync"/> bis der Run abgeschlossen ist
    /// (<c>active == false</c>). Standard-Timeout 2 Minuten, Poll-Intervall 1.5 s.
    /// </summary>
    public async Task WaitForServiceDiscoveryAsync(string hostName,
        TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(1500);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = await GetServiceDiscoveryRunAsync(hostName, ct);
            if (!state.Active)
                return;
            await Task.Delay(delay, ct);
        }
        throw new TimeoutException(
            $"Service-Discovery fuer Host '{hostName}' hat das Zeitlimit ueberschritten.");
    }

    /// <summary>Convenience: Discovery starten und auf Ende warten (kombiniert Start + Wait).</summary>
    public async Task DiscoverServicesAsync(string hostName,
        string mode = ServiceDiscoveryMode.FixAll,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await StartServiceDiscoveryAsync(hostName, mode, ct);
        await WaitForServiceDiscoveryAsync(hostName, timeout, ct: ct);
    }

    // ---------------------------------------------------------------------
    // Activate Changes
    // ---------------------------------------------------------------------

    /// <summary>
    /// Aktiviert ausstehende Aenderungen (Setup -> scharfschalten).
    /// If-Match: * erspart das vorherige ETag-Abholen.
    /// </summary>
    public async Task ActivateChangesAsync(bool forceForeignChanges = false,
        CancellationToken ct = default)
    {
        var payload = new
        {
            redirect = false,
            force_foreign_changes = forceForeignChanges,
            sites = Array.Empty<string>()
        };
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "domain-types/activation_run/actions/activate-changes/invoke")
        {
            Content = JsonContent.Create(payload, options: JsonOpts)
        };
        req.Headers.TryAddWithoutValidation("If-Match", "*");

        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// GET + Deserialisieren. Die Antwort wird <b>gestreamt</b> statt erst
    /// komplett in einen String zu lesen: bei zehntausenden Services sind das
    /// zweistellige Megabytes, und der alte Weg
    /// (<c>ReadAsStringAsync</c> + synchrones <c>Deserialize</c>) lief nach dem
    /// <c>await</c> auf dem UI-Thread wieder an — die App stand fuer die Dauer
    /// des Parsens. <c>DeserializeAsync</c> + durchgaengiges
    /// <c>ConfigureAwait(false)</c> haelt die Arbeit auf dem ThreadPool und
    /// erlaubt nebenbei den Byte-Fortschritt fuer die Statusleiste.
    /// </summary>
    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct,
        IProgress<TransferProgress>? progress = null)
    {
        using var resp = await _http
            .GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var counting = new CountingStream(stream, resp.Content.Headers.ContentLength, progress);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(counting, JsonOpts, ct).ConfigureAwait(false)
                ?? throw new CheckmkApiException("Antwort war leer/null.",
                    resp.StatusCode, counting.HeadSnippet);
        }
        catch (JsonException ex)
        {
            // Kein vollstaendiger Body mehr (der wird bewusst nicht gepuffert) —
            // der Kopf-Ausschnitt reicht, um Loginseite/Proxy-HTML zu erkennen.
            throw new CheckmkApiException(
                $"Antwort konnte nicht deserialisiert werden: {ex.Message}",
                resp.StatusCode, counting.HeadSnippet, ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;

        var body = await resp.Content.ReadAsStringAsync(ct);
        var detail = TryExtractProblemDetail(body) ?? resp.ReasonPhrase ?? "Unbekannter Fehler";

        Log.Warn("Checkmk API {Status}: {Detail}", (int)resp.StatusCode, detail);

        throw new CheckmkApiException(
            $"Checkmk API antwortete {(int)resp.StatusCode} ({resp.StatusCode}): {detail}",
            resp.StatusCode, body);
    }

    /// <summary>
    /// Zieht "title"/"detail" aus einer RFC7807 problem+json-Antwort — und
    /// <b>die Feldfehler aus <c>fields</c></b>.
    ///
    /// <para>Ohne die Feldfehler lautet die Meldung nur „These fields have
    /// problems: comment_id, site_id" — man weiss, <i>welche</i> Felder
    /// Checkmk stoeren, aber nicht <i>warum</i>, und raet dann an der Nutzlast
    /// herum. Checkmk liefert den Grund gleich mit
    /// (<c>"comment_id": ["Not a valid integer."]</c>); er stand bisher nur
    /// ungenutzt im Antwortkoerper. Real: das Loeschen eines Kommentars
    /// scheiterte, und der erste Verdacht ging auf die Rechte.</para>
    /// </summary>
    private static string? TryExtractProblemDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;

            if (ExtractFieldErrors(root) is { Length: > 0 } fields)
                detail = detail is null ? fields : $"{detail} ({fields})";

            return (title, detail) switch
            {
                (not null, not null) => $"{title} — {detail}",
                (not null, null) => title,
                (null, not null) => detail,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Baut aus Checkmks <c>fields</c>-Objekt eine lesbare Zeile:
    /// <c>comment_id: Not a valid integer.; site_id: Missing data for required field.</c>
    ///
    /// <para>Die Struktur ist verschachtelt und nicht einheitlich — mal
    /// <c>{"feld": ["Grund"]}</c>, mal <c>{"body": {"feld": [...]}}</c>.
    /// Deshalb rekursiv über alles, was nach Feld/Grund aussieht, statt eine
    /// feste Form anzunehmen, die beim naechsten Endpunkt nicht mehr passt.</para>
    /// </summary>
    private static string ExtractFieldErrors(JsonElement root)
    {
        if (!root.TryGetProperty("fields", out var fields)) return "";

        var parts = new List<string>();
        Collect(fields, "");
        return string.Join("; ", parts.Take(8));

        void Collect(JsonElement node, string path)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in node.EnumerateObject())
                        Collect(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}");
                    break;

                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                        Collect(item, path);
                    break;

                case JsonValueKind.String when node.GetString() is { Length: > 0 } reason:
                    parts.Add(path.Length == 0 ? reason : $"{path}: {reason}");
                    break;
            }
        }
    }

    private static string ColumnsQuery(IEnumerable<string> columns, string? hostNameCol = null)
        => string.Join("&", columns.Select(c => "columns=" + Uri.EscapeDataString(c)));

    /// <summary>Maskiert ein Secret fuer Logausgaben (nur erste/letzte 2 Zeichen).</summary>
    private static string Mask(string secret)
        => secret.Length <= 4
            ? new string('*', secret.Length)
            : $"{secret[..2]}{new string('*', secret.Length - 4)}{secret[^2..]}";

    // Interne Envelopes: Livestatus-Endpunkte packen die Spalten in "extensions".
    private sealed record HostStatusEnvelope(HostStatus Extensions);
    private sealed record ServiceStatusEnvelope(ServiceStatus Extensions);
}
