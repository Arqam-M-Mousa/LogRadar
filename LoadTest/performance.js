// Performance / load benchmark for the log-ingestion service.
//
// PREREQUISITE: run seed-history.js once against the target environment
// first. That populates ~1M historical rows spread across HISTORY_DAYS, so
// the queries below actually hit a real-sized table instead of only the
// handful of rows this script itself just wrote.
//
// Usage: k6 run performance.js \
//   -e BASE_URL=http://localhost:8080 \
//   -e SCENARIO=load|stress|spike|breakpoint \
//   [-e BATCH=33] [-e MAX_VUS=70] [-e BEARER=token] \
//   [-e HISTORY_DAYS=30] [-e AGG_WINDOW_MIN=60] [-e QUERY_WINDOW_MIN=10]
//
// Scenarios (stages of target logs/sec):
//   load:        120s @ 15000
//   stress:       30s @ 15000, 60s @ 22500, 60s @ 30000
//   spike:        30s @  7500, 10s @ 30000, 60s @  7500
//   breakpoint:   30s @ 15000, 30s @ 22500, 30s @ 30000, 30s @ 45000
//
// What each scenario tests:
//   ingest            - live write throughput (unchanged from before)
//   aggregator        - aggregate query over a random window inside the
//                        seeded HISTORY_DAYS range (was: last 60s only)
//   freshnessReaders  - write-then-immediately-read-it-back, checks that new
//                        writes become queryable fast (unchanged)
//   historyReaders    - point query (service+level+time range) against a
//                        random window inside the 1M+ row seeded table -
//                        this is the "is it still fast at real scale" check
//
// Emits per-5s-bucket accepted-log counters (accepted_bucket_0..63) that the
// worker turns into a throughput series, plus latency/error metrics.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend, Rate } from 'k6/metrics';

const BASE = `${__ENV.BASE_URL ?? 'http://localhost:8080'}`.replace(/\/$/, '');
const SCENARIO = __ENV.SCENARIO ?? 'load';
const BATCH = parseInt(__ENV.BATCH ?? '33', 10);
const BEARER = __ENV.BEARER || '';
// Foothill calibration: cap the ingest VU pool. The reference load generator
// sustains ~450-520 req/s for a fast service and ~120 req/s for a slow one -
// both consistent with a ~70-VU pool whose achieved rate is VUs / avg response
// time (70/0.13s ~= 540 req/s fast, 70/0.58s ~= 120 req/s slow). Without the
// cap (our former 4000 VUs) the harness pushes services to raw capacity,
// which the real benchmark does not.
const MAX_VUS = parseInt(__ENV.MAX_VUS ?? '70', 10);

// Must match what you passed to seed-history.js, or these windows land
// outside the seeded range and queries mostly come back empty.
const HISTORY_DAYS = parseInt(__ENV.HISTORY_DAYS ?? '30', 10);
const AGG_WINDOW_MIN = parseInt(__ENV.AGG_WINDOW_MIN ?? '60', 10);
const QUERY_WINDOW_MIN = parseInt(__ENV.QUERY_WINDOW_MIN ?? '10', 10);
// How long a just-written log has to become queryable to count as "fresh".
const READ_AFTER_WRITE_WINDOW_MS = parseInt(__ENV.READ_AFTER_WRITE_WINDOW_SEC ?? '20', 10) * 1000;
// How often to re-check for the written log while inside the window above.
// The service ingests via a buffered channel + periodic flush (see
// IngestionOptions.FlushIntervalMs), so a log is *expected* to take a few
// tens of ms to become visible even when everything is healthy. Polling
// instead of checking once is what actually tests "does it show up within
// the window", rather than "is it visible with zero delay".
const READ_AFTER_WRITE_POLL_MS = parseInt(__ENV.READ_AFTER_WRITE_POLL_MS ?? '200', 10);

const STAGES = {
    load: [{ duration: '120s', target: 15000 }],
    stress: [
        { duration: '30s', target: 15000 },
        { duration: '60s', target: 22500 },
        { duration: '60s', target: 30000 },
    ],
    spike: [
        { duration: '30s', target: 7500 },
        { duration: '10s', target: 30000 },
        { duration: '60s', target: 7500 },
    ],
    breakpoint: [
        { duration: '30s', target: 15000 },
        { duration: '30s', target: 22500 },
        { duration: '30s', target: 30000 },
        { duration: '30s', target: 45000 },
    ],
};

