using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopFlow.Shipping.Application;
using ShopFlow.Shipping.Domain;
using ShopFlow.Shipping.Infrastructure;

namespace ShopFlow.Shipping.UnitTests;

public sealed class CarrierGatewayClientTests
{
    private static readonly Guid ShipmentId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000b1");

    [Fact]
    public async Task Parcel_request_carries_the_shipment_id_as_idempotency_key_and_uses_snake_case()
    {
        var handler = new StubHandler(HttpStatusCode.Created, """{"tracking_number":"TRK-9","label":{"format":"pdf","content_base64":"JVBERg=="}}""");

        var result = await Client(handler).CreateParcelAsync(Request(pickupPoint: true), CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        result.TrackingNumber.ShouldBe("TRK-9");
        result.LabelPdf.ShouldBe("%PDF"u8.ToArray());

        handler.LastRequest!.Headers.GetValues("Idempotency-Key").Single().ShouldBe(ShipmentId.ToString("N"));
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("service").GetString().ShouldBe("pickup_point");
        body.RootElement.GetProperty("pickup_point_id").GetString().ShouldBe("WAW-PP-7");
        body.RootElement.GetProperty("reference").GetProperty("order_number").GetString().ShouldBe("SF-1");
    }

    [Fact]
    public async Task Validation_error_is_a_refusal_not_an_exception()
    {
        var handler = new StubHandler(HttpStatusCode.UnprocessableEntity, """{"error":{"code":"postal_code_invalid","message":"Unknown postal code"}}""");

        var result = await Client(handler).CreateParcelAsync(Request(pickupPoint: false), CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.FailureCode.ShouldBe("postal_code_invalid");
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Conflict, false)]
    public async Task Cancellation_treats_a_missing_parcel_as_cancelled_and_a_conflict_as_too_late(HttpStatusCode status, bool expected)
    {
        var cancelled = await Client(new StubHandler(status, null)).CancelParcelAsync(ShipmentId, "TRK 9", CancellationToken.None);

        cancelled.ShouldBe(expected);
    }

    private static CarrierGatewayClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.carrier.example/") }, Options.Create(new CarrierOptions()));

    private static ParcelRequest Request(bool pickupPoint) => new(
        ShipmentId,
        Guid.NewGuid(),
        "SF-1",
        "WAW-1",
        pickupPoint
            ? new ShipmentDestination(true, "WAW-PP-7", null, null, null, null, null, null, null)
            : new ShipmentDestination(false, null, "Jan", "ul. Prosta 1", null, "Warszawa", "00-001", "PL", null),
        [new ParcelLine("SKU-1", 1)]);

    private sealed class StubHandler(HttpStatusCode status, string? json) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
