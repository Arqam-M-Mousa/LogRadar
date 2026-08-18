namespace LogRadar.Domain.Ingestion;

public interface ILogIngestionService
{
    ValueTask WriteBatchAsync(IReadOnlyList<LogEntry> logs, CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
}
