// One-time seed script for the log-ingestion service.
//
// Populates the store with a large, realistic historical dataset (default
// 1,000,000 rows spread uniformly across the last 30 days) BEFORE you run
// performance.js. Without this, the aggregate/query endpoints in the load
// test are only ever exercised against a handful of just-written rows -
// which tells you nothing about how the service performs once there's a
// real amount of data behind the index.
//
// Usage:
//   k6 run seed-history.js \
//     -e BASE_URL=http://localhost:8080 \
//     [-e TOTAL_ROWS=1000000] [-e BATCH=500] [-e HISTORY_DAYS=30] \
//     [-e VUS=20] [-e BEARER=token]
//
// Notes:
//   - Uses shared-iterations, so the row count is exact regardless of VUS -
//     raise VUS to seed faster, it won't change how many rows land.
//   - Re-run this whenever you rebuild/wipe the target database.
//   - HISTORY_DAYS here MUST match the value you pass to performance.js,
//     otherwise its "historical" queries will land outside the range you
//     actually seeded and mostly come back empty.

import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const BASE = `${__ENV.BASE_URL ?? 'http://localhost:8080'}`.replace(/\/$/, '');
const TOTAL_ROWS = parseInt(__ENV.TOTAL_ROWS ?? '1000000', 10);
const BATCH = parseInt(__ENV.BATCH ?? '500', 10);
const HISTORY_DAYS = parseInt(__ENV.HISTORY_DAYS ?? '30', 10);
const VUS = parseInt(__ENV.VUS ?? '20', 10);
const BEARER = __ENV.BEARER || '';

const ITERATIONS = Math.ceil(TOTAL_ROWS / BATCH);
const HISTORY_MS = HISTORY_DAYS * 24 * 60 * 60 * 1000;
const now = Date.now();

const headers = {
  'Content-Type': 'application/json',
  ...(BEARER ? { Authorization: `Bearer ${BEARER}` } : {}),
};

export const options = {
  scenarios: {
    seed: {
      executor: 'shared-iterations',
      vus: VUS,
      iterations: ITERATIONS,
      maxDuration: '3h',
      exec: 'seed',
    },
  },
  thresholds: {},
};

const insertedCounter = new Counter('seed_rows_inserted');
const errorCounter = new Counter('seed_errors');

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

function makeBatch(n) {
  const logs = [];
  for (let i = 0; i < n; i++) {
    // Spread rows uniformly across the whole history window, not clustered
    // near "now" - this is what makes the table look aged instead of like a
    // burst of recent inserts.
    const ts = new Date(now - Math.floor(Math.random() * HISTORY_MS)).toISOString();
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

export function seed() {
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
  insertedCounter.add(accepted);
  if (res.status !== 200) errorCounter.add(1);
  check(res, { 'seed insert status 200': (r) => r.status === 200 });
}
