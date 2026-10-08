import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';
import { test } from 'node:test';

const endpoint = process.env.ELWOOD_M6_HOST_URL;
test('Hosted Blob outputs outlive history and the independent timer deletes both run and real-test outputs',
  { skip: !endpoint || !process.env.ELWOOD_M6_TRIGGER_PASSWORD_FILE, timeout: 900_000 }, async () => {
    const base = new URL(endpoint);
    assert.match(base.hostname, /^elwood-m6-api-[a-z0-9]+(?:-[a-z0-9]+)?\.azurewebsites\.net$/);
    assert.equal(base.protocol, 'https:'); assert.equal(base.username + base.password + base.search + base.hash, '');
    const token = (await readFile(process.env.ELWOOD_M6_HOST_TOKEN_FILE, 'utf8')).trim();
    const password = (await readFile(process.env.ELWOOD_M6_TRIGGER_PASSWORD_FILE, 'utf8')).trim();
    const blobUrl = new URL(process.env.ELWOOD_M6_BLOB_URL);
    assert.match(blobUrl.hostname, /^elwoodm6[a-z0-9]+\.blob\.core\.windows\.net$/);
    const account = blobUrl.hostname.split('.')[0];
    const id = 'm6-blob-retention';
    const runObject = 'run-' + randomUUID(); const testObject = 'test-' + randomUUID();
    const cli = process.platform === 'win32'
      ? [process.env.AZURE_CLI_PYTHON ?? 'C:/Program Files/Microsoft SDKs/Azure/CLI2/python.exe', ['-IBm', 'azure.cli']]
      : ['az', []];
    async function versions(name) {
      const { stdout } = await promisify(execFile)(cli[0], [...cli[1], 'storage', 'blob', 'list', '--account-name', account,
        '--container-name', 'contract-outputs', '--prefix', `hostpreview/${name}.json`, '--include', 'vs', '--auth-mode', 'login', '-o', 'json'],
      { timeout: 30_000, maxBuffer: 1024 * 1024 });
      return JSON.parse(stdout).filter(blob => blob.name === `hostpreview/${name}.json`);
    }
    async function request(path, method = 'GET', body, headers = {}) {
      return fetch(new URL(path, base), { method, headers: { Authorization: `Bearer ${token}`,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...headers },
        body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(60_000) });
    }
    async function json(response, expected = 200) {
      assert.equal(response.status, expected, `Unexpected HTTP ${response.status} on ${new URL(response.url).pathname}`);
      return response.json();
    }
    const content = { yaml: `name: ${id}
mode: async
ttlSeconds: 30
sources:
  - name: input
    trigger: http
    endpoint: /${id}
    auth: { type: basic, user: m6-test, password: '$secrets.m6.triggerPassword' }
    map: '{ input: $ }'
outputs:
  - name: snapshot
    map: '$idm.input'
    destinations:
      blobStorage:
        - { accountUrl: '${blobUrl.origin}', container: contract-outputs, filename: 'hostpreview/{$.id}.json', ttlSeconds: 360 }
`, scripts: {} };
    const existing = await request(`/api/pipelines/${id}`);
    const draft = existing.status === 404
      ? await json(await request(`/api/pipelines/${id}`, 'POST', content), 201)
      : await json(await request(`/api/pipelines/${id}`, 'PUT', content,
        { 'If-Match': `"${(await json(existing)).draftRevisionId}"` }));
    const tested = await json(await request('/api/executions', 'POST', { pipelineId: id, testMode: 'real', payload: { id: testObject } }));
    assert.equal(tested.success, true, JSON.stringify(tested.errors));
    await json(await request(`/api/pipelines/${id}/deploy`, 'POST', { expectedRevisionId: draft.draftRevisionId }));
    assert.equal((await request(`/api/v1/trigger/${id}`, 'POST', { id: runObject }, { Authorization: '' })).status, 401);
    const accepted = await json(await request(`/api/v1/trigger/${id}`, 'POST', { id: runObject },
      { Authorization: `Basic ${Buffer.from('m6-test:' + password).toString('base64')}` }), 202);
    const started = Date.now();
    while (true) {
      const state = await json(await request(`/api/executions/${accepted.executionId}`));
      if (state.status === 2) break;
      assert.ok([0, 1, 4].includes(state.status), JSON.stringify(state.errors));
      assert.ok(Date.now() - started < 120_000, 'Hosted output did not complete.'); await delay(1500);
    }
    assert.ok((await versions(runObject)).length > 0); assert.ok((await versions(testObject)).length > 0);
    console.log('Hosted output verification:', JSON.stringify({ executionId: accepted.executionId, runObject, testObject, waitingForIndependentTimer: true }));
    while ((await request(`/api/executions/${accepted.executionId}`)).status !== 404) {
      assert.ok(Date.now() - started < 420_000, 'Execution history was not pruned by the independent timer.'); await delay(5000);
    }
    assert.ok((await versions(runObject)).length > 0, 'Output must outlive its shorter execution history.');
    console.log('History expired; output is still retained.');
    while ((await versions(runObject)).length || (await versions(testObject)).length) {
      assert.ok(Date.now() - started < 840_000, 'Owned output versions did not expire.'); await delay(15_000);
    }
    const health = await json(await request('/api/retention'));
    assert.equal(health.storage.healthy, true); assert.equal(health.policy.error, null);
    console.log('Independent timer removed both outputs and all live versions after history expiry.');
    // Keep this authenticated synthetic definition reusable; the entire lab is disposable.
  });
