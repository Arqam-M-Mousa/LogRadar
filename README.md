# LogRadar

LogRadar is a structured-log ingestion and query service built with ASP.NET Core and PostgreSQL. It accepts batched logs, validates each entry independently, buffers accepted logs in-process, writes asynchronously with PostgreSQL binary `COPY`, supports cursor-based search and time-bucketed aggregation, and removes expired logs automatically.

## Setup and usage

Prerequisite: Docker Desktop with Docker Compose v2.

```powershell
docker compose up --build
```

The API is exposed at `http://localhost:8080`.

```powershell
curl.exe -i http://localhost:8080/health
```

The health endpoint returns `200` after PostgreSQL is reachable and database migrations have completed.

```powershell
docker compose down
```

## API documentation

### `GET /health`

Returns HTTP 200 once the database connection is established, migrations have been applied, and the service is ready to accept traffic.

### `POST /logs` — Ingest Logs

The endpoint accepts one or more logs. Each entry is validated independently: valid entries are accepted even if other entries in the batch are rejected.

```powershell
curl.exe -X POST "http://localhost:8080/logs" `
  -H "Content-Type: application/json" `
  -d '{"logs":[{"timestamp":"2026-08-12T10:00:00Z","level":"error","service":"checkout","message":"payment declined","attributes":{"user_id":"42","region":"eu-west","retries":3}}]}'
```

```json
{
  "accepted": 1,
  "rejected": []
}
```

**Validation rules:**

| Field | Rules |
|---|---|
| `timestamp` | Required. Valid ISO 8601. Must not be more than 5 minutes in the future. |
| `level` | Required. One of `debug`, `info`, `warn`, `error`. |
| `service` | Required. Non-empty string. |
| `message` | Required. Non-empty string. |
| `attributes` | Optional. Flat object with string, number, or boolean values. Nested objects and arrays are not allowed. |

The maximum request size is 4 MiB. Valid-entry responses return HTTP 200; an entirely rejected batch, malformed JSON, or invalid top-level body returns HTTP 400 with:

```json
{ "error": "<description>" }
```

### `GET /logs` — Query Logs

All filters are optional and may be combined.

```powershell
curl.exe "http://localhost:8080/logs?service=loadtest&level=info&attr.host=loadgen&q=synthetic%20log&limit=100"
```

| Parameter | Meaning | Example |
|---|---|---|
| `service` | Exact service-name match | `service=checkout` |
| `level` | Exact level match | `level=error` |
| `since` | Inclusive start of the time range | `since=2026-07-20T14:00:00Z` |
| `until` | Exclusive end of the time range | `until=2026-07-20T15:00:00Z` |
| `attr.<key>` | Attribute equality, compared as strings | `attr.user_id=42` |
| `q` | Case-insensitive substring match on message | `q=declined` |
| `limit` | Maximum number of results; default 100, max 1000 | `limit=500` |
| `cursor` | Opaque cursor from a prior response | `cursor=eyJpZCI6...` |

Results are sorted by timestamp descending, then ID descending (deterministic). `next_cursor` is `null` when no more rows exist. The cursor is Base64-encoded and opaque.

```json
{
  "logs": [
    {
      "id": "123",
      "timestamp": "2026-08-12T10:00:00Z",
      "level": "error",
      "service": "checkout",
      "message": "payment declined",
      "attributes": { "user_id": "42" }
    }
  ],
  "next_cursor": "eyJpZCI6..."
}
```

### `GET /logs/aggregate` — Aggregate Logs

Returns time-bucketed log counts. `since`, `until`, and `bucket` are required.

```powershell
curl.exe "http://localhost:8080/logs/aggregate?since=2026-08-12T15%3A41%3A00Z&until=2026-08-12T15%3A45%3A00Z&bucket=1m&service=loadtest&level=info"

curl.exe "http://localhost:8080/logs/aggregate?since=2026-08-12T15%3A41%3A00Z&until=2026-08-12T15%3A45%3A00Z&bucket=1m&group_by=service"
```

| Parameter | Required | Meaning | Example |
|---|---|---|---|
| `since` | Yes | Inclusive start of aggregation range | `since=2026-07-20T14:00:00Z` |
| `until` | Yes | Exclusive end of aggregation range | `until=2026-07-20T15:00:00Z` |
| `bucket` | Yes | Bucket size: `1m`, `5m`, `1h`, or `1d` | `bucket=1m` |
| `service` | No | Exact service-name filter | `service=checkout` |
| `level` | No | Exact level filter | `level=error` |
| `attr.<key>` | No | Attribute equality, compared as strings | `attr.user_id=42` |
| `q` | No | Case-insensitive message substring | `q=declined` |
| `group_by` | No | Group results by `service` or `level` | `group_by=service` |

```json
{
  "buckets": [
    { "start": "2026-08-12T15:41:00Z", "group": "loadtest", "count": 600 },
    { "start": "2026-08-12T15:42:00Z", "group": "loadtest", "count": 450 }
  ]
}
```

