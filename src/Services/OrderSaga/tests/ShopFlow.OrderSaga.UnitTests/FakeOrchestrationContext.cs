using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShopFlow.OrderSaga.Model;

namespace ShopFlow.OrderSaga.UnitTests;

/// <summary>
/// Orchestration context driven by the test: participant replies, timeouts, cancellation, operator decisions.
/// </summary>
/// <remarks>
/// All tasks complete synchronously or never. The orchestrator therefore runs in one <c>RunAsync</c> call to the end
/// or to the first wait without a reply (e.g. for an operator). The test then sees an incomplete task and can check the
/// state the saga stopped in.
/// <para>
/// By default every step gets a "success" reply. A test overrides only what it cares about:
/// <c>Reply(step, failure)</c> is a participant refusal and <c>Timeout(step)</c> is no reply.
/// </para>
/// </remarks>
internal sealed class FakeOrchestrationContext(OrderSagaInput input) : TaskOrchestrationContext
{
    private static readonly DateTime Start = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, Queue<SagaReply?>> _replies = [];
    private readonly Queue<OperatorResolution> _resolutions = new();
    private readonly TaskCompletionSource<SagaReply> _cancellation = new();
    private string? _cancelWhenAwaiting;
    private bool _lastWaitPending;
    private int _guids;

    public OrderSagaInput Input { get; } = input;

    public List<(string Name, object? Input)> Activities { get; } = [];

    public List<SagaCustomStatus> Statuses { get; } = [];

    public Dictionary<string, Func<object?, object?>> ActivityResults { get; } = new()
    {
        [SagaActivityNames.ConfirmOrder] = _ => OrdersCallOutcome.Applied,
        [SagaActivityNames.RejectOrder] = _ => OrdersCallOutcome.Applied,
        [SagaActivityNames.CompleteOrderCancellation] = _ => OrdersCallOutcome.Applied,
    };

    public IReadOnlyList<OutgoingCommand> SentCommands =>
        [.. Activities.Where(call => call.Name == SagaActivityNames.SendCommand).Select(call => (OutgoingCommand)call.Input!)];

    public IReadOnlyList<string> SentContracts => [.. SentCommands.Select(command => command.ContractName)];

    public override TaskName Name => OrderSagaInstance.OrchestratorName;

    public override string InstanceId => OrderSagaInstance.IdFor(Input.OrderId);

    public override ParentOrchestrationInstance? Parent => null;

    public override DateTime CurrentUtcDateTime => Start;

    public override bool IsReplaying => false;

    protected override ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

    public FakeOrchestrationContext Reply(string step, SagaReply reply)
    {
        Queue(step).Enqueue(reply);
        return this;
    }

    public FakeOrchestrationContext Fail(string step, string reasonCode) =>
        Reply(step, new SagaReply { Succeeded = false, ReplyType = $"{step}Failed", ReasonCode = reasonCode });

    public FakeOrchestrationContext Timeout(string step)
    {
        Queue(step).Enqueue(null);
        return this;
    }

    /// <summary>The customer asks for cancellation at the moment the saga starts waiting for the reply to a given step.</summary>
    public FakeOrchestrationContext CancelWhenAwaiting(string step)
    {
        _cancelWhenAwaiting = step;
        return this;
    }

    public FakeOrchestrationContext Resolve(OperatorAction action)
    {
        _resolutions.Enqueue(new OperatorResolution(action, "operator@shopflow", "test"));
        return this;
    }

    public override T GetInput<T>() => (T)(object)Input;

    public override Task<TResult> CallActivityAsync<TResult>(TaskName name, object? input = null, TaskOptions? options = null)
    {
        Activities.Add((name.Name, input));

        try
        {
            var result = ActivityResults.TryGetValue(name.Name, out var produce) ? produce(input) : null;
            return Task.FromResult(result is null ? default! : (TResult)result);
        }
        catch (Exception exception)
        {
            // This is how the orchestrator sees an activity that exhausted all its retries.
            return Task.FromException<TResult>(new TaskFailedException(name.Name, Activities.Count, exception));
        }
    }

    public override Task<T> WaitForExternalEvent<T>(string eventName, CancellationToken cancellationToken = default)
    {
        if (eventName == SagaEvents.CancellationRequested)
        {
            return (Task<T>)(object)_cancellation.Task;
        }

        if (eventName == SagaEvents.OperatorResolution)
        {
            return _resolutions.TryDequeue(out var resolution)
                ? Task.FromResult((T)(object)resolution)
                : Never<T>(cancellationToken);
        }

        if (eventName == _cancelWhenAwaiting)
        {
            _cancellation.TrySetResult(new SagaReply { Succeeded = true, ReplyType = "OrderCancellationRequested" });
        }

        var reply = _replies.TryGetValue(eventName, out var queue) && queue.Count > 0
            ? queue.Dequeue()
            : new SagaReply { Succeeded = true, ReplyType = $"{eventName}Done", WarehouseCode = "WAW-1", TrackingNumber = "TRK-1" };

        _lastWaitPending = reply is null;
        return reply is null ? Never<T>(cancellationToken) : Task.FromResult((T)(object)reply);
    }

    /// <summary>The timer "fires" immediately only when the reply we are waiting for in parallel will not arrive.</summary>
    public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken) =>
        _lastWaitPending ? Task.CompletedTask : Never<bool>(cancellationToken);

    public override void SetCustomStatus(object? customStatus) => Statuses.Add((SagaCustomStatus)customStatus!);

    public override Guid NewGuid() => new($"00000000-0000-0000-0000-{++_guids:D12}");

    public override void SendEvent(string instanceId, string eventName, object payload) => throw new NotSupportedException();

    public override Task<TResult> CallSubOrchestratorAsync<TResult>(TaskName orchestratorName, object? input = null, TaskOptions? options = null) =>
        throw new NotSupportedException();

    public override void ContinueAsNew(object? newInput = null, bool preserveUnprocessedEvents = true) => throw new NotSupportedException();

    private Queue<SagaReply?> Queue(string step)
    {
        if (!_replies.TryGetValue(step, out var queue))
        {
            _replies[step] = queue = new Queue<SagaReply?>();
        }

        return queue;
    }

    private static Task<T> Never<T>(CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<T>();
        cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
        return source.Task;
    }
}
