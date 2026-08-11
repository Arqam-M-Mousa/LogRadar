using FluentValidation;
using LogRadar.API.Contracts.Logs.Validation;

namespace LogRadar.API;

public static class DependencyInjection
{
    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.AddControllers();
        services.AddValidatorsFromAssemblyContaining<LogInputValidator>();

        return services;
    }
}
