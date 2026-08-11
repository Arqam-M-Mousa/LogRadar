using LogRadar.Application.Contracts;
using Npgsql;

namespace LogRadar.Infrastructure.Messaging;

public sealed class NpgsqlLogBulkWriter
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlLogBulkWriter(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task WriteAsync(
        IReadOnlyList<LogMessage> logs,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY log (\"Timestamp\", \"Level\", \"Service\", \"Message\", \"Attributes\") FROM STDIN (FORMAT BINARY)",
            cancellationToken);

        foreach (var log in logs)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(log.Timestamp, NpgsqlTypes.NpgsqlDbType.TimestampTz, cancellationToken);
            await writer.WriteAsync(log.Level, NpgsqlTypes.NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(log.Service, NpgsqlTypes.NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(log.Message, NpgsqlTypes.NpgsqlDbType.Varchar, cancellationToken);

            if (log.Attributes is null)
                await writer.WriteNullAsync(cancellationToken);
            else
                await writer.WriteAsync(
                    System.Text.Json.JsonSerializer.Serialize(log.Attributes),
                    NpgsqlTypes.NpgsqlDbType.Jsonb,
                    cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }
}