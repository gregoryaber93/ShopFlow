using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;

namespace ShopFlow.Resilience;

/// <summary>
/// Resilience profiles for HTTP clients. Each dependency gets a profile that matches its nature
/// instead of one "default retry" for everything.
/// </summary>
/// <remarks>
/// The standard handler from Microsoft.Extensions.Http.Resilience composes five Polly strategies
/// in a fixed order (outermost first):
/// <list type="number">
/// <item>rate limiter (caps concurrent outgoing requests),</item>
/// <item>total timeout (including all retries),</item>
/// <item>retry (exponential with jitter, honours the Retry-After header),</item>
/// <item>circuit breaker (stops calling a dependency that mostly fails),</item>
/// <item>per-attempt timeout.</item>
/// </list>
/// WHY: the order matters. The circuit breaker sits inside the retry, so every attempt counts towards
/// the circuit's statistics, and an open circuit ends the retries immediately instead of waiting for timeouts.
/// </remarks>
public static class HttpResilienceExtensions
{
    /// <summary>
    /// Calls to internal ShopFlow services in the same network. Short timeouts: if the neighbouring service
    /// does not answer within 2 seconds, waiting longer only ties up our threads.
    /// </summary>
    public static IHttpStandardResiliencePipelineBuilder AddInternalServiceResilience(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(8);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);

            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromMilliseconds(200);
            options.Retry.ShouldHandle = ShouldRetrySafely;

            // Options validation requires the sampling window to be at least twice as long
            // as the attempt timeout.
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.MinimumThroughput = 20;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
        });
    }

    /// <summary>
    /// Payment provider. Longer timeouts (card authorization can be slow), fewer retries,
    /// POST retried only with an idempotency key.
    /// </summary>
    public static IHttpStandardResiliencePipelineBuilder AddPaymentProviderResilience(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);

            options.Retry.MaxRetryAttempts = 2;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromSeconds(1);
            options.Retry.ShouldHandle = ShouldRetrySafely;

            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            options.CircuitBreaker.MinimumThroughput = 10;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);

            // WHY: the concurrency limit protects the provider and us. When it slows down, without a limit every
            // function instance opens more and more connections until it exhausts the SNAT ports on the NAT
            // Gateway, and then calls to every other dependency fail too.
            options.RateLimiter.DefaultRateLimiterOptions.PermitLimit = 50;
            options.RateLimiter.DefaultRateLimiterOptions.QueueLimit = 0;
        });
    }

    /// <summary>Carrier API: similar to payments, but carriers return 429 with Retry-After more often.</summary>
    public static IHttpStandardResiliencePipelineBuilder AddCarrierResilience(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(20);

            options.Retry.MaxRetryAttempts = 4;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromSeconds(2);
            options.Retry.ShouldHandle = ShouldRetrySafely;

            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            options.CircuitBreaker.MinimumThroughput = 10;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        });
    }

    /// <summary>
    /// Microsoft Graph (reading customer profiles from the External ID tenant).
    /// </summary>
    /// <remarks>
    /// AZURE: Graph throttles per application and per tenant and answers 429 with a Retry-After header, which
    /// the standard handler honours. When Retry-After is longer than the total timeout, the attempt ends with an
    /// exception and the message goes back to Service Bus. This is intentional: it is better to hand the message
    /// back to the broker for redelivery than to keep a function instance that only waits for a minute.
    /// </remarks>
    public static IHttpStandardResiliencePipelineBuilder AddMicrosoftGraphResilience(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);

            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromSeconds(1);
            options.Retry.ShouldHandle = ShouldRetrySafely;

            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            options.CircuitBreaker.MinimumThroughput = 20;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        });
    }

    /// <summary>
    /// Retry transient errors, but non-idempotent methods only when the request carries an idempotency key.
    /// </summary>
    /// <remarks>
    /// WHY: the default predicate retries every HTTP method. For POST /payments that is a recipe for charging a
    /// card twice: the first attempt reached the provider, but the response was lost to a timeout, so the second
    /// attempt creates a second payment. With an idempotency key the provider recognises the repeat and returns
    /// the result of the first attempt, which makes the retry safe.
    /// </remarks>
    internal static ValueTask<bool> ShouldRetrySafely(RetryPredicateArguments<HttpResponseMessage> args)
    {
        if (!HttpClientResiliencePredicates.IsTransient(args.Outcome))
        {
            return ValueTask.FromResult(false);
        }

        var request = args.Context.GetRequestMessage() ?? args.Outcome.Result?.RequestMessage;
        if (request is null)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(IsIdempotent(request));
    }

    internal static bool IsIdempotent(HttpRequestMessage request)
    {
        var method = request.Method;
        if (method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options
            || method == HttpMethod.Put || method == HttpMethod.Delete)
        {
            return true;
        }

        return request.Headers.Contains(ShopFlowHttpHeaders.IdempotencyKey);
    }

    /// <summary>Whether the response means throttling on the dependency's side.</summary>
    public static bool IsThrottled(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
    }
}
