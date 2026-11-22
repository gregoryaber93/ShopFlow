using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using ShopFlow.OrderSaga.Model;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Persistence.Cosmos.Documents;

namespace ShopFlow.OrderSaga.Activities;

public sealed class SagaStateDocument : CosmosDocument
{
    public const string DocumentType = "saga";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    public required string OrderId { get; init; }

    public required string InstanceId { get; init; }

    public required SagaStatus Status { get; init; }

    public string? CurrentStep { get; init; }

    public string? RejectionReason { get; init; }

    public required IReadOnlyList<SagaStepRecord> Steps { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Projection of the saga's progress in the <c>saga-state</c> container (database <c>saga</c>, partition key <c>/orderId</c>).
/// </summary>
/// <remarks>
/// WHY: why the saga state lives in two places. The orchestration history in the Durable Task Scheduler is the source
/// of truth, but it is a log of technical events, convenient for a developer, not for customer support. Durable
/// Functions also has no Cosmos DB provider. This document is a business-readable view of "which stage order X is at
/// and why it failed", available through a SQL query and from the back-office panel. Only the orchestration writes
/// it, so a plain upsert without an ETag is safe: the activities of one instance run one after another.
/// </remarks>
public sealed class SagaStateStore(Database database)
{
    public const string ContainerName = "saga-state";

    private static readonly int TimeToLiveSeconds = (int)TimeSpan.FromDays(90).TotalSeconds;

    private readonly Container _container = database.GetContainer(ContainerName);

    public async Task SaveAsync(SagaProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var key = progress.OrderId.ToString("D");
        var document = new SagaStateDocument
        {
            Id = key,
            OrderId = key,
            InstanceId = progress.InstanceId,
            Status = progress.Status,
            CurrentStep = progress.CurrentStep,
            RejectionReason = progress.RejectionReason,
            Steps = progress.Steps,
            StartedAt = progress.StartedAt,
            UpdatedAt = progress.UpdatedAt,
            TimeToLive = TimeToLiveSeconds,
        };

        var response = await _container
            .UpsertItemAsync(document, new PartitionKey(key), new ItemRequestOptions { EnableContentResponseOnWrite = false }, cancellationToken)
            .ConfigureAwait(false);

        CosmosTelemetry.RecordRequestCharge(ContainerName, "upsert", response.RequestCharge);
    }
}
