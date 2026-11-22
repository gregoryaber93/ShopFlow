using System.Text.Json;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Common;
using ShopFlow.Messaging;
using Inventory = ShopFlow.Contracts.Inventory.V1;
using Payments = ShopFlow.Contracts.Payments.V1;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;
using ShippingV2 = ShopFlow.Contracts.Shipping.V2;

namespace ShopFlow.OrderSaga.Model;

/// <summary>
/// Command to be sent by an activity. Serializable, because it goes into the orchestration history.
/// </summary>
/// <remarks>
/// WHY: the payload is already serialized to JSON instead of being passed as a contract object. The Durable Functions
/// serializer does not handle polymorphism of the <see cref="ICommand"/> interface, and a string with the contract name
/// and version is unambiguous and readable in the instance history in the dashboard.
/// </remarks>
public sealed record OutgoingCommand(
    string Destination,
    string ContractName,
    int ContractVersion,
    string PayloadJson,
    Guid MessageId,
    string CorrelationId,
    string CausationId);

/// <summary>
/// Builds saga commands. Pure functions: the result depends only on the arguments, so they may be called
/// from orchestrator code.
/// </summary>
public static class SagaCommandFactory
{
    public static OutgoingCommand ReserveStock(OrderSagaInput input) =>
        Create(
            new Inventory.ReserveStock { OrderId = input.OrderId, CustomerId = input.CustomerId, Lines = input.Lines },
            MessagingTopology.Queues.InventoryCommands,
            input,
            idempotencyKey: SagaEvents.ReserveStock);

    public static OutgoingCommand AuthorizePayment(OrderSagaInput input, Guid paymentId) =>
        Create(
            new Payments.AuthorizePayment
            {
                OrderId = input.OrderId,
                CustomerId = input.CustomerId,
                Amount = input.Total,
                PaymentMethodToken = input.PaymentMethodToken,
                PaymentId = paymentId,
            },
            MessagingTopology.Queues.PaymentsCommands,
            input,
            SagaEvents.AuthorizePayment);

    /// <summary>
    /// v1 for courier delivery, v2 only for pickup points (the oldest version that can express the command).
    /// </summary>
    public static OutgoingCommand CreateShipment(OrderSagaInput input, Guid shipmentId, string warehouseCode)
    {
        ICommand command = input.Delivery.IsPickupPoint
            ? new ShippingV2.CreateShipment
            {
                OrderId = input.OrderId,
                CustomerId = input.CustomerId,
                OrderNumber = input.OrderNumber,
                ShipmentId = shipmentId,
                WarehouseCode = warehouseCode,
                Destination = new ShippingV2.ShipmentDestinationDto
                {
                    Method = ShippingV2.ShipmentDeliveryMethod.PickupPoint,
                    PickupPointId = input.Delivery.PickupPointId,
                },
                Lines = input.Lines,
            }
            : new ShippingV1.CreateShipment
            {
                OrderId = input.OrderId,
                CustomerId = input.CustomerId,
                OrderNumber = input.OrderNumber,
                ShipmentId = shipmentId,
                WarehouseCode = warehouseCode,
                Destination = input.Delivery.Address
                    ?? throw new InvalidOperationException($"Courier order {input.OrderId} has no address."),
                Lines = input.Lines,
            };

        return Create(command, MessagingTopology.Queues.ShippingCommands, input, SagaEvents.CreateShipment);
    }

    public static OutgoingCommand CapturePayment(OrderSagaInput input, Guid paymentId) =>
        Create(
            new Payments.CapturePayment { OrderId = input.OrderId, CustomerId = input.CustomerId, PaymentId = paymentId, Amount = input.Total },
            MessagingTopology.Queues.PaymentsCommands,
            input,
            SagaEvents.CapturePayment);

    // Compensations have the attempt number in the idempotency key: an operator may order a retry, and the same
    // command with the same MessageId would be silently dropped by the broker's duplicate detection.

    public static OutgoingCommand ReleaseStock(OrderSagaInput input, string reason, int attempt) =>
        Create(
            new Inventory.ReleaseStock { OrderId = input.OrderId, CustomerId = input.CustomerId, Reason = reason },
            MessagingTopology.Queues.InventoryCommands,
            input,
            $"{SagaEvents.ReleaseStock}/{attempt}");

    public static OutgoingCommand VoidPayment(OrderSagaInput input, Guid paymentId, string reason, int attempt) =>
        Create(
            new Payments.VoidPayment { OrderId = input.OrderId, CustomerId = input.CustomerId, PaymentId = paymentId, Reason = reason },
            MessagingTopology.Queues.PaymentsCommands,
            input,
            $"{SagaEvents.VoidPayment}/{attempt}");

    public static OutgoingCommand RefundPayment(OrderSagaInput input, Guid paymentId, string reason, int attempt) =>
        Create(
            new Payments.RefundPayment { OrderId = input.OrderId, CustomerId = input.CustomerId, PaymentId = paymentId, Amount = input.Total, Reason = reason },
            MessagingTopology.Queues.PaymentsCommands,
            input,
            $"{SagaEvents.RefundPayment}/{attempt}");

    public static OutgoingCommand CancelShipment(OrderSagaInput input, Guid shipmentId, string reason, int attempt) =>
        Create(
            new ShippingV1.CancelShipment { OrderId = input.OrderId, CustomerId = input.CustomerId, ShipmentId = shipmentId, Reason = reason },
            MessagingTopology.Queues.ShippingCommands,
            input,
            $"{SagaEvents.CancelShipment}/{attempt}");

    private static OutgoingCommand Create(ICommand command, string destination, OrderSagaInput input, string idempotencyKey)
    {
        var contract = MessageTypeRegistry.Default.GetContractId(command.GetType());

        return new OutgoingCommand(
            destination,
            contract.Name,
            contract.Version,
            JsonSerializer.Serialize(command, command.GetType(), ContractJson.Options),

            // WHY: the activity that sends a command may run more than once (e.g. the process crashed after sending but before
            // the result was recorded in the history). An identifier derived from the instance and the step gives every copy the
            // same MessageId, and the broker rejects the duplicate.
            DeterministicGuid.Create($"{OrderSagaInstance.IdFor(input.OrderId)}/{idempotencyKey}"),
            input.CorrelationId,
            input.TriggeringMessageId);
    }
}
