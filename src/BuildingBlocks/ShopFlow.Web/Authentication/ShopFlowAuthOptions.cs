namespace ShopFlow.Web.Authentication;

/// <summary>Configuration of token issuers accepted by the API.</summary>
public sealed class ShopFlowAuthOptions
{
    public const string SectionName = "Authentication";

    /// <summary>Shop customers: the Microsoft Entra External ID tenant. No section = the API does not serve customers.</summary>
    public CustomerIssuerOptions? Customers { get; set; }

    /// <summary>Back-office employees and services (managed identities): the workforce tenant.</summary>
    public WorkforceIssuerOptions Workforce { get; set; } = new();
}

public sealed class CustomerIssuerOptions
{
    /// <summary>
    /// E.g. <c>https://shopflowcustomers.ciamlogin.com/&lt;tenant-id&gt;/v2.0</c>.
    /// </summary>
    /// <remarks>
    /// AZURE: Entra External ID is a separate tenant meant for external customers (CIAM): self-service sign-up,
    /// sign-in with Google/Apple, custom branding of sign-in pages. It has its own <c>ciamlogin.com</c> domain, and
    /// from the API's point of view it is just another OpenID Connect token issuer. The middleware fetches signing
    /// keys from <c>{Authority}/.well-known/openid-configuration</c> and refreshes them automatically on rotation.
    /// </remarks>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Client ID of the API registration in the customers tenant.</summary>
    public string Audience { get; set; } = string.Empty;
}

public sealed class WorkforceIssuerOptions
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Accepted audiences: the App ID URI (<c>api://shopflow-orders</c>) and the client ID of the API registration.
    /// </summary>
    /// <remarks>
    /// AZURE: a v1 token has the App ID URI in <c>aud</c>, a v2 token has the client ID. Which one we get depends on
    /// the <c>requestedAccessTokenVersion</c> setting in the API registration manifest, not on the caller.
    /// We accept both so that changing the manifest is not a breaking change.
    /// </remarks>
    public IReadOnlyList<string> Audiences { get; set; } = [];
}
