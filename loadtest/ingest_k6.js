import http from 'k6/http';
import { check } from 'k6';
import { Rate } from 'k6/metrics';

export let errorRate = new Rate('errors');

const baseUrl = __ENV.TARGET_URL || 'http://localhost:8080';
const endpoint = __ENV.ENDPOINT || '/logs';
const batchSize = __ENV.BATCH_SIZE ? parseInt(__ENV.BATCH_SIZE) : 100;
const logsPerSecTarget = __ENV.LOGS_PER_SEC ? parseInt(__ENV.LOGS_PER_SEC) : 15000;
const requestsPerSecTarget = Math.ceil(logsPerSecTarget / batchSize);

export const options = {
    scenarios: {
        ingest: {
            executor: 'ramping-arrival-rate',
            startRate: 0,
            timeUnit: '1s',
            preAllocatedVUs: Math.max(100, requestsPerSecTarget * 2),
            maxVUs: Math.max(200, requestsPerSecTarget * 4),
            stages: [
                { duration: '15s', target: Math.max(5, Math.floor(requestsPerSecTarget * 0.05)) }, // warm-up: low, sustained
                { duration: '8s', target: Math.max(5, Math.floor(requestsPerSecTarget * 0.1)) },
                { duration: '7s', target: Math.floor(requestsPerSecTarget * 0.5) },
                { duration: '35s', target: requestsPerSecTarget }, // sustained
                { duration: '10s', target: 0 },
            ],
        },
    },
    thresholds: {
        errors: ['rate<0.01'],
        http_req_duration: ['p(95)<2000'],
    },
};
function makeLog(i) {
    return {
        timestamp: new Date().toISOString(),
        level: 'info',
        service: 'loadtest',
        message: `synthetic log ${i}`,
        attributes: { host: 'loadgen', index: i, rnd: Math.random() },
    };
}

export default function () {
    const batch = [];
    const base = Math.floor(Math.random() * 1e9);
    for (let i = 0; i < batchSize; i++) batch.push(makeLog(base + i));

    const res = http.post(baseUrl + endpoint, JSON.stringify({ logs: batch }), {
        headers: { 'Content-Type': 'application/json' },
        timeout: '10s',
    });

    const ok = res.status === 200 || res.status === 201;
    check(res, { 'status ok': () => ok });
    errorRate.add(ok ? 0 : 1);
}