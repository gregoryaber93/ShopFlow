using ShopFlow.Contracts;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Handling;

namespace ShopFlow.BuildingBlocks.Tests.Messaging;

[MessageContract("TestOrderPlaced", 1)]
public sealed record TestOrderPlaced : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string Note { get; init; }
}

[MessageContract("TestPing", 1)]
public sealed record TestPing : ICommand
{
    public required string Text { get; init; }
}

/// <summary>Handler whose behaviour the test sets through a delegate.</summary>
public sealed class RecordingHandler : IMessageHandler<TestOrderPlaced>
{
    public static Func<TestOrderPlaced, MessageContext, Task> Behavior { get; set; } = (_, _) => Task.CompletedTask;

    public static List<(TestOrderPlaced Message, MessageContext Context, string? CorrelationFromAccessor)> Calls { get; } = [];

    private readonly IMessageContextAccessor _accessor;

    public RecordingHandler(IMessageContextAccessor accessor) => _accessor = accessor;

    public Task HandleAsync(TestOrderPlaced message, MessageContext context, CancellationToken cancellationToken)
    {
        Calls.Add((message, context, _accessor.Current?.CorrelationId));
        return Behavior(message, context);
    }
}

/// <summary>Test classes using the static <see cref="RecordingHandler"/> must not run in parallel.</summary>
[CollectionDefinition(Name)]
public sealed class RecordingHandlerCollection
{
    public const string Name = "RecordingHandler";
}

internal static class TestRegistry
{
    public static MessageTypeRegistry Instance { get; } = MessageTypeRegistry.FromAssemblies(typeof(TestOrderPlaced).Assembly);
}
