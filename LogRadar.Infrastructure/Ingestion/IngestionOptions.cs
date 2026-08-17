namespace LogRadar.Infrastructure.Ingestion;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";
    public int ChannelCapacity { get; init; } = 200_000;
    public int MaxBatchSize { get; init; } = 2_000;
    public int FlushIntervalMs { get; init; } = 50;
    public int WriterConcurrency { get; init; } = 2;
}
