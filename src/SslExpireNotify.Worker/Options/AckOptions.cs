namespace SslExpireNotify.Worker.Options;

/// <summary>Bound from the root level "AckBaseUrl" key.</summary>
public sealed class AckOptions
{
    public string AckBaseUrl { get; set; } = string.Empty;
}
