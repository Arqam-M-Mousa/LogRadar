using LogRadar.Domain.Aggregation;
using LogRadar.Domain.Ingestion;
using Microsoft.Extensions.Hosting;

namespace LogRadar.Infrastructure.Caching;

public sealed class NoopAggregateRollup : IAggregateRollup, IHostedService
{
    public Task AddAsync(IReadOnlyList<LogEntry> logs, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<LogAggregationResult?> TryGetAsync(
        LogAggregationFilter filter,
        CancellationToken cancellationToken) => Task.FromResult<LogAggregationResult?>(null);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
