using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopFlow.Resilience;
using ShopFlow.Web.Authentication;

namespace ShopFlow.Web.Idempotency;

/// <summary>Marker for an endpoint that requires the Idempotency-Key header.</summary>
public sealed class IdempotentEndpointMetadata;

/// <summary>
/// Implementation of the <c>Idempotency-Key</c> header (per the IETF httpapi-idempotency-key-header draft).
/// </summary>
/// <remarks>
/// Flow:
/// <list type="number">
/// <item>Missing or invalid key → 400.</item>
/// <item>New key → "in progress" record, handler execution, response stored.</item>
/// <item>Known key, same body, completed → the same response, without executing again.</item>
/// <item>Known key, same body, in progress → 409 with Retry-After (the first request is still running).</item>
/// <item>Known key, different body → 422 (the client reused the key for a different operation).</item>
/// </list>
/// WHY: the key is scoped to the (client, key) pair. Clients generate keys, so two different clients can pick the
/// same one, and one client must not learn the response given to another.
/// </remarks>
public sealed partial class IdempotencyEndpointFilter(
    IIdempotencyStore store,
    TimeProvider timeProvider,
    ILogger<IdempotencyEndpointFilter> logger) : IEndpointFilter
{
    private const int MaxKeyLength = 128;
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var key = http.Request.Headers[ShopFlowHttpHeaders.IdempotencyKey].ToString();

        if (key.Length is 0 or > MaxKeyLength)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Idempotency-Key header is required",
                detail: $"Send a unique value of at most {MaxKeyLength} characters (e.g. a UUID) in the {ShopFlowHttpHeaders.IdempotencyKey} header.");
        }

        var subject = http.User.GetObjectId() ?? "anonymous";
        var scopeKey = $"{subject}:{key}";
        var requestHash = await ComputeRequestHashAsync(http.Request).ConfigureAwait(false);

        var existing = await store.TryBeginAsync(scopeKey, requestHash, LockDuration, http.RequestAborted).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, requestHash, http);
        }

        return await ExecuteAndStoreAsync(context, next, scopeKey, requestHash).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteAndStoreAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string scopeKey,
        string requestHash)
    {
        var http = context.HttpContext;
        var originalBody = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;

        try
        {
            // We execute the handler's result into a buffer to store exactly what the client received:
            // status, headers and body, regardless of the IResult type.
            var result = await next(context).ConfigureAwait(false);
            var asResult = result as IResult ?? (result is null ? Results.Empty : TypedResults.Ok(result));
            await asResult.ExecuteAsync(http).ConfigureAwait(false);

            var response = new StoredResponse(
                http.Response.StatusCode,
                http.Response.ContentType,
                http.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
                http.Response.Headers.ETag.ToString() is { Length: > 0 } etag ? etag : null,
                buffer.ToArray());

            if (ShouldRemember(response.StatusCode))
            {
                await store.CompleteAsync(scopeKey, requestHash, response, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await store.ReleaseAsync(scopeKey, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Exception = outcome unknown to the client; release the key so that a retry can execute.
            await store.ReleaseAsync(scopeKey, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            http.Response.Body = originalBody;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, http.RequestAborted).ConfigureAwait(false);
        return Results.Empty;
    }

    /// <summary>
    /// Which responses to store. We store results that are deterministic for the same body: successes and
    /// validation errors. Results that depend on transient state (409, 412, 429, 5xx) release the key, because a
    /// retry with the same key has a chance of a different, correct result.
    /// </summary>
    internal static bool ShouldRemember(int statusCode) =>
        statusCode < 500
        && statusCode is not (StatusCodes.Status409Conflict
            or StatusCodes.Status412PreconditionFailed
            or StatusCodes.Status429TooManyRequests);

    private IResult Replay(IdempotencyRecord existing, string requestHash, HttpContext http)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Idempotency-Key reused with a different request",
                detail: "This key was already used for a request with a different body. Generate a new key for a new operation.");
        }

        if (existing.State == IdempotencyRecordState.InProgress || existing.Response is null)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((existing.LockedUntil - timeProvider.GetUtcNow()).TotalSeconds));
            http.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "A request with this Idempotency-Key is still being processed");
        }

        LogReplayed(existing.ScopeKey, existing.Response.StatusCode);
        return new ReplayedResult(existing.Response);
    }

    private static async Task<string> ComputeRequestHashAsync(HttpRequest request)
    {
        // The body is buffered by IdempotencyBufferingMiddleware before model binding,
        // so it can be read again.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{request.Method} {request.Path}\n"));

        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
            var chunk = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(chunk, 0, read);
            }

            request.Body.Position = 0;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class ReplayedResult(StoredResponse response) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = response.StatusCode;
            httpContext.Response.Headers[ShopFlowHttpHeaders.IdempotentReplayed] = "true";

            if (response.ContentType is not null)
            {
                httpContext.Response.ContentType = response.ContentType;
            }

            if (response.Location is not null)
            {
                httpContext.Response.Headers.Location = response.Location;
            }

            if (response.ETag is not null)
            {
                httpContext.Response.Headers.ETag = response.ETag;
            }

            await httpContext.Response.Body.WriteAsync(response.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Replayed stored response {StatusCode} for idempotency key {ScopeKey}")]
    private partial void LogReplayed(string scopeKey, int statusCode);
}

/// <summary>Enables request body buffering on idempotent endpoints (before model binding).</summary>
public sealed class IdempotencyBufferingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentEndpointMetadata>() is not null)
        {
            context.Request.EnableBuffering();
        }

        return next(context);
    }
}

public static class IdempotencyEndpointExtensions
{
    /// <summary>The endpoint accepts a request only with the Idempotency-Key header and honours it.</summary>
    public static RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithMetadata(new IdempotentEndpointMetadata())
            .AddEndpointFilter(async (context, next) =>
            {
                var filter = ActivatorUtilities.CreateInstance<IdempotencyEndpointFilter>(context.HttpContext.RequestServices);
                return await filter.InvokeAsync(context, next).ConfigureAwait(false);
            });
    }
}
