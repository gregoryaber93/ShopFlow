using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ShopFlow.DlqReplay;

// AZURE: an operator tool for the DLQ. Run from a network that can see the Service Bus private endpoint (the namespace has
// public access disabled): from a machine through Azure Bastion or as a Container Apps Job in the VNet.
// Authentication: DefaultAzureCredential, i.e. the operator's account from `az login` (or the job's managed identity).
// The operator has no standing permissions. The Azure Service Bus Data Receiver (read the DLQ) and Data Sender (target
// entity) roles are activated for a few hours through Privileged Identity Management, with a justification in the audit log.

try
{
    var arguments = CommandLine.Parse(args, Environment.GetEnvironmentVariable);
    await using var client = new ServiceBusClient(arguments.Namespace, new DefaultAzureCredential());
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var tool = new DeadLetterTool(client, ReplayTargetResolver.FromEmbeddedTopology(), TimeProvider.System, Console.Out);
    return arguments.Command switch
    {
        DlqCommand.Peek => await tool.PeekAsync(arguments, cancellation.Token),
        _ => await tool.ReplayAsync(arguments, cancellation.Token),
    };
}
catch (ArgumentException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    await Console.Error.WriteLineAsync(CommandLine.Usage);
    return 2;
}
