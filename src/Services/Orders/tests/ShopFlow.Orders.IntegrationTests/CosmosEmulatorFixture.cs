using Microsoft.Azure.Cosmos;
using ShopFlow.Orders.Infrastructure.Cosmos;
using ShopFlow.Persistence.Cosmos;
using Testcontainers.CosmosDb;

namespace ShopFlow.Orders.IntegrationTests;

/// <summary>
/// Cosmos DB emulator in a Docker container, started once per test class.
/// </summary>
/// <remarks>
/// The tests are skipped by default: they need Docker, and the emulator takes tens of seconds to start. In CI a
/// separate job runs them with the <c>SHOPFLOW_RUN_INTEGRATION_TESTS=1</c> variable (.github/workflows/ci.yml).
/// <para>
/// AZURE: the emulator authenticates only with a publicly known key, and this is the only place in the repository
/// where a key is sent to Cosmos DB. In Azure keys are disabled (<c>disableLocalAuth</c>). The emulator also creates
/// databases and containers from code; in Azure Bicep does that, because application identities have no such
/// permissions.
/// </para>
/// </remarks>
public sealed class CosmosEmulatorFixture : IAsyncLifetime
{
    private const string EmulatorImage = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";

    private CosmosDbContainer? _container;

    public static bool Enabled => Environment.GetEnvironmentVariable("SHOPFLOW_RUN_INTEGRATION_TESTS") == "1";

    public CosmosClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        _container = new CosmosDbBuilder(EmulatorImage).Build();
        await _container.StartAsync();

        Client = new CosmosClient(_container.GetConnectionString(), new CosmosClientOptions
        {
            // The containerized emulator supports Gateway mode only and has a self-signed certificate,
            // which the HttpClient provided by Testcontainers accepts.
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () => _container.HttpClient,
            LimitToEndpoint = true,
            UseSystemTextJsonSerializerWithOptions = CosmosJson.Options,
        });

        // The same structure as in infra/modules/data/cosmos-database.bicep.
        Database database = await Client.CreateDatabaseIfNotExistsAsync(OrdersContainers.DatabaseName);
        await database.CreateContainerIfNotExistsAsync(new ContainerProperties(OrdersContainers.OrdersName, $"/{OrdersContainers.OrdersPartitionKey}") { DefaultTimeToLive = -1 });
        await database.CreateContainerIfNotExistsAsync(new ContainerProperties(OrdersContainers.CartsName, $"/{OrdersContainers.CartsPartitionKey}") { DefaultTimeToLive = -1 });
        await database.CreateContainerIfNotExistsAsync(new ContainerProperties(OrdersContainers.IdempotencyName, $"/{OrdersContainers.IdempotencyPartitionKey}") { DefaultTimeToLive = -1 });
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