Results are ordered by bucket start ascending. Empty buckets may be omitted. When `group_by` is not provided, `group` is `null`.

Invalid parameters return HTTP 400:

```json
{ "error": "<description>" }
```

## Schema and index design

### Table: `log`

| Column | Type | Notes |
|---|---|---|
| `Id` | `bigint` | Identity primary key |
| `Timestamp` | `timestamptz` | Required |
| `Level` | `varchar(5)` | Stored as string (`debug`, `info`, `warn`, `error`) |
| `Service` | `text` | Required |
| `Message` | `text` | Required |
| `Attributes` | `jsonb` | Nullable |

### Indexes

- `(Timestamp DESC, Id DESC)` — supports chronological reads and deterministic cursor pagination.
- `(Service ASC, Timestamp DESC, Id DESC)` — supports service-filtered reads with cursor pagination.

### Aggregation

Aggregation is executed in PostgreSQL using `date_bin()` for time bucketing. Dynamic `GROUP BY` columns are selected exclusively from the validated `service`/`level` allow-list. All user-supplied values are parameterized.

Redis is used as a disposable acceleration layer for minute, five-minute, hourly, and daily aggregations grouped or filtered by service and level. PostgreSQL is written first, and rollup updates are processed asynchronously without blocking ingestion. Hourly and daily results are composed by summing minute Redis buckets. Partial minute edges, message or attribute filters, missing or evicted Redis rollups, or expired rollups fall back to PostgreSQL.

## Attribute storage strategy

Attributes are stored as a PostgreSQL `jsonb` column. This provides:

- Schema-free storage of arbitrary key-value pairs per log entry.
- Efficient equality lookups via the `->>` operator, which extracts a JSON key's value as text.
- The API spec requires `attr.<key>` filters to be compared as strings, so `Attributes ->> key = $parameter` provides the exact required semantics without type-coercion surprises.

**Trade-off:** `jsonb` attribute equality requires a sequential scan of the `Attributes` column for each `attr.<key>` filter (no GIN index is used). This is acceptable for the current scale; at higher volumes, a GIN index on `Attributes` or a materialized attributes table would improve filtered-query performance.

## Retention strategy

Logs are retained for a configurable number of days (default 30). A background service runs daily at a configured UTC time (default 02:00) and deletes expired rows in batches.

```json
"Retention": {
  "RetentionDays": 30,
  "RunAtUtc": "02:00:00",
  "DeleteBatchSize": 10000
}
```

Each batch uses `DELETE ... WHERE Id IN (SELECT ... LIMIT)` to avoid long-running locks. The batch size is configurable (default 10,000). Configuration can be overridden via environment variables, for example `Retention__RetentionDays=14`.

## Ingestion pipeline

```text
HTTP POST /logs
  -> validate and map each entry (single pass)
  -> bounded in-process channel (back-pressure when full)
  -> concurrent batch writers
  -> PostgreSQL binary COPY

HTTP GET /logs or /logs/aggregate
  -> parameterized Npgsql query
  -> Redis rollups when supported, otherwise PostgreSQL
```

The bounded in-process channel separates request acceptance from database persistence while applying back-pressure when it reaches capacity. PostgreSQL remains the system of record for all log writes and reads. Because the buffer is process-local, accepted logs waiting in it are not durable until their batch has been written to PostgreSQL.

### Request flow in detail

#### Ingestion

```text
POST /logs
  -> validate every entry
  -> enqueue valid entries
  -> wait for PostgreSQL COPY to complete
  -> enqueue a small Redis rollup update
  -> return HTTP 200
```

The PostgreSQL write happens before the log is acknowledged. Redis rollup processing happens separately and never blocks the request when its queue is full. If Redis is unavailable or falls behind, rollups are disabled for that process and aggregate queries use PostgreSQL instead.

#### Aggregation

```text
GET /logs/aggregate
  -> can Redis answer this filter?
       yes: read minute counters from Redis
            sum them into 5m, 1h, or 1d buckets
            query PostgreSQL only for partial minute edges
       no:  use the exact-result cache
            -> 2-second local cache
            -> in-flight request coalescing
            -> Redis result cache
            -> PostgreSQL aggregate query
```

Redis rollups support service/level filters and do not support message or attribute filters. Redis is an acceleration layer only; PostgreSQL remains authoritative in every path.

### Configuration

```json
"Ingestion": {
  "ChannelCapacity": 50000,
  "MaxBatchSize": 2000,
  "FlushIntervalMs": 50,
  "WriterConcurrency": 3
}
```

Redis rollups are configured separately. Redis is intentionally disposable because PostgreSQL remains the source of truth:

```json
"AggregationCache": {
  "RedisEnabled": true,
  "RollupEnabled": true,
  "RollupRetentionHours": 48
}
```

### Caching classes

Caching code lives under `LogRadar.Infrastructure/Caching`:

