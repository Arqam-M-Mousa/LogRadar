using LogRadar.Domain.Ingestion;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace LogRadar.Infrastructure.Ingestion;

public sealed class LogIngestionChannel
{
    private readonly Channel<IngestBatch> _channel;
    private readonly object _sync = new();
    private readonly List<(long Start, long End)> _completedRanges = [];
    private readonly List<(long RequiredSequence, TaskCompletionSource Tcs)> _flushWaiters = [];
    private long _nextSequence;
    private long _enqueuedSequence;
    private long _committedSequence;

    public LogIngestionChannel(IOptions<IngestionOptions> options)
    {
        var opts = options.Value;
        var capacity = Math.Max(1, opts.ChannelCapacity);

        _channel = Channel.CreateBounded<IngestBatch>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = opts.WriterConcurrency <= 1,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<IngestBatch> Reader => _channel.Reader;

    public async ValueTask WriteBatchAsync(IReadOnlyList<LogEntry> logs, CancellationToken cancellationToken)
    {
        if (logs.Count == 0)
            return;

        await _channel.Writer.WaitToWriteAsync(cancellationToken);

        IngestBatch batch;
        lock (_sync)
        {
            var start = _nextSequence + 1;
            var end = _nextSequence + logs.Count;
            _nextSequence = end;
            _enqueuedSequence = end;
            batch = new IngestBatch(logs, start, end);

            if (!_channel.Writer.TryWrite(batch))
            {
                // Extremely unlikely after WaitToWriteAsync; roll sequence forward as failed
                // so flush waiters are not stranded on a hole.
                _committedSequence = Math.Max(_committedSequence, end);
                throw new InvalidOperationException("Failed to enqueue ingest batch.");
            }
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        long required;
        lock (_sync)
        {
            required = _enqueuedSequence;
            if (required <= _committedSequence)
                return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            if (required <= _committedSequence)
                return Task.CompletedTask;

            _flushWaiters.Add((required, tcs));
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(static state =>
            {
                var (source, token) = ((TaskCompletionSource, CancellationToken))state!;
                source.TrySetCanceled(token);
            }, (tcs, cancellationToken));
        }

        return tcs.Task;
    }

    public void NotifyCommitted(long startSequence, long endSequence)
    {
        List<TaskCompletionSource>? ready = null;

        lock (_sync)
        {
            _completedRanges.Add((startSequence, endSequence));
            _completedRanges.Sort(static (a, b) => a.Start.CompareTo(b.Start));

            var index = 0;
            while (index < _completedRanges.Count)
            {
                var range = _completedRanges[index];
                if (range.Start > _committedSequence + 1)
                    break;

                if (range.End > _committedSequence)
                    _committedSequence = range.End;

                index++;
            }

            if (index > 0)
                _completedRanges.RemoveRange(0, index);

            for (var i = _flushWaiters.Count - 1; i >= 0; i--)
            {
                if (_flushWaiters[i].RequiredSequence > _committedSequence)
                    continue;

                ready ??= [];
                ready.Add(_flushWaiters[i].Tcs);
                _flushWaiters.RemoveAt(i);
            }
        }

        if (ready is null)
            return;

        foreach (var tcs in ready)
            tcs.TrySetResult();
    }

    public void NotifyFailed(long startSequence, long endSequence, Exception exception)
    {
        List<TaskCompletionSource>? failed = null;

        lock (_sync)
        {
            if (startSequence <= _committedSequence + 1 && endSequence > _committedSequence)
                _committedSequence = endSequence;

            for (var i = _flushWaiters.Count - 1; i >= 0; i--)
            {
                var required = _flushWaiters[i].RequiredSequence;
                if (required < startSequence || required > endSequence)
                    continue;

                failed ??= [];
                failed.Add(_flushWaiters[i].Tcs);
                _flushWaiters.RemoveAt(i);
            }
        }

        if (failed is null)
            return;

        foreach (var tcs in failed)
            tcs.TrySetException(exception);
    }
}
