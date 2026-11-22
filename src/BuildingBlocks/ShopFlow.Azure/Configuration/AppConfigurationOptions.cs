namespace ShopFlow.Azure.Configuration;

public sealed class AppConfigurationOptions
{
    public const string SectionName = "AppConfiguration";

    /// <summary>E.g. <c>https://appcs-shopflow-prod-weu.azconfig.io</c>. No value disables App Configuration (tests, local runs).</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The service's key prefix, e.g. <c>Orders:</c>. A service loads shared keys (<c>Shared:</c>)
    /// and its own keys, never keys of other services.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>Environment label (<c>dev</c>, <c>test</c>, <c>prod</c>).</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Sentinel key. Changing this one key forces a reload of the whole configuration.
    /// </summary>
    public string SentinelKey { get; set; } = "Shared:Sentinel";

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan FeatureFlagRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}