const stages = STAGES[SCENARIO];
if (!stages) throw new Error(`unknown scenario: ${SCENARIO}`);
// Optional overrides for quick/calibration runs.
const overrideRate = __ENV.TARGET_LOG_RATE ? parseInt(__ENV.TARGET_LOG_RATE, 10) : null;
const overrideDur = __ENV.DURATION_SEC ? `${__ENV.DURATION_SEC}s` : null;
const stagesFinal = overrideRate || overrideDur
    ? [{ duration: overrideDur ?? stages[0].duration, target: overrideRate ?? stages[0].target }]
    : stages;
const totalSec = stagesFinal.reduce((acc, s) => acc + parseDuration(s.duration), 0);
const targetRates = stagesFinal.map((s) => s.target);

const headers = {
    'Content-Type': 'application/json',
    ...(BEARER ? { Authorization: `Bearer ${BEARER}` } : {}),
};

export const options = {
    scenarios: {
        ingest: {
            executor: 'ramping-arrival-rate',
            startRate: Math.round(stagesFinal[0].target / BATCH),
            timeUnit: '1s',
            preAllocatedVUs: Math.min(MAX_VUS, 50),
            maxVUs: MAX_VUS,
            stages: stagesFinal.map((s) => ({ duration: s.duration, target: Math.round(s.target / BATCH) })),
            exec: 'ingest',
        },
        aggregator: {
            executor: 'constant-arrival-rate',
            rate: 1,
            timeUnit: '1s',
            duration: `${totalSec}s`,
            preAllocatedVUs: 1,
            maxVUs: 2,
            exec: 'aggregate',
        },
        freshnessReaders: {
            executor: 'constant-arrival-rate',
            rate: Math.max(5, Math.round(stagesFinal[0].target / BATCH / 30)),
            timeUnit: '1s',
            duration: `${totalSec}s`,
            // Each iteration can now poll for up to READ_AFTER_WRITE_WINDOW_MS
            // instead of returning after one immediate check, so it needs a
            // much bigger VU pool to sustain the same arrival rate: worst
            // case (every iteration polls the full window) is
            // rate * (window in seconds) concurrent iterations.
            preAllocatedVUs: 40,
            maxVUs: 400,
            gracefulStop: '35s',
            exec: 'readAfterWrite',
        },
        historyReaders: {
            executor: 'constant-arrival-rate',
            rate: Math.max(5, Math.round(stagesFinal[0].target / BATCH / 30)),
            timeUnit: '1s',
            duration: `${totalSec}s`,
            preAllocatedVUs: 20,
            maxVUs: 100,
            exec: 'historicalQuery',
        },
    },
    discardResponseBodies: false,
    // "Should be fast" as an actual pass/fail bar instead of just eyeballing
    // the summary. Tune these to your real SLA.
    thresholds: {
        historical_query_latency: ['p(95)<300', 'p(99)<800'],
        aggregate_latency: ['p(95)<500'],
        read_after_write_success: ['rate>0.95'],
    },
};

const ingestionLatency = new Trend('ingestion_latency', true);
const aggregateLatency = new Trend('aggregate_latency', true);
const readLatency = new Trend('read_latency', true);
const historicalQueryLatency = new Trend('historical_query_latency', true);
const readAfterWriteSuccess = new Rate('read_after_write_success');
const logVisibilityLatency = new Trend('log_visibility_latency', true);
const readAfterWritePollCount = new Trend('read_after_write_poll_count');
const acceptedCounter = new Counter('accepted_logs');
const rejectedCounter = new Counter('rejected_logs');
const errors = new Counter('http_errors');
const aggErrors = new Counter('aggregate_errors');
const historicalQueryErrors = new Counter('historical_query_errors');
const bucketCounters = [];
for (let i = 0; i < 64; i++) {
    bucketCounters.push(new Counter(`accepted_bucket_${i}`));
}

