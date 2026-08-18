using LogRadar.Domain.Aggregation;
using LogRadar.Domain.Ingestion;
using LogRadar.Domain.Query;
using LogRadar.Infrastructure.Aggregation;
using LogRadar.Infrastructure.Ingestion;
using LogRadar.Infrastructure.Persistence;
using LogRadar.Infrastructure.Query;
using LogRadar.Infrastructure.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddAggregationCache(configuration);
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

    private static IServiceCollection AddAggregationCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AggregationCacheOptions>(options =>
            configuration.GetSection(AggregationCacheOptions.SectionName).Bind(options));

        services.AddSingleton<IAggregationCache>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AggregationCacheOptions>>().Value;

            if (!options.RedisEnabled || string.IsNullOrWhiteSpace(options.RedisConnection))
                return new PassthroughAggregationCache();

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

                var logger = sp.GetRequiredService<ILogger<RedisAggregationCache>>();
                return new RedisAggregationCache(
                    multiplexer,
                    sp.GetRequiredService<IOptions<AggregationCacheOptions>>(),
                    logger);
            }
            catch (Exception)
            {
                return new PassthroughAggregationCache();
            }
        });

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
