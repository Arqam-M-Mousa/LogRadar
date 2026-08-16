using LogRadar.Infrastructure.Abstractions;
using LogRadar.Infrastructure.Contracts;

namespace LogRadar.Infrastructure.Ingestion;

public sealed class ChannelLogWriter : ILogIngestionWriter
{
    private readonly LogIngestionChannel _channel;

    public ChannelLogWriter(LogIngestionChannel channel)
    {
        _channel = channel;
    }

    public ValueTask WriteAsync(LogMessage log, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(log, cancellationToken);
}
