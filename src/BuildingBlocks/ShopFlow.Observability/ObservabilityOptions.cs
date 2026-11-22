namespace ShopFlow.Observability;

public sealed class ObservabilityOptions
{
    /// <summary>
    /// Application Insights connection string.
    /// </summary>
    /// <remarks>
    /// AZURE: despite its name it is not a secret. It contains the instrumentation key (a resource identifier) and
    /// the ingestion endpoint address. Bicep sets <c>DisableLocalAuth = true</c> on the Application Insights
    /// resource: telemetry without an Entra ID token is rejected, so knowing the connection string alone lets nobody
    /// inject fake data. The value uses the standard variable name <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>,
    /// which the Azure Functions host reads as well.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Fraction of traces kept (0–1).
    /// </summary>
    /// <remarks>
    /// AZURE: Application Insights is billed by the amount of data in Log Analytics. Sampling is the main cost
    /// lever for telemetry (see <c>docs/cost.md</c>). The sampling decision is based on the trace identifier, so a
    /// distributed trace is kept whole in all services or not at all. Metrics are not sampled.
    /// </remarks>
    public float SamplingRatio { get; set; } = 1.0f;

    /// <summary>E.g. <c>dev</c>, <c>prod</c>. Goes to the <c>deployment.environment.name</c> attribute.</summary>
    public string Environment { get; set; } = "local";

    /// <summary>Deployment version (build number). Lets KQL compare errors before and after a deployment.</summary>
    public string? ServiceVersion { get; set; }
}
