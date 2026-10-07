import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

test('HTTP budget and request abort settle sync and draft executions without background writes', { timeout: 120000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'elwood-sync-lifetime-'));
  const admin = 'sync-contract-token';
  let child, base, logs = '', writes = 0, retryCalls = 0;
  const upstream = createServer(async (req, res) => {
    for await (const chunk of req) { void chunk; }
    if (req.url === '/retry') { retryCalls++; res.writeHead(503, { 'Content-Type': 'application/json' }); res.end('{}'); }
    else { writes++; /* Simulate a write with no response. Caller cancellation must settle it conservatively. */ }
  });
  upstream.listen(0, '127.0.0.1'); await once(upstream, 'listening');
  const remote = `http://127.0.0.1:${upstream.address().port}`;
  const request = (path, body, signal) => fetch(base + path, { method: body === undefined ? 'GET' : 'POST',
    headers: { Authorization: `Bearer ${admin}`, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body), signal });
  async function until(check) {
    for (let i = 0; i < 150; i++) { const result = await check(); if (result) return result; await delay(50); }
    assert.fail('Condition did not settle: ' + logs);
  }
  function content(id, method) { return { yaml: `name: ${id}
mode: sync
sources:
  - name: input
    trigger: http
    endpoint: /${id}
  - name: remote
    depends: input
    trigger: pull
    retry: { mode: automatic, maxAttempts: 3, delayMs: 10000, maxDelayMs: 10000, allowUncertainWrites: true }
    from:
      http: { url: '${remote}/${method === 'GET' ? 'retry' : 'write'}', method: '${method}' }
    map: '{ remote: $ }'
outputs: [{ name: response, response: true, map: '$' }]
`, scripts: {} }; }
  try {
    await mkdir(join(root, 'pipelines'));
    await writeFile(join(root, 'appsettings.json'), JSON.stringify({ Elwood: { Sync: { ExecutionBudgetSeconds: 2 }, Management: { Tokens: [
      { Name: 'admin', TokenSha256: createHash('sha256').update(admin).digest('hex'), Permissions: ['read', 'write', 'test', 'deploy'], Pipelines: ['*'] }
    ] } } }));
    const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
    base = `http://127.0.0.1:${reservation.address().port}`; await new Promise(resolve => reservation.close(resolve));
    child = spawn('dotnet', [resolve(process.env.ELWOOD_TEST_RUNTIME_DLL ?? 'dotnet/src/Elwood.Runtime.Api/bin/Debug/net10.0/Elwood.Runtime.Api.dll'), '--urls', base], {
      cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, Logging__EventLog__LogLevel__Default: 'None', Elwood__PipelinesDir: join(root, 'pipelines'), Elwood__StateDir: join(root, 'state'),
        Elwood__Management__LocalDevelopment: 'false', Elwood__Durable__WorkerEnabled: 'true', Elwood__AppConfiguration: '', ELWOOD_APP_CONFIGURATION: '', APPLICATIONINSIGHTS_CONNECTION_STRING: '' },
    });
    child.stdout.on('data', data => { logs = (logs + data).slice(-5000); });
    child.stderr.on('data', data => { logs = (logs + data).slice(-5000); });
    const startup = Date.now() + 60000;
    let ready = false;
    while (Date.now() < startup) {
      if (child.exitCode !== null) break;
      try { ready = (await fetch(base + '/api/health', { signal: AbortSignal.timeout(1000) })).ok; } catch { /* starting */ }
      if (ready) break; await delay(100);
    }
    assert.ok(ready, logs);
    for (const [id, method] of [['retry-budget', 'GET'], ['aborted-write', 'POST']]) {
      const created = await request('/api/pipelines/' + id, content(id, method)); assert.equal(created.status, 201);
      const draft = await created.json();
      assert.equal((await request(`/api/pipelines/${id}/deploy`, { expectedRevisionId: draft.draftRevisionId })).status, 200);
    }
    const response = await request('/api/v1/trigger/retry-budget', {});
    assert.equal(response.status, 504); const budget = await response.json();
    assert.match(budget.errors.join(' '), /budget exceeded/);
    const state = await (await request('/api/executions/' + budget.executionId)).json();
    assert.equal(state.status, 3); assert.equal(state.responseStatusCode, 504); assert.ok(state.expiresAt);
    assert.equal(retryCalls, 1);
    for (const isTest of [false, true]) {
      const cancel = new AbortController(); const before = writes;
      const pending = request(isTest ? '/api/executions' : '/api/v1/trigger/aborted-write',
        isTest ? { pipelineId: 'aborted-write', testMode: 'real', payload: {} } : {}, cancel.signal).catch(error => error);
      await until(() => writes === before + 1); cancel.abort(); await pending;
      const settled = await until(async () => {
        const list = await (await request('/api/executions')).json();
        return list.find(e => e.pipelineId === 'aborted-write' && e.isTest === isTest && e.status === 5);
      });
      assert.equal(settled.responseStatusCode, 499);
      assert.equal(settled.sources.remote.outcomeUncertain, true);
      assert.match(settled.errors.join(' '), /canceled/);
    }
    await delay(1200); // Let a recovery tick see all terminal states.
    assert.equal(writes, 2); assert.equal(retryCalls, 1);
  } finally {
    if (child && child.exitCode === null) { const stopped = once(child, 'exit'); child.kill('SIGKILL'); await stopped; }
    upstream.closeAllConnections(); await new Promise(resolve => upstream.close(resolve));
    await rm(root, { recursive: true, force: true });
  }
});
