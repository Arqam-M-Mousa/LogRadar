using LogRadar.Domain.Ingestion;

namespace LogRadar.Infrastructure.Ingestion;

public sealed class IngestBatch
{
    public IngestBatch(IReadOnlyList<LogEntry> logs)
    {
        Logs = logs;
    }

    public IReadOnlyList<LogEntry> Logs { get; }
}
