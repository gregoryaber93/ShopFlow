using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Payments.Application.Abstractions;
using ShopFlow.Payments.Domain;
using ShopFlow.Payments.Infrastructure.Psp;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Payments.UnitTests;

public sealed class PspWebhookVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = """{"id":"evt_1","type":"authorization.succeeded"}"""u8.ToArray();

    [Fact]
    public void Correctly_signed_webhook_is_accepted()
    {
        Verifier().IsValid(Sign(Body, "current-secret", Now), Body).ShouldBeTrue();
    }

    [Fact]
    public void Changing_a_single_byte_of_the_body_invalidates_the_signature()
    {
        var tampered = Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"authorization.succeedeD"}""");

        Verifier().IsValid(Sign(Body, "current-secret", Now), tampered).ShouldBeFalse();
    }

    [Fact]
    public void Captured_request_cannot_be_replayed_after_the_tolerance_window()
    {
        Verifier().IsValid(Sign(Body, "current-secret", Now.AddMinutes(-6)), Body).ShouldBeFalse();
    }

    [Fact]
    public void Previous_secret_is_still_accepted_during_rotation()
    {
        Verifier().IsValid(Sign(Body, "previous-secret", Now), Body).ShouldBeTrue();
        Verifier().IsValid(Sign(Body, "someone-elses-secret", Now), Body).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("t=abc,v1=00")]
    [InlineData("v1=deadbeef")]
    public void Malformed_signature_header_is_rejected(string? header)
    {
        Verifier().IsValid(header, Body).ShouldBeFalse();
    }

    [Fact]
    public void Webhook_metadata_identifies_the_payment_partition()
    {
        var body = """
            {"id":"evt_9","type":"authorization.failed","data":{"authorization_id":"auth_1","decline_code":"authentication_failed",
             "metadata":{"order_id":"0193a1b2-0000-7000-8000-0000000000e1","payment_id":"0193a1b2-0000-7000-8000-0000000000e3"}}}
            """u8;

        var webhook = PspWebhookParser.Parse(body)!;

        webhook.OrderId.ShouldBe(Guid.Parse("0193a1b2-0000-7000-8000-0000000000e1"));
        webhook.DeclineCode.ShouldBe("authentication_failed");
    }

    private static PspWebhookVerifier Verifier() => new(
        new StaticOptionsMonitor(new PspOptions { BaseUrl = new Uri("https://psp.test/"), ApiKey = "k", WebhookSecrets = ["current-secret", "previous-secret"] }),
        new FakeTimeProvider(Now));

    private static string Sign(byte[] body, string secret, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.").Concat(body).ToArray();
        return $"t={timestamp},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed))}";
    }
}

public sealed class PspGatewayClientTests
{
    private static readonly Guid PaymentId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000e3");

    [Fact]
    public async Task Authorization_is_sent_in_minor_units_with_idempotency_key_and_current_api_key()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, """{"id":"auth_1","status":"authorized","expires_at":"2026-11-28T12:00:00Z"}""");

        var result = await Client(handler).AuthorizeAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(AuthorizationOutcome.Authorized);
        result.ProviderReference.ShouldBe("auth_1");
        handler.IdempotencyKey.ShouldBe($"{PaymentId:N}-authorize");
        handler.Authorization.ShouldBe("Bearer api-key-v2");

        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("amount").GetInt64().ShouldBe(3998);
        body.RootElement.GetProperty("capture_method").GetString().ShouldBe("manual");
    }

    [Fact]
    public async Task Payment_required_is_a_business_decline_not_an_exception()
    {
        var handler = new RecordingHandler(HttpStatusCode.PaymentRequired, """{"error":{"code":"card_declined","message":"Do not honor"}}""");

        var result = await Client(handler).AuthorizeAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(AuthorizationOutcome.Declined);
        result.DeclineCode.ShouldBe("card_declined");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task Technical_errors_are_classified_for_retry_or_dlq(HttpStatusCode status, bool permanent)
    {
        var handler = new RecordingHandler(status, """{"error":{"code":"some_error"}}""");

        var exception = await Should.ThrowAsync<PaymentGatewayException>(() => Client(handler).AuthorizeAsync(Request(), TestContext.Current.CancellationToken));

        exception.IsPermanent.ShouldBe(permanent);
    }

    private static AuthorizationRequest Request() => new(PaymentId, Guid.NewGuid(), new Money(39.98m, "PLN"), "pm_tok", ForceThreeDSecure: false);

    private static PspGatewayClient Client(RecordingHandler handler)
    {
        var options = new StaticOptionsMonitor(new PspOptions { BaseUrl = new Uri("https://psp.test/"), ApiKey = "api-key-v2", WebhookSecrets = ["s"] });
        var pipeline = new PspAuthenticationHandler(options) { InnerHandler = handler };
        return new PspGatewayClient(new HttpClient(pipeline) { BaseAddress = new Uri("https://psp.test/") });
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? IdempotencyKey { get; private set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}

internal sealed class StaticOptionsMonitor(PspOptions value) : IOptionsMonitor<PspOptions>
{
    public PspOptions CurrentValue => value;

    public PspOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<PspOptions, string?> listener) => null;
}
