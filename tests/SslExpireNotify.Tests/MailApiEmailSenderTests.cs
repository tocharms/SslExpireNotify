using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class MailApiEmailSenderTests
{
    private const string Url = "https://mail.test/ksctracking_mailapi/api/email/send";

    private static readonly EmailMessage Message = new()
    {
        To = "somchai.j@ksc.net",
        Cc = "boss@ksc.net",
        Subject = "[แจ้งเตือน] ใบรับรอง SSL Certificate www.example.co.th",
        Body = "<html><body>สวัสดี</body></html>"
    };

    private sealed class StubHandler(Func<int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<Uri?> Uris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return responder(Bodies.Count - 1);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (MailApiEmailSender Sender, StubHandler Handler) Build(
        Func<int, HttpResponseMessage> responder,
        int circuitBreakerThreshold = 100)
    {
        var handler = new StubHandler(responder);

        var options = new MailApiOptions
        {
            Url = Url,
            From = "noreplay@ksc.net",
            FromDisplayName = "ksc mail alert",
            TimeoutSeconds = 30,
            CircuitBreakerFailureThreshold = circuitBreakerThreshold,
            CircuitBreakerBreakSeconds = 300,
            RetryBaseDelaySeconds = 0   // keep the tests fast; the production default is 2s
        };

        var sender = new MailApiEmailSender(
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<MailApiEmailSender>.Instance);

        return (sender, handler);
    }

    private static HttpResponseMessage Response(HttpStatusCode code) =>
        new(code) { Content = new StringContent("{\"status\":\"" + code + "\"}") };

    [Fact]
    public async Task The_payload_matches_the_KSC_Mail_API_contract()
    {
        var (sender, handler) = Build(_ => Response(HttpStatusCode.OK));

        var result = await sender.SendAsync(Message, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Url, handler.Uris[0]!.ToString());

        using var document = JsonDocument.Parse(handler.Bodies[0]);
        var root = document.RootElement;

        Assert.Equal("noreplay@ksc.net", root.GetProperty("from").GetString());
        Assert.Equal("ksc mail alert", root.GetProperty("fromdisplayname").GetString());
        Assert.Equal("somchai.j@ksc.net", root.GetProperty("to").GetString());
        Assert.Equal(Message.Subject, root.GetProperty("subject").GetString());
        Assert.Equal(Message.Body, root.GetProperty("body").GetString());
        Assert.True(root.GetProperty("isHtml").GetBoolean());
        Assert.Equal("boss@ksc.net", root.GetProperty("cc").GetString());
    }

    [Fact]
    public async Task An_empty_Cc_is_sent_as_an_empty_string_not_null()
    {
        var (sender, handler) = Build(_ => Response(HttpStatusCode.OK));

        await sender.SendAsync(new EmailMessage
        {
            To = "somchai.j@ksc.net",
            Cc = string.Empty,
            Subject = "s",
            Body = "b"
        }, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(string.Empty, document.RootElement.GetProperty("cc").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Any_2xx_counts_as_delivered(HttpStatusCode code)
    {
        var (sender, handler) = Build(_ => Response(code));

        var result = await sender.SendAsync(Message, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(EmailChannels.MailApi, result.Channel);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_4xx_fails_permanently_and_is_not_retried(HttpStatusCode code)
    {
        var (sender, handler) = Build(_ => Response(code));

        var result = await sender.SendAsync(Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SendFailureKind.Permanent, result.FailureKind);
        Assert.Single(handler.Bodies);           // no retry: the request itself is wrong
        Assert.Equal(0, result.RetryCount);
        Assert.Contains(((int)code).ToString(), result.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_5xx_is_retried_three_times_then_reported_as_transient(HttpStatusCode code)
    {
        var (sender, handler) = Build(_ => Response(code));

        var result = await sender.SendAsync(Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SendFailureKind.Transient, result.FailureKind);
        Assert.Equal(4, handler.Bodies.Count);   // first attempt + 3 retries
        Assert.Equal(3, result.RetryCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Throttling_and_timeout_statuses_are_treated_as_transient(HttpStatusCode code)
    {
        var (sender, handler) = Build(_ => Response(code));

        var result = await sender.SendAsync(Message, CancellationToken.None);

        Assert.Equal(SendFailureKind.Transient, result.FailureKind);
        Assert.Equal(4, handler.Bodies.Count);
    }

    [Fact]
    public async Task A_retry_that_finally_succeeds_reports_the_attempts_it_took()
    {
        var (sender, _) = Build(attempt => attempt < 2
            ? Response(HttpStatusCode.ServiceUnavailable)
            : Response(HttpStatusCode.OK));

        var result = await sender.SendAsync(Message, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.RetryCount);
    }

    [Fact]
    public async Task A_network_failure_is_transient_and_never_escapes_as_an_exception()
    {
        var (sender, _) = Build(_ => throw new HttpRequestException("connection refused"));

        var result = await sender.SendAsync(Message, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SendFailureKind.Transient, result.FailureKind);
        Assert.Contains("connection refused", result.ErrorMessage);
    }

    [Fact]
    public async Task The_circuit_opens_after_the_configured_number_of_failures_and_stops_calling_out()
    {
        var (sender, handler) = Build(_ => Response(HttpStatusCode.ServiceUnavailable), circuitBreakerThreshold: 2);

        Assert.False(sender.IsCircuitOpen);

        var first = await sender.SendAsync(Message, CancellationToken.None);
        Assert.False(first.Success);
        Assert.True(sender.IsCircuitOpen);

        var callsSoFar = handler.Bodies.Count;

        var second = await sender.SendAsync(Message, CancellationToken.None);

        Assert.False(second.Success);
        Assert.Equal(SendFailureKind.CircuitOpen, second.FailureKind);
        Assert.Equal(callsSoFar, handler.Bodies.Count);   // nothing left the process
    }

    [Fact]
    public async Task A_permanent_failure_does_not_trip_the_circuit()
    {
        // Bad data on one certificate must not stop the Mail API being used for the others.
        var (sender, _) = Build(_ => Response(HttpStatusCode.BadRequest), circuitBreakerThreshold: 2);

        await sender.SendAsync(Message, CancellationToken.None);
        await sender.SendAsync(Message, CancellationToken.None);
        await sender.SendAsync(Message, CancellationToken.None);

        Assert.False(sender.IsCircuitOpen);
    }
}
