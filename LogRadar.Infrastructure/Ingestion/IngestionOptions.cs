namespace LogRadar.Infrastructure.Ingestion;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Max in-flight log entries buffered in memory before writers push back on POST /logs.</summary>
    public int ChannelCapacity { get; init; } = 200_000;

    /// <summary>Max rows flushed to PostgreSQL in a single COPY BINARY call.</summary>
    public int MaxBatchSize { get; init; } = 2_000;

    /// <summary>How long a writer waits to fill a batch before flushing early.</summary>
    public int FlushIntervalMs { get; init; } = 50;

    /// <summary>Number of concurrent background writers draining the channel into PostgreSQL.</summary>
    public int WriterConcurrency { get; init; } = 2;
}