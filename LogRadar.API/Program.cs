using LogRadar.API;
using LogRadar.Infrastructure;
using LogRadar.Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await app.Services.ApplyDatabaseMigrationsAsync();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
