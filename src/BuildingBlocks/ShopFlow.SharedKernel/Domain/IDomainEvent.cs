namespace ShopFlow.SharedKernel.Domain;

/// <summary>
/// A fact that happened inside an aggregate. Lives only within the boundaries of one service.
/// </summary>
/// <remarks>
/// WHY: a domain event is not the same as an integration event from <c>ShopFlow.Contracts</c>.
/// A domain event may change with every refactoring of the model. An integration event is a
/// versioned public contract. The Application layer translates one into the other, so changes to
/// the domain model do not break consumers.
/// </remarks>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
