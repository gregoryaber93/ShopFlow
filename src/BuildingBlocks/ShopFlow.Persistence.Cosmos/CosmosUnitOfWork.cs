using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Persistence.Cosmos.Documents;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Persistence.Cosmos;

/// <summary>Creates a unit of work for one logical partition.</summary>
public interface ICosmosUnitOfWorkFactory
{
    /// <param name="container">Container that holds the aggregate.</param>
    /// <param name="partitionKeyProperty">Name of the partition key field without the slash, e.g. <c>orderId</c>.</param>
    /// <param name="partitionKeyValue">Partition key value shared by all operations.</param>
    CosmosUnitOfWork Create(Container container, string partitionKeyProperty, string partitionKeyValue);
}

internal sealed class CosmosUnitOfWorkFactory(
    IPendingOutbox pendingOutbox,
    IMessageContextAccessor contextAccessor,
    IOptions<CosmosOptions> options,
    TimeProvider timeProvider) : ICosmosUnitOfWorkFactory
{
    public CosmosUnitOfWork Create(Container container, string partitionKeyProperty, string partitionKeyValue) =>
        new(container, partitionKeyProperty, partitionKeyValue, pendingOutbox, contextAccessor, options.Value, timeProvider);
}

/// <summary>Commit result: new ETags of the saved documents and the RU cost.</summary>
public sealed class CosmosCommitResult(IReadOnlyDictionary<string, string> etags, double requestCharge)
{
    public static CosmosCommitResult Empty { get; } = new(new Dictionary<string, string>(), 0);

    public double RequestCharge { get; } = requestCharge;

    /// <summary>ETag assigned to the document by this write. Needed for the ETag header of the HTTP response.</summary>
    public string? GetETag(string documentId) => etags.GetValueOrDefault(documentId);
}

/// <summary>
/// Atomic write of the aggregate's changes together with outbox messages and the inbox entry.
/// </summary>
/// <remarks>
/// AZURE: <c>TransactionalBatch</c> is the only form of ACID transaction in Cosmos DB available from the SDK.
/// Limits: one logical partition, one container, at most 100 operations and 2 MB. Operations run in order;
/// the first failure stops and rolls back the whole batch.
/// WHY: these limits dictate the data model. The outbox and inbox have no container of their own; they live
/// in the aggregate's partition, because otherwise they could not be saved together with it. That is what
/// this class does: it appends to the repository's operations the messages from the current operation's
/// buffer (<see cref="IPendingOutbox"/>) and the record of the message being handled.
/// </remarks>
public sealed class CosmosUnitOfWork
{
    private const int MaxBatchOperations = 100;

    private readonly Container _container;
    private readonly string _partitionKeyProperty;
    private readonly string _partitionKeyValue;
    private readonly IPendingOutbox _pendingOutbox;
    private readonly IMessageContextAccessor _contextAccessor;
    private readonly CosmosOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly List<PendingOperation> _operations = [];

