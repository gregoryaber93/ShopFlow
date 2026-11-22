using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PactNet;
using PactNet.Verifier;
using ShopFlow.Contracts;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Testing;
using ShopFlow.Messaging.Outbox;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.Shipping.Application;
using ShopFlow.Shipping.Domain;
using ShopFlow.Testing;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.Pact.Tests.Providers;

/// <summary>
/// Providers verify the pacts written by consumers.
/// </summary>
[Collection(PactCollection.Name)]
public sealed class ProviderPactTests(ITestOutputHelper output)
{
    /// <remarks>
    /// WHY: the Pact verifier sends real HTTP requests, so the API must listen on a port (Kestrel) rather than run in an
    /// in-memory TestServer. Since .NET 10 WebApplicationFactory can do the same (<c>UseKestrel</c>).
    /// Authorization is real: the verifier attaches a test identity header with the Catalog.Prices.Read role, just as
    /// orders-api has it in its Entra ID token.
    /// </remarks>
    [Fact]
    public void Inventory_api_honours_the_pacts_of_its_consumers()
    {
        using var factory = new InventoryProviderFactory();
        factory.StartServer();

        using var verifier = new PactVerifier("inventory-api", new PactVerifierConfig { LogLevel = PactLogLevel.Warn, Outputters = [new XunitOutput(output)] });

        verifier
            .WithHttpEndpoint(factory.ServerUri)
            .WithFileSource(PactFiles.For("orders-api", "inventory-api"))
            .WithProviderStateUrl(new Uri(factory.ServerUri, ProviderStates.Path))
            .WithCustomHeader(TestAuthHandler.Header, "app:Catalog.Prices.Read")
            .Verify();
    }

    /// <remarks>
    /// WHY: the message body comes from the real provider code: the Shipment aggregate moves to the "dispatched" state, and
    /// ShipmentEventPublisher maps the domain event to the contract. The metadata (type and version) comes from the contract
    /// catalog that the Service Bus adapter also uses. Renaming a field in the contract or a bug in the mapping stops this
    /// test before the event reaches Orders.
    /// </remarks>
    [Fact]
    public async Task Shipping_messages_honour_the_pacts_of_their_consumers()
    {
        var contract = MessageTypeRegistry.Default.GetContractId<ShippingV1.ShipmentDispatched>();

        await using var provider = await MessageProviderHost.StartAsync(
            new Dictionary<string, Func<ProvidedMessage>>
            {
                ["ShipmentDispatched for a confirmed order"] = () => new ProvidedMessage(
                    DispatchedEvent(),
                    new Dictionary<string, string>
                    {
                        ["subject"] = contract.Name,
                        ["contractVersion"] = contract.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }),
            },
            ContractJson.Options);

        using var verifier = new PactVerifier("shipping-processor", new PactVerifierConfig { LogLevel = PactLogLevel.Warn, Outputters = [new XunitOutput(output)] });

        // WHY: a custom message endpoint instead of verifier.WithMessages(...). The built-in message provider in PactNet 5.0.0 and
        // 5.0.1 registers a transport with a "message://" address that the verifier cannot call (pact-net issues #530 and #544).
        // The protocol is simple: the verifier sends a POST with the interaction description, and the provider answers with the
        // message body and the metadata in the Pact-Message-Metadata header.
        verifier
            .WithHttpEndpoint(provider.Uri)
            .WithFileSource(PactFiles.For("orders-processors", "shipping-processor"))
            .Verify();
    }

    /// <remarks>
    /// Control test: a provider that lost a field Orders reads (the tracking number) must fail the verification.
    /// Without it, a green result above could mean the verifier compared nothing.
    /// </remarks>
    [Fact]
    public async Task Verification_rejects_a_message_without_a_field_the_consumer_reads()
    {
        var dispatched = DispatchedEvent();

        await using var provider = await MessageProviderHost.StartAsync(
            new Dictionary<string, Func<ProvidedMessage>>
            {
                ["ShipmentDispatched for a confirmed order"] = () => new ProvidedMessage(
                    new { dispatched.OrderId, dispatched.CustomerId, dispatched.ShipmentId, dispatched.CarrierCode, dispatched.DispatchedAt },
                    new Dictionary<string, string> { ["subject"] = "ShipmentDispatched", ["contractVersion"] = "1" }),
            },
            ContractJson.Options);

        using var verifier = new PactVerifier("shipping-processor", new PactVerifierConfig { LogLevel = PactLogLevel.Warn, Outputters = [new XunitOutput(output)] });

        Should.Throw<PactNet.Exceptions.PactVerificationFailedException>(() => verifier
            .WithHttpEndpoint(provider.Uri)
            .WithFileSource(PactFiles.For("orders-processors", "shipping-processor"))
            .Verify());
    }

