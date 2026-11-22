using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ShopFlow.Azure.Storage;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Shipping.V1;
using ShopFlow.Messaging;
using ShopFlow.Observability;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Persistence.Cosmos.Documents;
using ShopFlow.Resilience;
using ShopFlow.Shipping.Application;
using ShopFlow.Shipping.Domain;

namespace ShopFlow.Shipping.Infrastructure;

public sealed class ShippingContainers(Database database)
{
    public const string DatabaseName = "shipping";
    public const string ShipmentsName = "shipments";
    public const string PartitionKey = "orderId";
    public const string LeasesName = "leases";

    public Container Shipments { get; } = database.GetContainer(ShipmentsName);
}

public sealed class ShipmentDocument : CosmosDocument
{
    [JsonPropertyName("docType")]
    public override string DocType => "shipment";

    public required string OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    public required string WarehouseCode { get; init; }

    public ShipmentDestination? Destination { get; init; }

    public string? CarrierCode { get; init; }

    public string? TrackingNumber { get; init; }

    public string? LabelBlobPath { get; init; }

    public required ShipmentStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DispatchedAt { get; init; }

    public DateOnly? EstimatedDeliveryDate { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }
}

internal sealed class CosmosShipmentRepository(ShippingContainers containers, ICosmosUnitOfWorkFactory unitOfWork) : IShipmentRepository
{
    public async Task<Shipment?> GetAsync(Guid orderId, Guid shipmentId, CancellationToken cancellationToken)
    {
        var document = await containers.Shipments.ReadOrDefaultAsync<ShipmentDocument>(shipmentId.ToString("D"), orderId.ToString("D"), cancellationToken).ConfigureAwait(false);

        return document is null
            ? null
            : Shipment.Restore(
                Guid.Parse(document.Id),
                Guid.Parse(document.OrderId),
                document.CustomerId,
                document.OrderNumber,
                document.WarehouseCode,
                document.Destination,
                document.CarrierCode,
                document.TrackingNumber,
                document.LabelBlobPath,
                document.Status,
                document.CreatedAt,
                document.DispatchedAt,
                document.EstimatedDeliveryDate,
                document.ClosedAt,
                document.ETag);
    }

    public async Task SaveAsync(Shipment shipment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shipment);

        var document = new ShipmentDocument
        {
            Id = shipment.Id.ToString("D"),
            OrderId = shipment.OrderId.ToString("D"),
            CustomerId = shipment.CustomerId,
            OrderNumber = shipment.OrderNumber,
            WarehouseCode = shipment.WarehouseCode,
            Destination = shipment.Destination,
            CarrierCode = shipment.CarrierCode,
            TrackingNumber = shipment.TrackingNumber,
            LabelBlobPath = shipment.LabelBlobPath,
            Status = shipment.Status,
            CreatedAt = shipment.CreatedAt,
            DispatchedAt = shipment.DispatchedAt,
            EstimatedDeliveryDate = shipment.EstimatedDeliveryDate,
            ClosedAt = shipment.ClosedAt,
        };

        var work = unitOfWork.Create(containers.Shipments, ShippingContainers.PartitionKey, document.OrderId);
        work = shipment.ConcurrencyToken is { } etag ? work.Replace(document, etag) : work.Create(document);
        var result = await work.CommitAsync(cancellationToken).ConfigureAwait(false);
        shipment.SetConcurrencyToken(result.GetETag(document.Id) ?? shipment.ConcurrencyToken);
    }

    public Task CommitWithoutShipmentAsync(Guid orderId, CancellationToken cancellationToken) =>
        unitOfWork.Create(containers.Shipments, ShippingContainers.PartitionKey, orderId.ToString("D")).CommitAsync(cancellationToken);
}

public sealed class CarrierOptions
{
    public const string SectionName = "Carrier";

    [Required]
    public Uri? BaseUrl { get; set; }

    [Required]
    public string CarrierCode { get; set; } = "DPD";

    /// <summary>Carrier API key: Key Vault through a reference in App Configuration (as in Payments).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// REST adapter of the carrier (a fictitious API in the style of typical courier APIs).
/// </summary>
/// <remarks>
/// WHY: carriers have hard request limits and answer 429 with a Retry-After header. The AddCarrierResilience profile
/// honours that header (waits as long as the carrier says), and under longer overload the circuit breaker stops calling
/// the API. Commands then wait in the shipping-commands queue and do not burn through the delivery limit.
/// </remarks>
internal sealed class CarrierGatewayClient(HttpClient http, IOptions<CarrierOptions> options) : ICarrierGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string CarrierCode => options.Value.CarrierCode;

