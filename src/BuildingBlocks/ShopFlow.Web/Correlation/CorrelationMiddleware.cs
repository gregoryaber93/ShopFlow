using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Diagnostics;
using ShopFlow.Resilience;

namespace ShopFlow.Web.Correlation;

/// <summary>
/// Determines the request's correlation identifier and makes it available to messages, logs and the response.
/// </summary>
/// <remarks>
/// WHY: the trace identifier (traceparent) and the correlation identifier are two different things. A trace
/// describes one technical execution and ends when the saga waits an hour for the carrier's webhook.
/// Correlation is a business concept: all messages of one order, from placement to delivery, share the same
/// CorrelationId, and one KQL query returns the whole history (docs/kql/order-trace.kql).
/// </remarks>
public sealed partial class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context, MessageContextAccessor contextAccessor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contextAccessor);

        var incoming = context.Request.Headers[ShopFlowHttpHeaders.CorrelationId].ToString();
        var correlationId = IsValid(incoming) ? incoming : Guid.CreateVersion7().ToString("D");

        contextAccessor.SetCorrelationId(correlationId);
        Activity.Current?.SetTag(MessagingTelemetry.Tags.CorrelationId, correlationId);
        context.Response.Headers[ShopFlowHttpHeaders.CorrelationId] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    // The value comes from the client and goes into logs and message headers, so we accept only short
    // identifiers without control characters (protection against log injection).
    private static bool IsValid(string value) =>
        value.Length is > 0 and <= MaxLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
