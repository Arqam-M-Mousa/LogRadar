using LogRadar.Domain.Aggregation;
using LogRadar.Domain.Ingestion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Threading.Channels;

namespace LogRadar.Infrastructure.Caching;

public sealed class RedisAggregateRollup : IAggregateRollup, IHostedService
{
    private const int RollupMinutes = 5;
    private readonly IDatabase _database;
    private readonly AggregateCacheOptions _options;
    private readonly ILogger<RedisAggregateRollup> _logger;
    private readonly TimeSpan _retention;
    private readonly Channel<LogEntry[]> _pending = Channel.CreateBounded<LogEntry[]>(new BoundedChannelOptions(512)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private Task? _processingTask;

    public RedisAggregateRollup(
        IConnectionMultiplexer redis,
        IOptions<AggregateCacheOptions> options,
        ILogger<RedisAggregateRollup> logger)
    {
        _database = redis.GetDatabase();
        _options = options.Value;
        _logger = logger;
        _retention = TimeSpan.FromHours(Math.Max(1, _options.RollupRetentionHours));
    }

    public async Task AddAsync(IReadOnlyList<LogEntry> logs, CancellationToken cancellationToken)
    {
        if (logs.Count == 0 || !_options.RollupEnabled)
            return;

        await _pending.Writer.WriteAsync(logs.ToArray(), cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _processingTask = ProcessAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pending.Writer.TryComplete();
        return _processingTask ?? Task.CompletedTask;
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var logs in _pending.Reader.ReadAllAsync(cancellationToken))
                await ApplyAsync(logs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ApplyAsync(IReadOnlyList<LogEntry> logs, CancellationToken cancellationToken)
    {

        var increments = new Dictionary<RedisKey, Dictionary<RedisValue, long>>();

        foreach (var log in logs)
        {
            var bucket = FloorToRollupBucket(log.Timestamp);
            var key = CreateKey(bucket);
            if (!increments.TryGetValue(key, out var fields))
            {
                fields = new Dictionary<RedisValue, long>();
                increments[key] = fields;
            }

            Increment(fields, "total");
            Increment(fields, $"service:{log.Service}");
            Increment(fields, $"level:{log.Level}");
            Increment(fields, $"pair:{log.Service}|{log.Level}");
        }

        try
        {
            var batch = _database.CreateBatch();
            var tasks = new List<Task>(increments.Sum(x => x.Value.Count + 1));

            foreach (var (key, fields) in increments)
            {
                foreach (var (field, count) in fields)
                    tasks.Add(batch.HashIncrementAsync(key, field, count));

                tasks.Add(batch.KeyExpireAsync(key, _retention));
            }

            batch.Execute();
            await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                var deleteTasks = increments.Keys.Select(key => _database.KeyDeleteAsync(key));
                await Task.WhenAll(deleteTasks);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException, "Redis aggregation rollup cleanup failed");
            }

            _logger.LogWarning(ex, "Redis aggregation rollup update failed for {Count} logs", logs.Count);
        }
    }

    public async Task<LogAggregationResult?> TryGetAsync(
        LogAggregationFilter filter,
        CancellationToken cancellationToken)
    {
        if (!_options.RollupEnabled
            || filter.Bucket is not ("5m" or "1h" or "1d")
            || filter.MessageContains is not null
            || filter.AttributeFilters.Count > 0
            || !IsAligned(filter.Since, filter.Bucket)
            || !IsAligned(filter.Until, filter.Bucket))
            return null;

        var outputMinutes = filter.Bucket switch
        {
            "5m" => 5,
            "1h" => 60,
            "1d" => 24 * 60,
            _ => 0
        };

        var keys = new List<RedisKey>();
        for (var cursor = filter.Since; cursor < filter.Until; cursor = cursor.AddMinutes(RollupMinutes))
            keys.Add(CreateKey(cursor));

        if (keys.Count == 0)
            return new LogAggregationResult([]);

        try
        {
            var existsBatch = _database.CreateBatch();
            var existsTasks = keys.Select(key => existsBatch.KeyExistsAsync(key)).ToArray();
            existsBatch.Execute();
            var exists = await Task.WhenAll(existsTasks).WaitAsync(cancellationToken);
            if (exists.Any(value => !value))
                return null;

            var readBatch = _database.CreateBatch();
            var hashTasks = keys.Select(key => readBatch.HashGetAllAsync(key)).ToArray();
            readBatch.Execute();
            var hashes = await Task.WhenAll(hashTasks).WaitAsync(cancellationToken);

            var buckets = new List<LogAggregationBucket>();
            var segmentsPerOutputBucket = outputMinutes / RollupMinutes;
            for (var outputIndex = 0; outputIndex < hashes.Length / segmentsPerOutputBucket; outputIndex++)
            {
                var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                var firstSegment = outputIndex * segmentsPerOutputBucket;
                var lastSegment = firstSegment + segmentsPerOutputBucket;

                for (var segmentIndex = firstSegment; segmentIndex < lastSegment; segmentIndex++)
                {
                    foreach (var (group, count) in ReadBucketCounts(filter, hashes[segmentIndex]))
                        counts[group] = counts.GetValueOrDefault(group) + count;
                }

                var start = filter.Since.AddMinutes(outputIndex * outputMinutes);
                foreach (var (group, count) in counts)
                    buckets.Add(new LogAggregationBucket(start, group == "__total" ? null : group, count));
            }

            buckets.Sort(static (left, right) =>
            {
                var result = left.Start.CompareTo(right.Start);
                return result != 0 ? result : string.CompareOrdinal(left.Group, right.Group);
            });

            return new LogAggregationResult(buckets);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Redis aggregation rollup read failed; falling back to PostgreSQL");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, long> ReadBucketCounts(
        LogAggregationFilter filter,
        HashEntry[] entries)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = (string?)entry.Name;
            if (name is not null)
                values[name] = (long)entry.Value;
        }

        if (filter.GroupBy is null)
        {
            var field = SelectField(filter, null);
            return values.TryGetValue(field, out var count) && count > 0
                ? new Dictionary<string, long> { ["__total"] = count }
                : new Dictionary<string, long>();
        }

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var groupBy = filter.GroupBy!;
        var prefix = groupBy == "service" && filter.Level is not null
            ? "pair:"
            : groupBy == "level" && filter.Service is not null
                ? "pair:"
                : groupBy + ":";
        foreach (var (field, count) in values)
        {
            if (!field.StartsWith(prefix, StringComparison.Ordinal) || count <= 0)
                continue;

            var group = field[prefix.Length..];
            if (groupBy == "service" && filter.Level is not null)
            {
                var separator = group.LastIndexOf('|');
                if (separator < 0 || !string.Equals(group[(separator + 1)..], filter.Level, StringComparison.Ordinal))
                    continue;
                group = group[..separator];
                if (filter.Service is not null && !string.Equals(group, filter.Service, StringComparison.Ordinal))
                    continue;
            }
            else if (groupBy == "level" && filter.Service is not null)
            {
                var separator = group.IndexOf('|');
                if (separator < 0 || !string.Equals(group[..separator], filter.Service, StringComparison.Ordinal))
                    continue;
                group = group[(separator + 1)..];
                if (filter.Level is not null && !string.Equals(group, filter.Level, StringComparison.Ordinal))
                    continue;
            }

            result[group] = result.GetValueOrDefault(group) + count;
        }

        return result;
    }

