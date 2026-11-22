namespace ShopFlow.Observability;

/// <summary>Health check tags, shared by APIs, containers and function apps.</summary>
public static class HealthTags
{
    /// <summary>Checks of the instance itself (configuration loaded, startup finished).</summary>
    public const string Ready = "ready";

    /// <summary>Checks of dependencies shared by all instances (Cosmos, Service Bus, SQL).</summary>
    public const string Dependencies = "dependencies";
}
