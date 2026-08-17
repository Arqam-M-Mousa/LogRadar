using LogRadar.Domain.Ingestion;

namespace LogRadar.Infrastructure.Ingestion;
  

public sealed class ChannelLogIngestionService : ILogIngestionService
{
    private readonly LogIngestionChannel _channel;

    public ChannelLogIngestionService(LogIngestionChannel channel)
    {
        _channel = channel;
    }

    public ValueTask WriteAsync(LogEntry log, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(log, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken) =>
        _channel.FlushAsync(cancellationToken);
}