    public async Task<ParcelResult> CreateParcelAsync(ParcelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var destination = request.Destination;

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/parcels")
        {
            Content = JsonContent.Create(new
            {
                reference = new { orderNumber = request.OrderNumber, orderId = request.OrderId, shipmentId = request.ShipmentId },
                sender = new { warehouseCode = request.WarehouseCode },
                service = destination.IsPickupPoint ? "pickup_point" : "standard",
                pickupPointId = destination.PickupPointId,
                receiver = destination.IsPickupPoint ? null : new
                {
                    name = destination.RecipientName,
                    street = destination.Line1,
                    street2 = destination.Line2,
                    city = destination.City,
                    postalCode = destination.PostalCode,
                    country = destination.CountryCode,
                    phone = destination.Phone,
                },
                items = request.Lines.Select(line => new { sku = line.Sku, quantity = line.Quantity }),
                labelFormat = "pdf",
            }, options: Json),
        };

        // A retry (Polly or a command redelivery) does not create a second parcel at the carrier.
        message.Headers.Add(ShopFlowHttpHeaders.IdempotencyKey, request.ShipmentId.ToString("N"));

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(Json, cancellationToken).ConfigureAwait(false);
            return ParcelResult.Refused(error?.Error?.Code ?? "address_undeliverable", error?.Error?.Message);
        }

        response.EnsureSuccessStatusCode();
        var parcel = await response.Content.ReadFromJsonAsync<ParcelResponse>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("Carrier returned an empty parcel.");

        return ParcelResult.Created(parcel.TrackingNumber, Convert.FromBase64String(parcel.Label.ContentBase64));
    }

    public async Task<bool> CancelParcelAsync(Guid shipmentId, string trackingNumber, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"v1/parcels/{Uri.EscapeDataString(trackingNumber)}", cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent or HttpStatusCode.OK)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private sealed record ParcelResponse(string TrackingNumber, LabelResponse Label);

    private sealed record LabelResponse(string Format, string ContentBase64);

    private sealed record ErrorEnvelope(ErrorBody? Error);

    private sealed record ErrorBody(string Code, string? Message);
}

internal sealed class CarrierAuthenticationHandler(IOptionsMonitor<CarrierOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CurrentValue.ApiKey);
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class LabelStorageOptions
{
    public const string SectionName = "LabelStorage";

    [Required]
    public Uri? ServiceUri { get; set; }

    public string ContainerName { get; set; } = "labels";
}

/// <summary>
/// Labels in the <c>labels</c> container. AZURE: a lifecycle rule moves them to the cool tier after 7 days (they are
/// printed right after creation) and deletes them after 90 (the period for shipping claims).
/// </summary>
internal sealed class BlobLabelStorage(BlobServiceClient blobs, IOptions<LabelStorageOptions> options, TimeProvider timeProvider) : ILabelStorage
{
    public async Task<string> UploadAsync(Guid orderId, Guid shipmentId, byte[] labelPdf, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var path = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy}/{now:MM}/{orderId:D}/{shipmentId:D}.pdf");

        // Overwriting is safe here: on a retry the carrier (idempotency key) returns the same label.
        await blobs.GetBlobContainerClient(options.Value.ContainerName)
            .GetBlobClient(path)
            .UploadAsync(BinaryData.FromBytes(labelPdf), new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" } }, cancellationToken)
            .ConfigureAwait(false);

        return path;
    }
}

public static class ShippingInfrastructureExtensions
{
    public static IServiceCollection AddShippingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddShopFlowCosmos(configuration);
        services.AddSingleton<ShippingContainers>();
        services.AddScoped<IShipmentRepository, CosmosShipmentRepository>();
        services.TryAddSingleton(TimeProvider.System);

        var labels = configuration.GetSection(LabelStorageOptions.SectionName);
        services.AddOptions<LabelStorageOptions>().Bind(labels).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<BlobLinkOptions>().Bind(configuration.GetSection(BlobLinkOptions.SectionName));
        services.AddAzureClients(clients => clients.AddBlobServiceClient(labels));
        services.AddSingleton<ILabelStorage, BlobLabelStorage>();
        services.AddSingleton<BlobReadLinkFactory>();

        services.AddOptions<CarrierOptions>().Bind(configuration.GetSection(CarrierOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        var baseUrl = configuration.GetSection(CarrierOptions.SectionName).Get<CarrierOptions>()?.BaseUrl;
        services.AddTransient<CarrierAuthenticationHandler>();
        var carrier = services.AddHttpClient<ICarrierGateway, CarrierGatewayClient>(http =>
        {
            http.BaseAddress = baseUrl;
            http.Timeout = Timeout.InfiniteTimeSpan;
        });
        carrier.AddCarrierResilience();
        carrier.AddHttpMessageHandler<CarrierAuthenticationHandler>();

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "cosmos-shipping",
            provider => new CosmosContainerHealthCheck(provider.GetRequiredService<ShippingContainers>().Shipments),
            HealthStatus.Unhealthy,
            [HealthTags.Dependencies]));

        return services;
    }

    public static MessagingBuilder AddShippingMessaging(this IServiceCollection services) =>
        services.AddShopFlowMessaging(MessagingTopology.Sources.Shipping)
            .Route<ShipmentCreated>(MessagingTopology.Topics.ShippingEvents)
            .Route<ShipmentCreationFailed>(MessagingTopology.Topics.ShippingEvents)
            .Route<ShipmentCancelled>(MessagingTopology.Topics.ShippingEvents)
            .Route<ShipmentDispatched>(MessagingTopology.Topics.ShippingEvents)
            .Route<ShipmentDelivered>(MessagingTopology.Topics.ShippingEvents);
}
