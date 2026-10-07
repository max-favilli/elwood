import { createHash } from 'node:crypto';
import { createServer as createHttpServer } from 'node:http';
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { createServer } from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';

test('Runtime API exposes warnings, validation errors and failed execution history', { timeout: 60000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'elwood-api-test-'));
  let child;
  const admin = 'integration-admin-token';
  const reader = 'integration-reader-token';
  const writer = 'integration-writer-token';
  const scoped = 'integration-scoped-token';
  let requests = 0;
  const upstream = createHttpServer((req, res) => {
    requests++;
    res.setHeader('Content-Type', 'application/json');
    res.setHeader('X-Request-Id', 'request-42');
    res.setHeader('Set-Cookie', ['session=private-cookie; HttpOnly', 'other=second-private-cookie']);
    res.end('{"ok":true}');
  });
  upstream.listen(0, '127.0.0.1');
  await once(upstream, 'listening');
  let logs = '';
  try {
    await writeFile(join(root, 'appsettings.json'), JSON.stringify({ Elwood: { Management: { Tokens: [
      { Name: 'admin', TokenSha256: createHash('sha256').update(admin).digest('hex'), Permissions: ['read', 'write', 'test', 'deploy'], Pipelines: ['*'] },
      { Name: 'reader', TokenSha256: createHash('sha256').update(reader).digest('hex'), Permissions: ['read'], Pipelines: ['orders'] },
      { Name: 'writer', TokenSha256: createHash('sha256').update(writer).digest('hex'), Permissions: ['read', 'write', 'test'], Pipelines: ['orders'] },
      { Name: 'write-only', TokenSha256: createHash('sha256').update('write-only').digest('hex'), Permissions: ['write'], Pipelines: ['orders'] },
      { Name: 'tester', TokenSha256: createHash('sha256').update('test-only').digest('hex'), Permissions: ['test'], Pipelines: ['orders'] },
      { Name: 'scoped', TokenSha256: createHash('sha256').update(scoped).digest('hex'), Permissions: ['read', 'write', 'test', 'deploy'], Pipelines: ['another'] },
    ] }, SecretGrants: { orders: { Run: ['allowed'] } } } }));
    const pipelines = join(root, 'pipelines');
    const dir = join(pipelines, 'regression');
    await mkdir(dir, { recursive: true });
    await writeFile(join(dir, 'pipeline.elwood.yaml'), `name: regression
sources:
  - name: input
    trigger: http
    map: broken.elwood
outputs:
  - name: result
    response: true
`);
    await writeFile(join(dir, 'broken.elwood'), '{ broken:');
    await writeFile(join(dir, 'warning.elwood'), 'let n = $.rows[*] | where r => !($.list[*] | any x => x == r) | count\nreturn n');
    await writeFile(join(dir, 'literal.elwood'), '{ message: "return shipment" }');
    const portReservation = createServer();
    portReservation.listen(0, '127.0.0.1');
    await once(portReservation, 'listening');
    const port = portReservation.address().port;
    await new Promise(resolve => portReservation.close(resolve));
    const base = `http://127.0.0.1:${port}`;
    child = spawn('dotnet', [resolve(process.env.ELWOOD_TEST_RUNTIME_DLL ?? 'dotnet/src/Elwood.Runtime.Api/bin/Debug/net10.0/Elwood.Runtime.Api.dll'), '--urls', base], {
      cwd: root, windowsHide: true,
      env: { ...process.env, Elwood__PipelinesDir: pipelines, Elwood__StateDir: join(root, 'state'),
        Elwood__Management__LocalDevelopment: 'false', ELWOOD_SECRET_ALLOWED: `http://127.0.0.1:${upstream.address().port}`, Elwood__AppConfiguration: '', ELWOOD_APP_CONFIGURATION: '', APPLICATIONINSIGHTS_CONNECTION_STRING: '' },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    child.on('error', error => { logs += error.message; });
    child.stdout.on('data', chunk => { logs = (logs + chunk).slice(-8000); });
    child.stderr.on('data', chunk => { logs = (logs + chunk).slice(-8000); });
    let ready = false;
    for (let i = 0; i < 100; i++) {
      if (child.exitCode !== null) break;
      try { ready = (await fetch(`${base}/api/health`)).ok; } catch { /* starting */ }
      if (ready) break;
      await delay(100);
    }
    assert.ok(ready, `API did not start: ${logs}`);
    const post = (path, data) => fetch(base + path, { method: 'POST', headers: {
      Authorization: `Bearer ${admin}`, 'Content-Type': 'application/json', Origin: 'http://localhost:3000',
    }, body: JSON.stringify(data) });

    const literal = await post('/api/pipelines/regression/scripts/literal.elwood/test', {});
    assert.equal(literal.status, 200);
    assert.deepEqual(await literal.json(), { message: 'return shipment' });

    const warning = await post('/api/pipelines/regression/scripts/warning.elwood/test', {
      rows: Array.from({ length: 3200 }, (_, i) => `r${i}`),
      list: Array.from({ length: 3200 }, (_, i) => `l${i}`),
    });
    assert.equal(warning.status, 200);
    assert.equal(await warning.json(), 3200);
    const warnings = JSON.parse(warning.headers.get('x-elwood-warnings'));
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /Warning/);
    assert.match(warning.headers.get('access-control-expose-headers'), /X-Elwood-Warnings/i);

    const execution = await post('/api/executions', { testMode: 'real', pipelineId: 'regression', payload: { id: 42 }, isTest: true });
    assert.equal(execution.status, 200);
    const result = await execution.json();
    assert.equal(result.success, false);
    assert.match(result.errors.join(' '), /input/);
    const state = await (await fetch(`${base}/api/executions/${result.executionId}`, { headers: { Authorization: `Bearer ${admin}` } })).json();
    assert.equal(state.status, 3); // Failed, preserving the existing wire contract.
    assert.equal(state.sources.input.status, 3);
    assert.ok(state.completedAt);
    assert.ok(state.sources.input.diagnostics.some(d => d.severity === 'error'));

    // M2 contracts: authN/authZ are enforced by the API, not merely hidden buttons.
    const request = (path, method = 'GET', data, token = admin, revision) => fetch(base + path, {
      method, headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}),
        'Content-Type': 'application/json', ...(revision ? { 'If-Match': `"${revision}"` } : {}) },
      body: data === undefined ? undefined : JSON.stringify(data),
    });
    assert.equal((await request('/api/pipelines', 'GET', undefined, '')).status, 401);
    assert.equal((await request('/api/metrics', 'GET', undefined, 'invalid')).status, 401);
    assert.equal((await request('/api/pipelines/regression', 'GET', undefined, reader)).status, 403);
    assert.equal((await request('/api/executions', 'POST', { testMode: 'real', pipelineId: 'orders', payload: {} }, reader)).status, 403);
    assert.equal((await request('/api/executions', 'POST', { testMode: 'real', pipelineId: 'regression', payload: {} }, writer)).status, 403);
    assert.equal((await request('/api/pipelines/orders/deploy', 'POST', { expectedRevisionId: 'x' }, writer)).status, 403);
    for (const suffix of ['deploy', 'migrate', 'scripts/example.elwood/test', 'test-template']) {
      for (const trailing of ['', '/'])
        assert.equal((await request('/api/pipelines/orders/' + suffix + trailing, 'POST', {}, 'write-only')).status, 403, suffix + trailing);
    }
    assert.deepEqual(await (await request('/api/executions', 'GET', undefined, reader)).json(), []);
    assert.equal((await request(`/api/executions/${result.executionId}`, 'GET', undefined, reader)).status, 404);
    const legacy = await (await request('/api/pipelines/regression')).json();
    assert.equal(legacy.migrationRequired, true);
    assert.equal((await request('/api/pipelines/regression', 'PUT', legacy.content, admin, legacy.draftRevisionId)).status, 409);
    const adopted = await (await request('/api/pipelines/regression/migrate', 'POST', { expectedRevisionId: legacy.draftRevisionId })).json();
    assert.equal(adopted.activeRevisionId, legacy.activeRevisionId);
    assert.equal(adopted.migrationRequired, false);

    const content = { yaml: `version: 2
name: orders
sources:
  - name: input
    trigger: http
    endpoint: /orders
outputs:
  - name: result
    response: true
    map: transform.elwood
`, scripts: { 'transform.elwood': "return 'v1'" } };
    const created = await request('/api/pipelines/orders', 'POST', content);
    assert.equal(created.status, 201);
    const draft1 = await created.json();
    assert.equal(draft1.activeRevisionId, null);
    assert.equal((await post('/api/v1/trigger/orders', {})).status, 404);
    const checked = await (await post('/api/pipelines/orders/validate', { content })).json();
    assert.equal(checked.valid, true);
    assert.equal(checked.revisionId, draft1.draftRevisionId);
    const deployed1 = await post('/api/pipelines/orders/deploy', { expectedRevisionId: draft1.draftRevisionId });
    assert.equal(deployed1.status, 200, await deployed1.text());
    const live1 = await post('/api/v1/trigger/orders', {});
    assert.equal(await live1.json(), 'v1');
    assert.equal(live1.headers.get('x-elwood-revision'), draft1.draftRevisionId);
    const content2 = { ...content, scripts: { 'transform.elwood': "return 'v2'" } };
    const draft2 = await (await request('/api/pipelines/orders', 'PUT', content2, writer, draft1.draftRevisionId)).json();
    assert.equal(draft2.activeRevisionId, draft1.draftRevisionId);
    assert.equal(await (await post('/api/v1/trigger/orders', {})).json(), 'v1');
    assert.equal((await post('/api/pipelines/orders/deploy', { expectedRevisionId: draft1.draftRevisionId })).status, 409);
    assert.equal((await request('/api/pipelines/orders', 'PUT', content, writer, draft1.draftRevisionId)).status, 409);
    const unsaved = { ...content, scripts: { 'transform.elwood': "return 'unsaved'" } };
    const tested = await (await post('/api/executions', { testMode: 'real', pipelineId: 'orders', payload: {}, content: unsaved })).json();
    assert.equal(tested.outputs.result, 'unsaved');
    assert.equal(tested.isTest, true);
    assert.notEqual(tested.revisionId, draft2.draftRevisionId);
    const testState = await (await request(`/api/executions/${tested.executionId}`)).json();
    assert.equal(testState.revisionId, tested.revisionId);
    assert.equal(testState.pipelineId, 'orders');
    assert.equal((await (await request('/api/pipelines/orders')).json()).draftRevisionId, draft2.draftRevisionId);
    assert.equal((await post('/api/pipelines/orders/deploy', { expectedRevisionId: draft2.draftRevisionId })).status, 200);
    assert.equal(await (await post('/api/v1/trigger/orders', {})).json(), 'v2');
    assert.equal((await (await request('/api/pipelines/orders/revisions')).json()).length, 2);
    const oldBundle = await (await request(`/api/pipelines/orders/revisions/${draft1.draftRevisionId}`)).json();
    assert.equal(oldBundle.content.scripts['transform.elwood'], "return 'v1'");
    assert.deepEqual((await (await request('/api/pipelines', 'GET', undefined, reader)).json()).map(p => p.id), ['orders']);

    const traversal = { ...content, scripts: { '../escaped.elwood': 'return 42' } };
    assert.equal((await request('/api/pipelines/orders', 'PUT', traversal, admin, draft2.draftRevisionId)).status, 400);
    assert.equal((await request('/api/pipelines/..%5Cescaped', 'POST', content)).status, 400);
    assert.equal((await post('/api/pipelines/orders/validate', { content: { ...content, yaml: content.yaml.replace('transform.elwood', '../outside.elwood') } })).status, 200);
    assert.equal((await (await post('/api/pipelines/orders/validate', { content: { ...content, yaml: content.yaml.replace('transform.elwood', '../outside.elwood') } })).json()).valid, false);
    assert.equal((await request('/api/pipelines/orders', 'DELETE', undefined, reader, draft2.draftRevisionId)).status, 403);
    assert.equal((await request('/api/pipelines/orders/deploy', 'POST', { expectedRevisionId: draft2.draftRevisionId }, scoped)).status, 403);
    const evilOrigin = await fetch(base + '/api/session', { headers: { Authorization: `Bearer ${admin}`, Origin: 'https://untrusted.example' } });
    assert.equal(evilOrigin.headers.get('access-control-allow-origin'), null);

    // A host grants this credential to live runs only. A test cannot escalate by setting isTest:false.
    const secretContent = { ...content, yaml: content.yaml.replace('outputs:', `  - name: remote
    trigger: pull
    depends: input
    from:
      http:
        url: '$secrets.allowed'
outputs:`) };
    const denied = await (await post('/api/executions', { testMode: 'real', pipelineId: 'orders', payload: {}, content: secretContent, isTest: false })).json();
    assert.equal(denied.success, false);
    assert.match(denied.errors.join(' '), /Secret access denied/);
    assert.equal(requests, 0);
    const secretDraft = await (await request('/api/pipelines/orders', 'PUT', secretContent, admin, draft2.draftRevisionId)).json();
    assert.equal((await post('/api/pipelines/orders/deploy', { expectedRevisionId: secretDraft.draftRevisionId })).status, 200);
    assert.equal(await (await post('/api/v1/trigger/orders', {})).json(), 'v1');
    assert.equal(requests, 1);

    // M3: explicit selection, fully isolated mocks, async drafts on the same synchronous engine.
    assert.equal((await post('/api/executions', { pipelineId: 'orders', payload: {} })).status, 400);
    assert.equal((await post('/api/executions', { pipelineId: 'orders', testMode: 'mixed', payload: {} })).status, 400);
    const mockSecret = await (await post('/api/executions', { pipelineId: 'orders', testMode: 'mock', payload: {}, content: secretContent,
      mocks: { sources: { remote: { body: {} } }, outputs: {} } })).json();
    assert.equal(mockSecret.success, true);
    assert.equal(requests, 1, 'mock must not use the live grant or contact the upstream');
    const flow = { yaml: `version: 2
name: order-test
mode: async
sources:
  - name: input
    trigger: queue
    map: '{ order: $ }'
  - name: crm
    trigger: pull
    depends: input
    from:
      http:
        url: http://127.0.0.1:${upstream.address().port}
        method: POST
        headers:
          Authorization: Bearer upstream-private-token
          X-Correlation-Id: trace-42
        body: '{ orderId: $idm.order.id }'
    map: '{ crm: $, status: $source.http.statusCode }'
outputs:
  - name: target
    map: '{ orderId: $idm.order.id, status: $idm.status }'
    destinations:
      http:
        - url: http://127.0.0.1:${upstream.address().port}
`, scripts: {} };
    assert.equal((await request('/api/pipelines/orders/test-template', 'POST', { content: flow }, reader)).status, 403);
    const templateResponse = await request('/api/pipelines/orders/test-template', 'POST', { content: flow }, writer);
    assert.equal(templateResponse.status, 200);
    const template = await templateResponse.json();
    assert.deepEqual(Object.keys(template.mocks.sources), ['crm']);
    assert.equal(template.mocks.outputs.target.length, 1);
    template.mocks.sources.crm.expectBody = { orderId: 42 };
    template.mocks.outputs.target[0].expectBody = { status: 200, orderId: 42 };
    const mockBody = { pipelineId: 'orders', content: flow, payload: { id: 42 }, testMode: 'mock', mocks: template.mocks };
    const mockResponse = await request('/api/executions', 'POST', mockBody, writer);
    assert.equal(mockResponse.status, 200);
    const mock = await mockResponse.json();
    assert.equal(mock.success, true, JSON.stringify(mock.errors));
    assert.equal(mock.revisionId, template.revisionId);
    assert.equal(mock.inspection.execution.testMode, 'mock');
    assert.equal(mock.inspection.execution.status, 2);
    assert.equal(mock.inspection.testContext.value.input.id, 42);
    assert.equal(mock.inspection.testContext.value.calls.length, 2);
    assert.equal(mock.inspection.sources.crm.value.status, 200);
    assert.deepEqual(mock.inspection.outputs.target.value, { orderId: 42, status: 200 });
    assert.equal(requests, 1);
    const inspected = await (await request(`/api/executions/${mock.executionId}/inspection`, 'GET', undefined, reader)).json();
    assert.deepEqual(inspected, mock.inspection);
    assert.equal((await request(`/api/executions/${mock.executionId}/inspection`, 'GET', undefined, scoped)).status, 404);
    const testOnly = await (await request('/api/executions', 'POST', mockBody, 'test-only')).json();
    assert.equal(testOnly.success, true);
    assert.equal(testOnly.inspection, null, 'test permission alone must not disclose saved pipeline code or stored intermediate data');
    assert.deepEqual(testOnly.outputs, mock.outputs);
    assert.equal((await request(`/api/executions/${testOnly.executionId}/inspection`, 'GET', undefined, 'test-only')).status, 403);
    const real = await (await post('/api/executions', { ...mockBody, testMode: 'real', mocks: undefined })).json();
    assert.equal(real.success, true, JSON.stringify(real.errors));
    assert.deepEqual(real.outputs, mock.outputs);
    assert.equal(real.revisionId, mock.revisionId);
    for (const call of real.inspection.testContext.value.calls) {
      assert.equal(call.method, 'POST');
      assert.equal(call.url, `http://127.0.0.1:${upstream.address().port}/`);
      assert.match(call.requestHeaders['Content-Type'][0], /application\/json/);
      const responseHeaders = Object.fromEntries(Object.entries(call.responseHeaders).map(([key, value]) => [key.toLowerCase(), value]));
      assert.deepEqual(responseHeaders['x-request-id'], ['request-42']);
      assert.deepEqual(responseHeaders['set-cookie'], ['[REDACTED]']);
      assert.deepEqual(JSON.parse(call.responseBody), { ok: true });
      assert.equal(call.statusCode, 200);
      assert.ok(call.durationMs >= 0);
      assert.ok(call.startedAt);
      assert.doesNotMatch(JSON.stringify(call), /private-token|private-cookie/);
    }
    assert.deepEqual(real.inspection.testContext.value.calls[0].requestHeaders.Authorization, ['[REDACTED]']);
    assert.deepEqual(real.inspection.testContext.value.calls[0].requestHeaders['X-Correlation-Id'], ['trace-42']);
    const realInspection = await (await request(`/api/executions/${real.executionId}/inspection`, 'GET', undefined, reader)).json();
    assert.deepEqual(realInspection.testContext, real.inspection.testContext, 'HTTP details must survive storage and reload');
    assert.equal(requests, 3);
    for (const mocks of [null, { sources: {}, outputs: {} }, { ...template.mocks, typo: {} },
      { sources: { crm: { body: {}, stautsCode: 200 } }, outputs: template.mocks.outputs },
      { sources: template.mocks.sources, outputs: { target: [] } }]) {
      assert.equal((await post('/api/executions', { ...mockBody, mocks })).status, 400);
    }
    assert.equal((await post('/api/executions', { ...mockBody, testMode: 'real' })).status, 400);
    const failingMocks = structuredClone(template.mocks);
    failingMocks.outputs.target[0].statusCode = 503;
    const failed = await (await post('/api/executions', { ...mockBody, mocks: failingMocks })).json();
    assert.equal(failed.success, false);
    assert.equal(failed.inspection.execution.outputs.target.status, 3);
    assert.equal(failed.inspection.outputs.target.available, true);
    assert.equal(failed.inspection.testContext.value.calls[1].statusCode, 503);
    assert.equal(requests, 3, 'neither rejected fixtures nor mock failures may fall through');
    const afterTests = await (await request('/api/pipelines/orders')).json();
    assert.equal(afterTests.draftRevisionId, secretDraft.draftRevisionId);
    assert.equal(afterTests.activeRevisionId, secretDraft.draftRevisionId);

    await writeFile(join(dir, 'pipeline.elwood.yaml'), 'name: regression\nretry: { attempts: 3 }\n');
    const validation = await (await post('/api/pipelines/regression/validate', { content: { yaml: 'name: regression\nretry: { attempts: 3 }\n', scripts: {} } })).json();
    assert.equal(validation.valid, false);
    assert.match(validation.errors.join(' '), /retry/);
  } finally {
    if (child && child.exitCode === null) {
      const exited = once(child, 'exit');
      child.kill();
      await exited;
    }
    await new Promise(resolve => upstream.close(resolve));
    await rm(root, { recursive: true, force: true });
  }
});