const SERVICES = [
    'checkout', 'auth', 'payments', 'inventory', 'search', 'notifications',
    'gateway', 'catalog', 'orders', 'users', 'cart', 'billing',
];
const LEVELS = ['debug', 'info', 'warn', 'error'];
const REGIONS = ['us-east', 'us-west', 'eu-west', 'eu-central', 'ap-south', 'ap-northeast'];
const MESSAGES = [
    'request handled', 'payment declined', 'cache miss', 'db query slow',
    'user logged in', 'order created', 'item out of stock', 'retry scheduled',
    'job completed', 'timeout waiting for upstream', 'rate limited', 'session expired',
    'webhook delivered', 'index refreshed', 'queue depth high', 'config reloaded',
];

const startTs = Date.now();
const HISTORY_MS = HISTORY_DAYS * 24 * 60 * 60 * 1000;

function bucketIndex() {
    return Math.min(63, Math.floor((Date.now() - startTs) / 5000));
}

function makeBatch(n) {
    const logs = [];
    const now = Date.now();
    for (let i = 0; i < n; i++) {
        const ts = new Date(now - Math.floor(Math.random() * 5000)).toISOString();
        logs.push({
            timestamp: ts,
            level: LEVELS[Math.floor(Math.random() * LEVELS.length)],
            service: SERVICES[Math.floor(Math.random() * SERVICES.length)],
            message: MESSAGES[Math.floor(Math.random() * MESSAGES.length)],
            attributes: {
                user_id: String(Math.floor(Math.random() * 1000000)),
                region: REGIONS[Math.floor(Math.random() * REGIONS.length)],
                retries: Math.floor(Math.random() * 5),
                request_id: `req-${Math.random().toString(36).slice(2, 10)}`,
            },
        });
    }
    return logs;
}

// Picks a window of `windowMs` fully inside the seeded [-HISTORY_MS, 0]
// range (relative to test start), anchored at a uniformly random point. This
// is what makes queries actually land on the 1M+ historical rows instead of
// only ever the last few seconds of live-ingested data.
function randomHistoricalRange(windowMs) {
    const maxAgo = HISTORY_MS;
    const minAgo = windowMs;
    const ago = minAgo + Math.random() * (maxAgo - minAgo);
    const since = new Date(startTs - ago);
    const until = new Date(startTs - ago + windowMs);
    return { since: since.toISOString(), until: until.toISOString() };
}

export function ingest() {
    const batch = makeBatch(BATCH);
    const res = http.post(`${BASE}/logs`, JSON.stringify({ logs: batch }), {
        headers,
        timeout: '60s',
    });
    let accepted = 0;
    try {
        const body = res.json();
        accepted = typeof body.accepted === 'number' ? body.accepted : 0;
    } catch {
        accepted = 0;
    }
    if (accepted > 0) {
        acceptedCounter.add(accepted);
        bucketCounters[bucketIndex()].add(accepted);
    }
    const rejected = res.status === 200 ? BATCH - accepted : BATCH;
    if (rejected > 0) rejectedCounter.add(rejected);
    if (res.status !== 200) errors.add(1);
    ingestionLatency.add(res.timings.duration);
    check(res, { 'ingest status 200': (r) => r.status === 200 });
}

export function aggregate() {
    const { since, until } = randomHistoricalRange(AGG_WINDOW_MIN * 60 * 1000);
    const url = `${BASE}/logs/aggregate?since=${encodeURIComponent(since)}&until=${encodeURIComponent(until)}&bucket=1m&group_by=service`;
    const res = http.get(url, { headers, timeout: '60s' });
    aggregateLatency.add(res.timings.duration);
    if (res.status !== 200) aggErrors.add(1);
    check(res, { 'aggregate status 200': (r) => r.status === 200 });
}

