using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ShopFlow.Observability;

namespace ShopFlow.Functions;

public static class FunctionsHostBuilderExtensions
{
    /// <summary>
    /// Common configuration of ShopFlow function apps.
    /// </summary>
    /// <remarks>
    /// AZURE: the isolated worker model runs two processes: the Functions host (manages triggers, scaling and the
    /// connection to Service Bus) and our .NET worker, connected over gRPC. Both produce telemetry. With
    /// <c>"telemetryMode": "OpenTelemetry"</c> in host.json and <c>UseFunctionsWorkerDefaults()</c> here, host and
    /// worker spans join into one trace and the function invocation becomes a child of the Service Bus message span.
    /// Without this Application Insights holds two independent sets of data and the trace breaks.
    /// </remarks>
    public static IHostApplicationBuilder AddShopFlowFunctionsDefaults(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddShopFlowObservability(serviceName);
        builder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults();
        builder.Services.TryAddSingleton<ServiceBusTriggerAdapter>();

        return builder;
    }
}
