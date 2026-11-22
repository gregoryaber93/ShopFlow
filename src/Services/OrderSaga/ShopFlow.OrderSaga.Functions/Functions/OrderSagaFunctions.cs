using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using ShopFlow.OrderSaga.Activities;
using ShopFlow.OrderSaga.Model;
using ShopFlow.OrderSaga.Workflow;

namespace ShopFlow.OrderSaga.Functions.Functions;

/// <summary>Orchestrator: a thin layer over <see cref="OrderSagaWorkflow"/>.</summary>
public static class OrderSagaOrchestration
{
    /// <remarks>
    /// AZURE: a static method with no DI dependencies. An orchestrator must not do I/O, so there is nothing to inject,
    /// and the lack of a constructor with services removes the temptation to use them in the orchestration.
    /// </remarks>
    [Function(OrderSagaInstance.OrchestratorName)]
    public static Task<SagaOutcome> RunAsync([OrchestrationTrigger] TaskOrchestrationContext context) =>
        new OrderSagaWorkflow(context).RunAsync();
}

/// <summary>
/// Saga activities: the only place where the saga touches the outside world.
/// </summary>
/// <remarks>
/// AZURE: an activity runs at least once. Durable Functions records its result in the history only after it
/// completes, so after a crash mid-way it runs again. Each of these methods is therefore idempotent: a command has a
/// deterministic MessageId, an Orders operation checks the target state, and the projection is an upsert of the
/// whole document.
/// </remarks>
public sealed class SagaActivityFunctions(SagaCommandSender sender, OrdersApiClient orders, SagaStateStore state)
{
    [Function(SagaActivityNames.SendCommand)]
    public Task SendCommand([ActivityTrigger] OutgoingCommand command, CancellationToken cancellationToken) =>
        sender.SendAsync(command, cancellationToken);

    [Function(SagaActivityNames.ConfirmOrder)]
    public Task<OrdersCallOutcome> ConfirmOrder([ActivityTrigger] ConfirmOrderInput input, CancellationToken cancellationToken) =>
        orders.ConfirmAsync(input, cancellationToken);

    [Function(SagaActivityNames.RejectOrder)]
    public Task<OrdersCallOutcome> RejectOrder([ActivityTrigger] RejectOrderInput input, CancellationToken cancellationToken) =>
        orders.RejectAsync(input, cancellationToken);

    [Function(SagaActivityNames.CompleteOrderCancellation)]
    public Task<OrdersCallOutcome> CompleteOrderCancellation([ActivityTrigger] Guid orderId, CancellationToken cancellationToken) =>
        orders.CompleteCancellationAsync(orderId, cancellationToken);

    [Function(SagaActivityNames.RecordProgress)]
    public Task RecordProgress([ActivityTrigger] SagaProgress progress, CancellationToken cancellationToken) =>
        state.SaveAsync(progress, cancellationToken);
}
