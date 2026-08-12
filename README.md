# LogRadar

LogRadar is a high-throughput structured-log service built with ASP.NET Core, RabbitMQ, and PostgreSQL. It accepts batches of logs, persists them asynchronously, supports filtered cursor-based reads and time-bucketed aggregation, and applies a configurable retention policy.

## Run locally

Prerequisites: Docker Desktop with Compose v2.

```powershell
docker compose up --build
```

The API is available at `http://localhost:8080`. The API container listens on port 8080, applies EF Core migrations before accepting requests, and exposes PostgreSQL and RabbitMQ health-gated startup dependencies.

```powershell
curl.exe http://localhost:8080/health
```

To stop the stack while preserving the database volume:

```powershell
docker compose down
```

## API

### `POST /logs`

The endpoint always accepts a batch. Valid entries are accepted even when other entries in the same batch are invalid.

```powershell
curl.exe -X POST http://localhost:8080/logs `
  -H "Content-Type: application/json" `
  -d '{"logs":[{"timestamp":"2026-08-12T10:00:00Z","level":"error","service":"checkout","message":"payment declined","attributes":{"user_id":"42","region":"eu-west","retries":3}}]}'
```

Successful responses return `200`; a fully rejected batch returns `400`.

```json
{
  "accepted": 1,
  "rejected": []
}
```

Validation rules:

- `timestamp` is required, must include an ISO-8601 time component, and cannot be more than five minutes in the future.
- `level` is `debug`, `info`, `warn`, or `error`.
- `service` and `message` must contain non-whitespace text.
- `attributes`, when supplied, must be a flat JSON object; values may be strings, numbers, or booleans.
- Request bodies are limited to 4 MiB to protect the API’s 256 MiB memory allocation. Larger requests return `413 Payload Too Large`.

### `GET /logs`

All filters are optional and combinable.

```powershell
curl.exe "http://localhost:8080/logs?service=checkout&level=error&since=2026-08-12T10%3A00%3A00Z&until=2026-08-12T11%3A00%3A00Z&attr.region=eu-west&q=declined&limit=100"
```

Parameters: `service`, `level`, `since` (inclusive), `until` (exclusive), `attr.<key>`, `q`, `limit` (default 100, maximum 1000), and `cursor`.

Results are sorted by timestamp descending and then ID descending. `next_cursor` is an opaque value and is `null` when there are no further results.

### `GET /logs/aggregate`

`since`, `until`, and `bucket` are required. Valid buckets are `1m`, `5m`, `1h`, and `1d`. Optional filters are `service`, `level`, `attr.<key>`, `q`, and `group_by` (`service` or `level`).

```powershell
# Basic aggregation
curl.exe "http://localhost:8080/logs/aggregate?since=2026-08-12T10%3A00%3A00Z&until=2026-08-12T11%3A00%3A00Z&bucket=1m"

# Group by service
curl.exe "http://localhost:8080/logs/aggregate?since=2026-08-12T10%3A00%3A00Z&until=2026-08-12T11%3A00%3A00Z&bucket=5m&group_by=service"

# Combine filters
curl.exe "http://localhost:8080/logs/aggregate?since=2026-08-12T10%3A00%3A00Z&until=2026-08-12T11%3A00%3A00Z&bucket=1m&service=checkout&level=error&attr.region=eu-west&q=declined"
```

```json
{
  "buckets": [
    { "start": "2026-08-12T10:00:00Z", "group": "checkout", "count": 118 }
  ]
}
```

Bucket rows are ordered by start time ascending. Empty buckets may be omitted. Without `group_by`, `group` is `null`.

For invalid query parameters, both read endpoints return `400` with:

```json
{ "error": "<description>" }
```

## Architecture and data flow

```text
POST /logs
  -> single-pass validation and mapping
  -> RabbitMQ message
  -> MassTransit consumer
  -> Npgsql binary COPY
  -> PostgreSQL log table

GET /logs or /logs/aggregate
  -> parameterized Npgsql query
  -> PostgreSQL
```

RabbitMQ decouples API acknowledgement from database work. PostgreSQL remains the source of truth for writes and reads.

## Storage, schema, and indexes

Logs are stored in PostgreSQL table `log` with:

- `Id` (`bigint` identity primary key)
- `Timestamp` (`timestamptz`)
- `Level` (`varchar(5)`)
- `Service` and `Message` (`text`)
- `Attributes` (`jsonb`, nullable)

Attributes use `jsonb` because keys are arbitrary while filters require string equality. Queries use `Attributes ->> key`, which extracts JSON values as text and therefore matches the API contract for `attr.<key>`.

