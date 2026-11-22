using System.Reflection;
using Azure.Core;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ShopFlow.Observability;

public static class ObservabilityHostBuilderExtensions
{
    public const string ServiceNamespace = "shopflow";

    /// <summary>
    /// Traces, metrics and logs through OpenTelemetry, exported to Application Insights.
    /// Host-specific instrumentation (ASP.NET Core, Azure Functions) is added by
    /// <c>ShopFlow.Web</c> and <c>ShopFlow.Functions</c>.
    /// </summary>
    /// <param name="builder">Host builder.</param>
    /// <param name="serviceName">
    /// Deployable unit name, e.g. <c>orders-api</c>. In Application Insights it becomes <c>cloud_RoleName</c>,
    /// which tells services apart on the application map and in KQL queries.
    /// </param>
    public static IHostApplicationBuilder AddShopFlowObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var options = new ObservabilityOptions
        {
            ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"],
            Environment = builder.Environment.EnvironmentName,
        };
        builder.Configuration.GetSection("Observability").Bind(options);

        var version = options.ServiceVersion
            ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        // AZURE: Azure SDK clients (Service Bus, Cosmos, Blob...) emit their own spans through ActivitySources
        // named "Azure.*", but only after this switch is turned on. Without it the order trace stops at our code
        // and you cannot see how long sending a message took or which Cosmos write was slow.
        AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

        var exportToAzureMonitor = !string.IsNullOrWhiteSpace(options.ConnectionString);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, ServiceNamespace, version, serviceInstanceId: Environment.MachineName)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", options.Environment)]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("ShopFlow.*")
                    .AddSource("Azure.*")
                    .AddHttpClientInstrumentation(http =>
                    {
                        // Calls to the managed identity token endpoint and App Configuration refreshes are thousands of
                        // identical spans a day with no diagnostic value.
                        http.FilterHttpRequestMessage = request =>
                            request.RequestUri is not { } uri
                            || !(uri.AbsolutePath.Contains("/msi/token", StringComparison.OrdinalIgnoreCase)
                                 || uri.Host.EndsWith(".azconfig.io", StringComparison.OrdinalIgnoreCase));
                    });

                if (exportToAzureMonitor)
                {
                    tracing.AddAzureMonitorTraceExporter(exporter => ConfigureExporter(exporter, options, builder.Services));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("ShopFlow.*")
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (exportToAzureMonitor)
                {
                    metrics.AddAzureMonitorMetricExporter(exporter => ConfigureExporter(exporter, options, builder.Services));
                }
            });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            // Logging scopes (e.g. BeginScope with an order identifier) end up in customDimensions.
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;

            if (exportToAzureMonitor)
            {
                logging.AddAzureMonitorLogExporter(exporter => ConfigureExporter(exporter, options, builder.Services));
            }
        });

        return builder;
    }

    private static void ConfigureExporter(AzureMonitorExporterOptions exporter, ObservabilityOptions options, IServiceCollection services)
    {
        exporter.ConnectionString = options.ConnectionString;
        exporter.SamplingRatio = options.SamplingRatio;

        // AZURE: ingestion is authenticated with an Entra ID token (Monitoring Metrics Publisher role on the
        // Application Insights resource). The credential is taken from the DI container if ShopFlow.Azure registered
        // it; the order of calls in Program.cs then does not matter.
        var credential = services
            .Where(descriptor => descriptor.ServiceType == typeof(TokenCredential))
            .Select(descriptor => descriptor.ImplementationInstance as TokenCredential)
            .FirstOrDefault(instance => instance is not null);

        if (credential is not null)
        {
            exporter.Credential = credential;
        }

        // AZURE: when ingestion is briefly unavailable, the exporter stores telemetry on disk and sends it later.
        // The disk is ephemeral in containers, but it covers short interruptions.
        exporter.DisableOfflineStorage = false;
    }
}
