using LogRadar.Application.Abstractions;
using LogRadar.Application.Contracts;
using Npgsql;
using NpgsqlTypes;
using System.Text;
using System.Text.Json;

namespace LogRadar.Infrastructure.Persistence.Queries;

public sealed class NpgsqlLogQueryService : ILogQueryService
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlLogQueryService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<LogQueryResult> QueryAsync(
        LogQueryFilter filter,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(
            "SELECT \"Id\", \"Timestamp\", \"Level\", \"Service\", \"Message\", \"Attributes\" " +
            "FROM log WHERE 1=1");

        var parameters = new List<NpgsqlParameter>();
        var paramIndex = 0;

        string NextParamName() => $"p{paramIndex++}";

        if (!string.IsNullOrWhiteSpace(filter.Service))
        {
            var name = NextParamName();
            sql.Append(" AND \"Service\" = @").Append(name);
            parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar) { Value = filter.Service });
        }

        if (!string.IsNullOrWhiteSpace(filter.Level))
        {
            var name = NextParamName();
            sql.Append(" AND \"Level\" = @").Append(name);
            parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar) { Value = filter.Level });
        }

        if (filter.Since.HasValue)
        {
            var name = NextParamName();
            sql.Append(" AND \"Timestamp\" >= @").Append(name);
            parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = filter.Since.Value });
        }

        if (filter.Until.HasValue)
        {
            var name = NextParamName();
            sql.Append(" AND \"Timestamp\" < @").Append(name);
            parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = filter.Until.Value });
        }

        if (!string.IsNullOrWhiteSpace(filter.MessageContains))
        {
            var name = NextParamName();
            sql.Append(" AND \"Message\" ILIKE @").Append(name);
            parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar) { Value = $"%{filter.MessageContains}%" });
        }

        foreach (var (key, value) in filter.AttributeFilters)
        {
            // jsonb ->> extracts the value as text, matching the spec's
            // "compared as strings" requirement for attr.<key> filters.
            var keyName = NextParamName();
            var valueName = NextParamName();

            sql.Append(" AND \"Attributes\" ->> @").Append(keyName).Append(" = @").Append(valueName);

            parameters.Add(new NpgsqlParameter(keyName, NpgsqlDbType.Varchar) { Value = key });
            parameters.Add(new NpgsqlParameter(valueName, NpgsqlDbType.Varchar) { Value = value });
        }

        if (filter.CursorTimestamp.HasValue && filter.CursorId.HasValue)
        {
            var tsName = NextParamName();
            var idName = NextParamName();

            sql.Append(" AND (\"Timestamp\" < @").Append(tsName)
               .Append(" OR (\"Timestamp\" = @").Append(tsName)
               .Append(" AND \"Id\" < @").Append(idName).Append("))");

            parameters.Add(new NpgsqlParameter(tsName, NpgsqlDbType.TimestampTz) { Value = filter.CursorTimestamp.Value });
            parameters.Add(new NpgsqlParameter(idName, NpgsqlDbType.Bigint) { Value = filter.CursorId.Value });
        }

        sql.Append(" ORDER BY \"Timestamp\" DESC, \"Id\" DESC LIMIT @limit");
        var limitName = "limit";
        parameters.Add(new NpgsqlParameter(limitName, NpgsqlDbType.Integer) { Value = filter.Limit + 1 });

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql.ToString(), connection);
        command.Parameters.AddRange(parameters.ToArray());

        var rows = new List<LogQueryResultRow>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            Dictionary<string, object>? attributes = null;

            if (!await reader.IsDBNullAsync(5, cancellationToken))
            {
                var json = reader.GetFieldValue<string>(5);
                attributes = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            }

            rows.Add(new LogQueryResultRow(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                attributes));
        }

        var hasMore = rows.Count > filter.Limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        return new LogQueryResult(rows, hasMore);
    }
}