Current indexes:

- `(Timestamp DESC, Id DESC)` for chronological queries and deterministic cursor pagination.
- `(Service ASC, Timestamp DESC, Id DESC)` for service-scoped reads.

Aggregation uses PostgreSQL `date_bin` and parameterized predicates. Dynamic grouping is selected only from the validated allow-list (`Service` or `Level`); all user values are parameters.

## Performance optimizations

### Ingestion

- PostgreSQL binary `COPY` via `NpgsqlLogBulkWriter` is used instead of EF Core inserts. This avoids change tracking, per-row SQL generation, and one round-trip per entity.
- EF Core is retained for schema configuration, migrations, and the database health check—not the ingestion hot path.
- The API publishes batches to RabbitMQ; the consumer persists them independently, smoothing database write pressure.
- The consumer has `PrefetchCount = 32` and `ConcurrentMessageLimit = 8` to process multiple batches concurrently without unbounded dispatch.
- Ingestion validation is a single pass. It parses the timestamp once, validates it, converts it to UTC, and creates the persisted attribute dictionary only after the entry is known valid.
- The previous per-entry FluentValidation result/error allocations are avoided on the hot path. Rejection objects are allocated only for invalid entries.
- The future-timestamp cutoff is computed once per HTTP batch.
- The 4 MiB request limit bounds memory usage for the API container.

### Reads

- Reads and aggregation use `NpgsqlDataSource`, parameterized SQL, async I/O, and forward-only data readers.
- Cursor pagination requests `limit + 1` records to determine whether a next cursor exists without a separate count query.
- Aggregation runs in PostgreSQL, returning only bucket/group/count rows instead of raw log records.

## Retention

Retention is enabled by default:

```json
"Retention": {
  "RetentionDays": 30,
  "RunAtUtc": "02:00:00",
  "DeleteBatchSize": 10000
}
```

`LogRetentionService` runs daily at the configured UTC time. It deletes records older than the cutoff in 10,000-row batches, ordered by timestamp and ID. Batching shortens lock duration and avoids a single large deletion that could disrupt ingestion. All settings can be overridden with normal ASP.NET configuration providers, for example `Retention__RetentionDays=14`.

## Load testing

The included script is [LoadTest/ingest_k6.js](LoadTest/ingest_k6.js).

```powershell
k6 run LoadTest/ingest_k6.js --env LOGS_PER_SEC=30000 --env BATCH_SIZE=150
k6 run LoadTest/ingest_k6.js --env LOGS_PER_SEC=40000 --env BATCH_SIZE=150
```

Environment used: local Docker Compose, API constrained to 0.5 CPU / 256 MiB and PostgreSQL constrained to 1 CPU / 1 GiB. Batches contained 150 logs; each synthetic log had three attributes.

| Configured peak arrival rate | Batch | Completed requests | Errors | HTTP p95 | Whole-run average request rate | Whole-run average log rate |
|---:|---:|---:|---:|---:|---:|---:|
| 30,000 logs/s (200 req/s) | 150 | 6,864 | 0% | 698.71 ms | 91.52 req/s | 13,728 logs/s |
| 40,000 logs/s (267 req/s) | 150 | 9,144 | 0% | 1.41 s | 121.79 req/s | 18,269 logs/s |

The current k6 script ramps from 50% to 100% during its final 35-second stage. Therefore, the configured 30k/40k values are peak arrival rates, not sustained rates for the full stage; the whole-run averages are reported above to avoid overstating throughput. Both supplied runs completed all iterations without HTTP failures and met the script’s `p(95) < 2s` threshold.

For a true sustained-rate measurement, change the final stage to hold the target rate instead of ramping to it, then capture API/PostgreSQL CPU, memory, database row count, and aggregation latency under concurrent ingestion.

## Known limitations and next measurements

- No million-row aggregation benchmark has been recorded yet.
- `q` (`ILIKE '%...%'`) and arbitrary JSON attribute predicates do not currently have specialized indexes. Add indexes only after examining the real workload with `EXPLAIN (ANALYZE, BUFFERS)`; likely options include `pg_trgm` for message search and a GIN JSONB index for broadly used attribute predicates.
- RabbitMQ acknowledgement and database persistence are asynchronous. A successful ingestion response means the batch was accepted for delivery; it becomes queryable after consumer processing.
- The current load test measures successful HTTP ingestion but should be extended to verify eventual database row counts and concurrent aggregation p95.
- The application build currently emits an Npgsql warning for `GlobalTypeMapper.EnableDynamicJson`; migrate to data-source-specific JSON configuration in a future cleanup.
