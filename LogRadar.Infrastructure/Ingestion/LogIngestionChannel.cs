using LogRadar.Application.Contracts;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace LogRadar.Infrastructure.Ingestion;

public sealed class LogIngestionChannel
{
    private readonly Channel<LogMessage> _channel;

    public LogIngestionChannel(IOptions<IngestionOptions> options)
    {
        var opts = options.Value;

        _channel = Channel.CreateBounded<LogMessage>(new BoundedChannelOptions(Math.Max(1, opts.ChannelCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = opts.WriterConcurrency <= 1,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelWriter<LogMessage> Writer => _channel.Writer;
    public ChannelReader<LogMessage> Reader => _channel.Reader;
}