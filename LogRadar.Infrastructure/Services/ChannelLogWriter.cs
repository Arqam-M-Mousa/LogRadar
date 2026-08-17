using LogRadar.Infrastructure.Abstractions;
using LogRadar.Infrastructure.Ingestion;
using LogRadar.Infrastructure.Models;

namespace LogRadar.Infrastructure.Services;

public sealed class ChannelLogWriter : ILogIngestionWriter
{
    private readonly LogIngestionChannel _channel;

    public ChannelLogWriter(LogIngestionChannel channel)
    {
        _channel = channel;
    }

    public ValueTask WriteAsync(LogMessage log, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(log, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken) =>
        _channel.FlushAsync(cancellationToken);
}
