namespace LogRadar.Domain.Ingestion;

public interface ILogIngestionService
{
    ValueTask PublishAsync(IReadOnlyList<LogEntry> logs, CancellationToken cancellationToken);
}
