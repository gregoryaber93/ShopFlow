using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopFlow.Web.Authentication;

namespace ShopFlow.Testing;

/// <summary>
/// Test authentication described by the <c>X-Test-Principal</c> header:
/// <list type="bullet">
/// <item><c>customer:{oid}</c>: a shop customer (External ID tenant),</item>
/// <item><c>app:{role1,role2}</c>: another service's managed identity (application token, no scp),</item>
/// <item><c>user:{role1,role2}</c>: a back-office employee (user token with scp).</item>
/// </list>
/// </summary>
/// <remarks>
/// WHY: the test handler replaces JwtBearer under the same scheme names. The policies from <c>AuthenticationExtensions</c>
/// (RequireCustomer, RequireApplicationRole, RequireWorkforceUserRole) work unchanged, so the test checks real
/// authorization and only the identity source is replaced.
/// </remarks>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Header = "X-Test-Principal";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Headers[Header].ToString();
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var kind = value[..separator];
        var data = value[(separator + 1)..];
        var roles = data.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(role => new Claim("roles", role));

        Claim[]? claims = (Scheme.Name, kind) switch
        {
            (AuthenticationSchemes.Customers, "customer") => [new Claim("oid", data), new Claim("scp", "Orders.ReadWrite")],
            (AuthenticationSchemes.Workforce, "app") => [new Claim("oid", Guid.NewGuid().ToString()), .. roles],
            (AuthenticationSchemes.Workforce, "user") => [new Claim("oid", Guid.NewGuid().ToString()), new Claim("scp", "Backoffice"), .. roles],
            _ => null,
        };

        if (claims is null)
        {
            // A token from the wrong tenant: this scheme does not recognise it (like JwtBearer with a different issuer).
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name, "oid", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

public static class TestAuthenticationExtensions
{
    /// <summary>Replaces the Customers and Workforce scheme handlers (registered by the API) with <see cref="TestAuthHandler"/>.</summary>
    public static IServiceCollection UseTestAuthentication(this IServiceCollection services) =>
        services.Configure<AuthenticationOptions>(options =>
        {
            foreach (var scheme in new[] { AuthenticationSchemes.Customers, AuthenticationSchemes.Workforce })
            {
                if (options.SchemeMap.TryGetValue(scheme, out var builder))
                {
                    builder.HandlerType = typeof(TestAuthHandler);
                }
            }
        });

    public static HttpRequestMessage AsCustomer(this HttpRequestMessage request, Guid customerId) => With(request, $"customer:{customerId}");

    public static HttpRequestMessage AsApp(this HttpRequestMessage request, params string[] roles) => With(request, $"app:{string.Join(',', roles)}");

    public static HttpRequestMessage AsUser(this HttpRequestMessage request, params string[] roles) => With(request, $"user:{string.Join(',', roles)}");

    private static HttpRequestMessage With(HttpRequestMessage request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Add(TestAuthHandler.Header, principal);
        return request;
    }
}
