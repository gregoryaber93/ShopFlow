using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShopFlow.OrderSaga.Model;

namespace ShopFlow.OrderSaga.Activities;

public sealed class OrdersApiOptions
{
    public const string SectionName = "OrdersApi";

    /// <summary>
    /// E.g. <c>https://app-shopflow-orders-prod-weu.azurewebsites.net/</c>.
    /// </summary>
    /// <remarks>
    /// AZURE: the public App Service name, but with public access disabled and a private endpoint. The
    /// <c>privatelink.azurewebsites.net</c> private DNS zone resolves it inside the VNet to a private address, so traffic
    /// from the function (VNet integration) never leaves the network.
    /// </remarks>
    [Required]
    public Uri? BaseUrl { get; set; }

    /// <summary>E.g. <c>api://shopflow-orders/.default</c>. The token carries the Orders.Internal.Write role.</summary>
    [Required]
    public string Scope { get; set; } = string.Empty;
}

/// <summary>
/// The saga's operations on an order: GET for the current ETag, PUT with If-Match, retry after 412.
/// </summary>
public sealed partial class OrdersApiClient(HttpClient http, ILogger<OrdersApiClient> logger)
{
    private const int MaxConcurrencyAttempts = 3;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<OrdersCallOutcome> ConfirmAsync(ConfirmOrderInput input, CancellationToken cancellationToken) =>
        ApplyAsync(input.OrderId, "confirmation", new { trackingNumber = input.TrackingNumber }, ["confirmed", "shipped", "completed"], cancellationToken);

    public Task<OrdersCallOutcome> RejectAsync(RejectOrderInput input, CancellationToken cancellationToken) =>
        ApplyAsync(input.OrderId, "rejection", new { reasonCode = input.ReasonCode, description = input.Description }, ["rejected"], cancellationToken);

    public Task<OrdersCallOutcome> CompleteCancellationAsync(Guid orderId, CancellationToken cancellationToken) =>
        ApplyAsync(orderId, "cancellation", new { }, ["cancelled"], cancellationToken);

    /// <remarks>
    /// An activity may run more than once. When the first run changed the order and the response was lost, the second
    /// run sees the target state already at GET and finishes without a PUT. That is why the "Applied" result means
    /// "the order is in the target state", not "this call changed it".
    /// </remarks>
    private async Task<OrdersCallOutcome> ApplyAsync(
        Guid orderId,
        string operation,
        object body,
        string[] targetStatuses,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            using var current = await http.GetAsync($"internal/orders/{orderId}", cancellationToken).ConfigureAwait(false);

            // 404 is also a transient error here: an order saved a moment ago may not be visible to this read yet.
            // Exception → the activity is retried with a durable delay.
            current.EnsureSuccessStatusCode();

            var order = await current.Content.ReadFromJsonAsync<OrderView>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new HttpRequestException("Orders API returned an empty body.");

            if (targetStatuses.Contains(order.Status, StringComparer.OrdinalIgnoreCase))
            {
                return OrdersCallOutcome.Applied;
            }

            using var request = new HttpRequestMessage(HttpMethod.Put, $"internal/orders/{orderId}/{operation}")
            {
                Content = JsonContent.Create(body, options: Json),
            };
            request.Headers.IfMatch.Add(current.Headers.ETag ?? throw new HttpRequestException("Orders API did not return an ETag."));

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return OrdersCallOutcome.Applied;
            }

            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // The order changed between GET and PUT (e.g. the customer asked for cancellation). Read again.
                LogConcurrencyRetry(orderId, operation, attempt);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var problem = await response.Content.ReadFromJsonAsync<ProblemView>(Json, cancellationToken).ConfigureAwait(false);
                return problem?.Code == "order.cancellation_pending"
                    ? OrdersCallOutcome.CancellationPending
                    : OrdersCallOutcome.OrderClosed;
            }

            response.EnsureSuccessStatusCode();
        }

        throw new InvalidOperationException($"Order {orderId} kept changing during {operation}; the activity will be retried.");
    }

    private sealed record OrderView(string Status);

    private sealed record ProblemView(string? Code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} changed during {Operation}; re-reading (attempt {Attempt})")]
    private partial void LogConcurrencyRetry(Guid orderId, string operation, int attempt);
}