// Point query against the seeded historical table: random service + level +
// a random time window somewhere in the last HISTORY_DAYS. This is the core
// "is it still fast once there are 1M+ rows" check - unlike aggregate(),
// it exercises the same filtered-lookup path a real user-facing search does.
export function historicalQuery() {
    const service = SERVICES[Math.floor(Math.random() * SERVICES.length)];
    const level = LEVELS[Math.floor(Math.random() * LEVELS.length)];
    const { since, until } = randomHistoricalRange(QUERY_WINDOW_MIN * 60 * 1000);
    const url = `${BASE}/logs?service=${encodeURIComponent(service)}` +
        `&level=${encodeURIComponent(level)}&since=${encodeURIComponent(since)}` +
        `&until=${encodeURIComponent(until)}&limit=100`;
    const res = http.get(url, { headers, timeout: '60s' });
    historicalQueryLatency.add(res.timings.duration);
    if (res.status !== 200) historicalQueryErrors.add(1);
    check(res, { 'historical query status 200': (r) => r.status === 200 });
}

// Read-after-write: write a uniquely-marked log, then poll for that exact
// record until it shows up or the SLA window elapses. `read_after_write_success`
// is the fraction of writes that become readable within the window (a direct
// freshness / eventual consistency proxy under load). This mirrors the
// foothill read-after-write workload, which correlates with EC health, rather
// than merely checking that "any" records exist for a service.
//
// IMPORTANT: this deliberately polls instead of checking once. The service
// ingests through a bounded channel + periodic batch flush (see
// IngestionOptions.FlushIntervalMs) and explicitly does NOT make writes
// durable/visible synchronously - POST /logs returns as soon as a log is
// accepted onto the in-process buffer, not once it has been written to
// PostgreSQL (see README, "Design" section). A single check fired
// immediately after the POST response races the flush interval and will
// fail almost every time even when the pipeline is completely healthy -
// that's a mismatch between this check and the system's own design, not a
// production bug. Polling for up to READ_AFTER_WRITE_WINDOW_MS is what
// actually answers "does it become visible within the window", and
// log_visibility_latency below tells you the real distribution of how long
// that takes - watch that trend for genuine regressions/backlog under load.
export function readAfterWrite() {
    const service = SERVICES[Math.floor(Math.random() * SERVICES.length)];
    const marker = `raw-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
    const log = {
        timestamp: new Date().toISOString(),
        level: LEVELS[Math.floor(Math.random() * LEVELS.length)],
        service,
        message: marker,
        attributes: {
            user_id: String(Math.floor(Math.random() * 1000000)),
            region: REGIONS[Math.floor(Math.random() * REGIONS.length)],
            retries: 0,
            request_id: `req-${marker}`,
        },
    };
    const writeStart = Date.now();
    const write = http.post(`${BASE}/logs`, JSON.stringify({ logs: [log] }), {
        headers,
        timeout: '60s',
    });

    if (write.status !== 200) {
        readAfterWriteSuccess.add(false);
        check(write, { 'write status 200': (r) => r.status === 200 });
        return;
    }

    const deadline = writeStart + READ_AFTER_WRITE_WINDOW_MS;
    let found = false;
    let lastRes;
    let polls = 0;

    while (Date.now() < deadline) {
        polls++;
        const until = new Date().toISOString();
        const since = new Date(Date.now() - READ_AFTER_WRITE_WINDOW_MS).toISOString();
        const url = `${BASE}/logs?service=${encodeURIComponent(service)}` +
            `&q=${encodeURIComponent(marker)}&since=${encodeURIComponent(since)}` +
            `&until=${encodeURIComponent(until)}&limit=100`;
        lastRes = http.get(url, { headers, timeout: '60s' });
        readLatency.add(lastRes.timings.duration);

        if (lastRes.status === 200) {
            try {
                const body = lastRes.json();
                found = Array.isArray(body.logs) && body.logs.some((l) => l.message === marker);
            } catch {
                found = false;
            }
        }

        if (found) break;

        const remaining = deadline - Date.now();
        if (remaining <= 0) break;
        sleep(Math.min(READ_AFTER_WRITE_POLL_MS, remaining) / 1000);
    }

    readAfterWritePollCount.add(polls);
    if (found) logVisibilityLatency.add(Date.now() - writeStart);

    readAfterWriteSuccess.add(found);
    check(lastRes, { 'read status 200': (r) => r && r.status === 200 });
}

function parseDuration(d) {
    const m = /^(\d+)s$/.exec(d);
    if (m) return parseInt(m[1], 10);
    throw new Error(`bad duration: ${d}`);
}