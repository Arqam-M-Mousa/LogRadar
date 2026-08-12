using LogRadar.Application.Contracts;
using LogRadar.Infrastructure.Persistence.Writers;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace LogRadar.Infrastructure.Messaging;

public sealed class LogBatchConsumer : IConsumer<LogIngestedBatch>
{
    private readonly NpgsqlLogBulkWriter _bulkWriter;
    private readonly ILogger<LogBatchConsumer> _logger;

    public LogBatchConsumer(
        NpgsqlLogBulkWriter bulkWriter,
        ILogger<LogBatchConsumer> logger)
    {
        _bulkWriter = bulkWriter;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<LogIngestedBatch> context)
    {
        var batch = context.Message;

        await _bulkWriter.WriteAsync(batch.Logs, context.CancellationToken);

        _logger.LogInformation(
            "Persisted batch of {Count} logs",
            batch.Logs.Count);
    }
}