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

namespace LogRadar.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbConfig(configuration);
        services.AddNpgsqlDataSource(configuration.GetConnectionString("DefaultConnection")!);
        services.AddSingleton<NpgsqlLogBulkWriter>();
        services.AddSingleton(new AggregationCache(TimeSpan.FromSeconds(5)));
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
