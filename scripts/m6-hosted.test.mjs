import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { test } from 'node:test';

const endpoint = process.env.ELWOOD_M6_HOST_URL;
test('M6 hosted management, synchronous draft test, durable queue execution, inspection and replay',
  { skip: !endpoint, timeout: 300_000 }, async () => {
    const base = new URL(endpoint);
    assert.match(base.hostname, /^elwood-(?:m6|review)-api-[a-z0-9]+(?:-[a-z0-9]+)?\.azurewebsites\.net$/);
    assert.equal(base.protocol, 'https:'); assert.equal(base.pathname, '/');
    assert.equal(base.username + base.password + base.search + base.hash, '');
    const token = (await readFile(process.env.ELWOOD_M6_HOST_TOKEN_FILE, 'utf8')).trim();
    const id = 'host-' + randomUUID();
    const content = value => ({ yaml: `name: ${id}
mode: async
ttlSeconds: 300
sources:
  - { name: input, trigger: http, endpoint: /${id}, map: '{ input: $ }' }
outputs:
  - { name: result, map: '{ answer: $idm.input.value + ${value} }' }
`, scripts: {} });
    async function request(path, { method = 'GET', body, authenticated = true, headers = {} } = {}) {
      return fetch(new URL(path, base), { method, headers: {
        ...(authenticated ? { Authorization: `Bearer ${token}` } : {}),
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...headers,
      }, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(60_000) });
    }
    async function json(response, expected) {
      assert.equal(response.status, expected, `Unexpected HTTP ${response.status} from ${new URL(response.url).pathname}`);
      return response.json();
    }
    async function status(response, expected) {
      // Drain every response before the next request so Undici can release its
      // connection, including authentication failures and empty responses.
      await response.arrayBuffer();
      assert.equal(response.status, expected, `Unexpected HTTP ${response.status} from ${new URL(response.url).pathname}`);
    }
    async function completed(executionId) {
      const deadline = Date.now() + 120_000;
      while (Date.now() < deadline) {
        const state = await json(await request(`/api/executions/${executionId}`), 200);
        if (state.status === 2) return state;
        assert.ok([0, 1, 4].includes(state.status), `Execution terminated unexpectedly with status ${state.status}`);
        await delay(1500);
      }
      assert.fail('Hosted queue execution did not complete within two minutes.');
    }
    await status(await request('/api/session', { authenticated: false }), 401);
    await status(await request('/api/readiness', { authenticated: false }), Number(process.env.ELWOOD_M6_EXPECT_READINESS ?? 200));
    assert.equal((await json(await request('/api/session'), 200)).name, process.env.ELWOOD_M6_OPERATOR ?? 'operator');
    let revision;
    try {
      const draft = await json(await request(`/api/pipelines/${id}`, { method: 'POST', body: content(1) }), 201);
      revision = draft.draftRevisionId;
      await status(await request(`/api/v1/trigger/${id}`, { method: 'POST', body: { value: 41 }, authenticated: false }), 404);
      const localTest = await json(await request('/api/executions', { method: 'POST', body: {
        pipelineId: id, payload: { value: 41 }, testMode: 'mock', mocks: {},
      } }), 200);
      assert.equal(localTest.success, true); assert.equal(localTest.outputs.result.answer, 42);
      await json(await request(`/api/pipelines/${id}/deploy`, { method: 'POST', body: { expectedRevisionId: revision } }), 200);
      const invoke = () => request(`/api/v1/trigger/${id}`, { method: 'POST', body: { value: 41 }, authenticated: false, headers: { 'Idempotency-Key': id } });
      const acceptance = await json(await invoke(), 202);
      const first = await completed(acceptance.executionId);
      assert.equal(first.revisionId, revision);
      assert.equal((await json(await invoke(), 202)).executionId, first.executionId);
      const inspection = await json(await request(`/api/executions/${first.executionId}/inspection`), 200);
      assert.equal(inspection.outputs.result.value.answer, 42);
      const edited = await json(await request(`/api/pipelines/${id}`, { method: 'PUT', body: content(9), headers: { 'If-Match': `"${revision}"` } }), 200);
      revision = edited.draftRevisionId;
      await json(await request(`/api/pipelines/${id}/deploy`, { method: 'POST', body: { expectedRevisionId: revision } }), 200);
      const replay = await json(await request(`/api/executions/${first.executionId}/replay`, { method: 'POST', body: {} }), 202);
      const replayed = await completed(replay.executionId);
      assert.equal(replayed.replayOf, first.executionId); assert.equal(replayed.revisionId, first.revisionId);
      console.log('Hosted evidence:', JSON.stringify({ pipelineId: id, originalExecution: first.executionId, replayExecution: replayed.executionId,
        input: 41, output: 42, duplicateAcceptedOnce: true, originalRevisionPreserved: true }));
    } finally {
      if (revision) await status(await request(`/api/pipelines/${id}`, { method: 'DELETE', headers: { 'If-Match': `"${revision}"` } }), 204);
      // Execution data has a five-minute TTL and is removed by the independent retention timer.
      // Retired definition IDs remain in the disposable preview catalog until lab teardown.
    }
  });
