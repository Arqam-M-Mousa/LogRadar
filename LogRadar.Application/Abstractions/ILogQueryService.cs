
using LogRadar.Application.Contracts;

namespace LogRadar.Application.Abstractions;

public interface ILogQueryService
{
    Task<LogQueryResult> QueryAsync(
        LogQueryFilter filter,
        CancellationToken cancellationToken);
}