using Azure.Core;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;
using ShopFlow.Azure.Configuration;
using ShopFlow.Azure.Identity;

namespace ShopFlow.Azure;

public static class AzureHostBuilderExtensions
{
    /// <summary>
    /// Common Azure integration: credentials, App Configuration with Key Vault, and feature flags.
    /// Called first in every service's <c>Program.cs</c>, because later steps already read
    /// configuration from App Configuration.
    /// </summary>
    public static IHostApplicationBuilder AddShopFlowAzure(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var identity = new AzureIdentityOptions
        {
            ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"],
        };
        builder.Configuration.GetSection(AzureIdentityOptions.SectionName).Bind(identity);

        // One credential object per process: it has an internal token cache, and every new instance
        // means a separate token request.
        var credential = AzureCredentialFactory.Create(identity);
        builder.Services.TryAddSingleton(credential);

        // AZURE: Microsoft.Extensions.Azure registers SDK clients (Blob, Event Hubs, Key Vault...)
        // in DI, binds their options to configuration and gives them shared credentials. Services add
        // their own clients here through builder.Services.AddAzureClients(...).
        builder.Services.AddAzureClients(clients => clients.UseCredential(credential));

        AddAppConfiguration(builder, credential);

        builder.Services.AddFeatureManagement();

        return builder;
    }

    private static void AddAppConfiguration(IHostApplicationBuilder builder, TokenCredential credential)
    {
        var settings = new AppConfigurationOptions();
        builder.Configuration.GetSection(AppConfigurationOptions.SectionName).Bind(settings);

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            return;
        }

        builder.Configuration.AddAzureAppConfiguration(options =>
        {
            // AZURE: connection through the endpoint and a managed identity (App Configuration Data Reader
            // role). Bicep sets disableLocalAuth = true on the store, so access keys do not exist.
            options.Connect(new Uri(settings.Endpoint), credential);

            // WHY: two levels of keys. Keys without a label are defaults, keys with the environment label
            // override them (the later Select wins). The prefix is trimmed, so "Orders:Checkout:MaxLines"
            // reaches the application as "Checkout:MaxLines".
            options.Select("Shared:*", LabelFilter.Null);
            if (settings.KeyPrefix is not null)
            {
                options.Select($"{settings.KeyPrefix}*", LabelFilter.Null);
            }

            if (settings.Label is not null)
            {
                options.Select("Shared:*", settings.Label);
                if (settings.KeyPrefix is not null)
                {
                    options.Select($"{settings.KeyPrefix}*", settings.Label);
                }
            }

            if (settings.KeyPrefix is not null)
            {
                options.TrimKeyPrefix(settings.KeyPrefix);
            }

            options.TrimKeyPrefix("Shared:");

            // AZURE: a value in App Configuration can be a reference to a Key Vault secret
            // (content type application/vnd.microsoft.appconfig.keyvaultref+json). The provider then fetches the
            // secret from Key Vault with the same credentials; the Key Vault Secrets User role is required.
            // The application sees a regular configuration key and does not know the value is a secret.
            options.ConfigureKeyVault(keyVault =>
            {
                keyVault.SetCredential(credential);

                // Key Vault secret rotation without restarting the application (see the secret-rotation runbook).
                keyVault.SetSecretRefreshInterval(TimeSpan.FromHours(1));
            });

            // AZURE: sentinel-based refresh. Every RefreshInterval the provider checks a single key
            // (one cheap request). Only when the sentinel has changed does it reload everything.
            // Without a sentinel a change to several related keys could be picked up half-way.
            options.ConfigureRefresh(refresh => refresh
                .Register(settings.SentinelKey, settings.Label ?? LabelFilter.Null, refreshAll: true)
                .SetRefreshInterval(settings.RefreshInterval));

            options.UseFeatureFlags(featureFlags =>
            {
                featureFlags.Select(KeyFilter.Any, LabelFilter.Null);
                if (settings.Label is not null)
                {
                    featureFlags.Select(KeyFilter.Any, settings.Label);
                }

                featureFlags.SetRefreshInterval(settings.FeatureFlagRefreshInterval);
            });
        });

        // Registers IConfigurationRefresherProvider. In ASP.NET Core a middleware triggers the refresh
        // (UseAzureAppConfiguration); in processes without HTTP, AppConfigurationRefreshService does it.
        builder.Services.AddAzureAppConfiguration();
        builder.Services.AddHostedService<AppConfigurationRefreshService>();
    }
}
