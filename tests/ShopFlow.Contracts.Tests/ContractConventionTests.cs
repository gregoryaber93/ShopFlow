using System.Reflection;

namespace ShopFlow.Contracts.Tests;

/// <summary>Rules whose violation does not break the build, but breaks the system in production.</summary>
public sealed class ContractConventionTests
{
    private static readonly Type[] MessageTypes =
    [
        .. typeof(IMessage).Assembly.GetTypes()
            .Where(type => typeof(IMessage).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false }),
    ];

    [Fact]
    public void Registry_contains_every_message_type()
    {
        // Building the registry throws if a type has no [MessageContract] or two types declare the same contract.
        // This test makes sure nothing was left out.
        MessageTypeRegistry.Default.Contracts.Count.ShouldBe(MessageTypes.Length);
    }

    [Fact]
    public void Every_message_is_a_command_or_an_event_but_not_both()
    {
        foreach (var type in MessageTypes)
        {
            var isCommand = typeof(ICommand).IsAssignableFrom(type);
            var isEvent = typeof(IIntegrationEvent).IsAssignableFrom(type);

            (isCommand ^ isEvent).ShouldBeTrue($"{type.FullName} must implement exactly one of ICommand or IIntegrationEvent.");
        }
    }

    [Fact]
    public void Contract_name_never_changes_meaning_between_command_and_event()
    {
        // The same name once as a command and once as an event would mean that a subscription filter on Subject catches
        // messages of two different kinds.
        var kinds = MessageTypes
            .GroupBy(type => type.GetCustomAttribute<MessageContractAttribute>()!.Name)
            .Where(group => group.Select(typeof(ICommand).IsAssignableFrom).Distinct().Count() > 1)
            .Select(group => group.Key);

        kinds.ShouldBeEmpty();
    }

    [Fact]
    public void Contracts_are_immutable_sealed_records()
    {
        foreach (var type in MessageTypes)
        {
            type.IsSealed.ShouldBeTrue($"{type.Name} must be sealed: consumers deserialize the exact type.");

            var settable = type.GetProperties()
                .Where(property => property.SetMethod is { IsPublic: true } setter
                    && !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
                .Select(property => property.Name);

            settable.ShouldBeEmpty($"{type.Name} has mutable properties; use init-only setters.");
        }
    }

    [Fact]
    public void Contracts_do_not_leak_internal_types()
    {
        // The contracts package must not depend on SharedKernel or on service code.
        var references = typeof(IMessage).Assembly.GetReferencedAssemblies().Select(name => name.Name);

        references.ShouldNotContain(name => name!.StartsWith("ShopFlow.", StringComparison.Ordinal));
    }

    [Fact]
    public void Contract_versions_start_at_one_and_have_no_gaps()
    {
        var gaps = MessageTypeRegistry.Default.Contracts
            .GroupBy(id => id.Name)
            .Where(group => !group.Select(id => id.Version).Order().SequenceEqual(Enumerable.Range(1, group.Count())))
            .Select(group => group.Key);

        gaps.ShouldBeEmpty();
    }
}
