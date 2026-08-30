using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SslExpireNotify.Worker.Models;
using SslExpireNotify.Worker.Options;

namespace SslExpireNotify.Worker.Services;

public sealed class MailApiEmailSender : IMailApiEmailSender
{
    public const string HttpClientName = "KscMailApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MailApiOptions _options;
    private readonly ILogger<MailApiEmailSender> _logger;
    private readonly ResiliencePipeline<MailApiAttempt> _pipeline;
    private readonly CircuitBreakerStateProvider _circuitState = new();

    public MailApiEmailSender(
        IHttpClientFactory httpClientFactory,
        IOptions<MailApiOptions> options,
        ILogger<MailApiEmailSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _pipeline = BuildPipeline();
    }

    public string Channel => EmailChannels.MailApi;

    public bool IsCircuitOpen =>
        _circuitState.CircuitState is CircuitState.Open or CircuitState.Isolated;

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var attempts = 0;

        try
        {
            var attempt = await _pipeline.ExecuteAsync(
                async ct =>
                {
                    attempts++;
                    return await SendOnceAsync(message, ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            var retryCount = Math.Max(0, attempts - 1);

            if (attempt.Success)
            {
                _logger.LogInformation(
                    "Mail API accepted message to {To} (cc {Cc}) subject {Subject} after {RetryCount} retries",
                    message.To, string.IsNullOrEmpty(message.Cc) ? "-" : message.Cc, message.Subject, retryCount);

                return EmailSendResult.Ok(Channel, retryCount);
            }

            return EmailSendResult.Fail(Channel, attempt.Error ?? "Mail API call failed.", attempt.FailureKind, retryCount);
        }
        catch (BrokenCircuitException)
        {
            return EmailSendResult.Fail(
                Channel,
                "Mail API circuit breaker is open; the call was not attempted.",
                SendFailureKind.CircuitOpen,
                Math.Max(0, attempts - 1));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EmailSendResult.Fail(Channel, ex.Message, SendFailureKind.Transient, Math.Max(0, attempts - 1));
        }
    }

    private async Task<MailApiAttempt> SendOnceAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var payload = new MailApiRequest
        {
            From = _options.From,
            FromDisplayName = _options.FromDisplayName,
            To = message.To,
            Subject = message.Subject,
            Body = message.Body,
            IsHtml = message.IsHtml,
            Cc = message.Cc ?? string.Empty
        };

        try
        {
            using var response = await client
                .PostAsJsonAsync(_options.Url, payload, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return MailApiAttempt.Ok();
            }

            var responseText = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            // 4xx means the request itself is wrong (bad address, malformed payload). Retrying or switching
            // channel will not fix that, so it is a permanent failure.
            var permanent = status is >= 400 and < 500 and not (int)HttpStatusCode.RequestTimeout
                            and not (int)HttpStatusCode.TooManyRequests;

            return MailApiAttempt.Failed(
                $"Mail API returned HTTP {status} ({response.ReasonPhrase}): {responseText}",
                permanent ? SendFailureKind.Permanent : SendFailureKind.Transient);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return MailApiAttempt.Failed($"Mail API request timed out after {_options.TimeoutSeconds}s: {ex.Message}", SendFailureKind.Transient);
        }
        catch (HttpRequestException ex)
        {
            return MailApiAttempt.Failed($"Mail API request failed: {ex.Message}", SendFailureKind.Transient);
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return text.Length > 500 ? text[..500] : text;
        }
        catch
        {
            return "<response body unavailable>";
        }
    }

    private ResiliencePipeline<MailApiAttempt> BuildPipeline()
    {
        var samplingSeconds = Math.Max(30, _options.CircuitBreakerBreakSeconds);

        return new ResiliencePipelineBuilder<MailApiAttempt>()
            // Retry sits outside the breaker so every failed attempt is counted by the breaker.
            .AddRetry(new RetryStrategyOptions<MailApiAttempt>
            {
                ShouldHandle = new PredicateBuilder<MailApiAttempt>()
                    .HandleResult(static r => !r.Success && r.FailureKind == SendFailureKind.Transient),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(Math.Max(0, _options.RetryBaseDelaySeconds)),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Mail API call failed (attempt {Attempt}), retrying in {Delay}. Reason: {Reason}",
                        args.AttemptNumber + 1,
                        args.RetryDelay,
                        args.Outcome.Result.Error ?? args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<MailApiAttempt>
            {
                ShouldHandle = new PredicateBuilder<MailApiAttempt>()
                    .HandleResult(static r => !r.Success && r.FailureKind == SendFailureKind.Transient),
                FailureRatio = 1.0,
                MinimumThroughput = Math.Max(2, _options.CircuitBreakerFailureThreshold),
                SamplingDuration = TimeSpan.FromSeconds(samplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(_options.CircuitBreakerBreakSeconds),
                StateProvider = _circuitState,
                OnOpened = args =>
                {
                    _logger.LogError(
                        "Mail API circuit breaker OPENED for {BreakDuration}. Outgoing mail will use the SMTP fallback where enabled.",
                        args.BreakDuration);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    _logger.LogInformation("Mail API circuit breaker closed; the Mail API is being used again.");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    private readonly record struct MailApiAttempt(bool Success, string? Error, SendFailureKind FailureKind)
    {
        public static MailApiAttempt Ok() => new(true, null, SendFailureKind.None);

        public static MailApiAttempt Failed(string error, SendFailureKind kind) => new(false, error, kind);
    }

    private sealed class MailApiRequest
    {
        [JsonPropertyName("from")]
        public string From { get; init; } = string.Empty;

        [JsonPropertyName("fromdisplayname")]
        public string FromDisplayName { get; init; } = string.Empty;

        [JsonPropertyName("to")]
        public string To { get; init; } = string.Empty;

        [JsonPropertyName("subject")]
        public string Subject { get; init; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; init; } = string.Empty;

        [JsonPropertyName("isHtml")]
        public bool IsHtml { get; init; } = true;

        [JsonPropertyName("cc")]
        public string Cc { get; init; } = string.Empty;
    }
}
