using ShopFlow.Contracts.Common;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Payments.Application.Webhooks;
using ShopFlow.Payments.Domain;

namespace ShopFlow.Payments.UnitTests;

public sealed class PaymentHandlerTests : IDisposable
{
    private static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000e1");
    private static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000e2");
    private static readonly Guid PaymentId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000e3");
    private static readonly MoneyDto Amount = new() { Amount = 39.98m, Currency = "PLN" };

    private readonly PaymentsTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Successful_authorization_is_announced_to_the_saga()
    {
        await _host.DispatchAsync(Authorize());

        _host.Published<PaymentAuthorized>().ShouldHaveSingleItem().ProviderReference.ShouldBe("psp-auth-1");
        _host.Gateway.Calls.ShouldBe(["authorize"]);
    }

    [Fact]
    public async Task Declined_card_is_answered_with_the_decline_code()
    {
        _host.Gateway.Authorizations.Enqueue(new AuthorizationResult(AuthorizationOutcome.Declined, null, "insufficient_funds", "Not enough money", null));

        await _host.DispatchAsync(Authorize());

        _host.Published<PaymentDeclined>().ShouldHaveSingleItem().DeclineCode.ShouldBe("insufficient_funds");
    }

    [Fact]
    public async Task Three_d_secure_waits_for_the_webhook_before_answering()
    {
        _host.Gateway.Authorizations.Enqueue(new AuthorizationResult(AuthorizationOutcome.RequiresAction, "psp-auth-3ds", null, null, null));

        await _host.DispatchAsync(Authorize());
        _host.Store.Published.ShouldBeEmpty();

        var outcome = await Webhook("evt-1", "authorization.succeeded");

        outcome.ShouldBe(WebhookOutcome.Processed);
        _host.Published<PaymentAuthorized>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Webhook_delivered_twice_is_processed_once()
    {
        _host.Gateway.Authorizations.Enqueue(new AuthorizationResult(AuthorizationOutcome.RequiresAction, "psp-auth-3ds", null, null, null));
        await _host.DispatchAsync(Authorize());
        await Webhook("evt-1", "authorization.succeeded");

        await Should.ThrowAsync<DuplicateMessageException>(() => Webhook("evt-1", "authorization.succeeded"));
        _host.Published<PaymentAuthorized>().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Repeated_authorize_command_answers_again_without_charging_again()
    {
        await _host.DispatchAsync(Authorize());
        await _host.DispatchAsync(Authorize(), Guid.NewGuid());

        _host.Gateway.Calls.ShouldBe(["authorize"]);
        _host.Published<PaymentAuthorized>().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Capture_after_authorization_is_announced()
    {
        await _host.DispatchAsync(Authorize());

        await _host.DispatchAsync(Capture());

        _host.Published<PaymentCaptured>().ShouldHaveSingleItem();
        _host.Gateway.Calls.ShouldBe(["authorize", "capture"]);
    }

    [Fact]
    public async Task Expired_authorization_fails_capture_without_calling_the_provider()
    {
        await _host.DispatchAsync(Authorize());
        _host.Time.Advance(TimeSpan.FromDays(8));

        await _host.DispatchAsync(Capture());

        _host.Published<PaymentCaptureFailed>().ShouldHaveSingleItem().ReasonCode.ShouldBe("authorization_expired");
        _host.Gateway.Calls.ShouldBe(["authorize"]);
    }

    [Fact]
    public async Task Void_of_a_captured_payment_refunds_and_still_answers_payment_voided()
    {
        await _host.DispatchAsync(Authorize());
        await _host.DispatchAsync(Capture());

        await _host.DispatchAsync(new VoidPayment { OrderId = OrderId, CustomerId = CustomerId, PaymentId = PaymentId, Reason = "shipment_failed" });

        _host.Gateway.Calls[^1].ShouldBe("refund");
        _host.Published<PaymentVoided>().ShouldHaveSingleItem();
        _host.Published<PaymentRefunded>().ShouldHaveSingleItem().Amount.Amount.ShouldBe(39.98m);
    }

    [Fact]
    public async Task Refund_of_an_uncaptured_payment_releases_the_hold_and_reports_zero()
    {
        await _host.DispatchAsync(Authorize());

        await _host.DispatchAsync(new RefundPayment { OrderId = OrderId, CustomerId = CustomerId, PaymentId = PaymentId, Amount = Amount, Reason = "capture_timeout" });

        _host.Gateway.Calls[^1].ShouldBe("void");
        _host.Published<PaymentRefunded>().ShouldHaveSingleItem().Amount.Amount.ShouldBe(0m);
    }

    [Fact]
    public async Task Void_before_authorization_leaves_a_tombstone_that_refuses_a_late_authorization()
    {
        await _host.DispatchAsync(new VoidPayment { OrderId = OrderId, CustomerId = CustomerId, PaymentId = PaymentId, Reason = "stock_timeout" });
        await _host.DispatchAsync(Authorize());

        _host.Gateway.Calls.ShouldBeEmpty();
        _host.Published<PaymentDeclined>().ShouldHaveSingleItem().DeclineCode.ShouldBe("payment_voided");
    }

    [Fact]
    public async Task Provider_refusing_a_refund_needs_a_human()
    {
        await _host.DispatchAsync(Authorize());
        await _host.DispatchAsync(Capture());
        _host.Gateway.NextOperation = Application.Abstractions.GatewayOperationResult.Failure("dispute_open");

        var exception = await Should.ThrowAsync<PermanentMessageException>(() =>
            _host.DispatchAsync(new RefundPayment { OrderId = OrderId, CustomerId = CustomerId, PaymentId = PaymentId, Amount = Amount, Reason = "x" }));

        exception.Message.ShouldContain("dispute_open");
    }

    private static AuthorizePayment Authorize() => new()
    {
        OrderId = OrderId,
        CustomerId = CustomerId,
        Amount = Amount,
        PaymentMethodToken = "pm_tok",
        PaymentId = PaymentId,
    };

    private static CapturePayment Capture() => new() { OrderId = OrderId, CustomerId = CustomerId, PaymentId = PaymentId, Amount = Amount };

    private Task<WebhookOutcome> Webhook(string eventId, string type) =>
        _host.AsIncomingAsync<PspWebhookProcessor, WebhookOutcome>(
            ShopFlow.Messaging.DeterministicGuid.Create($"psp-webhook/{eventId}"),
            "psp-webhook",
            processor => processor.ProcessAsync(
                new PspWebhookEvent(eventId, type, OrderId, PaymentId, "psp-auth-3ds", null, _host.Time.GetUtcNow().AddDays(7)),
                CancellationToken.None));
}
