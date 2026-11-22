using System.Globalization;

namespace ShopFlow.DlqReplay;

internal enum DlqCommand
{
    Peek,
    Replay,
}

internal sealed record DlqArguments(
    DlqCommand Command,
    string Namespace,
    DeadLetterSource Source,
    int Max,
    ReplayFilter Filter,
    bool DryRun);

/// <summary>
/// Hand-written argument parser.
/// </summary>
/// <remarks>
/// WHY: no command-line library. The tool has two commands and a few options, and every dependency is another package to
/// update in a tool that runs rarely and in emergencies.
/// </remarks>
internal static class CommandLine
{
    public const string Usage = """
        shopflow-dlq peek   --namespace <fqdn> (--queue <name> | --topic <name> --subscription <name>) [--max 20]
        shopflow-dlq replay --namespace <fqdn> (--queue <name> | --topic <name> --subscription <name>)
                            [--message-id <id> ...] [--reason <DeadLetterReason>] [--max 50] [--dry-run] [--force]

          --namespace   e.g. sbns-shopflow-prod-weu.servicebus.windows.net (or the SHOPFLOW_SERVICEBUS_NAMESPACE variable)
          --dry-run     show what would be replayed, without sending
          --force       skip the replay limit and the duplicate detection window (a deliberate operator decision)
        """;

    public static DlqArguments Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        if (args.Count == 0)
        {
            throw new ArgumentException("Missing command.");
        }

        var command = args[0] switch
        {
            "peek" => DlqCommand.Peek,
            "replay" => DlqCommand.Replay,
            var other => throw new ArgumentException($"Unknown command '{other}'."),
        };

        string? ns = environment("SHOPFLOW_SERVICEBUS_NAMESPACE");
        string? queue = null, topic = null, subscription = null, reason = null;
        int? max = null;
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        bool dryRun = false, force = false;

        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--namespace": ns = Value(args, ref index); break;
                case "--queue": queue = Value(args, ref index); break;
                case "--topic": topic = Value(args, ref index); break;
                case "--subscription": subscription = Value(args, ref index); break;
                case "--reason": reason = Value(args, ref index); break;
                case "--message-id": messageIds.Add(Value(args, ref index)); break;
                case "--max": max = int.Parse(Value(args, ref index), CultureInfo.InvariantCulture); break;
                case "--dry-run": dryRun = true; break;
                case "--force": force = true; break;
                default: throw new ArgumentException($"Unknown option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(ns))
        {
            throw new ArgumentException("Missing --namespace.");
        }

        if ((queue is null) == (topic is null) || (topic is null) != (subscription is null))
        {
            throw new ArgumentException("Use either --queue, or --topic together with --subscription.");
        }

        if (command == DlqCommand.Peek && (messageIds.Count > 0 || reason is not null || dryRun || force))
        {
            throw new ArgumentException("peek does not take replay options.");
        }

        return new DlqArguments(
            command,
            ns,
            new DeadLetterSource(queue, topic, subscription),
            Math.Clamp(max ?? (command == DlqCommand.Peek ? 20 : 50), 1, 500),
            new ReplayFilter(messageIds, reason, force),
            dryRun);
    }

    private static string Value(IReadOnlyList<string> args, ref int index) =>
        ++index < args.Count && !args[index].StartsWith("--", StringComparison.Ordinal)
            ? args[index]
            : throw new ArgumentException($"Option '{args[index - 1]}' needs a value.");
}
