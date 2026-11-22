using System.Text.Json;
using ShopFlow.OrderSaga.Model;
using static ShopFlow.OrderSaga.UnitTests.SagaTestData;

namespace ShopFlow.OrderSaga.UnitTests;

public sealed class SagaCommandFactoryTests
{
    [Fact]
    public void Commands_inherit_correlation_and_causation_from_the_triggering_event()
    {
        var command = SagaCommandFactory.ReserveStock(Input());

        command.CorrelationId.ShouldBe("corr-saga");
        command.CausationId.ShouldBe(Input().TriggeringMessageId);
        command.Destination.ShouldBe("inventory-commands");
    }

    [Fact]
    public void Message_id_depends_only_on_instance_and_step()
    {
        SagaCommandFactory.ReserveStock(Input()).MessageId.ShouldBe(SagaCommandFactory.ReserveStock(Input()).MessageId);
        SagaCommandFactory.ReserveStock(Input()).MessageId.ShouldNotBe(SagaCommandFactory.ReleaseStock(Input(), "x", 1).MessageId);
    }

    [Fact]
    public void Each_compensation_attempt_gets_its_own_message_id()
    {
        SagaCommandFactory.ReleaseStock(Input(), "x", 1).MessageId.ShouldNotBe(SagaCommandFactory.ReleaseStock(Input(), "x", 2).MessageId);
    }

    [Fact]
    public void Payment_id_assigned_by_the_saga_travels_in_the_authorize_command()
    {
        var paymentId = Guid.NewGuid();

        var command = SagaCommandFactory.AuthorizePayment(Input(), paymentId);

        using var json = JsonDocument.Parse(command.PayloadJson);
        json.RootElement.GetProperty("paymentId").GetGuid().ShouldBe(paymentId);
        command.Destination.ShouldBe("payments-commands");
    }
}
