using System.ComponentModel.DataAnnotations;

namespace ShopFlow.Persistence.SqlServer;

public sealed class SqlServerOptions
{
    public const string SectionName = "Sql";

    /// <summary>
    /// Connection string without a password.
    /// </summary>
    /// <remarks>
    /// AZURE: example for a user-assigned managed identity:
    /// <code>
    /// Server=tcp:sql-shopflow-prod-weu.database.windows.net,1433;Database=sqldb-inventory;
    /// Authentication=Active Directory Managed Identity;User Id=&lt;identity client id&gt;;
    /// Encrypt=True;Connect Timeout=30
    /// </code>
    /// SqlClient obtains the Entra ID token itself and refreshes it before it expires. The server name is the
    /// public name, but the <c>privatelink.database.windows.net</c> private DNS zone linked to the VNet resolves it
    /// to the private endpoint address. The same connection string therefore works from anywhere in the network,
    /// while the server's public endpoint is disabled.
    /// <para>
    /// Locally: <c>Authentication=Active Directory Default</c> uses the account signed in to the Azure CLI
    /// or Visual Studio.
    /// </para>
    /// </remarks>
    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Timeout of a single SQL command.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times EF Core retries an operation after a transient error.</summary>
    [Range(0, 10)]
    public int MaxRetryCount { get; set; } = 6;

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class SqlOutboxOptions
{
    public const string SectionName = "Sql:Outbox";

    /// <summary>How often the poller checks for pending messages when nobody wakes it up.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    [Range(1, 500)]
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// How long an instance claims a fetched batch. If it does not confirm publication within that time
    /// (e.g. it crashed), another instance takes the batch over.
    /// </summary>
    public TimeSpan ClaimDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>After how many failed attempts a record is no longer fetched and needs intervention.</summary>
    [Range(1, 1000)]
    public int MaxAttempts { get; set; } = 50;

    /// <summary>How long published records are kept (for diagnostics) before the clean-up removes them.</summary>
    public TimeSpan PublishedRetention { get; set; } = TimeSpan.FromDays(3);

    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);
}
