using LogRadar.Domain.Ingestion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogRadar.Infrastructure.Ingestion;
  

public sealed class LogBatchWriterService : BackgroundService
{
    private readonly LogIngestionChannel _channel;
    private readonly NpgsqlLogBulkWriter _bulkWriter;
    private readonly IngestionOptions _options;
    private readonly ILogger<LogBatchWriterService> _logger;

    public LogBatchWriterService(
        LogIngestionChannel channel,
        NpgsqlLogBulkWriter bulkWriter,
        IOptions<IngestionOptions> options,
        ILogger<LogBatchWriterService> logger)
    {
        _channel = channel;
        _bulkWriter = bulkWriter;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = Math.Max(1, _options.WriterConcurrency);
        var workers = Enumerable.Range(0, concurrency)
            .Select(_ => RunWriterLoopAsync(stoppingToken));

        return Task.WhenAll(workers);
    }

    private async Task RunWriterLoopAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        var maxBatchSize = Math.Max(1, _options.MaxBatchSize);
        var flushInterval = TimeSpan.FromMilliseconds(Math.Max(1, _options.FlushIntervalMs));
        var buffer = new List<LogEntry>(maxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            _channel.CompletePendingFlushes();
            buffer.Clear();

            bool canRead;
            try
            {
                canRead = await reader.WaitToReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!canRead)
                return;

            while (buffer.Count < maxBatchSize && reader.TryRead(out var first))
                buffer.Add(first);

            if (buffer.Count < maxBatchSize)
            {
                var deadline = DateTime.UtcNow + flushInterval;

                while (buffer.Count < maxBatchSize)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        break;

                    var waitTask = reader.WaitToReadAsync(stoppingToken).AsTask();
                    var delayTask = Task.Delay(remaining, stoppingToken);
                    var completed = await Task.WhenAny(waitTask, delayTask);

                    if (completed != waitTask)
                        break;

                    bool moreAvailable;
                    try
                    {
                        moreAvailable = await waitTask;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!moreAvailable)
                        break;

                    while (buffer.Count < maxBatchSize && reader.TryRead(out var log))
                        buffer.Add(log);
                }
            }

            if (buffer.Count == 0)
                continue;

            try
            {
                await _bulkWriter.WriteAsync(buffer, stoppingToken);
                _channel.CompletePendingFlushes();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write batch of {Count} logs to PostgreSQL", buffer.Count);
            }
        }
    }
}