    private static string SelectField(LogAggregationFilter filter, string? group) =>
        filter.Service is not null && filter.Level is not null
            ? $"pair:{filter.Service}|{filter.Level}"
            : filter.Service is not null
                ? $"service:{filter.Service}"
                : filter.Level is not null
                    ? $"level:{filter.Level}"
                    : "total";

    private RedisKey CreateKey(DateTimeOffset bucket) =>
        _options.RedisKeyPrefix + "rollup:" + bucket.ToUnixTimeSeconds();

    private static DateTimeOffset FloorToRollupBucket(DateTimeOffset timestamp)
    {
        var seconds = timestamp.ToUnixTimeSeconds();
        var bucketSeconds = RollupMinutes * 60;
        return DateTimeOffset.FromUnixTimeSeconds(seconds - (seconds % bucketSeconds));
    }

    private static bool IsAligned(DateTimeOffset timestamp, string bucket) =>
        timestamp.Offset == TimeSpan.Zero
        && timestamp.Second == 0
        && timestamp.Millisecond == 0
        && bucket switch
        {
            "5m" => timestamp.Minute % 5 == 0,
            "1h" => timestamp.Minute == 0,
            "1d" => timestamp.Minute == 0 && timestamp.Hour == 0,
            _ => false
        };

    private static void Increment(Dictionary<RedisValue, long> fields, RedisValue field) =>
        fields[field] = fields.GetValueOrDefault(field) + 1;
}
