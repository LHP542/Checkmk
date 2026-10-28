using System.Net;
using System.Text;
using Checkmk.App.Services;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Tests fuer den manuell ausgeloesten Update-Check (About-Box). Die "aktuelle"
/// Version stammt aus der Checkmk.App-Assembly und ist im Testlauf nicht fix —
/// deshalb Grenz-Tags: v0.0.0 ist immer <= aktuell (UpToDate), v999.0.0 immer
/// neuer (UpdateAvailable). Das haelt die Tests unabhaengig vom Build-Tag.
/// </summary>
public class ManualUpdateCheckTests
{
    private const string ApiUrl = "https://git.flexfamily.de/api/v1/repos/LHP542/Checkmk/releases/latest";

    [Fact]
    public async Task Manual_check_reports_up_to_date_for_older_release()
    {
        var checker = Make(tag: "v0.0.0");

        var result = await checker.CheckManuallyAsync(TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(UpdateCheckOutcome.UpToDate);
        result.Info.Should().BeNull();
    }

    [Fact]
    public async Task Manual_check_reports_failure_on_http_error()
    {
        var checker = Make(status: HttpStatusCode.InternalServerError);

        var result = await checker.CheckManuallyAsync(TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(UpdateCheckOutcome.Failed);
        result.Info.Should().BeNull();
    }

    [Fact]
    public async Task Manual_check_ignores_skipped_version_while_auto_check_honors_it()
    {
        var prefs = new StubPrefs(new Version(999, 0, 0));
        var checker = Make(tag: "v999.0.0", prefs: prefs);

        // Manuell: die uebersprungene Version wird trotzdem gemeldet.
        var manual = await checker.CheckManuallyAsync(TestContext.Current.CancellationToken);
        manual.Outcome.Should().Be(UpdateCheckOutcome.UpdateAvailable);
        manual.Info!.Version.Should().Be(new Version(999, 0, 0));

        // Automatisch: der Skip wird respektiert -> nichts gemeldet.
        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    // ---- Helfer ----

    private static GitHubReleasesUpdateChecker Make(
        string? tag = null,
        HttpStatusCode status = HttpStatusCode.OK,
        IUpdatePreferences? prefs = null)
    {
        Func<HttpResponseMessage> responder;
        if (status != HttpStatusCode.OK)
        {
            responder = () => new HttpResponseMessage(status);
        }
        else
        {
            var json =
                "{\"tag_name\":\"" + tag + "\",\"name\":\"rel\",\"body\":\"notes\"," +
                "\"html_url\":\"https://example/rel\",\"draft\":false,\"prerelease\":false,\"assets\":[]}";
            responder = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        var http = new HttpClient(new StubHandler(responder));
        return new GitHubReleasesUpdateChecker(http, ApiUrl, prefs ?? new StubPrefs());
    }

    private sealed class StubHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder());
    }

    private sealed class StubPrefs(Version? skipped = null) : IUpdatePreferences
    {
        private Version? _skipped = skipped;
        public Version? LoadSkippedVersion() => _skipped;
        public void SaveSkippedVersion(Version version) => _skipped = version;
    }
}
