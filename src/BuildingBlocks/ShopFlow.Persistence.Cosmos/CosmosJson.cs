using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopFlow.Persistence.Cosmos;

/// <summary>Serialization of Cosmos DB documents (separate from message contract serialization).</summary>
public static class CosmosJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            // AZURE: the partition key path (e.g. /orderId) is case-sensitive. One naming policy for all
            // documents prevents writing an "OrderId" field that Cosmos would not recognise as the key,
            // treating the document as belonging to the "no value" partition.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
