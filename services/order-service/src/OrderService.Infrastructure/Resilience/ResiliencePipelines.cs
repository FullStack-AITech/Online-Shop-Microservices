using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace OrderService.Infrastructure.Resilience;

/// <summary>
/// Builds the resilience pipelines that every outbound call goes through.
/// </summary>
/// <remarks>
/// <para>Order matters, outermost first:</para>
/// <list type="number">
///   <item>total timeout — caps the whole operation, retries included</item>
///   <item>retry — re-issues transient failures with exponential backoff and jitter</item>
///   <item>circuit breaker — stops calling a dependency that is clearly down</item>
///   <item>attempt timeout — caps each individual try, so a retry can actually happen</item>
/// </list>
/// <para>
/// The attempt timeout must sit <i>inside</i> the retry: with the order reversed, one slow
/// call would consume the entire budget and no retry would ever be attempted.
/// </para>
/// </remarks>
public static class ResiliencePipelines
{
    /// <summary>
    /// Full pipeline for <b>idempotent</b> requests — safe to repeat, so retries are on.
    /// </summary>
    public static IHttpResiliencePipelineBuilder AddIdempotentPipeline(
        this IHttpClientBuilder builder, DownstreamOptions options)
    {
        return builder.AddResilienceHandler("idempotent", pipeline =>
        {
            pipeline.AddTimeout(TimeSpan.FromMilliseconds(options.TotalTimeoutMs));

            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetries,
                Delay = TimeSpan.FromMilliseconds(options.BaseRetryDelayMs),
                BackoffType = DelayBackoffType.Exponential,

                // Jitter spreads retries out. Without it every caller that failed at the
                // same moment retries at the same moment, and the recovering dependency is
                // knocked straight back over.
                UseJitter = true,

                ShouldHandle = arguments => ValueTask.FromResult(IsTransient(arguments.Outcome))
            });

            AddCircuitBreaker(pipeline, options);

            pipeline.AddTimeout(TimeSpan.FromMilliseconds(options.AttemptTimeoutMs));
        }).SelectPipelineByAuthority();
    }

    /// <summary>
    /// Pipeline for <b>non-idempotent</b> requests: timeouts and a circuit breaker, but
    /// deliberately <b>no retry</b>.
    /// </summary>
    /// <remarks>
    /// Reserving stock twice reserves twice. When a reservation times out we genuinely do
    /// not know whether it was applied, so repeating it risks removing stock that was
    /// already taken. The correct answer is to fail the order and let the caller retry the
    /// whole operation, not to silently re-issue the side effect.
    /// </remarks>
    public static IHttpResiliencePipelineBuilder AddNonIdempotentPipeline(
        this IHttpClientBuilder builder, DownstreamOptions options)
    {
        return builder.AddResilienceHandler("non-idempotent", pipeline =>
        {
            pipeline.AddTimeout(TimeSpan.FromMilliseconds(options.TotalTimeoutMs));
            AddCircuitBreaker(pipeline, options);
            pipeline.AddTimeout(TimeSpan.FromMilliseconds(options.AttemptTimeoutMs));
        }).SelectPipelineByAuthority();
    }

    private static void AddCircuitBreaker(
        ResiliencePipelineBuilder<HttpResponseMessage> pipeline, DownstreamOptions options)
    {
        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = options.CircuitBreakerFailureRatio,
            MinimumThroughput = options.CircuitBreakerMinimumThroughput,
            SamplingDuration = TimeSpan.FromMilliseconds(options.CircuitBreakerSamplingDurationMs),

            // Once open, calls fail instantly instead of queueing behind a dead dependency.
            // After this window one trial call is allowed through to test recovery.
            BreakDuration = TimeSpan.FromMilliseconds(options.CircuitBreakerBreakDurationMs),

            ShouldHandle = arguments => ValueTask.FromResult(IsTransient(arguments.Outcome))
        });
    }

    /// <summary>
    /// Decides what counts as a transient failure worth retrying or counting against the
    /// circuit.
    /// </summary>
    /// <remarks>
    /// 4xx responses are excluded on purpose. A 404 or a 409 is a correct, considered
    /// answer from a healthy service — retrying it wastes time and tripping a breaker on
    /// it would take down a working dependency because callers sent bad requests.
    /// </remarks>
    private static bool IsTransient(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is not null)
        {
            return outcome.Exception is HttpRequestException or TimeoutRejectedException
                or TaskCanceledException;
        }

        var response = outcome.Result;
        if (response is null)
        {
            return false;
        }

        return (int)response.StatusCode >= 500
               || response.StatusCode is HttpStatusCode.RequestTimeout
                   or HttpStatusCode.TooManyRequests;
    }
}
