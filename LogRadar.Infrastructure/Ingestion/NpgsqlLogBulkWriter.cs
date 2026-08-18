using LogRadar.Domain.Ingestion;
using LogRadar.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace LogRadar.Infrastructure.Ingestion;

public sealed class NpgsqlLogBulkWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlLogBulkWriter(WriteNpgsqlDataSource writeDataSource)
    {
        _dataSource = writeDataSource.DataSource;
    }

    public async Task WriteAsync(
        IReadOnlyList<LogEntry> logs,
        CancellationToken cancellationToken)
    {
        if (logs.Count == 0)
            return;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY log (\"Timestamp\", \"Level\", \"Service\", \"Message\", \"Attributes\") FROM STDIN (FORMAT BINARY)",
            cancellationToken);

        foreach (var log in logs)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(log.Timestamp, NpgsqlDbType.TimestampTz, cancellationToken);
            await writer.WriteAsync(log.Level, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(log.Service, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(log.Message, NpgsqlDbType.Varchar, cancellationToken);

            if (log.Attributes is null || log.Attributes.Count == 0)
            {
                await writer.WriteNullAsync(cancellationToken);
            }
            else
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(log.Attributes, JsonOptions);
                await writer.WriteAsync(payload, NpgsqlDbType.Jsonb, cancellationToken);
            }
        }

        await writer.CompleteAsync(cancellationToken);
    }
}
