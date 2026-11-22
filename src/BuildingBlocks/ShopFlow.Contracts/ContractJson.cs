using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopFlow.Contracts;

/// <summary>The only allowed serialization configuration for message payloads.</summary>
public static class ContractJson
{
    public const string ContentType = "application/json";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // WHY: forward compatibility. A publisher may add a field before a consumer updates the
            // contracts package. Unknown fields must be ignored; otherwise every additive change
            // would require deploying all services at the same time.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,

            // WHY: backward compatibility is enforced in the other direction. A missing `required` field
            // or null in a non-nullable field ends with a JsonException, not with an object full of
            // default values that would corrupt data three services further down the line.
            RespectNullableAnnotations = true,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
