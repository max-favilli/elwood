import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, access, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, basename } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

test('durable HTTP acceptance, process restart, retry, resume, replay and recovery permissions', { timeout: 240000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'elwood-durable-api-'));
  const admin = 'durable-admin';
  let child, base, logs = '';
  let sourceCalls = 0, deliveryCalls = 0, crashCalls = 0;
  let duplicateDeliveries = 0;
  const delivered = [], keys = [];
  const upstream = createServer(async (req, res) => {
    let body = ''; for await (const chunk of req) body += chunk;
    res.setHeader('Content-Type', 'application/json');
    if (req.url.startsWith('/identical-file/')) {
      res.setHeader('ETag', '"' + req.url.split('/').at(-1) + '"'); res.end('{"sku":42}');
    } else if (req.url === '/dedup-delivery') {
      duplicateDeliveries++; res.end('{"accepted":true}');
    } else if (req.url === '/source') {
      sourceCalls++; keys.push(req.headers['idempotency-key']);
      if (sourceCalls === 1) { res.statusCode = 503; res.end('{"error":"temporary"}'); }
      else res.end('{"remoteId":"remote-42"}');
    } else if (req.url === '/delivery') {
      deliveryCalls++; delivered.push(JSON.parse(body));
      res.statusCode = deliveryCalls === 1 ? 503 : 200; res.end('{}');
    } else if (req.url === '/crash') {
      crashCalls++;
      // First write has taken effect, but its response is lost when the API process is killed.
      if (crashCalls > 1) res.end('{"remoteId":"recovered"}');
    } else { res.statusCode = 503; res.end('{}'); }
  });
  upstream.listen(0, '127.0.0.1'); await once(upstream, 'listening');
  const remote = `http://127.0.0.1:${upstream.address().port}`;
  const apiDll = resolve(process.env.ELWOOD_TEST_RUNTIME_DLL ?? 'dotnet/src/Elwood.Runtime.Api/bin/Debug/net10.0/Elwood.Runtime.Api.dll');
  const hash = token => createHash('sha256').update(token).digest('hex');
  async function stop() {
    if (child && child.exitCode === null) { const exited = once(child, 'exit'); child.kill('SIGKILL'); await exited; }
  }
  async function start(enabled) {
    const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
    const port = reservation.address().port; await new Promise(resolve => reservation.close(resolve));
    base = `http://127.0.0.1:${port}`;
    child = spawn('dotnet', [apiDll, '--urls', base], {
      cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, Logging__EventLog__LogLevel__Default: 'None', Elwood__PipelinesDir: join(root, 'pipelines'), Elwood__StateDir: join(root, 'state'),
        Elwood__Durable__WorkerEnabled: String(enabled), Elwood__Management__LocalDevelopment: 'false',
        Elwood__AppConfiguration: '', ELWOOD_APP_CONFIGURATION: '', APPLICATIONINSIGHTS_CONNECTION_STRING: '' },
    });
    child.stdout.on('data', data => { logs = (logs + data).slice(-5000); });
    child.stderr.on('data', data => { logs = (logs + data).slice(-5000); });
    const startupDeadline = Date.now() + 60000;
    while (Date.now() < startupDeadline) {
      try { if ((await fetch(base + '/api/health', { signal: AbortSignal.timeout(1000) })).ok) return; } catch { /* starting */ }
      if (child.exitCode !== null) break;
      await delay(100);
    }
    assert.fail('Runtime did not start: ' + logs);
  }
  const request = (path, method = 'GET', body, token = admin, headers = {}) => fetch(base + path, {
    method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const trigger = (path, key, payload = { id: 42 }) => request('/api/v1/trigger/' + path, 'POST', payload, '', key ? { 'Idempotency-Key': key } : {});
  async function until(predicate, description) {
    for (let i = 0; i < 150; i++) { const result = await predicate(); if (result) return result; await delay(100); }
    assert.fail(description + '\n' + logs);
  }
  async function stateWhen(id, status) {
    let last;
    try {
      return await until(async () => {
        const response = await request('/api/executions/' + id); last = await response.json();
        if ([2, 3, 5, 6].includes(last.status) && last.status !== status)
          assert.fail(`Unexpected terminal execution: ${JSON.stringify(last)}`);
        return last.status === status ? last : null;
      }, `Execution ${id} did not reach status ${status}`);
    } catch (error) { error.message += '\nLast state: ' + JSON.stringify(last); throw error; }
  }
  async function publish(id, content) {
    const response = await request('/api/pipelines/' + id, 'POST', content);
    assert.equal(response.status, 201);
    const draft = await response.json();
    assert.equal((await request(`/api/pipelines/${id}/deploy`, 'POST', { expectedRevisionId: draft.draftRevisionId })).status, 200);
    return draft;
  }
  const content = label => ({ yaml: `name: durable-orders
mode: async
sources:
  - name: input
    trigger: http
    endpoint: /durable-orders
    map: '{ input: $ }'
  - name: remote
    trigger: pull
    depends: input
    retry: { mode: automatic, maxAttempts: 3, delayMs: 100 }
    from:
      http:
        url: ${remote}/source
        method: POST
        headers:
          Idempotency-Key: '{$execution.id}:remote'
        body: '{ id: $idm.input.id }'
    map: '{ remote: $ }'
outputs:
  - name: result
    retry: { mode: manual }
    map: '{ id: $idm.input.id, remoteId: $idm.remote.remoteId, revision: "${label}" }'
    destinations:
      http:
        - url: ${remote}/delivery
`, scripts: {} });
  try {
    await writeFile(join(root, 'appsettings.json'), JSON.stringify({ Logging: { LogLevel: { Default: 'Warning' } }, Elwood: { Management: { Tokens: [
      { Name: 'admin', TokenSha256: hash(admin), Permissions: ['read', 'write', 'test', 'deploy', 'recover'], Pipelines: ['*'] },
      { Name: 'reader', TokenSha256: hash('reader'), Permissions: ['read'], Pipelines: ['durable-orders'] },
      { Name: 'scoped', TokenSha256: hash('scoped'), Permissions: ['read', 'recover'], Pipelines: ['other'] },
    ] } } }));
    // Retention is independent of paused processing workers and covers test records too.
    await mkdir(join(root, 'state', 'documents'), {recursive:true});
    for (const [id, status] of [['expired-test',2], ['active-protected',1]]) {
      await writeFile(join(root, 'state', id+'.json'), JSON.stringify({executionId:id, pipelineId:'durable-orders', pipelineName:'durable-orders', status,
        isTest:true, startedAt:'2026-01-01T00:00:00Z', completedAt: status===2 ? '2026-01-01T00:01:00Z' : null,
        expiresAt:'2026-01-31T00:01:00Z', sources:{}, outputs:{}, errors:[]}));
      await writeFile(join(root, 'state', 'documents', 'exec_'+id+'_trigger'), '{}');
    }
    await start(false);
    const exists = async path => { try { await access(path); return true; } catch (e) { if(e.code==='ENOENT') return false; throw e; } };
    await until(async () => !(await exists(join(root,'state','expired-test.json'))), 'Paused workers must not disable retention');
    assert.equal(await exists(join(root,'state','documents','exec_expired-test_trigger')),false);
    assert.equal(await exists(join(root,'state','active-protected.json')),true);
    assert.equal(await exists(join(root,'state','documents','exec_active-protected_trigger')),true);
    const original = await publish('durable-orders', content('v1'));
    const accepted = await trigger('durable-orders', 'order-42');
    assert.equal(accepted.status, 202);
    const { executionId } = await accepted.json();
    assert.equal(accepted.headers.get('x-elwood-revision'), original.draftRevisionId);
    const pending = await (await request('/api/executions/' + executionId)).json();
    assert.equal(pending.durable, true); assert.equal(pending.status, 1); assert.equal(sourceCalls, 0);
    const duplicate = await trigger('durable-orders', 'order-42'); assert.equal(duplicate.status, 202);
    assert.equal((await duplicate.json()).executionId, executionId);
    assert.equal((await trigger('durable-orders', 'order-42', { id: 43 })).status, 409);

    const updated = await (await request('/api/pipelines/durable-orders', 'PUT', content('v2'), admin, { 'If-Match': `"${original.draftRevisionId}"` })).json();
    assert.equal((await request('/api/pipelines/durable-orders/deploy', 'POST', { expectedRevisionId: updated.draftRevisionId })).status, 200);
    await stop(); await start(true); // No queue survives this restart.
    const paused = await stateWhen(executionId, 5);
    assert.equal(paused.revisionId, original.draftRevisionId);
    assert.equal(paused.sources.remote.attempts.length, 2);
    assert.equal(delivered[0].revision, 'v1');
    assert.equal(keys[0], `${executionId}:remote`); assert.equal(keys[1], keys[0]);
    assert.equal(sourceCalls, 2); assert.equal(deliveryCalls, 1);
    assert.equal((await request(`/api/executions/${executionId}/resume`, 'POST', {}, 'reader')).status, 403);
    assert.equal((await request(`/api/executions/${executionId}/replay`, 'POST', {}, 'scoped')).status, 404);
    assert.equal((await request(`/api/executions/${executionId}/resume`, 'POST', {})).status, 202);
    const resumed = await stateWhen(executionId, 2);
    assert.equal(sourceCalls, 2); assert.equal(deliveryCalls, 2);
    assert.equal(resumed.outputs.result.attempts.length, 2);
    assert.equal((await request(`/api/executions/${executionId}/resume`, 'POST', {})).status, 409);
    assert.equal(Date.parse(resumed.expiresAt) - Date.parse(resumed.completedAt), 30 * 86400000);
    const replay = await (await request(`/api/executions/${executionId}/replay`, 'POST', {})).json();
    const replayed = await stateWhen(replay.executionId, 2);
    assert.equal(replayed.replayOf, executionId); assert.equal(replayed.revisionId, original.draftRevisionId);
    const newer = await (await request(`/api/executions/${executionId}/replay`, 'POST', { revisionId: updated.draftRevisionId })).json();
    await stateWhen(newer.executionId, 2); assert.equal(delivered.at(-1).revision, 'v2');
    assert.equal((await request(`/api/executions/${executionId}/replay`, 'POST', { revisionId: 'not-a-published-revision' })).status, 404);

    const crashContent = content('crash');
    crashContent.yaml = crashContent.yaml.replaceAll('durable-orders', 'crash-orders').replace(`${remote}/source`, `${remote}/crash`);
    await publish('crash-orders', crashContent);
    const crash = await (await trigger('crash-orders')).json();
    await until(() => crashCalls === 1, 'The uncertain write did not start');
    await stop(); await start(true);
    const uncertain = await stateWhen(crash.executionId, 5);
    assert.equal(uncertain.sources.remote.outcomeUncertain, true); assert.equal(crashCalls, 1);
    assert.equal((await request(`/api/executions/${crash.executionId}/replay/`, 'POST', {})).status, 409);
    assert.equal(uncertain.sources.remote.attempts[0].status, 'Interrupted');
    const approvedReplayResponse = await request(`/api/executions/${crash.executionId}/replay`, 'POST', { acknowledgeUncertain: true });
    assert.equal(approvedReplayResponse.status, 202);
    const approvedReplay = await approvedReplayResponse.json();
    assert.equal((await stateWhen(approvedReplay.executionId, 2)).replayOf, crash.executionId);
    assert.equal((await stateWhen(crash.executionId, 5)).sources.remote.outcomeUncertain, true);
    assert.equal(crashCalls, 2);
    assert.equal((await request(`/api/executions/${crash.executionId}/resume`, 'POST', {})).status, 409);
    assert.equal((await request(`/api/executions/${crash.executionId}/resume`, 'POST', { acknowledgeUncertain: true })).status, 202);
    await stateWhen(crash.executionId, 2); assert.equal(crashCalls, 3);

    const failure = { yaml: `name: sync-failure
mode: sync
failureResponse:
  body: '{ failedStep: $error.step, message: "Please try later" }'
  statusCode: '502'
sources:
  - { name: input, trigger: http, endpoint: /sync-failure }
  - name: remote
    trigger: pull
    depends: input
    retry: { maxAttempts: 2, delayMs: 0 }
    from:
      http: { url: '${remote}/always-fail' }
outputs:
  - { name: result, response: true }
`, scripts: {} };
    await publish('sync-failure', failure);
    const response = await trigger('sync-failure');
    assert.equal(response.status, 502); assert.equal((await response.json()).failedStep, 'remote');

    const filePipeline = { yaml: `name: identical-files
mode: sync
deduplication: { mode: skip, source: file }
sources:
  - { name: input, trigger: http, endpoint: /identical-files, map: '{ event: $ }' }
  - name: file
    trigger: pull
    depends: input
    from:
      http: { url: '${remote}/identical-file/{$idm.event.path}' }
    map: '{ file: $ }'
outputs:
  - name: result
    response: true
    map: '$idm.file'
    destinations:
      http:
        - { url: '${remote}/dedup-delivery' }
`, scripts: {} };
    await publish('identical-files', filePipeline);
    const firstFile = await trigger('identical-files', null, { path: 'one.json' });
    assert.equal(firstFile.status, 200); assert.deepEqual(await firstFile.json(), { sku: 42 });
    await stop(); await start(true); // fingerprint history survives the API process
    const repeatedFile = await trigger('identical-files', null, { path: 'renamed.json' });
    assert.equal(repeatedFile.status, 200);
    const skipped = await repeatedFile.json(); assert.equal(skipped.skipped, true);
    assert.equal((await stateWhen(skipped.executionId, 6)).duplicateOf, skipped.duplicateOf);
    assert.equal(duplicateDeliveries, 1);
    const ordinaryReplay = await request(`/api/executions/${skipped.duplicateOf}/replay`, 'POST', {});
    assert.equal(ordinaryReplay.status, 202); await stateWhen((await ordinaryReplay.json()).executionId, 6);
    assert.equal(duplicateDeliveries, 1);
    const forceReplay = await request(`/api/executions/${skipped.executionId}/replay`, 'POST', { forceProcessing: true });
    assert.equal(forceReplay.status, 202);
    const forced = await stateWhen((await forceReplay.json()).executionId, 2);
    assert.equal(forced.forceProcessing, true); assert.equal(forced.replayOf, skipped.executionId);
    assert.equal(duplicateDeliveries, 2);
    const realTest = await request('/api/executions', 'POST', { pipelineId: 'identical-files', payload: { path: 'test.json' }, testMode: 'real' });
    assert.equal(realTest.status, 200); assert.equal((await realTest.json()).success, true);
    const afterTest = await (await trigger('identical-files', null, { path: 'another.json' })).json();
    assert.equal(afterTest.skipped, true); assert.equal(afterTest.duplicateOf, forced.executionId);
    assert.equal(duplicateDeliveries, 3);
  } finally {
    await stop(); upstream.closeAllConnections(); await new Promise(resolve => upstream.close(resolve));
    assert.ok(basename(root).startsWith('elwood-durable-api-'));
    await rm(root, { recursive: true, force: true });
  }
});
