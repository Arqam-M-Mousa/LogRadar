using LogRadar.Infrastructure.Abstractions;
using LogRadar.Infrastructure.Models;
using Npgsql;
using NpgsqlTypes;
using System.Text;
using System.Text.Json;

namespace LogRadar.Infrastructure.Services;

public sealed class NpgsqlLogQueryService : ILogQueryService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly AggregationCache _cache;

    public NpgsqlLogQueryService(NpgsqlDataSource dataSource, AggregationCache cache)
    {
        _dataSource = dataSource;
        _cache = cache;
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

    public async Task<LogAggregationResult> AggregateAsync(
        LogAggregationFilter filter,
        CancellationToken cancellationToken)
    {
        return await _cache.GetOrAddAsync(filter, ExecuteAggregateAsync, cancellationToken);
    }

    private async Task<LogAggregationResult> ExecuteAggregateAsync(
        LogAggregationFilter filter,
        CancellationToken cancellationToken)
    {
        var groupColumn = filter.GroupBy switch
        {
            "service" => "\"Service\"",
            "level" => "\"Level\"",
            _ => null
        };

        var sql = new StringBuilder(
            "SELECT date_bin(@bucket::interval, \"Timestamp\", '2000-01-01T00:00:00Z'::timestamptz) AS \"Start\",");
        sql.Append(groupColumn ?? "NULL::text").Append(" AS \"Group\", COUNT(*) AS \"Count\" ")
           .Append("FROM log WHERE \"Timestamp\" >= @since AND \"Timestamp\" < @until");

        var parameters = new List<NpgsqlParameter>
        {
            new("bucket", NpgsqlDbType.Varchar) { Value = filter.Bucket },
            new("since", NpgsqlDbType.TimestampTz) { Value = filter.Since },
            new("until", NpgsqlDbType.TimestampTz) { Value = filter.Until }
        };
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

        if (!string.IsNullOrWhiteSpace(filter.MessageContains))
        {
            var name = NextParamName();
            sql.Append(" AND \"Message\" ILIKE @").Append(name);
            parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar) { Value = $"%{filter.MessageContains}%" });
        }

        foreach (var (key, value) in filter.AttributeFilters)
        {
            var keyName = NextParamName();
            var valueName = NextParamName();
            sql.Append(" AND \"Attributes\" ->> @").Append(keyName).Append(" = @").Append(valueName);
            parameters.Add(new NpgsqlParameter(keyName, NpgsqlDbType.Varchar) { Value = key });
            parameters.Add(new NpgsqlParameter(valueName, NpgsqlDbType.Varchar) { Value = value });
        }

        sql.Append(" GROUP BY 1");
        if (groupColumn is not null)
            sql.Append(", 2");

        //sql.Append(" ORDER BY 1 ASC");
        //if (groupColumn is not null)
        //    sql.Append(", 2 ASC");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql.ToString(), connection);
        command.Parameters.AddRange(parameters.ToArray());

        var buckets = new List<LogAggregationResultRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buckets.Add(new LogAggregationResultRow(
                reader.GetFieldValue<DateTimeOffset>(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                reader.GetInt64(2)));
        }

        buckets.Sort(static (a, b) =>
        {
            var result = a.Start.CompareTo(b.Start);

            if (result != 0)
                return result;

            return string.CompareOrdinal(a.Group, b.Group);
        });


        return new LogAggregationResult(buckets);
    }
}
