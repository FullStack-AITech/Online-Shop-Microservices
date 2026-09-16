using System.ComponentModel.DataAnnotations;

namespace OrderService.Infrastructure.Resilience;

/// <summary>
/// Settings for one downstream service.
/// </summary>
/// <remarks>
/// Every value is tunable per environment. Resilience settings that are hardcoded cannot
/// be adjusted when a dependency turns out to be slower in production than in testing.
/// </remarks>
public sealed class DownstreamOptions
{
    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Budget for a single attempt. Without this a call can hang until the socket gives
    /// up, holding a request thread and a connection the whole time.
    /// </summary>
    [Range(100, 60_000)]
    public int AttemptTimeoutMs { get; set; } = 2_000;

    /// <summary>
    /// Budget for the whole operation including retries, so a retrying call cannot
    /// outlive the caller's own patience.
    /// </summary>
    [Range(100, 120_000)]
    public int TotalTimeoutMs { get; set; } = 8_000;

    /// <summary>Retries after the first attempt. Applies to idempotent requests only.</summary>
    [Range(0, 5)]
    public int MaxRetries { get; set; } = 2;

    /// <summary>First backoff delay; subsequent delays grow exponentially, with jitter.</summary>
    [Range(10, 10_000)]
    public int BaseRetryDelayMs { get; set; } = 200;

    /// <summary>Failure ratio within the sampling window that trips the circuit.</summary>
    [Range(0.05, 1.0)]
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>Minimum calls in the window before the ratio is allowed to trip it.</summary>
    [Range(2, 1000)]
    public int CircuitBreakerMinimumThroughput { get; set; } = 8;

    [Range(1_000, 300_000)]
    public int CircuitBreakerSamplingDurationMs { get; set; } = 30_000;

    /// <summary>How long the circuit stays open before a trial call is allowed through.</summary>
    [Range(1_000, 300_000)]
    public int CircuitBreakerBreakDurationMs { get; set; } = 15_000;
}
