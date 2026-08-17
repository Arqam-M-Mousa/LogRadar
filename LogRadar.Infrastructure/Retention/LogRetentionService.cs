using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace LogRadar.Infrastructure.Retention;

public sealed class LogRetentionService : BackgroundService
{
    private const string DeleteExpiredLogsSql = """
        DELETE FROM log
        WHERE "Id" IN
        (
            SELECT "Id"
            FROM log
            WHERE "Timestamp" < @cutoff
            ORDER BY "Timestamp", "Id"
            LIMIT @batchSize
        )
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly RetentionOptions _options;
    private readonly ILogger<LogRetentionService> _logger;

    public LogRetentionService(
        NpgsqlDataSource dataSource,
        IOptions<RetentionOptions> options,
        ILogger<LogRetentionService> logger)
    {
        _dataSource = dataSource;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GetDelayUntilNextRun(), stoppingToken);
                await DeleteExpiredLogsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Log retention failed");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var now = DateTimeOffset.UtcNow;
        var runAt = _options.RunAtUtc is { Ticks: >= 0 and < TimeSpan.TicksPerDay }
            ? _options.RunAtUtc
            : TimeSpan.FromHours(2);
        var nextRun = new DateTimeOffset(now.Date.Add(runAt), TimeSpan.Zero);

        if (nextRun <= now)
            nextRun = nextRun.AddDays(1);

        return nextRun - now;
    }

    private async Task DeleteExpiredLogsAsync(CancellationToken cancellationToken)
    {
        var retentionDays = Math.Max(1, _options.RetentionDays);
        var batchSize = Math.Clamp(_options.DeleteBatchSize, 1, 100_000);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var deleted = 0;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        while (true)
        {
            await using var command = new NpgsqlCommand(DeleteExpiredLogsSql, connection);
            command.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = cutoff });
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });

            var deletedInBatch = await command.ExecuteNonQueryAsync(cancellationToken);
            deleted += deletedInBatch;

            if (deletedInBatch < batchSize)
                break;
        }

        _logger.LogInformation("Deleted {Count} logs older than {Cutoff:O}", deleted, cutoff);
    }
}
