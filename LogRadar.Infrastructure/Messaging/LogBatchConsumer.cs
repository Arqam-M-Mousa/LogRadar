using LogRadar.Application.Contracts;
using LogRadar.Domain.Entities;
using LogRadar.Infrastructure.Persistence;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace LogRadar.Infrastructure.Messaging;

public sealed class LogBatchConsumer : IConsumer<LogIngestedBatch>
{
    private readonly LogRadarDbContext _dbContext;
    private readonly ILogger<LogBatchConsumer> _logger;

    public LogBatchConsumer(
        LogRadarDbContext dbContext,
        ILogger<LogBatchConsumer> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<LogIngestedBatch> context)
    {
        var batch = context.Message;

        var entities = batch.Logs
            .Select(ToEntity)
            .ToList();

        _dbContext.Logs.AddRange(entities);

        await _dbContext.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation(
            "Persisted batch of {Count} logs",
            entities.Count);
    }

    private static Log ToEntity(LogMessage message)
    {
        return new Log
        {
            Timestamp = message.Timestamp,
            Level = Enum.Parse<Domain.Enums.LogLevel>(message.Level, ignoreCase: true),
            Service = message.Service,
            Message = message.Message,
            Attributes = message.Attributes is null
                ? null
                : new Dictionary<string, object>(message.Attributes)
        };
    }
}