    private static ShippingV1.ShipmentDispatched DispatchedEvent()
    {
        var shipment = Shipment.Create(
            PactExamples.ShipmentId,
            PactExamples.OrderId,
            PactExamples.CustomerId,
            "SF-PACT-0001",
            "WAW-1",
            new ShipmentDestination(false, null, "Jan Kowalski", "ul. Prosta 1", null, "Warszawa", "00-001", "PL", null),
            "DPD",
            "TRK-0001",
            "2026/11/label.pdf",
            PactExamples.DispatchedAt.AddHours(-2));
        shipment.MarkDispatched(PactExamples.DispatchedAt, null);

        var outbox = new CapturingOutbox();
        new ShipmentEventPublisher(outbox).Publish(shipment);
        return outbox.Messages.OfType<ShippingV1.ShipmentDispatched>().Single();
    }

    private sealed class CapturingOutbox : IOutbox
    {
        public List<IMessage> Messages { get; } = [];

        public void Enqueue(IMessage message) => Messages.Add(message);
    }
}

/// <summary>inventory-api on a real Kestrel, with fakes of the stock and catalog stores.</summary>
internal sealed class InventoryProviderFactory : WebApplicationFactory<Program>
{
    public InventoryProviderFactory()
    {
        var port = FreePort();
        ServerUri = new Uri($"http://localhost:{port}");
        UseKestrel(port);
    }

    public Uri ServerUri { get; }

    public FakeCatalogReader Catalog { get; } = new();

    public InMemoryInventoryState State { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Sql:ConnectionString", "Server=tcp:test,1433;Database=sqldb-inventory");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(State);
            services.AddScoped<InMemoryInventoryStore>();
            services.Replace(ServiceDescriptor.Scoped<IStockRepository>(provider => provider.GetRequiredService<InMemoryInventoryStore>()));
            services.Replace(ServiceDescriptor.Scoped<IProductRepository>(provider => provider.GetRequiredService<InMemoryInventoryStore>()));
            services.Replace(ServiceDescriptor.Scoped<IInventoryUnitOfWork>(provider => provider.GetRequiredService<InMemoryInventoryStore>()));
            services.Replace(ServiceDescriptor.Singleton<ICatalogReader>(Catalog));
            services.UseTestAuthentication();
            services.AddSingleton<IStartupFilter>(new ProviderStates(Catalog));
        });
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// Provider states endpoint: before every interaction the verifier sends the state name from the pact ("Given ..."), and
/// the provider prepares the data that interaction needs.
/// </summary>
/// <remarks>
/// Plugged in through an <see cref="IStartupFilter"/> at the very beginning of the pipeline, before authentication. It
/// exists only in the test host; the production API has no such endpoint.
/// </remarks>
internal sealed class ProviderStates(FakeCatalogReader catalog) : IStartupFilter
{
    public const string Path = "/provider-states";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Map(Path, branch => branch.Run(async context =>
        {
            var request = await JsonSerializer.DeserializeAsync<ProviderStateRequest>(context.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), context.RequestAborted);
            Apply(request?.State);
            context.Response.StatusCode = StatusCodes.Status200OK;
        }));

        next(app);
    };

    private void Apply(string? state)
    {
        catalog.Prices.Clear();

        switch (state)
        {
            case "SKU-1 costs 19.99 PLN and is available":
                catalog.Prices.Add(new PriceView("SKU-1", "Kubek", new Money(19.99m, "PLN"), true));
                break;
            case null:
                break;
            default:
                throw new InvalidOperationException($"Unknown provider state '{state}'. Add it here when a consumer introduces it.");
        }
    }

    private sealed record ProviderStateRequest(string? State, string? Action);
}
