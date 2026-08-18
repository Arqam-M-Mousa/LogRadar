using LogRadar.Domain.Aggregation;
using LogRadar.Domain.Ingestion;
using LogRadar.Domain.Query;
using LogRadar.Infrastructure.Aggregation;
using LogRadar.Infrastructure.Caching;
using LogRadar.Infrastructure.Ingestion;
using LogRadar.Infrastructure.Persistence;
using LogRadar.Infrastructure.Query;
using LogRadar.Infrastructure.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace LogRadar.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbConfig(configuration);
        services.AddNpgsqlPools(configuration);
        services.AddSingleton<NpgsqlLogBulkWriter>();
        services.AddAggregateCaching(configuration);
        services.AddAggregateRollups();
        services.AddScoped<ILogQueryService, NpgsqlLogQueryService>();
        services.AddScoped<ILogAggregationService, NpgsqlLogAggregationService>();
        services.Configure<RetentionOptions>(options =>
            configuration.GetSection(RetentionOptions.SectionName).Bind(options));
        services.AddHostedService<LogRetentionService>();
        services.AddLogIngestion(configuration);
        services.AddHealthChecks()
            .AddDbContextCheck<LogRadarDbContext>();

        return services;
    }

    private static IServiceCollection AddNpgsqlPools(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing.");

        var writeSource = CreateDataSource(connectionString, maxPoolSize: 10, minPoolSize: 2);
        var readSource = CreateDataSource(connectionString, maxPoolSize: 10, minPoolSize: 1);

        services.AddSingleton(new WriteNpgsqlDataSource(writeSource));
        services.AddSingleton(new ReadNpgsqlDataSource(readSource));
        services.AddSingleton(writeSource);

        return services;
    }

    private static NpgsqlDataSource CreateDataSource(string connectionString, int maxPoolSize, int minPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = maxPoolSize,
            MinPoolSize = minPoolSize,
            Multiplexing = false
        };

        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }

    private static IServiceCollection AddAggregateCaching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AggregateCacheOptions>(options =>
            configuration.GetSection(AggregateCacheOptions.SectionName).Bind(options));

        services.AddSingleton<IAggregateCache>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AggregateCacheOptions>>().Value;

            if (!options.RedisEnabled || string.IsNullOrWhiteSpace(options.RedisConnection))
                return new NoopAggregateCache();

            try
            {
                var multiplexer = ConnectionMultiplexer.Connect(new ConfigurationOptions
                {
                    EndPoints = { options.RedisConnection },
                    AbortOnConnectFail = false,
                    ConnectTimeout = 1000,
                    SyncTimeout = 1000,
                    AsyncTimeout = 1000,
                    ConnectRetry = 2
                });

                var logger = sp.GetRequiredService<ILogger<RedisAggregateCache>>();
                return new RedisAggregateCache(
                    multiplexer,
                    sp.GetRequiredService<IOptions<AggregateCacheOptions>>(),
                    logger);
            }
            catch (Exception)
            {
                return new NoopAggregateCache();
            }
        });

        return services;
    }

    private static IServiceCollection AddAggregateRollups(
        this IServiceCollection services)
    {
        services.AddSingleton<IAggregateRollup>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AggregateCacheOptions>>().Value;
            if (!options.RedisEnabled || !options.RollupEnabled || string.IsNullOrWhiteSpace(options.RedisConnection))
                return new NoopAggregateRollup();

            var multiplexer = ConnectionMultiplexer.Connect(new ConfigurationOptions
            {
                EndPoints = { options.RedisConnection },
                AbortOnConnectFail = false,
                ConnectTimeout = 1000,
                SyncTimeout = 1000,
                AsyncTimeout = 1000,
                ConnectRetry = 2
            });

            return new RedisAggregateRollup(
                multiplexer,
                sp.GetRequiredService<IOptions<AggregateCacheOptions>>(),
                sp.GetRequiredService<ILogger<RedisAggregateRollup>>());
        });

        services.AddSingleton<IHostedService>(sp =>
            (IHostedService)sp.GetRequiredService<IAggregateRollup>());

        return services;
    }

    private static IServiceCollection AddLogIngestion(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IngestionOptions>(options =>
            configuration.GetSection(IngestionOptions.SectionName).Bind(options));

        services.AddSingleton<LogIngestionChannel>();
        services.AddSingleton<ILogIngestionService, ChannelLogIngestionService>();
        services.AddHostedService<LogBatchWriterService>();

        return services;
    }

    private static IServiceCollection AddDbConfig(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<LogRadarDbContext>(options =>
        {
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"));
        });

        return services;
    }

    public static async Task ApplyDatabaseMigrationsAsync(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LogRadarDbContext>();
        await dbContext.Database.MigrateAsync();
    }
}
