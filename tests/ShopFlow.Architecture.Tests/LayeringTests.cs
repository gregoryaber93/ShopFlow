using System.Reflection;
using NetArchTest.Rules;
using ShopFlow.Messaging.Handling;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Architecture.Tests;

/// <summary>
/// Dependency rules. Each of them describes an architectural decision that is easy to break with a single
/// <c>using</c> in a pull request, and whose breakage shows only months later.
/// </summary>
public sealed class LayeringTests
{
    /// <remarks>
    /// WHY: the domain is business rules that must survive a change of database, broker and cloud. It does not even know
    /// the integration contracts: the application layer maps domain events to integration events, so a change in the
    /// message schema does not touch the aggregates.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Solution.Domains), MemberType = typeof(Solution))]
    public void Domain_depends_only_on_the_shared_kernel(string assemblyName)
    {
        var context = Solution.ContextPrefix(assemblyName);

        var result = Types.InAssembly(Solution.Load(assemblyName))
            .ShouldNot()
            .HaveDependencyOnAny(
                $"{context}.Application",
                $"{context}.Infrastructure",
                "ShopFlow.Contracts",
                "ShopFlow.Messaging",
                "ShopFlow.Persistence",
                "ShopFlow.Azure",
                "Azure",
                "Microsoft.Azure",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Microsoft.Extensions",
                "System.Net.Http")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    /// <remarks>
    /// WHY: the application layer describes use cases through ports (interfaces). The Azure SDK, EF Core and HTTP appear only
    /// in adapters in Infrastructure. A handler is therefore tested with in-memory fakes, and replacing e.g. Cosmos DB with
    /// another store does not change a single line of logic.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Solution.Applications), MemberType = typeof(Solution))]
    public void Application_does_not_know_persistence_or_azure_sdks(string assemblyName)
    {
        var context = Solution.ContextPrefix(assemblyName);

        var result = Types.InAssembly(Solution.Load(assemblyName))
            .ShouldNot()
            .HaveDependencyOnAny(
                $"{context}.Infrastructure",
                "ShopFlow.Persistence",
                "ShopFlow.Azure",
                "ShopFlow.Messaging.ServiceBus",
                "ShopFlow.Web",
                "ShopFlow.Functions",
                "Azure",
                "Microsoft.Azure",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.Data.SqlClient",
                "Microsoft.AspNetCore",
                "StackExchange.Redis")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    /// <remarks>
    /// WHY: contexts talk to each other only through message contracts (ShopFlow.Contracts) and HTTP APIs. A project
    /// reference to another context is hidden coupling: a shared deployment, a shared type base and the end of independent
    /// teams. OrderSaga is a separate module too: it knows about an order only from contracts and from the orders-api API.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Solution.ContextNames), MemberType = typeof(Solution))]
    public void Context_references_only_building_blocks_and_itself(string context)
    {
        var own = Solution.Contexts[context];
        var violations = own
            .Select(Solution.Load)
            .SelectMany(assembly => Solution.ShopFlowReferences(assembly).Select(reference => (Assembly: assembly.GetName().Name, Reference: reference)))
            .Where(pair => !Solution.BuildingBlocks.Contains(pair.Reference) && !own.Contains(pair.Reference))
            .Select(pair => $"{pair.Assembly} → {pair.Reference}")
            .ToList();

        violations.ShouldBeEmpty();
    }

    /// <remarks>Shared libraries must not know about any service; otherwise they stop being shared.</remarks>
    [Fact]
    public void Building_blocks_do_not_depend_on_any_service()
    {
        var violations = Solution.BuildingBlocks
            .Select(Solution.Load)
            .SelectMany(assembly => Solution.ShopFlowReferences(assembly).Select(reference => $"{assembly.GetName().Name} → {reference}"))
            .Where(edge => !Solution.BuildingBlocks.Any(block => edge.EndsWith("→ " + block, StringComparison.Ordinal)))
            .ToList();

        violations.ShouldBeEmpty();
    }

    /// <remarks>
    /// WHY: the contracts package goes into every service. Each of its dependencies (Azure SDK, Microsoft.Extensions,
    /// SharedKernel) forces a version on all consumers and couples their update cycles. A contract is a data schema, so the
    /// standard library is enough for it.
    /// </remarks>
    [Fact]
    public void Contracts_depend_only_on_the_base_class_library()
    {
        var references = Solution.Load("ShopFlow.Contracts").GetReferencedAssemblies().Select(reference => reference.Name!).ToList();

        references.ShouldAllBe(name => name.StartsWith("System", StringComparison.Ordinal) || name == "netstandard" || name == "mscorlib");
    }

    [Fact]
    public void Shared_kernel_depends_on_nothing_from_shopflow()
    {
        Solution.ShopFlowReferences(Solution.Load("ShopFlow.SharedKernel")).ShouldBeEmpty();
    }

    /// <remarks>
    /// WHY: a public adapter invites calling it directly, bypassing the port (e.g. from an API endpoint). Only DI registration
    /// methods and options types are public; the rest of Infrastructure is an implementation detail.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Solution.Infrastructures), MemberType = typeof(Solution))]
    public void Infrastructure_adapters_of_application_ports_are_internal(string assemblyName)
    {
        var applicationAssembly = $"{Solution.ContextPrefix(assemblyName)}.Application";

        var publicAdapters = Solution.Load(assemblyName).GetExportedTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => type.GetInterfaces().Any(contract => contract.Assembly.GetName().Name == applicationAssembly))
            .Select(type => type.FullName)
            .ToList();

        publicAdapters.ShouldBeEmpty();
    }

    /// <remarks>
    /// WHY: a message handler is an entry point called by the infrastructure, not a base class. Inheriting from a handler
    /// would hide which logic actually handles a given contract.
    /// </remarks>
    [Fact]
    public void Message_handlers_are_sealed()
    {
        var open = Solution.ServiceAssemblies
            .SelectMany(SafeTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false, IsSealed: false })
            .Where(type => type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IMessageHandler<>)))
            .Select(type => type.FullName)
            .ToList();

        open.ShouldBeEmpty();
    }

    /// <remarks>
    /// WHY: a domain event is a fact that has already happened. A record (value equality) and no inheritance make it
    /// impossible to change after it is raised or to swap it for a subclass with extra behaviour.
    /// </remarks>
    [Fact]
    public void Domain_events_are_sealed_records()
    {
        var invalid = Solution.ServiceAssemblies
            .SelectMany(SafeTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IDomainEvent).IsAssignableFrom(type))
            .Where(type => !type.IsSealed || type.GetMethod("<Clone>$") is null)
            .Select(type => type.FullName)
            .ToList();

        invalid.ShouldBeEmpty();
    }

    /// <remarks>
    /// WHY: a control test for the rules themselves. If NetArchTest did not load the types (e.g. after a change in how the
    /// build works), every "should not depend on X" rule would pass green without checking anything.
    /// </remarks>
    [Fact]
    public void Rules_can_see_real_dependencies()
    {
        Types.InAssembly(Solution.Load("ShopFlow.Orders.Infrastructure"))
            .That().HaveDependencyOn("Microsoft.Azure.Cosmos")
            .GetTypes()
            .ShouldNotBeEmpty();

        Solution.ServiceAssemblies.SelectMany(SafeTypes)
            .Count(type => typeof(IDomainEvent).IsAssignableFrom(type) && !type.IsInterface)
            .ShouldBeGreaterThan(10);
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly) => assembly.GetTypes().Where(type => !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false));

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? string.Empty : "Naruszenia: " + string.Join(", ", result.FailingTypeNames ?? []);
}
