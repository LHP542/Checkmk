using System.Net;
using System.Text;
using Checkmk.Core;
using Checkmk.Core.Exceptions;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Das Löschen eines Kommentars und — wichtiger — die Frage, ob man einem
/// abgelehnten Aufruf ansieht, <b>warum</b> er abgelehnt wurde.
///
/// Real passiert (2026-10-13): Checkmk antwortete
/// „These fields have problems: comment_id, site_id". Welche Felder stören,
/// stand da; warum, nicht — und der erste Verdacht ging auf die Rechte, obwohl
/// es die Nutzlast war. Den Grund liefert Checkmk im selben Körper mit, er
/// wurde nur nicht ausgewertet.
/// </summary>
public class CommentDeleteTests
{
    // --- Die Nutzlast -----------------------------------------------------

    [Fact]
    public async Task Die_Nutzlast_traegt_die_Id_als_Zahl_und_die_Site()
    {
        var client = Make(HttpStatusCode.NoContent, "", out var handler);

        await client.DeleteCommentAsync("42", ct: TestContext.Current.CancellationToken);

        handler.LastBody.Should().Contain("\"comment_id\":42",
            "Checkmk erwartet eine Zahl, kein Array von Zeichenketten");
        handler.LastBody.Should().Contain("\"site_id\":\"prod\"",
            "site_id ist Pflicht und kommt sonst aus der Verbindung");
    }

    /// <summary>
    /// In einer verteilten Installation antwortet die Zentrale auch für
    /// Remote-Sites. Dann ist die Site des Kommentars die richtige, nicht die,
    /// mit der wir sprechen.
    /// </summary>
    [Fact]
    public async Task Die_Site_des_Kommentars_schlaegt_die_der_Verbindung()
    {
        var client = Make(HttpStatusCode.NoContent, "", out var handler);

        await client.DeleteCommentAsync("42", "aussenstelle",
            TestContext.Current.CancellationToken);

        handler.LastBody.Should().Contain("\"site_id\":\"aussenstelle\"");
    }

    // --- Die Fehlermeldung ------------------------------------------------

    /// <summary>
    /// <b>Der eigentliche Gewinn.</b> Ohne die Feldfehler steht nur da, welche
    /// Felder stören — und man rät an der Nutzlast herum.
    /// </summary>
    [Fact]
    public async Task Die_Feldfehler_stehen_in_der_Meldung()
    {
        const string body = """
            {
              "title": "Bad Request",
              "detail": "These fields have problems: comment_id, site_id",
              "fields": {
                "comment_id": ["Not a valid integer."],
                "site_id": ["Missing data for required field."]
              }
            }
            """;
        var client = Make(HttpStatusCode.BadRequest, body, out _);

        var act = () => client.DeleteCommentAsync("42", ct: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CheckmkApiException>()).Which.Message
            .Should().Contain("Not a valid integer")
            .And.Contain("Missing data for required field")
            .And.Contain("comment_id")
            .And.Contain("site_id");
    }

    /// <summary>Verschachtelte Formen kommen genauso durch — Checkmk packt die
    /// Feldfehler je nach Endpunkt in ein <c>body</c>-Objekt.</summary>
    [Fact]
    public async Task Auch_verschachtelte_Feldfehler_werden_ausgelesen()
    {
        const string body = """
            {
              "title": "Bad Request",
              "fields": { "body": { "acknowledge_type": ["Must be one of: host, service."] } }
            }
            """;
        var client = Make(HttpStatusCode.BadRequest, body, out _);

        var act = () => client.DeleteCommentAsync("1", ct: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CheckmkApiException>()).Which.Message
            .Should().Contain("acknowledge_type").And.Contain("Must be one of");
    }

    /// <summary>Ohne <c>fields</c> bleibt die Meldung wie bisher — die
    /// Erweiterung darf nichts kaputtmachen, was schon lesbar war.</summary>
    [Fact]
    public async Task Ohne_Feldfehler_bleibt_die_Meldung_unveraendert()
    {
        const string body = """
            { "title": "Forbidden", "detail": "You lack the permission." }
            """;
        var client = Make(HttpStatusCode.Forbidden, body, out _);

        var act = () => client.DeleteCommentAsync("1", ct: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CheckmkApiException>()).Which.Message
            .Should().Contain("Forbidden").And.Contain("You lack the permission");
    }

    /// <summary>Ein 400 ist ein echter Fehler, kein Grund für den
    /// DELETE-Fallback — der gilt nur, wenn es den Endpunkt nicht gibt.</summary>
    [Fact]
    public async Task Ein_400_faellt_nicht_auf_DELETE_zurueck()
    {
        var client = Make(HttpStatusCode.BadRequest, """{"title":"Bad Request"}""", out var handler);

        var act = () => client.DeleteCommentAsync("1", ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<CheckmkApiException>();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Ein_405_faellt_auf_DELETE_zurueck()
    {
        var client = Make(HttpStatusCode.MethodNotAllowed, "", out var handler);

        var act = () => client.DeleteCommentAsync("1", ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<CheckmkApiException>();
        handler.Calls.Should().Be(2, "erst POST, dann der DELETE-Fallback");
        handler.LastMethod.Should().Be(HttpMethod.Delete);
    }

    // --- Gerüst -----------------------------------------------------------

    private static CheckmkClient Make(HttpStatusCode status, string body, out StubHandler handler)
    {
        handler = new StubHandler(status, body);
        return new CheckmkClient(new HttpClient(handler), new CheckmkOptions
        {
            Host = "monitoring.test",
            Site = "prod",
            Username = "automation",
            Secret = "s3cr3t"
        });
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastBody { get; private set; } = "";
        public HttpMethod? LastMethod { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastMethod = request.Method;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json")
            };
        }
    }
}
