using LogRadar.Domain.Aggregation;
using LogRadar.Domain.Ingestion;

namespace LogRadar.Infrastructure.Caching;

public interface IAggregateRollup
{
    Task AddAsync(IReadOnlyList<LogEntry> logs, CancellationToken cancellationToken);

    Task<LogAggregationResult?> TryGetAsync(
        LogAggregationFilter filter,
        CancellationToken cancellationToken);
}
