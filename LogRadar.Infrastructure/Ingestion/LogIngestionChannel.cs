using LogRadar.Domain.Ingestion;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LogRadar.Infrastructure.Ingestion;
  

public sealed class LogIngestionChannel
{
    private readonly Channel<LogEntry> _channel;
    private readonly ConcurrentQueue<TaskCompletionSource<bool>> _pendingFlushes = new();

    public LogIngestionChannel(IOptions<IngestionOptions> options)
    {
        var opts = options.Value;

        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(Math.Max(1, opts.ChannelCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = opts.WriterConcurrency <= 1,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelWriter<LogEntry> Writer => _channel.Writer;
    public ChannelReader<LogEntry> Reader => _channel.Reader;

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingFlushes.Enqueue(tcs);

        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    public void CompletePendingFlushes()
    {
        while (_pendingFlushes.TryDequeue(out var tcs))
            tcs.TrySetResult(true);
    }
}
