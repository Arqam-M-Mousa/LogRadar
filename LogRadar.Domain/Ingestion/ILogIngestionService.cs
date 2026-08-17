namespace LogRadar.Domain.Ingestion;

public interface ILogIngestionService
{
    ValueTask WriteAsync(LogEntry log, CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
}
