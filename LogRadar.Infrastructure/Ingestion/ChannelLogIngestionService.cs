using LogRadar.Domain.Ingestion;

namespace LogRadar.Infrastructure.Ingestion;

public sealed class ChannelLogIngestionService : ILogIngestionService
{
    private readonly LogIngestionChannel _channel;

    public ChannelLogIngestionService(LogIngestionChannel channel)
    {
        _channel = channel;
    }

    public ValueTask PublishAsync(IReadOnlyList<LogEntry> logs, CancellationToken cancellationToken) =>
        _channel.PublishAsync(logs, cancellationToken);
}
