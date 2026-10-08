import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';

test('Hosted restart recovers lost Service Bus hints, authored delay, dead letters and interrupted sync writes',
  { skip: process.env.ELWOOD_M6_HOSTED_RECOVERY !== 'true', timeout: 900_000 }, async () => {
    const base = 'https://elwood-m6-api-r3m5auolhstum.azurewebsites.net', fixture = 'https://elwood-m6-fixture-r3m5auolhstum.azurewebsites.net';
    const app = 'elwood-m6-api-r3m5auolhstum', group = 'elwood-m6-rg', subscription = '79522cd2-4487-4289-b0b8-42ee8aa5d649';
    const token = (await readFile('.private/m6-host-token', 'utf8')).trim();
    const trigger = (await readFile('.private/m6-trigger-password', 'utf8')).trim();
    const password = (await readFile('.private/m6-fixture-password', 'utf8')).trim();
    const basic = value => 'Basic ' + Buffer.from(value).toString('base64');
    const invoke = promisify(execFile);
    const az = async args => (await invoke(process.env.ELWOOD_AZURE_CLI_PYTHON ?? 'C:/Program Files/Microsoft SDKs/Azure/CLI2/python.exe',
      ['-IBm', 'azure.cli', ...args, '--subscription', subscription], { windowsHide: true, timeout: 180_000, maxBuffer: 1024 * 1024 })).stdout.trim();
    const request = (path, body, authorization = 'Bearer ' + token, extra = {}) => fetch(base + path, {
      method: body === undefined ? 'GET' : 'POST', headers: { Authorization: authorization, 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(90_000), ...extra });
    async function json(r, status = 200) { const t = await r.text(); assert.equal(r.status, status, t.slice(0, 1000)); return t ? JSON.parse(t) : null; }
    async function until(action, predicate, label, seconds = 180) {
      const end = Date.now() + seconds * 1000; let last;
      while (Date.now() < end) { last = await action(); if (predicate(last)) return last; await delay(2000); }
      assert.fail(label + ': ' + JSON.stringify(last).slice(0, 1500));
    }
    async function workers(paused) {
      await az(['functionapp', 'stop', '-g', group, '-n', app, '-o', 'none']);
      await writeFile('.private/m6-worker-settings.json', JSON.stringify({ 'AzureWebJobs.ServiceBusExecutionStep.Disabled': String(paused), 'AzureWebJobs.ExecutionRecovery.Disabled': String(paused) }));
      await az(['functionapp', 'config', 'appsettings', 'set', '-g', group, '-n', app, '--settings', '@.private/m6-worker-settings.json', '-o', 'none']);
      await az(['functionapp', 'start', '-g', group, '-n', app, '-o', 'none']);
      await until(async () => { try { return (await request('/api/health')).status; } catch { return 0; } }, s => s === 200, 'Host restart');
    }
    async function deploy(id, mode, target) {
      const content = { scripts: {}, yaml: `name: ${id}
mode: ${mode}
ttlSeconds: 600
sources:
  - { name: input, trigger: http, endpoint: /${id}, auth: { type: basic, user: webhook, password: $secrets.m6.triggerPassword } }
outputs:
  - name: remote
    response: ${mode === 'sync'}
    retry: { mode: automatic, maxAttempts: 2, delayMs: 10000, maxDelayMs: 10000, allowUncertainWrites: false }
    destinations:
      http:
        - { url: '${fixture}/${target}', user: fixture, password: $secrets.m6.fixturePassword, connectionTimeout: 120000 }
` };
      const existing = await request('/api/pipelines/' + id);
      const draft = existing.status === 404 ? await json(await request('/api/pipelines/' + id, content), 201)
        : await json(await request('/api/pipelines/' + id, content, 'Bearer ' + token, { method: 'PUT', headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json', 'If-Match': '"' + (await existing.json()).draftRevisionId + '"' } }));
      await json(await request('/api/pipelines/' + id + '/deploy', { expectedRevisionId: draft.draftRevisionId }));
    }
    const writes = () => fetch(fixture + '/writes', { headers: { Authorization: basic('fixture:' + password) } }).then(r => json(r));
    await deploy('m6-recovery', 'async', 'retry');
    await deploy('m6-abort', 'sync', 'slow');
    let executionId;
    try {
      await workers(true);
      const input = { id: randomUUID() };
      executionId = (await json(await request('/api/v1/trigger/m6-recovery', input, basic('webhook:' + trigger)), 202)).executionId;
      const pending = await json(await request('/api/executions/' + executionId)); assert.notEqual(pending.status, 2);
      const dropped = await invoke('dotnet', ['.private/m6-fixture-build/publish/Elwood.M6.FixtureHost.dll', '--drop-hint', executionId], { windowsHide: true });
      assert.match(dropped.stdout, /"matched":1/);
      const poison = await invoke('dotnet', ['.private/m6-fixture-build/publish/Elwood.M6.FixtureHost.dll', '--seed-deadletter', 'synthetic'], { windowsHide: true });
      assert.match(poison.stdout, /"matched":1/);
      console.log('Removed accepted execution hint and seeded a dead-letter hint while native workers were paused.');
      await workers(false);
      const completed = await until(() => request('/api/executions/' + executionId).then(r => json(r)), s => s.status === 2, 'Indexed recovery and retry');
      assert.equal(completed.outputs.remote.attempts.length, 2);
      const calls = (await writes()).filter(w => w.path === '/retry' && w.body.id === input.id);
      assert.equal(calls.length, 2); assert.ok(Date.parse(calls[1].at) - Date.parse(calls[0].at) >= 10_000);
      console.log('Native restart/lost-notification recovery passed; HTTP retry respected the authored ten-second delay.');

      const abortedId = randomUUID(), controller = new AbortController(), abortedAfter = Date.now();
      const pendingWrite = request('/api/v1/trigger/m6-abort', { id: abortedId }, basic('webhook:' + trigger), { signal: controller.signal }).catch(e => e);
      await until(writes, values => values.some(w => w.path === '/slow' && w.body.id === abortedId), 'Remote write started');
      controller.abort(); await pendingWrite;
      const state = await until(() => request('/api/executions?pipeline=m6-abort').then(r => json(r)), rows => rows.some(s => s.status === 5 && Date.parse(s.startedAt) >= abortedAfter), 'Bounded interrupted sync write', 90);
      const interrupted = state.find(s => s.status === 5 && Date.parse(s.startedAt) >= abortedAfter);
      assert.equal(interrupted.outputs.remote.outcomeUncertain, true);
      assert.ok([499, 504].includes(interrupted.responseStatusCode));
      assert.equal((await writes()).filter(w => w.path === '/slow' && w.body.id === abortedId).length, 1);
      const count = async () => Number(await az(['servicebus', 'queue', 'show', '-g', group, '--namespace-name', 'elwood-m6-bus-r3m5auolhstum', '-n', 'contract-work', '--query', 'countDetails.deadLetterMessageCount', '-o', 'tsv']));
      await until(count, n => n === 0, 'Independent native dead-letter cleanup', 360);
      const evidence = { recoveredExecution: executionId, interruptedExecution: interrupted.executionId, cancellationStatus: interrupted.responseStatusCode, authoredDelay: true, deadLettersRemoved: true, verifiedAt: new Date().toISOString() };
      await writeFile('.private/m6-recovery-evidence.json', JSON.stringify(evidence, null, 2)); console.log('Hosted recovery evidence:', JSON.stringify(evidence));
    } finally { await workers(false); }
  });