    internal CosmosUnitOfWork(
        Container container,
        string partitionKeyProperty,
        string partitionKeyValue,
        IPendingOutbox pendingOutbox,
        IMessageContextAccessor contextAccessor,
        CosmosOptions options,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKeyProperty);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKeyValue);

        _container = container;
        _partitionKeyProperty = partitionKeyProperty;
        _partitionKeyValue = partitionKeyValue;
        _pendingOutbox = pendingOutbox;
        _contextAccessor = contextAccessor;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>Inserts a new document. Fails if a document with this identifier exists.</summary>
    public CosmosUnitOfWork Create<TDocument>(TDocument document)
        where TDocument : CosmosDocument
    {
        ArgumentNullException.ThrowIfNull(document);
        _operations.Add(new PendingOperation(
            OperationKind.Create,
            document.Id,
            document.DocType,
            (batch, _) => batch.CreateItem(document, WriteOptions(ifMatchETag: null))));
        return this;
    }

    /// <summary>
    /// Replaces the document provided its version in the database is still <paramref name="ifMatchETag"/>.
    /// </summary>
    public CosmosUnitOfWork Replace<TDocument>(TDocument document, string ifMatchETag)
        where TDocument : CosmosDocument
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatchETag);
        _operations.Add(new PendingOperation(
            OperationKind.Replace,
            document.Id,
            document.DocType,
            (batch, _) => batch.ReplaceItem(document.Id, document, WriteOptions(ifMatchETag))));
        return this;
    }

    /// <summary>
    /// Inserts or overwrites a document. Without an ETag the last write wins, so it is only suitable
    /// for data where that is acceptable (e.g. projections rebuilt from scratch).
    /// </summary>
    public CosmosUnitOfWork Upsert<TDocument>(TDocument document, string? ifMatchETag = null)
        where TDocument : CosmosDocument
    {
        ArgumentNullException.ThrowIfNull(document);
        _operations.Add(new PendingOperation(
            OperationKind.Upsert,
            document.Id,
            document.DocType,
            (batch, _) => batch.UpsertItem(document, WriteOptions(ifMatchETag))));
        return this;
    }

    public CosmosUnitOfWork Delete(string id, string documentType, string? ifMatchETag = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _operations.Add(new PendingOperation(
            OperationKind.Delete,
            id,
            documentType,
            (batch, _) => batch.DeleteItem(id, WriteOptions(ifMatchETag))));
        return this;
    }

    public async Task<CosmosCommitResult> CommitAsync(CancellationToken cancellationToken)
    {
        var operations = BuildOperations();
        if (operations.Count == 0)
        {
            return CosmosCommitResult.Empty;
        }

        if (operations.Count > MaxBatchOperations)
        {
            throw new InvalidOperationException(
                $"A Cosmos DB transactional batch is limited to {MaxBatchOperations} operations but {operations.Count} were queued. " +
                "An aggregate that produces this many writes in one operation should be split.");
        }

        var batch = _container.CreateTransactionalBatch(new PartitionKey(_partitionKeyValue));
        foreach (var operation in operations)
        {
            operation.Apply(batch, cancellationToken);
        }

        using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        CosmosTelemetry.RecordRequestCharge(_container.Id, "transactional_batch", response.RequestCharge);

        if (!response.IsSuccessStatusCode)
        {
            throw TranslateFailure(response, operations);
        }

        var etags = new Dictionary<string, string>(operations.Count, StringComparer.Ordinal);
        for (var index = 0; index < operations.Count; index++)
        {
            if (response[index].ETag is { } etag)
            {
                etags[operations[index].DocumentId] = etag;
            }
        }

        _operations.Clear();
        return new CosmosCommitResult(etags, response.RequestCharge);
    }

    private List<PendingOperation> BuildOperations()
    {
        var now = _timeProvider.GetUtcNow();
        var operations = new List<PendingOperation>(_operations.Count + 2);

        // WHY: the inbox entry is the first operation. The batch stops at the first failure, so a duplicate
        // message is rejected immediately, at the lowest possible cost, before Cosmos starts checking
        // the ETags of the other documents.
        if (_contextAccessor.Current is { } incoming)
        {
            var inbox = InboxDocument.From(incoming.InboxEntry, _partitionKeyProperty, _partitionKeyValue, now, _options.InboxTimeToLive);
            operations.Add(new PendingOperation(
                OperationKind.Inbox,
                inbox.Id,
                inbox.DocType,
                (batch, _) => batch.CreateItem(inbox, WriteOptions(ifMatchETag: null)),
                incoming.InboxEntry));
        }

        operations.AddRange(_operations);

        foreach (var outbound in _pendingOutbox.Drain())
        {
            var outbox = OutboxDocument.From(outbound, _partitionKeyProperty, _partitionKeyValue, now, _options.OutboxTimeToLive);
            operations.Add(new PendingOperation(
                OperationKind.Outbox,
                outbox.Id,
                outbox.DocType,
                (batch, _) => batch.CreateItem(outbox, WriteOptions(ifMatchETag: null))));
        }

        return operations;
    }

    private Exception TranslateFailure(TransactionalBatchResponse response, List<PendingOperation> operations)
    {
        // AZURE: in a failed batch exactly one operation has its own error code. The others get
        // 424 Failed Dependency ("not executed because another operation failed").
        for (var index = 0; index < response.Count && index < operations.Count; index++)
        {
            var result = response[index];
            if (result.IsSuccessStatusCode || result.StatusCode == HttpStatusCode.FailedDependency)
            {
                continue;
            }

            var operation = operations[index];

            switch (result.StatusCode)
            {
                case HttpStatusCode.Conflict when operation.InboxEntry is { } entry:
                    return new DuplicateMessageException(entry);

                case HttpStatusCode.Conflict:
                    return new DocumentAlreadyExistsException(operation.DocumentType, operation.DocumentId);

                // 412: the ETag from If-Match does not match the version in the database.
                // 404 on Replace/Delete: the document was deleted between the read and the write.
                case HttpStatusCode.PreconditionFailed:
                case HttpStatusCode.NotFound when operation.Kind is OperationKind.Replace or OperationKind.Delete:
                    return new ConcurrencyConflictException(operation.DocumentType, operation.DocumentId);
            }
        }

        // Batch-level error: 429 (no RU left), 413 (too large), 408/503 (outage).
        return new CosmosBatchException(response.StatusCode, response.ErrorMessage, response.RetryAfter, response.RequestCharge);
    }

    private static TransactionalBatchItemRequestOptions WriteOptions(string? ifMatchETag) =>
        new()
        {
            IfMatchEtag = ifMatchETag,

            // AZURE: by default Cosmos returns the whole saved document in the response. We do not need it
            // (we have it in memory) and transferring it costs bandwidth and time. The ETag comes back in the headers.
            EnableContentResponseOnWrite = false,
        };

    private enum OperationKind
    {
        Create,
        Replace,
        Upsert,
        Delete,
        Outbox,
        Inbox,
    }

    private sealed record PendingOperation(
        OperationKind Kind,
        string DocumentId,
        string DocumentType,
        Action<TransactionalBatch, CancellationToken> Apply,
        InboxEntry? InboxEntry = null);
}
