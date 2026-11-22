using Azure.Core;
using Azure.Identity;

namespace ShopFlow.Azure.Identity;

/// <summary>
/// Creates the single credential object in the process, used by every Azure SDK client.
/// </summary>
/// <remarks>
/// AZURE: a managed identity is an Entra ID identity assigned to a compute resource. The application
/// asks a local platform endpoint for a token (<c>IDENTITY_ENDPOINT</c> in App Service and Functions,
/// IMDS at 169.254.169.254 on virtual machines) and holds no secret that could leak. Permissions are
/// granted to this identity through RBAC on specific resources.
/// <para>
/// WHY: in Azure we use <see cref="ManagedIdentityCredential"/> directly, not
/// <see cref="DefaultAzureCredential"/>. The latter tries several sources in turn (environment
/// variables, workload identity, managed identity, Visual Studio, Azure CLI...). In production that
/// means a slower first token, unclear error messages and the risk that the process authenticates with
/// something other than we intended. Locally the same chain is convenient, so we use it there, but
/// without the sources we do not need.
/// </para>
/// </remarks>
public static class AzureCredentialFactory
{
    public static TokenCredential Create(AzureIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.UseDeveloperCredentials && !string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
        {
            return new ManagedIdentityCredential(
                new ManagedIdentityCredentialOptions(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId))
                {
                    Retry =
                    {
                        // The token endpoint is sometimes briefly unavailable right after the container starts.
                        MaxRetries = 5,
                        Delay = TimeSpan.FromMilliseconds(500),
                        Mode = RetryMode.Exponential,
                    },
                });
        }

        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeEnvironmentCredential = true,
            ExcludeWorkloadIdentityCredential = true,
            ExcludeManagedIdentityCredential = true,
            ExcludeInteractiveBrowserCredential = true,
        });
    }
}
