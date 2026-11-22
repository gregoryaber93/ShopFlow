using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ShopFlow.Web.Authentication;

public static class AuthenticationSchemes
{
    /// <summary>Selector scheme: picks one of the two schemes below based on the token issuer.</summary>
    public const string Bearer = "Bearer";

    public const string Customers = "Customers";

    public const string Workforce = "Workforce";
}

/// <summary>
/// Authentication with tokens from two Entra ID tenants.
/// </summary>
/// <remarks>
/// WHY: plain JwtBearer instead of Microsoft.Identity.Web. Identity.Web shortens the setup to a few lines, but it
/// hides exactly what this repository is meant to teach: which issuers are accepted, which token versions, where
/// roles come from. Here every validation is spelled out explicitly.
/// <para>
/// API Management already validates the JWT at the edge (validate-jwt policy), but the service validates it again.
/// This is defence in depth: the service is reachable from the internal network bypassing APIM (e.g. by the saga),
/// and a policy misconfiguration must not open the API to everyone.
/// </para>
/// </remarks>
public static class AuthenticationExtensions
{
    public static IServiceCollection AddShopFlowAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ShopFlowAuthOptions();
        configuration.GetSection(ShopFlowAuthOptions.SectionName).Bind(options);
        var workforceTenant = options.Workforce.TenantId;

        var authentication = services
            .AddAuthentication(AuthenticationSchemes.Bearer)
            .AddPolicyScheme(AuthenticationSchemes.Bearer, "Customer or workforce token", policy =>
            {
                // Scheme selection by issuer. The token is only read here, not validated:
                // signature, lifetime and audience validation is done by the selected scheme.
                policy.ForwardDefaultSelector = context =>
                {
                    var header = context.Request.Headers.Authorization.ToString();
                    if (options.Customers is null
                        || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        return AuthenticationSchemes.Workforce;
                    }

                    var handler = new JsonWebTokenHandler();
                    var raw = header["Bearer ".Length..].Trim();
                    if (!handler.CanReadToken(raw))
                    {
                        return AuthenticationSchemes.Workforce;
                    }

                    var issuer = handler.ReadJsonWebToken(raw).Issuer;
                    return issuer.Contains(".ciamlogin.com", StringComparison.OrdinalIgnoreCase)
                        ? AuthenticationSchemes.Customers
                        : AuthenticationSchemes.Workforce;
                };
            })
            .AddJwtBearer(AuthenticationSchemes.Workforce, jwt =>
            {
                jwt.Authority = $"https://login.microsoftonline.com/{workforceTenant}/v2.0";
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidAudiences = options.Workforce.Audiences,

                    // AZURE: managed identities get v1 tokens by default (issuer sts.windows.net), while users signing in
                    // through MSAL get v2 tokens (login.microsoftonline.com/.../v2.0). Both come from the same tenant and are
                    // signed with the same keys.
                    ValidIssuers =
                    [
                        $"https://login.microsoftonline.com/{workforceTenant}/v2.0",
                        $"https://sts.windows.net/{workforceTenant}/",
                    ],
                    RoleClaimType = "roles",
                    NameClaimType = "name",
                };
            });

        if (options.Customers is { } customers)
        {
            authentication.AddJwtBearer(AuthenticationSchemes.Customers, jwt =>
            {
                jwt.Authority = customers.Authority;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidAudience = customers.Audience,
                    NameClaimType = "name",
                };
            });
        }

        return services;
    }

    /// <summary>
    /// Requires an application token (a service, not a user) with the given app role.
    /// </summary>
    /// <remarks>
    /// AZURE: a token issued to an application (client credentials, managed identity) carries roles in the
    /// <c>roles</c> claim and has no <c>scp</c> claim. A user token has <c>scp</c> (delegated permissions), and the
    /// user's app roles also go into <c>roles</c>. Without checking that <c>scp</c> is absent, a back-office operator
    /// mistakenly assigned the <c>Orders.Internal.Write</c> role could call endpoints meant only for the saga.
    /// </remarks>
    public static AuthorizationPolicyBuilder RequireApplicationRole(this AuthorizationPolicyBuilder policy, string role)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy
            .AddAuthenticationSchemes(AuthenticationSchemes.Workforce)
            .RequireAuthenticatedUser()
            .RequireRole(role)
            .RequireAssertion(context => !context.User.HasClaim(claim => claim.Type == "scp"));
    }

    /// <summary>
    /// Requires an employee token (a user from the workforce tenant) with the given app role.
    /// </summary>
    /// <remarks>
    /// AZURE: users' app roles (e.g. Catalog.Manager) are assigned in Entra ID to employee groups under
    /// "Enterprise applications". A user token has roles in <c>roles</c> and delegated permissions in <c>scp</c>.
    /// Requiring <c>scp</c> distinguishes it from an application token (see <see cref="RequireApplicationRole"/>).
    /// </remarks>
    public static AuthorizationPolicyBuilder RequireWorkforceUserRole(this AuthorizationPolicyBuilder policy, string role)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy
            .AddAuthenticationSchemes(AuthenticationSchemes.Workforce)
            .RequireAuthenticatedUser()
            .RequireRole(role)
            .RequireClaim("scp");
    }

    /// <summary>Requires a shop customer token (from the External ID tenant).</summary>
    public static AuthorizationPolicyBuilder RequireCustomer(this AuthorizationPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy
            .AddAuthenticationSchemes(AuthenticationSchemes.Customers)
            .RequireAuthenticatedUser()
            .RequireClaim("oid");
    }

    /// <summary>
    /// Stable subject identifier.
    /// </summary>
    /// <remarks>
    /// AZURE: <c>oid</c> is the object identifier in the tenant, the same across all applications. <c>sub</c> is
    /// unique per (user, application) pair, so two of our APIs would see the same customer under different
    /// identifiers. That is why <c>oid</c> is the customer identifier in ShopFlow.
    /// </remarks>
    public static string? GetObjectId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirst("oid")?.Value;
    }
}
