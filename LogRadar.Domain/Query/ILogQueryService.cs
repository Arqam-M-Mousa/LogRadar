namespace LogRadar.Domain.Query;

public interface ILogQueryService
{
    Task<LogQueryResult> QueryAsync(
        LogQueryFilter filter,
        CancellationToken cancellationToken);
}
