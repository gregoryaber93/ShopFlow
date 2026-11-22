using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using ShopFlow.Contracts;
using ShopFlow.Functions;
using ShopFlow.Messaging;
using ShopFlow.Notifications.Infrastructure.Cosmos;

namespace ShopFlow.Notifications.Functions;

public sealed class NotificationsInboxFunction(ServiceBusTriggerAdapter adapter)
{
    /// <remarks>
    /// WHY: DeadLetter for types without a handler, even though this is an event consumer. The queue only gets the types
    /// selected by subscription filters, so a foreign type means someone widened a filter before the handler was deployed (or
    /// a new contract version arrived). In the DLQ the message waits for the deployment and a replay; with Ignore the customer
    /// would get no e-mail.
    /// </remarks>
    private static readonly ConsumerEndpoint Endpoint = new(MessagingTopology.Consumers.Notifications, UnhandledMessagePolicy.DeadLetter);

    /// <remarks>
    /// AZURE: a queue without sessions, fed by auto-forwarding from the <c>notifications</c> subscription on four topics.
    /// Order does not matter (every notification is independent), so messages can be processed in parallel on many instances.
    /// One queue instead of four subscriptions read directly: one function, one Data Receiver role, one DLQ to monitor.
    /// </remarks>
    [Function("notifications-inbox")]
    public Task Run(
        [ServiceBusTrigger(MessagingTopology.Queues.NotificationsInbox, Connection = "ServiceBus", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken) =>
        adapter.ProcessAsync(message, actions, Endpoint, cancellationToken);
}

/// <summary>A customer's notification history for customer support ("did you send me the invoice?").</summary>
public sealed class NotificationHistoryFunction(NotificationHistoryReader history)
{
    public const string SupportRole = "Support.Reader";

    /// <remarks>
    /// AZURE: Easy Auth (App Service Authentication) performs authorization before the function code. Only a request with a
    /// valid Entra ID token reaches the function, and the X-MS-CLIENT-PRINCIPAL header carries the app roles assigned to the
    /// employee. Entries show the masked address and the skip reason, never the message content.
    /// </remarks>
    [Function("notification-history")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{customerId:guid}/notifications")] HttpRequest request,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (EasyAuthPrincipal.Parse(request.Headers[EasyAuthPrincipal.HeaderName])?.IsInRole(SupportRole) != true)
        {
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        var top = int.TryParse(request.Query["top"], out var requested) ? requested : 50;
        var items = await history.GetForCustomerAsync(customerId, top, cancellationToken);
        return new OkObjectResult(items);
    }
}
