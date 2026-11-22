using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Azure.Storage;
using ShopFlow.Contracts;
using ShopFlow.Functions;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Shipping.Application;
using ShopFlow.Shipping.Infrastructure;

namespace ShopFlow.Shipping.Functions;

public sealed class ShippingMessagingFunctions(ServiceBusTriggerAdapter adapter, CosmosOutboxRelay relay)
{
    private static readonly ConsumerEndpoint Endpoint = new(MessagingTopology.Consumers.Shipping, UnhandledMessagePolicy.DeadLetter);

    /// <remarks>
    /// AZURE: a queue without sessions. Creating a shipment is a slow carrier API call, so parallelism across orders matters
    /// more than ordering; the "cancel before create" race is resolved by a tombstone.
    /// </remarks>
    [Function("shipping-commands")]
    public Task Commands(
        [ServiceBusTrigger(MessagingTopology.Queues.ShippingCommands, Connection = "ServiceBus", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken) =>
        adapter.ProcessAsync(message, actions, Endpoint, cancellationToken);

    [Function("shipping-outbox-relay")]
    [ExponentialBackoffRetry(-1, "00:00:02", "00:05:00")]
    public Task OutboxRelay(
        [CosmosDBTrigger(
            databaseName: ShippingContainers.DatabaseName,
            containerName: ShippingContainers.ShipmentsName,
            Connection = "Cosmos",
            LeaseContainerName = ShippingContainers.LeasesName,
            LeaseContainerPrefix = "outbox-relay/",
            CreateLeaseContainerIfNotExists = false)]
        IReadOnlyList<JsonElement> changes,
        CancellationToken cancellationToken) =>
        relay.RelayAsync(changes, ShippingContainers.ShipmentsName, cancellationToken);
}

/// <summary>
/// Carrier webhook (parcel dispatched, delivered).
/// </summary>
/// <remarks>
/// AZURE: a different trust model from the PSP webhook in Payments. The carrier does not sign the body; it sends a fixed
/// token in a header. Verification is moved to the gateway:
/// <list type="number">
/// <item>APIM checks the carrier's IP address and the <c>Authorization: Bearer</c> header against a named value fetched
/// from Key Vault (the ip-filter and check-header policies in infra/apim/policies/carrier-webhook.xml).</item>
/// <item>APIM removes the carrier's token and attaches its own Entra ID token (the authentication-managed-identity
/// policy), issued for the APIM managed identity with the Carrier.Webhooks.Write app role.</item>
/// <item>Easy Auth on the function app lets in only requests with a valid token, and the code checks the role.</item>
/// </list>
/// Only APIM knows the carrier's secret, and the function trusts only identities from our tenant.
/// </remarks>
public sealed class CarrierWebhookFunction(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
{
    public const string WebhookRole = "Carrier.Webhooks.Write";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Function("carrier-webhook")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhooks/carrier")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (EasyAuthPrincipal.Parse(request.Headers[EasyAuthPrincipal.HeaderName])?.IsInRole(WebhookRole) != true)
        {
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        CarrierPayload? payload;
        try
        {
            payload = await request.ReadFromJsonAsync<CarrierPayload>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            return new BadRequestResult();
        }

        if (payload?.Reference is null || string.IsNullOrEmpty(payload.EventId))
        {
            return new BadRequestResult();
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var now = timeProvider.GetUtcNow();

            // The same trick as in Payments: the carrier event as an "incoming message" for the inbox.
            scope.ServiceProvider.GetRequiredService<MessageContextAccessor>().SetIncoming(new MessageContext
            {
                MessageId = DeterministicGuid.Create($"carrier-webhook/{payload.EventId}"),
                MessageType = payload.Type,
                ContractVersion = 1,
                CorrelationId = payload.Reference.OrderId.ToString("D"),
                Source = "carrier",
                OccurredAt = payload.OccurredAt,
                Consumer = "carrier-webhook",
                DeliveryCount = 1,
                EnqueuedAt = now,
            });

            try
            {
                var outcome = await scope.ServiceProvider.GetRequiredService<CarrierWebhookProcessor>().ProcessAsync(
                    new CarrierEvent(payload.EventId, payload.Type, payload.Reference.OrderId, payload.Reference.ShipmentId, payload.OccurredAt, payload.EstimatedDeliveryDate),
                    cancellationToken);

                return outcome == CarrierEventOutcome.RetryLater ? new StatusCodeResult(StatusCodes.Status503ServiceUnavailable) : new OkResult();
            }
            catch (DuplicateMessageException)
            {
                return new OkResult();
            }
        }
    }

    private sealed record CarrierPayload(string EventId, string Type, DateTimeOffset OccurredAt, DateOnly? EstimatedDeliveryDate, CarrierReference? Reference);

    private sealed record CarrierReference(Guid OrderId, Guid ShipmentId);
}

/// <summary>Label download by a warehouse employee: a redirect to a short-lived SAS link.</summary>
/// <remarks>
/// WHY: the function does not stream the file itself (that would cost execution time and memory); it issues a link valid
/// for 15 minutes and answers 302. The file goes from Blob Storage through Front Door straight to the browser.
/// </remarks>
public sealed class ShipmentLabelFunction(IShipmentRepository shipments, BlobReadLinkFactory links, Microsoft.Extensions.Options.IOptions<LabelStorageOptions> storage)
{
    public const string OperatorRole = "Warehouse.Operator";

    [Function("shipment-label")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipments/{orderId:guid}/{shipmentId:guid}/label")] HttpRequest request,
        Guid orderId,
        Guid shipmentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (EasyAuthPrincipal.Parse(request.Headers[EasyAuthPrincipal.HeaderName])?.IsInRole(OperatorRole) != true)
        {
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        var shipment = await shipments.GetAsync(orderId, shipmentId, cancellationToken);
        if (shipment?.LabelBlobPath is not { } path)
        {
            return new NotFoundResult();
        }

        var link = await links.CreateReadLinkAsync(storage.Value.ContainerName, path, TimeSpan.FromMinutes(15), cancellationToken);
        return new RedirectResult(link.ToString(), permanent: false);
    }
}
