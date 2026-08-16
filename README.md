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
  -> PostgreSQL
```

The bounded in-process channel separates request acceptance from database persistence while applying back-pressure when it reaches capacity. PostgreSQL remains the system of record for all log writes and reads. Because the buffer is process-local, accepted logs waiting in it are not durable until their batch has been written to PostgreSQL.

### Configuration

```json
"Ingestion": {
  "ChannelCapacity": 50000,
  "MaxBatchSize": 2000,
  "FlushIntervalMs": 50,
  "WriterConcurrency": 3
}
```

| Setting | Default | Description |
|---|---|---|
| `ChannelCapacity` | 50000 | Max in-flight logs buffered in memory before back-pressure |
| `MaxBatchSize` | 2000 | Max rows per PostgreSQL binary `COPY` call |
| `FlushIntervalMs` | 50 | How long a writer waits to fill a batch before flushing early |
| `WriterConcurrency` | 3 | Number of concurrent background writers draining the channel |

## Performance results

*To be filled in after load testing.*

## Known limitations

- **No durability for in-flight logs:** Accepted logs buffered in the in-process channel are lost if the process crashes before the batch is written to PostgreSQL.
- **No authentication:** The API is open by default. Authentication can be enabled via environment configuration (see Optional Features below).
- **Attribute query performance:** `attr.<key>` filters use sequential `jsonb` scans. No GIN index is maintained on the `Attributes` column, so filtered queries slow down as the attribute cardinality or row count grows.
- **Message search performance:** The `q` parameter uses `ILIKE '%pattern%'`, which cannot use indexes and performs a sequential scan of the `Message` column.
- **No retry on batch write failure:** If a PostgreSQL `COPY` batch fails, the batch is logged and dropped. Failed logs are not retried or persisted to a dead-letter queue.
- **Single retention schedule:** Only one daily retention run is supported. There is no way to trigger ad-hoc retention from the API.

## Optional features

- **Authentication:** Not implemented by default. Set the `AUTH_ENABLED=true` environment variable to enable it. When enabled, the `LOADGEN_API_KEY` environment variable provides the expected API key for the load generator.
