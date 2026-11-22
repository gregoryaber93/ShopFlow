using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopFlow.Functions;

/// <summary>
/// Identity passed by the built-in App Service / Functions authentication ("Easy Auth") in the
/// <c>X-MS-CLIENT-PRINCIPAL</c> header (Base64-encoded JSON).
/// </summary>
/// <remarks>
/// AZURE: Easy Auth runs before application code: it validates the Entra ID token (issuer, audience, signature,
/// lifetime) and rejects requests without a valid token before the call reaches the function. The application
/// receives ready-made claims in a header that the platform always overwrites, so a client cannot forge it.
/// The role check stays in code, because Easy Auth only enforces "is signed in".
/// </remarks>
public sealed class EasyAuthPrincipal
{
    public const string HeaderName = "X-MS-CLIENT-PRINCIPAL";

    private EasyAuthPrincipal(IReadOnlyList<ClientClaim> claims, string nameType, string roleType)
    {
        Claims = claims;
        NameType = nameType;
        RoleType = roleType;
    }

    public IReadOnlyList<ClientClaim> Claims { get; }

    private string NameType { get; }

    private string RoleType { get; }

    public string? Name => Claims.FirstOrDefault(claim => claim.Type == NameType)?.Value;

    public bool IsInRole(string role) => Claims.Any(claim => claim.Type == RoleType && claim.Value == role);

    /// <returns>The identity, or <c>null</c> when the header is missing or malformed.</returns>
    public static EasyAuthPrincipal? Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(Convert.FromBase64String(headerValue)));
            return payload?.Claims is null
                ? null
                : new EasyAuthPrincipal(payload.Claims, payload.NameType ?? "name", payload.RoleType ?? "roles");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    public sealed record ClientClaim(
        [property: JsonPropertyName("typ")] string Type,
        [property: JsonPropertyName("val")] string Value);

    private sealed record Payload(
        [property: JsonPropertyName("claims")] IReadOnlyList<ClientClaim>? Claims,
        [property: JsonPropertyName("name_typ")] string? NameType,
        [property: JsonPropertyName("role_typ")] string? RoleType);
}
