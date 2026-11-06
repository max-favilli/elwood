import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

// Run only against the consumer's approved installation. Creates one synthetic
// definition/execution, sends no external connector calls, and retires its route.
const base = new URL(process.env.ELWOOD_RUNTIME_URL ?? '');
// CI exercises this exact client against an isolated local runtime. Remote
// installations always require TLS; this opt-in accepts only literal loopback.
const loopbackTest = process.argv.includes('--allow-loopback-http') && base.protocol === 'http:' &&
  ['127.0.0.1', '[::1]'].includes(base.hostname);
if ((!loopbackTest && base.protocol !== 'https:') || base.pathname !== '/' || base.username || base.password || base.search || base.hash)
  throw new Error('ELWOOD_RUNTIME_URL must be an HTTPS origin without credentials or a path.');
const token = process.env.ELWOOD_MANAGEMENT_TOKEN;
if (!token) throw new Error('Set ELWOOD_MANAGEMENT_TOKEN without putting it in command history or Git.');
const distributed = process.argv.includes('--distributed');
const id = 'install-check-' + randomUUID().replaceAll('-', '');
let draft, executionId;
async function request(path, method = 'GET', body, extra = {}) {
  const response = await fetch(new URL(path, base), { method, redirect: 'error',
    headers: { authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }), ...extra },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error(`${method} ${path}: HTTP ${response.status}`);
  if (response.status === 204) return null;
  return response.json();
}
async function until(fn, description, milliseconds = 600000) {
  const end = Date.now() + milliseconds;
  do { const result = await fn(); if (result) return result; await delay(5000); } while (Date.now() < end);
  throw new Error(`Timed out: ${description}`);
}
try {
  await until(async () => {
    const response = await fetch(new URL('/api/readiness', base), { redirect: 'error', signal: AbortSignal.timeout(30000) });
    if (response.status === 503) return false;
    assert.equal(response.status, 200, 'Readiness must return 200'); return true;
  }, 'first healthy native retention sweep');
  await request('/api/session');
  const before = await request('/api/retention'); assert.equal(before.storage.healthy, true);
  const yaml = `name: ${id}\nmode: async\nttlSeconds: 900\nmaxExecutionSeconds: 600\nsources:\n  - { name: input, trigger: http, endpoint: /${id}, map: '{ items: $ }' }\noutputs:\n  - name: result\n${distributed ? "    fanOut: { path: '$idm.items', concurrency: 4 }\n    map: '{ ordinal: $index, value: $ }'\n" : "    map: '$idm.items'\n"}`;
  draft = await request(`/api/pipelines/${id}`, 'POST', { yaml, scripts: {} });
  await request(`/api/pipelines/${id}/deploy`, 'POST', { expectedRevisionId: draft.draftRevisionId });
  ({ executionId } = await request(`/api/v1/trigger/${id}`, 'POST', Array.from({ length: 32 }, (_, i) => i)));
  assert.ok(executionId, 'Durable acceptance must return an execution ID');
  const state = await until(async () => {
    const value = await request(`/api/executions/${executionId}`);
    if ([3, 5, 6].includes(value.status)) throw new Error(`Synthetic execution ${executionId} stopped with status ${value.status}; inspect it in the portal.`);
    return value.status === 2 ? value : null;
  }, 'synthetic execution completion');
  assert.equal(state.revisionId, draft.draftRevisionId);
  if (distributed) {
    const stages = await request(`/api/executions/${executionId}/work-stages`);
    const stage = stages.find(value => value.stepName === 'result');
    assert.equal(stage?.itemCount, 32); assert.equal(stage.status, 'Completed');
    let cursor, count = 0;
    do {
      const page = await request(`/api/executions/${executionId}/work-stages/${stage.stageId}/items?limit=10${cursor ? '&cursor=' + encodeURIComponent(cursor) : ''}`);
      for (const item of page.items) { assert.equal(item.status, 'Completed'); assert.equal(item.attempt, 1); count++; }
      cursor = page.nextCursor;
    } while (cursor);
    assert.equal(count, 32);
    await request(`/api/executions/${executionId}/work-stages/${stage.stageId}/items/0/inspection`);
    assert.equal((await request(`/api/executions/${executionId}/recovery-options`)).replayEnabled, true);
  }
  assert.equal((await request('/api/retention')).storage.healthy, true);
  console.log(JSON.stringify({ result: 'passed', origin: base.origin, distributed, pipelineId: id, executionId,
    revision: draft.draftRevisionId, at: new Date().toISOString(), historyTtlSeconds: 900 }));
} finally {
  if (draft) {
    try { await request(`/api/pipelines/${id}`, 'DELETE', undefined, { 'if-match': `"${draft.draftRevisionId}"` }); }
    catch { console.error(`Cleanup pending: retire synthetic pipeline ${id} in the portal. Execution ${executionId ?? '(not accepted)'} retains its 15-minute history policy.`); process.exitCode = 1; }
  }
}
