using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using ShopFlow.Messaging;
using ShopFlow.Observability;
using ShopFlow.Web.Authentication;
using ShopFlow.Web.Correlation;
using ShopFlow.Web.Health;
using ShopFlow.Web.Http;
using ShopFlow.Web.Idempotency;

namespace ShopFlow.Web;

public static class WebApplicationExtensions
{
    /// <summary>Common configuration of all ShopFlow HTTP APIs.</summary>
    public static WebApplicationBuilder AddShopFlowWebDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddShopFlowObservability(serviceName);
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddAspNetCoreInstrumentation(aspnet =>
        {
            // Health probes called every few seconds by the platform would flood the traces.
            aspnet.Filter = context => !context.Request.Path.StartsWithSegments("/health");
            aspnet.RecordException = true;
        }));

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ShopFlowExceptionHandler>();
        builder.Services.AddShopFlowAuthentication(builder.Configuration);
        builder.Services.AddAuthorization();
        builder.Services.AddHealthChecks().AddLifecycleCheck();
        builder.Services.TryAddSingleton(TimeProvider.System);

        // WHY: CorrelationMiddleware writes the correlation identifier to MessageContextAccessor so that it reaches the
        // messages published during the request. An API that publishes nothing (readmodels-api) does not call
        // AddShopFlowMessaging, but the middleware still needs the accessor. Registering it here (TryAdd, so no conflict
        // with messaging) makes the library provide its own dependencies instead of relying on call order.
        builder.Services.TryAddScoped<MessageContextAccessor>();
        builder.Services.TryAddScoped<IMessageContextAccessor>(provider => provider.GetRequiredService<MessageContextAccessor>());

        // AZURE: App Service and Container Apps terminate TLS in front of the application and pass the original scheme
        // and client address in X-Forwarded-*. On App Service for Linux the platform sets
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED=true; for Container Apps we do it in Bicep.
        // Note: X-Forwarded-For then contains the Front Door or APIM address, not the client's. Front Door passes the real
        // client address in the X-Azure-ClientIP header.

        return builder;
    }

    /// <summary>Middleware order matters and is defined here once for all services.</summary>
    public static WebApplication UseShopFlowWebDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();

        // After routing (so the endpoint and its metadata are known), before the endpoint executes (so the body is
        // buffered before model binding reads it).
        app.UseMiddleware<IdempotencyBufferingMiddleware>();

        app.MapShopFlowHealthChecks();
        return app;
    }
}
