namespace ShopFlow.SharedKernel.Domain;

/// <summary>
/// Aggregate root: the transactional consistency boundary. One business operation changes one aggregate
/// and saves it in one transaction together with the events it produced.
/// </summary>
public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id)
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Opaque version token assigned by the data store: <c>_etag</c> in Cosmos DB or <c>rowversion</c>
    /// in SQL. The domain does not interpret it.
    /// </summary>
    /// <remarks>
    /// WHY: the token lives on the aggregate, not in a separate dictionary in the repository, because it
    /// must survive the "read → logic → write" round trip and reach the client as the ETag header. The
    /// aggregate is the only object that passes through all of these layers.
    /// </remarks>
    public string? ConcurrencyToken { get; private set; }

    public void SetConcurrencyToken(string? token) => ConcurrencyToken = token;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>Returns the collected events and clears the list. Called by the application layer right before saving.</summary>
    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        IDomainEvent[] events = [.. _domainEvents];
        _domainEvents.Clear();
        return events;
    }
}
