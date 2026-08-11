using LogRadar.Application.Abstractions;
using LogRadar.Infrastructure.Messaging;
using LogRadar.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LogRadar.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbConfig(configuration);
        services.AddNpgsqlDataSource(configuration.GetConnectionString("DefaultConnection")!);
        services.AddScoped<NpgsqlLogBulkWriter>();
        services.AddLogRadarMessaging(configuration);
        services.AddHealthChecks()
            .AddDbContextCheck<LogRadarDbContext>();

        return services;
    }

    private static IServiceCollection AddLogRadarMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var rabbitMqOptions = configuration
            .GetSection(RabbitMqOptions.SectionName)
            .Get<RabbitMqOptions>() ?? new RabbitMqOptions();

        services.AddMassTransit(busConfig =>
        {
            busConfig.AddConsumer<LogBatchConsumer>();

            busConfig.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(rabbitMqOptions.Host, "/", h =>
                {
                    h.Username(rabbitMqOptions.Username);
                    h.Password(rabbitMqOptions.Password);
                });

                cfg.ReceiveEndpoint("log-batch-consumer", e =>
                {
                    e.PrefetchCount = 32;
                    e.ConcurrentMessageLimit = 8;

                    e.ConfigureConsumer<LogBatchConsumer>(context);
                });
            });
        });

        services.AddScoped<ILogBatchPublisher, MassTransitLogBatchPublisher>();

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


}
