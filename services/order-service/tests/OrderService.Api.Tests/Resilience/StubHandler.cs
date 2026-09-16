using System.Net;

namespace OrderService.Api.Tests.Resilience;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/> that counts attempts.
/// </summary>
/// <remarks>
/// Counting is the whole point: these tests assert how many times a request actually
/// reached the wire, which is what distinguishes "retried" from "not retried".
/// </remarks>
public sealed class StubHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Always returns the given status.</summary>
    public static StubHandler AlwaysReturns(HttpStatusCode status) =>
        new((_, _, _) => Task.FromResult(new HttpResponseMessage(status)));

    /// <summary>Fails with <paramref name="status"/> until the given attempt succeeds.</summary>
    public static StubHandler FailsUntilAttempt(int successfulAttempt, HttpStatusCode status,
        string successBody = "{}") =>
        new((attempt, _, _) => Task.FromResult(attempt >= successfulAttempt
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(successBody) }
            : new HttpResponseMessage(status)));

    /// <summary>Hangs for longer than any configured timeout.</summary>
    public static StubHandler Hangs(TimeSpan delay) =>
        new(async (_, _, cancellationToken) =>
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        return await respond(attempt, request, cancellationToken);
    }
}
