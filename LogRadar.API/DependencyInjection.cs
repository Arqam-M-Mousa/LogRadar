using FluentValidation;
using LogRadar.API.Contracts.Logs.LogAggregate;
using LogRadar.API.Contracts.Logs.LogQuery;
using LogRadar.API.Contracts.Logs.Validation;

namespace LogRadar.API;

public static class DependencyInjection
{
    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.AddControllers();
        services.AddValidatorsFromAssemblyContaining<LogInputValidator>();
        services.AddScoped<IValidator<QueryLogsRequest>, QueryLogsRequestValidator>();
        services.AddScoped<IValidator<AggregateLogsRequest>, AggregateLogsRequestValidator>();

        return services;
    }
}
