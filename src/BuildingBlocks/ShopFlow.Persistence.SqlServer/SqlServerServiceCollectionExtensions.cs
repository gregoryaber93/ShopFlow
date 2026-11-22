using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ShopFlow.Messaging.Failures;
using ShopFlow.Persistence.SqlServer.Messaging;

namespace ShopFlow.Persistence.SqlServer;

public static class SqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the service's EF Core context with an outbox and inbox in SQL. Requires a prior <c>AddShopFlowMessaging</c>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration with the <c>Sql</c> and <c>Sql:Outbox</c> sections.</param>
    /// <param name="runOutboxPoller">
    /// <c>false</c> for processes that only read (e.g. the catalog API) or must not run background work
    /// (the migration tool). The poller runs in processes that write the outbox.
    /// </param>
    public static IServiceCollection AddShopFlowSqlServer<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        bool runOutboxPoller = true)
        where TContext : MessagingDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SqlServerOptions>()
            .Bind(configuration.GetSection(SqlServerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SqlOutboxOptions>()
            .Bind(configuration.GetSection(SqlOutboxOptions.SectionName))
            .ValidateDataAnnotations();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutboxSignal, OutboxSignal>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFailureClassifier, SqlFailureClassifier>());

        services.AddDbContext<TContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<SqlServerOptions>>().Value;

            builder.UseSqlServer(options.ConnectionString, sql =>
            {
                // AZURE: Azure SQL regularly moves the database to another replica (updates, scaling, hardware
                // failures), which for the application means a few to a dozen seconds of connection errors. This is
                // a normal part of a PaaS service's life, not an outage, so a retry strategy is mandatory. The set of
                // transient codes (40613, 40197, 49918...) is built into SqlServerRetryingExecutionStrategy.
                // WHY: the strategy retries the whole SaveChanges. If the connection drops during the commit, we do
                // not know whether the write succeeded, and the retry may hit an already saved inbox entry. That is
                // exactly the case the inbox is meant to detect: we get DuplicateMessageException and the message is
                // completed correctly.
                // Limitation: with this strategy explicit transactions (BeginTransaction) must not be opened outside
                // CreateExecutionStrategy().ExecuteAsync(...).
                sql.EnableRetryOnFailure(options.MaxRetryCount, options.MaxRetryDelay, errorNumbersToAdd: null);
                sql.CommandTimeout((int)options.CommandTimeout.TotalSeconds);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", MessagingModelBuilderExtensions.Schema);
            });
        });

        services.TryAddScoped<SqlOutboxRelay<TContext>>();

        if (runOutboxPoller)
        {
            services.AddSingleton<IHostedService, SqlOutboxPollerService<TContext>>();
        }

        return services;
    }
}
