using System.Reflection;

namespace ShopFlow.Architecture.Tests;

/// <summary>Map of the solution: which assemblies belong to which contexts and layers.</summary>
internal static class Solution
{
    public static readonly string[] BuildingBlocks =
    [
        "ShopFlow.SharedKernel",
        "ShopFlow.Contracts",
        "ShopFlow.Messaging",
        "ShopFlow.Messaging.ServiceBus",
        "ShopFlow.Persistence.Cosmos",
        "ShopFlow.Persistence.SqlServer",
        "ShopFlow.Azure",
        "ShopFlow.Resilience",
        "ShopFlow.Observability",
        "ShopFlow.Web",
        "ShopFlow.Functions",
    ];

    /// <summary>Bounded contexts and their assemblies (without hosts).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Contexts = new Dictionary<string, string[]>
    {
        ["Orders"] = ["ShopFlow.Orders.Domain", "ShopFlow.Orders.Application", "ShopFlow.Orders.Infrastructure"],
        ["OrderSaga"] = ["ShopFlow.OrderSaga"],
        ["Inventory"] = ["ShopFlow.Inventory.Domain", "ShopFlow.Inventory.Application", "ShopFlow.Inventory.Infrastructure"],
        ["Payments"] = ["ShopFlow.Payments.Domain", "ShopFlow.Payments.Application", "ShopFlow.Payments.Infrastructure"],
        ["Shipping"] = ["ShopFlow.Shipping.Domain", "ShopFlow.Shipping.Application", "ShopFlow.Shipping.Infrastructure"],
        ["Notifications"] = ["ShopFlow.Notifications.Application", "ShopFlow.Notifications.Infrastructure"],
        ["CustomerReadModels"] = ["ShopFlow.CustomerReadModels.Application", "ShopFlow.CustomerReadModels.Infrastructure"],
        ["Analytics"] = ["ShopFlow.Analytics.Application", "ShopFlow.Analytics.Infrastructure"],
    };

    public static IEnumerable<string> ServiceAssemblyNames => Contexts.Values.SelectMany(names => names);

    public static IEnumerable<Assembly> ServiceAssemblies => ServiceAssemblyNames.Select(Load);

    public static TheoryData<string> Domains => [.. ServiceAssemblyNames.Where(name => name.EndsWith(".Domain", StringComparison.Ordinal))];

    public static TheoryData<string> Applications => [.. ServiceAssemblyNames.Where(name => name.EndsWith(".Application", StringComparison.Ordinal))];

    public static TheoryData<string> Infrastructures => [.. ServiceAssemblyNames.Where(name => name.EndsWith(".Infrastructure", StringComparison.Ordinal))];

    public static TheoryData<string> ContextNames => [.. Contexts.Keys];

    public static Assembly Load(string name) => Assembly.Load(new AssemblyName(name));

    /// <summary>"ShopFlow.Orders.Application" → "ShopFlow.Orders".</summary>
    public static string ContextPrefix(string assemblyName) => assemblyName[..assemblyName.LastIndexOf('.')];

    public static IEnumerable<string> ShopFlowReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("ShopFlow.", StringComparison.Ordinal));
}
