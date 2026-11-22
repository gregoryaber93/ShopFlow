using System.Net;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.ServiceBus;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.BuildingBlocks.Tests.Messaging;

/// <summary>Classifier composed the same way as in a service: default rules + Service Bus + Cosmos.</summary>
public sealed class FailureClassificationTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly MessageFailureClassifier _classifier;

    public FailureClassificationTests()
    {
        var configuration = new ConfigurationBuilder().Build();
        _provider = MessagingTestHost.Build(
            messaging => messaging.UseAzureServiceBus(configuration),
            services => services.AddShopFlowCosmos(configuration));
        _classifier = _provider.GetRequiredService<MessageFailureClassifier>();
    }

    public void Dispose() => _provider.Dispose();

    public static TheoryData<Exception, FailureKind, string> Cases() => new()
    {
        { new PermanentMessageException("ValidationFailed", "bad"), FailureKind.Permanent, "ValidationFailed" },
        { new DomainException("order.already_confirmed", "nope"), FailureKind.Permanent, DeadLetterReasons.BusinessRuleViolation },
        { new System.Text.Json.JsonException("bad json"), FailureKind.Permanent, DeadLetterReasons.DeserializationFailed },
        { new ConcurrencyConflictException("Order", "1"), FailureKind.Transient, "ConcurrencyConflict" },
        { new TimeoutException(), FailureKind.Transient, "Timeout" },
        { new InvalidOperationException("who knows"), FailureKind.Transient, "UnhandledException" },
        { new ServiceBusException("too big", ServiceBusFailureReason.MessageSizeExceeded), FailureKind.Permanent, DeadLetterReasons.MessageTooLarge },
        { new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy), FailureKind.Transient, "ServiceBus.ServiceBusy" },
        { new CosmosException("throttled", HttpStatusCode.TooManyRequests, 0, "a", 1), FailureKind.Transient, "Cosmos.429" },
        { new CosmosException("bad doc", HttpStatusCode.BadRequest, 0, "a", 1), FailureKind.Permanent, "Cosmos.400" },
        { new CosmosBatchException(HttpStatusCode.TooManyRequests, "throttled", TimeSpan.FromSeconds(1), 5), FailureKind.Transient, "Cosmos.429" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Exception_is_classified_by_the_owning_adapter(Exception exception, FailureKind kind, string reason)
    {
        var classification = _classifier.Classify(exception);

        classification.Kind.ShouldBe(kind);
        classification.Reason.ShouldBe(reason);
    }

    [Fact]
    public void Wrapped_cause_is_found_in_the_inner_exception_chain()
    {
        var wrapped = new InvalidOperationException("outer", new DomainException("x", "inner"));

        _classifier.Classify(wrapped).Kind.ShouldBe(FailureKind.Permanent);
    }
}
