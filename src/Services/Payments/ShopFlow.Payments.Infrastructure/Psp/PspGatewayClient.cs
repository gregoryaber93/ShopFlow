using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopFlow.Messaging.Failures;
using ShopFlow.Payments.Application.Abstractions;
using ShopFlow.Payments.Domain;
using ShopFlow.Resilience;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Payments.Infrastructure.Psp;

public sealed class PspOptions
{
    public const string SectionName = "Psp";

    /// <summary>E.g. <c>https://api.psp.example/</c> (a fictitious provider; the adapter is written as for a real one).</summary>
    [Required]
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// The provider's API key.
    /// </summary>
    /// <remarks>
    /// AZURE: one of the few secrets in the system, because the external provider does not understand Entra ID identities.
    /// The value lives in Key Vault (secret <c>psp-api-key</c>), and App Configuration has the <c>Payments:Psp:ApiKey</c> key
    /// with a reference to it. The configuration provider fetches the secret with the managed identity (Key Vault Secrets
    /// User), so there is neither the value nor a Key Vault connection string in application settings or in the repository.
    /// Reading through IOptionsMonitor makes the new value take effect after a secret rotation without a restart.
    /// </remarks>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Webhook signing secrets: the current one and (during a rotation) the previous one.
    /// </summary>
    /// <remarks>
    /// WHY: two secrets at once. After a rotation the provider keeps signing webhooks with the old secret for a while (e.g.
    /// retries from before the change). Procedure from docs/runbooks/secret-rotation.md: add the new one alongside, switch
    /// the provider, and after a day set the previous secret to the current value.
    /// </remarks>
    [MinLength(1)]
    public string[] WebhookSecrets { get; set; } = [];

    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// REST adapter of the payment provider (authorize, capture, void, refund).
/// </summary>
/// <remarks>
/// AZURE: calls leave the function through VNet integration and the NAT Gateway, so the provider always sees the same
/// public IP address. Payment providers usually require an allowlist of addresses, and without a NAT Gateway the
/// outbound address of Flex Consumption could change.
/// </remarks>
internal sealed class PspGatewayClient(HttpClient http) : IPaymentGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<AuthorizationResult> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = Post("v1/authorizations", IdempotencyKey(request.PaymentId, "authorize"), new
        {
            amount = request.Amount.ToMinorUnits(),
            currency = request.Amount.Currency,
            paymentMethodToken = request.PaymentMethodToken,
            captureMethod = "manual",
            threeDs = request.ForceThreeDSecure ? "required" : "automatic",

            // The metadata comes back in the webhook: we use it to find the payment (partition = order) without a
            // cross-partition query.
            metadata = new { orderId = request.OrderId, paymentId = request.PaymentId },
        });

        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return new AuthorizationResult(AuthorizationOutcome.Declined, null, error.Code, error.Message, null);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<AuthorizationResponse>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new PaymentGatewayException("empty_body", isPermanent: false, "PSP returned an empty authorization response.");

        return body.Status switch
        {
            "authorized" => new AuthorizationResult(AuthorizationOutcome.Authorized, body.Id, null, null, body.ExpiresAt),
            "requires_action" => new AuthorizationResult(AuthorizationOutcome.RequiresAction, body.Id, null, null, null),
            "declined" => new AuthorizationResult(AuthorizationOutcome.Declined, body.Id, body.DeclineCode ?? "declined", null, null),
            _ => throw new PaymentGatewayException("unknown_status", isPermanent: false, $"PSP returned unknown authorization status '{body.Status}'."),
        };
    }

    public Task<GatewayOperationResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken) =>
        OperationAsync($"v1/authorizations/{providerReference}/capture", IdempotencyKey(paymentId, "capture"), new { amount = amount.ToMinorUnits() }, cancellationToken);

    public Task<GatewayOperationResult> VoidAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken) =>
        OperationAsync($"v1/authorizations/{providerReference}/void", IdempotencyKey(paymentId, "void"), new { }, cancellationToken);

    public Task<GatewayOperationResult> RefundAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken) =>
        OperationAsync("v1/refunds", IdempotencyKey(paymentId, "refund"), new { authorizationId = providerReference, amount = amount.ToMinorUnits() }, cancellationToken);

    /// <summary>
    /// Idempotency key = payment + operation type.
    /// </summary>
    /// <remarks>
    /// WHY: the key is deterministic, not random per call. A retry (through Polly in this process or through message
    /// redelivery, even in another function instance) must send the same key; otherwise the provider treats it as a new
    /// operation. The operation type in the key keeps the capture and the refund of the same payment from colliding.
    /// </remarks>
    internal static string IdempotencyKey(Guid paymentId, string operation) => $"{paymentId:N}-{operation}";

    private async Task<GatewayOperationResult> OperationAsync(string path, string idempotencyKey, object body, CancellationToken cancellationToken)
    {
        using var message = Post(path, idempotencyKey, body);
        using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            return GatewayOperationResult.Failure((await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false)).Code);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<OperationResponse>(Json, cancellationToken).ConfigureAwait(false);
        return GatewayOperationResult.Success(result?.Id);
    }

    private static HttpRequestMessage Post(string path, string idempotencyKey, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };

        // Header present = the resilience pipeline may safely retry the POST (HttpResilienceExtensions.ShouldRetrySafely).
        message.Headers.Add(ShopFlowHttpHeaders.IdempotencyKey, idempotencyKey);
        return message;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);

        // 409: the same idempotency key with different parameters, i.e. a bug in our code. Retrying will not help.
        // 400/422: rejected request (e.g. a malformed payment method token): also permanent.
        // 401/403: the API key is invalid (e.g. right after a rotation). Treated as transient: messages wait in the queue,
        // and the growing-queue alert brings in a human (docs/runbooks/payment-failure.md).
        var permanent = response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;
        throw new PaymentGatewayException(error.Code, permanent, $"PSP returned {(int)response.StatusCode}: {error.Code} {error.Message}");
    }

    private static async Task<PspError> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(Json, cancellationToken).ConfigureAwait(false);
            return envelope?.Error ?? new PspError($"http_{(int)response.StatusCode}", null);
        }
        catch (JsonException)
        {
            return new PspError($"http_{(int)response.StatusCode}", null);
        }
    }

    private sealed record AuthorizationResponse(string Id, string Status, string? DeclineCode, DateTimeOffset? ExpiresAt);

    private sealed record OperationResponse(string Id);

    private sealed record ErrorEnvelope(PspError? Error);

    private sealed record PspError(string Code, string? Message);
}

/// <summary>Attaches the current API key on every attempt (after a Key Vault rotation without restarting the process).</summary>
internal sealed class PspAuthenticationHandler(IOptionsMonitor<PspOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CurrentValue.ApiKey);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>What payment provider errors mean for the "retry or dead-letter" decision.</summary>
internal sealed class PaymentGatewayFailureClassifier : IFailureClassifier
{
    public bool TryClassify(Exception exception, out FailureClassification classification)
    {
        if (exception is not PaymentGatewayException gateway)
        {
            classification = default;
            return false;
        }

        classification = gateway.IsPermanent
            ? FailureClassification.Permanent($"Psp.{gateway.Code}", gateway.Message)
            : FailureClassification.Transient($"Psp.{gateway.Code}", gateway.Message);
        return true;
    }
}
