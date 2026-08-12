# LogRadar

LogRadar is a structured-log ingestion and query service built with ASP.NET Core, RabbitMQ, and PostgreSQL. It accepts batched logs, validates each entry independently, writes asynchronously with PostgreSQL binary `COPY`, supports cursor-based search and time-bucketed aggregation, and removes expired logs automatically.

## Start the service

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

## API reference

### `POST /logs`

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

Valid `level` values are `debug`, `info`, `warn`, and `error`. `timestamp`, `service`, and `message` are required; timestamps cannot be more than five minutes in the future. `attributes` is optional, but when supplied must be a flat object containing only strings, numbers, and booleans.

The maximum request size is 4 MiB. Valid-entry responses are `200`; an entirely rejected batch, malformed JSON, or invalid top-level body returns `400`.

### `GET /logs`

All filters are optional and may be combined.

```powershell
curl.exe "http://localhost:8080/logs?service=loadtest&level=info&attr.host=loadgen&q=synthetic%20log&limit=100"
```

| Parameter | Meaning |
|---|---|
| `service` | Exact service match |
| `level` | Exact log-level match |
| `since` | Inclusive ISO-8601 start timestamp |
| `until` | Exclusive ISO-8601 end timestamp |
| `attr.<key>` | Attribute equality, compared as text |
| `q` | Case-insensitive message substring |
| `limit` | 1–1000; defaults to 100 |
| `cursor` | Opaque cursor from the prior response |

Results are ordered by timestamp descending, then ID descending. `next_cursor` is `null` when no more rows exist.

### `GET /logs/aggregate`

`since`, `until`, and `bucket` are required. Supported bucket values are `1m`, `5m`, `1h`, and `1d`. Optional filters are the same as log search except pagination; `group_by` may be `service` or `level`.

```powershell
# Count load-generator logs in a two-second sample window
curl.exe "http://localhost:8080/logs/aggregate?since=2026-08-12T15%3A41%3A03.529%2B00%3A00&until=2026-08-12T15%3A41%3A05.529%2B00%3A00&bucket=1m&service=loadtest&level=info&attr.host=loadgen"

# Group counts by service
curl.exe "http://localhost:8080/logs/aggregate?since=2026-08-12T15%3A41%3A03.529%2B00%3A00&until=2026-08-12T15%3A41%3A05.529%2B00%3A00&bucket=1m&group_by=service"
```

```json
{
  "buckets": [
    {
      "start": "2026-08-12T15:41:00Z",
      "group": "loadtest",
      "count": 600
    }
  ]
}
```

Aggregate rows are sorted by bucket start ascending. Empty buckets may be omitted. When `group_by` is omitted, `group` is `null`.

Both read endpoints return invalid parameters as:

```json
{ "error": "<description>" }
```

## Design

### Data flow

```text
HTTP POST /logs
  -> validate and map each entry once
  -> publish batch to RabbitMQ
  -> MassTransit consumer
  -> PostgreSQL binary COPY

HTTP GET /logs or /logs/aggregate
  -> parameterized Npgsql query
  -> PostgreSQL
```

RabbitMQ separates request acceptance from database persistence. PostgreSQL remains the system of record for all log writes and reads.

### Schema and indexes

The `log` table stores `Id` (identity key), `Timestamp` (`timestamptz`), `Level`, `Service`, `Message`, and nullable `Attributes` (`jsonb`).

`jsonb` supports arbitrary attribute keys while `Attributes ->> key` supplies the required string-based equality semantics for `attr.<key>` filters.

Indexes:

- `(Timestamp DESC, Id DESC)` supports chronological reads and deterministic cursor pagination.
- `(Service ASC, Timestamp DESC, Id DESC)` supports service-filtered reads.

Aggregation is executed in PostgreSQL with `date_bin`. Dynamic group columns are selected exclusively from the validated `service`/`level` allow-list; all user-supplied values are SQL parameters.

### Retention

Retention defaults to 30 days and runs daily at 02:00 UTC:

```json
"Retention": {
  "RetentionDays": 30,
  "RunAtUtc": "02:00:00",
  "DeleteBatchSize": 10000
}
```

The retention worker deletes rows older than the configured cutoff in batches of 10,000. Batching prevents one long-running delete from creating excessive lock pressure or interrupting ingestion. Configuration can be overridden through environment variables, for example `Retention__RetentionDays=14`.

## Performance report

### Test environment

- Local Docker Compose on Windows.
- API: 0.5 CPU, 256 MiB memory limit.
- PostgreSQL 17: 1 CPU, 1 GiB memory limit.
- RabbitMQ 4: 1 CPU, 512 MiB memory limit.
- Load generator: local k6 using `LoadTest/ingest_k6.js` and its `ramping-arrival-rate` scenario.

### Dataset and workload

- Batch size: 150 logs/request.
- Each generated log: UTC timestamp, `info`, `loadtest`, a synthetic message, and `host`, `index`, and `rnd` attributes.
- The aggregate verification window contained 600 persisted logs. The visible query sample contained IDs near 9.1 million; an exact database row count was not recorded.

### Ingestion results

| Peak configured rate | Batch size | Completed requests | Error rate | HTTP p90 | HTTP p95 | Max latency | Whole-run average |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 30,000 logs/s (200 req/s) | 150 | 6,864 | 0% | 597.49 ms | 698.71 ms | 1.08 s | 13,728 logs/s |
| 40,000 logs/s (267 req/s) | 150 | 9,144 | 0% | 1.19 s | 1.41 s | 2.10 s | 18,269 logs/s |

The k6 scenario ramps during its final 35 seconds, from 50% to 100% of the target. The configured values are therefore peak arrival rates, not sustained throughput measurements. Both runs completed without failed HTTP requests and met the script’s p95 threshold of under two seconds.

### Query and aggregation results

Functional aggregation was verified against a two-second load-generator window:

- Unfiltered aggregation returned 600 logs.
- Combined `service`, `level`, and `attr.host` filters returned 600 logs.
- Grouping returned `loadtest: 600` and `info: 600`.
- Exact `attr.index` and message-substring filters each returned one log.

The recorded p90/p95/max figures in the ingestion-results table apply to the `POST /logs` workload. A dedicated query benchmark was not part of these two runs.

### Resource usage

The container limits above were enforced. CPU, memory, RabbitMQ queue depth, and PostgreSQL I/O/cache metrics were not captured during these runs, so no observed resource-use values are claimed.

### Bottlenecks discovered

- Ingestion p95 rose from 698.71 ms at the lower target to 1.41 s at the higher target, which indicates growing queueing/back-pressure in the HTTP → RabbitMQ → consumer → PostgreSQL path.
- The benchmark ramps rather than holds a rate, preventing a sustained-capacity conclusion.
- Arbitrary JSON-attribute filters and `%substring%` message searches have no specialized indexes yet and are expected to be the primary query bottlenecks at larger datasets.

### Optimizations applied

- PostgreSQL binary `COPY` through Npgsql instead of EF Core per-entity inserts.
- RabbitMQ batch buffering and concurrent consumer limits (`PrefetchCount = 32`, `ConcurrentMessageLimit = 8`).
- Single-pass ingestion validation/mapping: one timestamp parse, UTC normalization, and no FluentValidation-result allocation per log.
- 4 MiB request-size bound for the API memory budget.
- Composite indexes aligned with chronological and service-scoped cursor queries.
- PostgreSQL-side `date_bin` aggregation, parameterized queries, async I/O, and forward-only readers.
- Batched daily retention cleanup.
