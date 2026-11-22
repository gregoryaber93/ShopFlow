using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ShopFlow.Messaging.ServiceBus;

public sealed class ServiceBusConsumerOptions
{
    /// <summary>Queue name or, when <see cref="SubscriptionName"/> is set, topic name.</summary>
    public string EntityPath { get; set; } = string.Empty;

    public string? SubscriptionName { get; set; }

    public ConsumerEndpoint Endpoint { get; set; } = new("unknown");

    /// <summary>
    /// How many messages one replica processes concurrently.
    /// </summary>
    /// <remarks>
    /// AZURE: KEDA scales the number of replicas by the queue length divided by the scale rule's
    /// <c>messageCount</c> parameter. This value and <c>messageCount</c> should be close: if a replica
    /// handles 8 messages at a time and the rule assumes 1 per replica, KEDA starts eight times too
    /// many containers.
    /// </remarks>
    public int MaxConcurrentCalls { get; set; } = 8;

    /// <summary>
    /// How many messages the SDK prefetches into a local buffer.
    /// </summary>
    /// <remarks>
    /// AZURE: the lock of a prefetched message starts ticking when it is fetched, not when handling starts.
    /// Too large a prefetch with a slow handler makes locks expire in the buffer and artificially inflates
    /// DeliveryCount.
    /// </remarks>
    public int PrefetchCount { get; set; } = 16;

    /// <summary>How long the SDK automatically renews the message lock while it is being handled.</summary>
    public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Queue or subscription consumer for long-running processes (Container Apps).
/// Under Azure Functions the trigger plays this role.
/// </summary>
public sealed partial class ServiceBusQueueConsumer(
    ServiceBusClient client,
    ServiceBusMessageProcessor messageProcessor,
    ServiceBusConsumerOptions options,
    ILogger<ServiceBusQueueConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processorOptions = new ServiceBusProcessorOptions
        {
            // We settle manually: ServiceBusMessageProcessor decides between Complete and DeadLetter.
            AutoCompleteMessages = false,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            MaxConcurrentCalls = options.MaxConcurrentCalls,
            PrefetchCount = options.PrefetchCount,
            MaxAutoLockRenewalDuration = options.MaxAutoLockRenewalDuration,
            Identifier = $"{options.Endpoint.Name}-{Environment.MachineName}",
        };

        var processor = options.SubscriptionName is null
            ? client.CreateProcessor(options.EntityPath, processorOptions)
            : client.CreateProcessor(options.EntityPath, options.SubscriptionName, processorOptions);

        await using (processor.ConfigureAwait(false))
        {
            processor.ProcessMessageAsync += args => messageProcessor.ProcessAsync(
                args.Message,
                new ProcessorSettlement(args),
                options.Endpoint,
                args.CancellationToken);

            processor.ProcessErrorAsync += args =>
            {
                // Infrastructure errors (dropped connection, missing permissions) and handler exceptions end up here.
                // The latter are already logged; the SDK abandons the message after them on its own.
                LogProcessorError(args.Exception, args.EntityPath, args.ErrorSource.ToString());
                return Task.CompletedTask;
            };

            await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
            LogStarted(processor.EntityPath, options.MaxConcurrentCalls);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal host shutdown.
            }

            // AZURE: Container Apps sends SIGTERM and waits terminationGracePeriodSeconds (30 s by default)
            // before killing the container. StopProcessingAsync stops fetching new messages and waits for
            // in-flight handlers to finish. Unfinished ones return to the queue after the lock expires, so
            // nothing is lost, but a clean shutdown saves DeliveryCount.
            await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
            LogStopped(processor.EntityPath);
        }
    }

    private sealed class ProcessorSettlement(ProcessMessageEventArgs args) : IMessageSettlement
    {
        public Task CompleteAsync(CancellationToken cancellationToken) =>
            args.CompleteMessageAsync(args.Message, cancellationToken);

        public Task DeadLetterAsync(string reason, string description, CancellationToken cancellationToken) =>
            args.DeadLetterMessageAsync(args.Message, reason, description, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Service Bus consumer started on {EntityPath} with concurrency {MaxConcurrentCalls}")]
    private partial void LogStarted(string entityPath, int maxConcurrentCalls);

    [LoggerMessage(Level = LogLevel.Information, Message = "Service Bus consumer stopped on {EntityPath}")]
    private partial void LogStopped(string entityPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Service Bus processor error on {EntityPath} during {ErrorSource}")]
    private partial void LogProcessorError(Exception exception, string entityPath, string errorSource);
}
