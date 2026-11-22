using System.Net;
using Polly;
using Polly.Retry;
using ShopFlow.Resilience;

namespace ShopFlow.BuildingBlocks.Tests.Resilience;

public sealed class RetryPredicateTests
{
    [Theory]
    [InlineData("GET", null, HttpStatusCode.ServiceUnavailable, true)]
    [InlineData("PUT", null, HttpStatusCode.ServiceUnavailable, true)]
    [InlineData("POST", null, HttpStatusCode.ServiceUnavailable, false)]
    [InlineData("POST", "key-1", HttpStatusCode.ServiceUnavailable, true)]
    [InlineData("POST", "key-1", HttpStatusCode.TooManyRequests, true)]
    [InlineData("GET", null, HttpStatusCode.BadRequest, false)]
    [InlineData("GET", null, HttpStatusCode.NotFound, false)]
    public async Task Non_idempotent_requests_are_retried_only_with_an_idempotency_key(
        string method,
        string? idempotencyKey,
        HttpStatusCode status,
        bool expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://psp.example/payments");
        if (idempotencyKey is not null)
        {
            request.Headers.Add(ShopFlowHttpHeaders.IdempotencyKey, idempotencyKey);
        }

        using var response = new HttpResponseMessage(status) { RequestMessage = request };
        var context = ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken);

        try
        {
            var arguments = new RetryPredicateArguments<HttpResponseMessage>(context, Outcome.FromResult(response), attemptNumber: 0);

            (await HttpResilienceExtensions.ShouldRetrySafely(arguments)).ShouldBe(expected);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
