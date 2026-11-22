using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ShopFlow.Web.Authentication;

namespace ShopFlow.Orders.Api.Http;

public static class OrdersPolicies
{
    public const string Customer = "orders.customer";

    public const string InternalWrite = "orders.internal.write";

    /// <summary>
    /// App role defined in the orders-api Entra ID app registration and assigned to the order-saga managed
    /// identity (infra/scripts/bootstrap-entra-apps.sh).
    /// </summary>
    public const string InternalWriteRole = "Orders.Internal.Write";

    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(Customer, policy => policy.RequireCustomer());
        options.AddPolicy(InternalWrite, policy => policy.RequireApplicationRole(InternalWriteRole));
    }

    /// <summary>ShopFlow customer identifier = oid from the External ID token (the policy guarantees it is present).</summary>
    public static Guid GetCustomerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.GetObjectId(), out var id)
            ? id
            : throw new InvalidOperationException("The authenticated principal has no valid 'oid' claim.");
}
