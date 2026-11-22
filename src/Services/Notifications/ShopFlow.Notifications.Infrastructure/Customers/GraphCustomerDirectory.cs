using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using ShopFlow.Notifications.Application;

namespace ShopFlow.Notifications.Infrastructure.Customers;

public sealed class CustomerDirectoryOptions
{
    public const string SectionName = "CustomerDirectory";

    /// <summary>The Entra External ID tenant that holds the shop's customer accounts.</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>App registration in the External ID tenant with the User.Read.All application permission.</summary>
    [Required]
    public string ClientId { get; set; } = string.Empty;

    public Uri GraphBaseUrl { get; set; } = new("https://graph.microsoft.com/v1.0/");

    /// <summary>How long a customer's contact details are kept in process memory.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Credential for the customers tenant without any secret.
/// </summary>
/// <remarks>
/// AZURE: the function's managed identity lives in the company tenant, while customer accounts live in a separate External
/// ID tenant. A managed identity cannot get a token for a foreign tenant, and a client secret in Key Vault would have to
/// be rotated. The solution is workload identity federation:
/// <list type="number">
/// <item>The app registration in the External ID tenant has a federated credential: issuer
/// <c>https://login.microsoftonline.com/{company tenant}/v2.0</c>, subject = object ID of the managed identity,
/// audience <c>api://AzureADTokenExchange</c>.</item>
/// <item>The function gets a managed identity token for that audience and presents it as a client assertion.</item>
/// <item>The External ID tenant exchanges the assertion for a Graph token for its application.</item>
/// </list>
/// There is no secret to leak or rotate, and removing the function's managed identity cuts off access immediately.
/// </remarks>
internal static class ExternalTenantCredential
{
    private static readonly TokenRequestContext ExchangeScope = new(["api://AzureADTokenExchange/.default"]);

    public static TokenCredential Create(TokenCredential managedIdentity, CustomerDirectoryOptions options) =>
        new ClientAssertionCredential(
            options.TenantId,
            options.ClientId,
            async cancellationToken => (await managedIdentity.GetTokenAsync(ExchangeScope, cancellationToken).ConfigureAwait(false)).Token);
}

internal sealed class GraphAuthenticationHandler(TokenCredential credential) : DelegatingHandler
{
    private static readonly TokenRequestContext GraphScope = new(["https://graph.microsoft.com/.default"]);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The credential keeps the token in memory and renews it before it expires, so this is not a call to
        // Entra ID on every request.
        var token = await credential.GetTokenAsync(GraphScope, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Customer contact details from Microsoft Graph (External ID tenant). The ShopFlow customer identifier is the account's
/// <c>oid</c>, so the read is a plain <c>GET /users/{id}</c>.
/// </summary>
internal sealed class GraphCustomerDirectory(HttpClient http, HybridCache cache, IOptions<CustomerDirectoryOptions> options) : ICustomerDirectory
{
    private const string Select = "id,displayName,givenName,mail,otherMails,identities,mobilePhone,preferredLanguage";

    public async Task<CustomerContact?> FindAsync(Guid customerId, CancellationToken cancellationToken)
    {
        // WHY: HybridCache instead of IMemoryCache, because it protects against a wave of concurrent reads. Within seconds of an
        // order confirmation, OrderConfirmed and InvoiceIssued of the same customer arrive; concurrent calls for one key wait for
        // a single Graph query instead of sending several.
        // WHY: process memory only (DisableDistributedCache). Contact details are personal data and should not reach Redis,
        // even if someone adds a distributed cache here one day.
        var entry = await cache.GetOrCreateAsync(
                $"customer-contact:{customerId:D}",
                (Http: http, CustomerId: customerId),
                static async (state, token) => new CachedContact(await LoadAsync(state.Http, state.CustomerId, token).ConfigureAwait(false)),
                new HybridCacheEntryOptions
                {
                    Expiration = options.Value.CacheDuration,
                    LocalCacheExpiration = options.Value.CacheDuration,
                    Flags = HybridCacheEntryFlags.DisableDistributedCache,
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return entry.Contact;
    }

    internal static async Task<CustomerContact?> LoadAsync(HttpClient http, Guid customerId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"users/{customerId:D}?$select={Select}", cancellationToken).ConfigureAwait(false);

        // The account was deleted (e.g. on the customer's request). Not an error: the notification is skipped with a reason in the history.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        // AZURE: 401/403 is usually a deployment error (missing admin consent for User.Read.All or a misconfigured federated
        // credential). The classifier treats the exception as transient: messages wait in the queue for the configuration fix
        // instead of landing in the DLQ.
        response.EnsureSuccessStatusCode();

        var user = await response.Content.ReadFromJsonAsync<GraphUser>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException($"Microsoft Graph returned an empty user {customerId}.");

        return new CustomerContact(
            customerId,
            ResolveEmail(user),
            user.GivenName ?? user.DisplayName,
            user.MobilePhone,
            user.PreferredLanguage);
    }

    /// <remarks>
    /// AZURE: in External ID the customer's sign-in address is in the <c>identities</c> collection (type <c>emailAddress</c>),
    /// and the <c>mail</c> field is sometimes empty, depending on how the customer signed up. We check both places, and finally
    /// <c>otherMails</c>.
    /// </remarks>
    internal static string? ResolveEmail(GraphUser user) =>
        user.Mail
        ?? user.Identities?.FirstOrDefault(identity => string.Equals(identity.SignInType, "emailAddress", StringComparison.OrdinalIgnoreCase))?.IssuerAssignedId
        ?? user.OtherMails?.FirstOrDefault();

    // A wrapper so that "customer does not exist" (null) is cached too.
    // WHY: [ImmutableObject(true)] on a sealed type lets HybridCache return the same object from memory instead of
    // serializing it on write and deserializing it on every read (the default for types the caller could modify).
    [ImmutableObject(true)]
    private sealed record CachedContact(CustomerContact? Contact);
}

internal sealed record GraphUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("givenName")] string? GivenName,
    [property: JsonPropertyName("mail")] string? Mail,
    [property: JsonPropertyName("otherMails")] IReadOnlyList<string>? OtherMails,
    [property: JsonPropertyName("identities")] IReadOnlyList<GraphObjectIdentity>? Identities,
    [property: JsonPropertyName("mobilePhone")] string? MobilePhone,
    [property: JsonPropertyName("preferredLanguage")] string? PreferredLanguage);

internal sealed record GraphObjectIdentity(
    [property: JsonPropertyName("signInType")] string? SignInType,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("issuerAssignedId")] string? IssuerAssignedId);
