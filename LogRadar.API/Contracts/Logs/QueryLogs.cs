using LogRadar.Infrastructure.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogRadar.API.Contracts.Logs;

public sealed class QueryLogsRequest
{
    public string? Service { get; init; }
    public string? Level { get; init; }
    public string? Since { get; init; }
    public string? Until { get; init; }
    public string? Q { get; init; }
    public string? Limit { get; init; }
    public string? Cursor { get; init; }
}

public sealed class QueryLogsResponse
{
    [JsonPropertyName("logs")]
    public required List<QueryLogResultItem> Logs { get; init; }

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}

public sealed class QueryLogResultItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, object>? Attributes { get; init; }
}

public static class QueryLogsMappings
{
    private const string AttributePrefix = "attr.";

    public static LogQueryFilter ToLogQueryFilter(this QueryLogsRequest request, IQueryCollection query)
    {
        LogCursor.TryDecode(request.Cursor, out var cursor);

        var attributeFilters = query
            .Where(parameter => parameter.Key.StartsWith(AttributePrefix, StringComparison.Ordinal))
            .ToDictionary(
                parameter => parameter.Key[AttributePrefix.Length..],
                parameter => parameter.Value.ToString(),
                StringComparer.Ordinal);

        return new LogQueryFilter(
            request.Service,
            request.Level,
            ParseTimestamp(request.Since),
            ParseTimestamp(request.Until),
            request.Q,
            attributeFilters,
            string.IsNullOrWhiteSpace(request.Limit) ? 100 : int.Parse(request.Limit),
            cursor?.Timestamp,
            cursor?.Id);
    }

    public static QueryLogsResponse ToQueryLogsResponse(this LogQueryResult result)
    {
        var logs = result.Logs.Select(log => new QueryLogResultItem
        {
            Id = log.Id.ToString(),
            Timestamp = log.Timestamp,
            Level = log.Level,
            Service = log.Service,
            Message = log.Message,
            Attributes = log.Attributes?.ToDictionary(attribute => attribute.Key, attribute => attribute.Value)
        }).ToList();

        var nextCursor = result.HasMore && result.Logs.Count > 0
            ? new LogCursor(result.Logs[^1].Timestamp, result.Logs[^1].Id).Encode()
            : null;

        return new QueryLogsResponse { Logs = logs, NextCursor = nextCursor };
    }

    private static DateTimeOffset? ParseTimestamp(string? timestamp) =>
        string.IsNullOrWhiteSpace(timestamp) ? null : DateTimeOffset.Parse(timestamp);
}

public sealed record LogCursor(DateTimeOffset Timestamp, long Id)
{
    public string Encode()
    {
        var json = JsonSerializer.Serialize(this);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? value, out LogCursor? cursor)
    {
        cursor = null;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            var base64 = value
                .Replace('-', '+')
                .Replace('_', '/');

            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');

            var bytes = Convert.FromBase64String(base64);
            var json = Encoding.UTF8.GetString(bytes);
            cursor = JsonSerializer.Deserialize<LogCursor>(json);
            return cursor is { Id: > 0 } && cursor.Timestamp != default;
        }
        catch
        {
            return false;
        }
    }
}
