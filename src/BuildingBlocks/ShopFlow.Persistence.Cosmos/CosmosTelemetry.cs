using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ShopFlow.Persistence.Cosmos;

/// <summary>
/// Measures request unit (RU) consumption from the application's point of view.
/// </summary>
/// <remarks>
/// AZURE: every Cosmos DB operation has an RU cost returned in a response header
/// (<c>x-ms-request-charge</c>). Platform metrics show consumption per container but not which piece of
/// code caused it. By recording RU as a metric with the operation name and as an attribute of the current
/// span, KQL can answer "which HTTP request or which handler costs the most" (see <c>docs/kql/cosmos-ru.kql</c>).
/// </remarks>
public static class CosmosTelemetry
{
    public const string SourceName = "ShopFlow.Cosmos";

    public const string RequestChargeTag = "shopflow.cosmos.request_charge";

    public static readonly Meter Meter = new(SourceName);

    private static readonly Histogram<double> RequestCharge =
        Meter.CreateHistogram<double>("shopflow.cosmos.request_charge", "{RU}", "Request units consumed by a Cosmos DB operation.");

    public static void RecordRequestCharge(string container, string operation, double requestCharge)
    {
        RequestCharge.Record(
            requestCharge,
            new KeyValuePair<string, object?>("db.cosmosdb.container", container),
            new KeyValuePair<string, object?>("db.operation.name", operation));

        // Summed on the parent span: one business request is usually several operations.
        if (Activity.Current is { } activity)
        {
            var total = activity.GetTagItem(RequestChargeTag) is double previous ? previous + requestCharge : requestCharge;
            activity.SetTag(RequestChargeTag, total);
        }
    }
}
