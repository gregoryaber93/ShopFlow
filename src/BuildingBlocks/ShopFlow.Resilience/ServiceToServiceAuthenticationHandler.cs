using System.Net.Http.Headers;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ShopFlow.Resilience;

/// <summary>
/// Attaches an Entra ID token issued for the calling service's managed identity to the request.
/// </summary>
/// <remarks>
/// AZURE: the target service has an Entra ID app registration with an identifier (App ID URI), e.g.
/// <c>api://shopflow-orders</c>, and defined app roles (<c>Orders.Internal.Write</c>). The caller's managed
/// identity gets an assignment of that role (app role assignment), and a token for the
/// <c>api://shopflow-orders/.default</c> scope contains it in the <c>roles</c> claim. The target service
/// checks the issuer, the audience and the role. There is no client secret here: the managed identity
/// itself is the credential.
/// <para>
/// App role assignments are not made through Azure RBAC (they are Microsoft Graph permissions), so they are
/// created by the <c>infra/scripts/bootstrap-entra-apps.sh</c> script, not by the RBAC Bicep modules.
/// </para>
/// </remarks>
public sealed class ServiceToServiceAuthenticationHandler(TokenCredential credential, string scope) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The credential has an internal cache and refreshes the token before it expires, so calling it
        // for every request does not mean a call to Entra ID.
        var token = await credential
            .GetTokenAsync(new TokenRequestContext([scope]), cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public static class ServiceToServiceHttpClientBuilderExtensions
{
    /// <param name="builder">HTTP client.</param>
    /// <param name="scope">Scope of the target API, e.g. <c>api://shopflow-orders/.default</c>.</param>
    /// <remarks>
    /// WHY: call after <c>Add...Resilience()</c>. A handler added later sits closer to the network, i.e. it runs
    /// on every attempt rather than once before all of them. When retries take longer than the token lifetime,
    /// each attempt gets a current token.
    /// </remarks>
    public static IHttpClientBuilder AddServiceToServiceAuthentication(this IHttpClientBuilder builder, string scope)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        return builder.AddHttpMessageHandler(provider =>
            new ServiceToServiceAuthenticationHandler(provider.GetRequiredService<TokenCredential>(), scope));
    }
}
