namespace ShopFlow.Azure.Identity;

public sealed class AzureIdentityOptions
{
    public const string SectionName = "Azure";

    /// <summary>
    /// Client ID of the user-assigned managed identity attached to the compute resource.
    /// </summary>
    /// <remarks>
    /// AZURE: one App Service, Container App or Function App can have several user-assigned identities
    /// (plus one system-assigned). Without saying which one to use, the token endpoint returns an error or
    /// a token for the wrong identity. Bicep sets the value as the <c>AZURE_CLIENT_ID</c> variable and we read
    /// that one, because Azure SDK tooling outside our code understands the same variable.
    /// </remarks>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>
    /// Forces developer credentials (Azure CLI, Visual Studio) even when <see cref="ManagedIdentityClientId"/>
    /// is set. Useful when running locally with configuration taken from dev.
    /// </summary>
    public bool UseDeveloperCredentials { get; set; }
}
