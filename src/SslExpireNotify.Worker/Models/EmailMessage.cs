namespace SslExpireNotify.Worker.Models;

/// <summary>A single email, identical in shape whichever channel ends up delivering it.</summary>
public sealed class EmailMessage
{
    public required string To { get; init; }

    /// <summary>Comma separated Cc list straight from Recipients:Cc. Empty string = no Cc.</summary>
    public string Cc { get; init; } = string.Empty;

    public required string Subject { get; init; }

    public required string Body { get; init; }

    public bool IsHtml { get; init; } = true;
}

/// <summary>Why a send attempt failed, which decides whether falling back to SMTP makes sense.</summary>
public enum SendFailureKind
{
    None = 0,

    /// <summary>4xx from the Mail API: the request itself is wrong, another channel will not help.</summary>
    Permanent = 1,

    /// <summary>Timeout / 5xx / network error: the channel is unhealthy, another channel may work.</summary>
    Transient = 2,

    /// <summary>The Mail API circuit breaker was open so the call was not even attempted.</summary>
    CircuitOpen = 3
}

public sealed class EmailSendResult
{
    public required bool Success { get; init; }

    /// <summary>Channel that produced this result (or the last one tried).</summary>
    public required string Channel { get; init; }

    public string? ErrorMessage { get; init; }

    public int RetryCount { get; init; }

    public SendFailureKind FailureKind { get; init; } = SendFailureKind.None;

    /// <summary>True when the message was delivered by the SMTP fallback after the Mail API was unusable.</summary>
    public bool UsedFallback { get; init; }

    public static EmailSendResult Ok(string channel, int retryCount = 0, bool usedFallback = false) => new()
    {
        Success = true,
        Channel = channel,
        RetryCount = retryCount,
        UsedFallback = usedFallback
    };

    public static EmailSendResult Fail(string channel, string error, SendFailureKind kind, int retryCount = 0) => new()
    {
        Success = false,
        Channel = channel,
        ErrorMessage = error,
        FailureKind = kind,
        RetryCount = retryCount
    };
}
