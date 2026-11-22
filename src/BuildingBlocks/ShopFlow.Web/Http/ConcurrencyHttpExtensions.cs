using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace ShopFlow.Web.Http;

/// <summary>
/// Optimistic concurrency at the HTTP level: ETag in the response, If-Match in a modifying request.
/// </summary>
/// <remarks>
/// AZURE: the Cosmos DB ETag (<c>_etag</c>) is passed to the client unchanged. It is already a quoted string, e.g.
/// <c>"0000d986-0000-0d00-0000-655f2a1b0000"</c>, compliant with the ETag header syntax. The client sends it back
/// in If-Match and we pass it to Cosmos as the write condition. The whole "client → API → database" path uses one
/// version token and needs no version column of its own.
/// </remarks>
public static class ConcurrencyHttpExtensions
{
    /// <summary>Reads If-Match. Rejects <c>*</c> and weak ETags, which do not identify a specific version.</summary>
    public static bool TryGetIfMatch(this HttpRequest request, out string etag)
    {
        ArgumentNullException.ThrowIfNull(request);

        etag = string.Empty;
        var raw = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw)
            || !EntityTagHeaderValue.TryParse(raw, out var parsed)
            || parsed.IsWeak
            || parsed.Tag == "*")
        {
            return false;
        }

        etag = parsed.Tag.ToString();
        return true;
    }

    public static void SetETag(this HttpResponse response, string? etag)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!string.IsNullOrEmpty(etag))
        {
            response.Headers.ETag = etag;
        }
    }

    /// <summary>
    /// 428 Precondition Required: a modifying endpoint requires If-Match.
    /// </summary>
    /// <remarks>
    /// WHY: without enforcement, a client that does not send If-Match overwrites other people's changes (lost update)
    /// and nobody notices. RFC 6585 defines a dedicated status code for this.
    /// </remarks>
    public static IResult PreconditionRequired() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status428PreconditionRequired,
            title: "If-Match header is required",
            detail: "Read the resource first and send its ETag in the If-Match header.");
}
