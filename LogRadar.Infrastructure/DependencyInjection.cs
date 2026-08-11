
using LogRadar.Infrastructure.Persistence;
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

        services.AddHealthChecks()
            .AddDbContextCheck<LogRadarDbContext>();

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
