using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Infrastructure.Resilience;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace OrderService.Api.Tests.Resilience;

/// <summary>
/// Tests for the timeout, retry and circuit breaker behaviour that every outbound call
/// depends on. These assert the number of attempts that reached the wire, because that is
/// the observable difference between a retrying and a non-retrying pipeline.
/// </summary>
public class ResiliencePipelineTests
{
    private const string ClientName = "test-client";

    private static readonly DownstreamOptions FastOptions = new()
    {
        BaseUrl = "http://downstream.test",
        AttemptTimeoutMs = 200,
        TotalTimeoutMs = 5_000,
        MaxRetries = 2,
        BaseRetryDelayMs = 10,
        CircuitBreakerFailureRatio = 0.5,
        CircuitBreakerMinimumThroughput = 4,
        CircuitBreakerSamplingDurationMs = 10_000,
        CircuitBreakerBreakDurationMs = 2_000
    };

    private static HttpClient BuildClient(StubHandler handler, bool idempotent,
        DownstreamOptions? options = null)
    {
        options ??= FastOptions;

        var services = new ServiceCollection();
        var builder = services.AddHttpClient(ClientName, client =>
            {
                client.BaseAddress = new Uri(options.BaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        if (idempotent)
        {
            builder.AddIdempotentPipeline(options);
        }
        else
        {
            builder.AddNonIdempotentPipeline(options);
        }

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
    }

    // ------------------------------------------------------------------ retry

    [Fact]
    public async Task An_idempotent_request_retries_a_server_error_and_eventually_succeeds()
    {
        var handler = StubHandler.FailsUntilAttempt(3, HttpStatusCode.InternalServerError);
        var client = BuildClient(handler, idempotent: true);

        var response = await client.GetAsync("/api/v1/products/p-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // One initial attempt plus two retries.
        handler.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task Retries_stop_at_the_configured_maximum()
    {
        var handler = StubHandler.AlwaysReturns(HttpStatusCode.InternalServerError);
        var client = BuildClient(handler, idempotent: true);

        var response = await client.GetAsync("/api/v1/products/p-1");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        handler.Attempts.Should().Be(FastOptions.MaxRetries + 1);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        // A 404 is a considered answer from a healthy service. Repeating it wastes time
        // and would never produce a different result.
        var handler = StubHandler.AlwaysReturns(HttpStatusCode.NotFound);
        var client = BuildClient(handler, idempotent: true);

        var response = await client.GetAsync("/api/v1/products/missing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        handler.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_conflict_is_not_retried()
    {
        // 409 from the Product Service means "not enough stock" — a business answer.
        var handler = StubHandler.AlwaysReturns(HttpStatusCode.Conflict);
        var client = BuildClient(handler, idempotent: true);

        await client.GetAsync("/api/v1/products/p-1");

        handler.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Retries_back_off_rather_than_hammering_the_dependency()
    {
        var slowRetry = new DownstreamOptions
        {
            BaseUrl = FastOptions.BaseUrl,
            AttemptTimeoutMs = 500,
            TotalTimeoutMs = 10_000,
            MaxRetries = 2,
            BaseRetryDelayMs = 100,
            CircuitBreakerMinimumThroughput = 100,
            CircuitBreakerSamplingDurationMs = 30_000,
            CircuitBreakerBreakDurationMs = 5_000
        };

        var handler = StubHandler.AlwaysReturns(HttpStatusCode.ServiceUnavailable);
        var client = BuildClient(handler, idempotent: true, options: slowRetry);

        var stopwatch = Stopwatch.StartNew();
        await client.GetAsync("/api/v1/products/p-1");
        stopwatch.Stop();

        handler.Attempts.Should().Be(3);
        // Exponential backoff from 100ms means at least 100 + 200 of waiting. Jitter can
        // shorten each delay, so this asserts the floor rather than an exact figure.
        stopwatch.ElapsedMilliseconds.Should().BeGreaterThan(120);
    }

    // ------------------------------------------- non-idempotent: no retry at all

    [Fact]
    public async Task A_non_idempotent_request_is_never_retried_on_a_server_error()
    {
        // Reserving stock twice reserves twice. This is the single most important
        // assertion in this file.
        var handler = StubHandler.AlwaysReturns(HttpStatusCode.InternalServerError);
        var client = BuildClient(handler, idempotent: false);

        var response = await client.PostAsync("/api/v1/products/p-1/stock/reserve", null);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        handler.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_non_idempotent_request_is_never_retried_on_a_timeout()
    {
        // After a timeout we do not know whether the reservation was applied. Repeating it
        // could remove stock twice, so the pipeline must not.
        var handler = StubHandler.Hangs(TimeSpan.FromSeconds(5));
        var client = BuildClient(handler, idempotent: false);

        await FluentActions.Invoking(() =>
                client.PostAsync("/api/v1/products/p-1/stock/reserve", null))
            .Should().ThrowAsync<TimeoutRejectedException>();

        handler.Attempts.Should().Be(1);
    }

    // ---------------------------------------------------------------- timeouts

    [Fact]
    public async Task A_hanging_dependency_fails_fast_instead_of_blocking()
    {
        var handler = StubHandler.Hangs(TimeSpan.FromSeconds(30));
        var client = BuildClient(handler, idempotent: false);

        var stopwatch = Stopwatch.StartNew();
        await FluentActions.Invoking(() => client.GetAsync("/api/v1/products/p-1"))
            .Should().ThrowAsync<TimeoutRejectedException>();
        stopwatch.Stop();

        // The attempt timeout is 200ms. Without it this would have waited 30 seconds.
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(3_000);
    }

    [Fact]
    public async Task The_total_timeout_caps_the_whole_operation_including_retries()
    {
        var options = new DownstreamOptions
        {
            BaseUrl = FastOptions.BaseUrl,
            AttemptTimeoutMs = 300,
            TotalTimeoutMs = 700,
            MaxRetries = 5,
            BaseRetryDelayMs = 50,
            CircuitBreakerMinimumThroughput = 100,
            CircuitBreakerSamplingDurationMs = 30_000,
            CircuitBreakerBreakDurationMs = 5_000
        };

        var handler = StubHandler.Hangs(TimeSpan.FromSeconds(30));
        var client = BuildClient(handler, idempotent: true, options: options);

        var stopwatch = Stopwatch.StartNew();
        await FluentActions.Invoking(() => client.GetAsync("/api/v1/products/p-1"))
            .Should().ThrowAsync<TimeoutRejectedException>();
        stopwatch.Stop();

        // Five retries at 300ms each would be 1.8s; the 700ms total budget cuts it short.
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(2_000);
    }

    // --------------------------------------------------------- circuit breaker

    [Fact]
    public async Task The_circuit_opens_after_repeated_failures_and_stops_calling()
    {
        var options = new DownstreamOptions
        {
            BaseUrl = FastOptions.BaseUrl,
            AttemptTimeoutMs = 200,
            TotalTimeoutMs = 5_000,
            MaxRetries = 0,
            BaseRetryDelayMs = 10,
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerMinimumThroughput = 4,
            CircuitBreakerSamplingDurationMs = 30_000,
            CircuitBreakerBreakDurationMs = 10_000
        };

        var handler = StubHandler.AlwaysReturns(HttpStatusCode.InternalServerError);
        var client = BuildClient(handler, idempotent: false, options: options);

        // Drive enough failures through to trip the breaker.
        for (var i = 0; i < 10; i++)
        {
            try
            {
                await client.GetAsync("/api/v1/products/p-1");
            }
            catch (BrokenCircuitException)
            {
                break;
            }
        }

        var attemptsBefore = handler.Attempts;

        // With the circuit open the next call must fail without reaching the wire.
        await FluentActions.Invoking(() => client.GetAsync("/api/v1/products/p-1"))
            .Should().ThrowAsync<BrokenCircuitException>();

        handler.Attempts.Should().Be(attemptsBefore,
            "an open circuit must fail immediately rather than calling the dependency");
        attemptsBefore.Should().BeLessThan(10, "the breaker should have tripped early");
    }

    [Fact]
    public async Task A_healthy_dependency_never_trips_the_circuit()
    {
        var handler = StubHandler.AlwaysReturns(HttpStatusCode.OK);
        var client = BuildClient(handler, idempotent: true);

        for (var i = 0; i < 20; i++)
        {
            var response = await client.GetAsync("/api/v1/products/p-1");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        handler.Attempts.Should().Be(20);
    }

    [Fact]
    public async Task Client_errors_do_not_trip_the_circuit()
    {
        // Callers sending bad requests must not take a healthy dependency offline for
        // everyone else.
        var handler = StubHandler.AlwaysReturns(HttpStatusCode.NotFound);
        var client = BuildClient(handler, idempotent: true);

        for (var i = 0; i < 20; i++)
        {
            var response = await client.GetAsync("/api/v1/products/missing");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        handler.Attempts.Should().Be(20);
    }
}