- `AggregateCacheOptions` — settings for Redis result caching and rollups.
- `IAggregateCache` — contract for caching a complete aggregate response.
- `RedisAggregateCache` — stores complete fallback responses in Redis, keeps up to 128 exact results locally for 2 seconds, and coalesces identical concurrent requests.
- `NoopAggregateCache` — bypasses result caching when Redis is disabled.
- `IAggregateRollup` — contract for reusable time-bucket counters.
- `RedisAggregateRollup` — asynchronously writes minute counters, composes larger buckets, and combines PostgreSQL partial edges.
- `NoopAggregateRollup` — disables rollups without changing the ingestion or query pipeline.

| Setting | Default | Description |
|---|---|---|
| `ChannelCapacity` | 50000 | Max in-flight logs buffered in memory before back-pressure |
| `MaxBatchSize` | 2000 | Max rows per PostgreSQL binary `COPY` call |
| `FlushIntervalMs` | 10 | How long a writer waits to fill a batch before flushing early |
| `WriterConcurrency` | 2 | Number of concurrent background writers draining the channel |

## Performance results

### Test environment

- Local Docker Compose on Windows.
- API container: 0.5 CPU, 256 MB memory limit.
- PostgreSQL container: 1 CPU, 1 GB memory limit.
- Load generator: local k6 with 70 max VUs for ingestion.
- Database seeded with 1,000,000 historical rows spread across 30 days via `seed-history.js`.

### Pass/fail thresholds

| Metric | Threshold |
|---|---|
| `historical_query_latency` p(95) | < 300 ms |
| `historical_query_latency` p(99) | < 800 ms |
| `aggregate_latency` p(95) | < 500 ms |
| `read_after_write_success` rate | > 95% |

### Scenario results

| Scenario | Target rates | Duration | Total accepted | Avg throughput | Thresholds |
|---|---|---|---|---|---|
| **Load** | 15k/s sustained | 120s | 1,778,205 | 14,789/s | All pass |
| **Stress** | 15k → 22.5k → 30k/s | 150s | 2,512,785 | 16,464/s | Historical queries fail |
| **Spike** | 7.5k → 30k → 7.5k/s | 100s | 1,536,777 | 15,333/s | All pass |
| **Breakpoint** | 15k → 22.5k → 30k → 45k/s | 120s | 1,540,638 | 12,371/s | Aggregate + historical queries fail |

### Latency detail

| Metric | Load (15k/s) | Stress (30k/s peak) | Spike (30k burst) | Breakpoint (45k peak) |
|---|---|---|---|---|
| Ingestion p95 | 69 ms | 9 ms | 2 ms | 273 ms |
| Historical query p95 | 82 ms | 403 ms | 3 ms | 1.21 s |
| Historical query p99 | 247 ms | 1.98 s | 5 ms | 2.28 s |
| Aggregate p95 | 168 ms | 313 ms | 3 ms | 1.35 s |
| Read-after-write success | 100% | 98.48% | 100% | 99.12% |
| Log visibility p95 | 431 ms | 10.79 s | 261 ms | 17.83 s |
| HTTP error rate | 0% | 0.007% | 0% | 0.03% |

### Analysis

**Sustained capacity:** The service sustains 15,000 logs/s with all thresholds passing comfortably. Ingestion p95 stays under 70 ms and queries remain fast.

**Spike resilience:** A 4x burst (7.5k → 30k) is handled cleanly. The bounded channel absorbs the spike without degrading read performance, and the system recovers immediately.

**Breakpoint:** The system breaks between 30,000 and 45,000 sustained logs/s. Under sustained 30k+ writes, PostgreSQL query performance degrades due to I/O contention between the high-throughput COPY writes and concurrent reads. At the 45k stage, both aggregate and historical query p95 latencies exceed thresholds by 2–4x. Ingestion remains reliable (0% rejections) but the read path suffers.

**Bottleneck:** The primary bottleneck is PostgreSQL I/O contention under sustained high write volume. The write path (binary COPY) remains fast, but concurrent reads degrade as the buffer pool and shared_buffers compete with sequential scan pressure from `attr.<key>` and `ILIKE` queries against 1M+ rows.

## Known limitations

- **No durability for in-flight logs:** Accepted logs buffered in the in-process channel are lost if the process crashes before the batch is written to PostgreSQL.
- **No authentication:** The API is open by default. Authentication can be enabled via environment configuration (see Optional Features below).
- **Attribute query performance:** `attr.<key>` filters use sequential `jsonb` scans. No GIN index is maintained on the `Attributes` column, so filtered queries slow down as the attribute cardinality or row count grows.
- **Message search performance:** The `q` parameter uses `ILIKE '%pattern%'`, which cannot use indexes and performs a sequential scan of the `Message` column.
- **No retry on batch write failure:** If a PostgreSQL `COPY` batch fails, the batch is logged and dropped. Failed logs are not retried or persisted to a dead-letter queue.
- **Single retention schedule:** Only one daily retention run is supported. There is no way to trigger ad-hoc retention from the API.
