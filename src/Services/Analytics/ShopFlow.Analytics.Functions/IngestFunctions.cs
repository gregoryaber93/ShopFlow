using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ShopFlow.Analytics.Application;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Analytics.V1;
using ShopFlow.Functions;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.ServiceBus;

namespace ShopFlow.Analytics.Functions;

/// <summary>Ingesting the clickstream from the browser (through APIM) into the <c>clickstream</c> hub.</summary>
public sealed class ClickstreamIngestFunction(ClickstreamIngestor ingestor)
{
    /// <summary>App role of the APIM identity. Only the gateway may call the function.</summary>
    public const string IngestRole = "Analytics.Ingest";

    /// <summary>Customer identifier that APIM read from the token, if the visitor is signed in.</summary>
    public const string CustomerHeader = "X-ShopFlow-Customer-Id";

    private const long MaxBodyBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <remarks>
    /// AZURE: the browser calls APIM (a public endpoint behind Front Door), not the function. The APIM policy:
    /// <list type="number">
    /// <item>removes the <c>X-ShopFlow-Customer-Id</c> header sent by the client (otherwise anyone could impersonate),</item>
    /// <item>if there is an Authorization header, validates the External ID token and writes its <c>oid</c> into that header,</item>
    /// <item>limits the number of requests per IP address and the body size,</item>
    /// <item>forwards the request with its own managed identity token (Easy Auth lets in only that identity).</item>
    /// </list>
    /// </remarks>
    [Function("clickstream-ingest")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "clickstream")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (EasyAuthPrincipal.Parse(request.Headers[EasyAuthPrincipal.HeaderName])?.IsInRole(IngestRole) != true)
        {
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        if (request.ContentLength is not { } length || length > MaxBodyBytes)
        {
            return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
        }

        ClickstreamBatchRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ClickstreamBatchRequest>(request.Body, Json, cancellationToken);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new { error = "invalid_json" });
        }

        if (body is null)
        {
            return new BadRequestObjectResult(new { error = "empty_body" });
        }

        Guid? customerId = Guid.TryParse(request.Headers[CustomerHeader], out var parsed) ? parsed : null;

        try
        {
            var result = await ingestor.IngestAsync(body, customerId, cancellationToken);

            // 202: the events are stored in Event Hubs but not processed yet.
            return new ObjectResult(result) { StatusCode = StatusCodes.Status202Accepted };
        }
        catch (ArgumentException exception)
        {
            return new BadRequestObjectResult(new { error = "too_many_events", detail = exception.Message });
        }
    }
}

/// <summary>Forwarding integration events from Service Bus to the <c>business-events</c> hub.</summary>
public sealed partial class BusinessEventsForwarderFunction(IBusinessEventPublisher publisher, ILogger<BusinessEventsForwarderFunction> logger)
{
    /// <remarks>
    /// AZURE: a batched trigger (<c>IsBatched = true</c>). The function receives up to <c>maxMessageBatchSize</c> messages at
    /// once and sends them to Event Hubs in a few batches instead of one request per message. With the whole shop's traffic
    /// (all events from four topics) that is the difference between a few and hundreds of calls per second.
    /// <para>
    /// WHY: messages are completed only after they are written to Event Hubs. When the write fails we abandon all of them,
    /// including those from groups that were already written. The retry sends them a second time, so business-events delivers
    /// "at least once", and a consumer counting money must deduplicate (see <c>ApplyOnceAsync</c>).
    /// </para>
    /// <para>
    /// A message without the required metadata or with invalid JSON goes to the DLQ immediately and does not block the rest of
    /// the batch.
    /// </para>
    /// </remarks>
    [Function("business-events-forwarder")]
    public async Task Run(
        [ServiceBusTrigger(MessagingTopology.Queues.AnalyticsInbox, Connection = "ServiceBus", IsBatched = true, AutoCompleteMessages = false)]
        ServiceBusReceivedMessage[] messages,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(actions);

        var events = new List<BusinessEvent>(messages.Length);
        var accepted = new List<ServiceBusReceivedMessage>(messages.Length);

        foreach (var message in messages)
        {
            try
            {
                events.Add(BusinessEventMapper.FromEnvelope(ServiceBusEnvelopeMapper.ToEnvelope(message)));
                accepted.Add(message);
            }
            catch (Exception exception) when (exception is PermanentMessageException or JsonException)
            {
                LogMalformed(exception, message.MessageId);
                await actions.DeadLetterMessageAsync(
                    message,
                    deadLetterReason: DeadLetterReasons.MalformedEnvelope,
                    deadLetterErrorDescription: exception.Message,
                    cancellationToken: cancellationToken);
            }
        }

        try
        {
            if (events.Count > 0)
            {
                await publisher.PublishAsync(events, cancellationToken);
            }
        }
        catch
        {
            foreach (var message in accepted)
            {
                await actions.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
            }

            throw;
        }

        foreach (var message in accepted)
        {
            await actions.CompleteMessageAsync(message, cancellationToken);
        }

        AnalyticsTelemetry.RecordForwarded(events.Count);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} could not be forwarded and was dead-lettered")]
    private partial void LogMalformed(Exception exception, string messageId);
}
