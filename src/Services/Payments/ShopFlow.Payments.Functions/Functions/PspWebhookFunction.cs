using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Payments.Application.Webhooks;
using ShopFlow.Payments.Infrastructure.Psp;

namespace ShopFlow.Payments.Functions.Functions;

/// <summary>
/// Payment provider webhook (3-D Secure result).
/// </summary>
/// <remarks>
/// AZURE: the call comes from the internet through Front Door and API Management (the "psp-webhooks" API: an ip-filter
/// policy with the provider's addresses, a rate limit, the raw body forwarded without transformation) and reaches the
/// function through its private endpoint. Authentication is an HMAC signature of the body, because the provider does
/// not issue Entra ID tokens; hence AuthorizationLevel.Anonymous and no Easy Auth.
/// <para>
/// The status codes are the protocol with the provider: 2xx = accepted, do not retry; 5xx = retry later; 401 = bad
/// signature (the provider reports it in its panel). The answer must come quickly (providers usually wait a few
/// seconds), so the function only records the result; the saga does the rest after the event from the outbox.
/// </para>
/// </remarks>
public sealed partial class PspWebhookFunction(
    PspWebhookVerifier verifier,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PspWebhookFunction> logger)
{
    [Function("psp-webhook")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhooks/psp")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var body = buffer.ToArray();

        if (!verifier.IsValid(request.Headers[PspWebhookVerifier.SignatureHeader], body))
        {
            LogInvalidSignature(request.HttpContext.Connection.RemoteIpAddress?.ToString());
            return new UnauthorizedResult();
        }

        var webhook = PspWebhookParser.Parse(body);
        if (webhook is null)
        {
            // The signature is valid, but the body does not match our model (e.g. a new version of the provider's API).
            // 400 and a log entry; retrying changes nothing.
            return new BadRequestResult();
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var now = timeProvider.GetUtcNow();
            scope.ServiceProvider.GetRequiredService<MessageContextAccessor>().SetIncoming(new MessageContext
            {
                // We treat the webhook as an incoming message: the same inbox removes the provider's repeats.
                MessageId = DeterministicGuid.Create($"psp-webhook/{webhook.EventId}"),
                MessageType = webhook.Type,
                ContractVersion = 1,
                CorrelationId = webhook.OrderId.ToString("D"),
                Source = "psp",
                OccurredAt = now,
                Consumer = "psp-webhook",
                DeliveryCount = 1,
                EnqueuedAt = now,
            });

            try
            {
                var outcome = await scope.ServiceProvider.GetRequiredService<PspWebhookProcessor>().ProcessAsync(webhook, cancellationToken);
                return outcome == WebhookOutcome.RetryLater ? new StatusCodeResult(StatusCodes.Status503ServiceUnavailable) : new OkResult();
            }
            catch (DuplicateMessageException)
            {
                return new OkResult();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PSP webhook with an invalid signature rejected (remote address {RemoteAddress})")]
    private partial void LogInvalidSignature(string? remoteAddress);
}
