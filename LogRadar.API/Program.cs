using FluentValidation;
using LogRadar.API.Contracts.Logs;
using LogRadar.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddValidatorsFromAssemblyContaining<AggregateLogsRequestValidator>();
builder.Services.AddScoped<IValidator<QueryLogsRequest>, QueryLogsRequestValidator>();
builder.Services.AddScoped<IValidator<AggregateLogsRequest>, AggregateLogsRequestValidator>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

await app.Services.ApplyDatabaseMigrationsAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
