using LogRadar.Domain.Ingestion;

namespace LogRadar.Infrastructure.Ingestion;

public sealed class IngestBatch
{
    public IngestBatch(IReadOnlyList<LogEntry> logs, long startSequence, long endSequence)
    {
        Logs = logs;
        StartSequence = startSequence;
        EndSequence = endSequence;
    }

    public IReadOnlyList<LogEntry> Logs { get; }
    public long StartSequence { get; }
    public long EndSequence { get; }
}
