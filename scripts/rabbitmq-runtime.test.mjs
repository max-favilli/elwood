import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash, randomUUID } from 'node:crypto';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

// Real broker + separate Runtime API process. Build the API first. Requires the
// RabbitMQ management plugin, with permission to manage this test's own queue.
test('hosted RabbitMQ reconnect, durable acceptance and restart retain revision and deduplicate', { timeout: 120000 }, async () => {
  const uri = new URL(process.env.ELWOOD_TEST_RABBITMQ ?? 'amqp://guest:guest@127.0.0.1:5672/');
  const management = new URL(process.env.ELWOOD_TEST_RABBITMQ_MANAGEMENT ?? 'http://127.0.0.1:15672');
  const credentials = `${decodeURIComponent(management.username || uri.username)}:${decodeURIComponent(management.password || uri.password)}`;
  management.username = ''; management.password = '';
  const vhost = encodeURIComponent(decodeURIComponent(uri.pathname.slice(1) || '/'));
  const queue = 'elwood-hosted-' + randomUUID();
  const queuePath = `/api/queues/${vhost}/${queue}`;
  const root = await mkdtemp(join(tmpdir(), 'elwood-rabbitmq-api-'));
  const token = 'local-rabbitmq-management';
  const dll = resolve('dotnet/src/Elwood.Runtime.Api/bin/Debug/net10.0/Elwood.Runtime.Api.dll');
  let child, base, logs = '', declared = false;
  const deliveries = [];
  const destination = createServer(async (req, res) => {
    let body = ''; for await (const chunk of req) body += chunk;
    deliveries.push(JSON.parse(body));
    res.setHeader('Content-Type', 'application/json'); res.end('{"accepted":true}');
  });
  destination.listen(0, '127.0.0.1'); await once(destination, 'listening');
  const destinationUrl = `http://127.0.0.1:${destination.address().port}/sap`;

  async function broker(path, method = 'GET', body) {
    const response = await fetch(new URL(path, management), {
      method, signal: AbortSignal.timeout(5000),
      headers: { Authorization: 'Basic ' + Buffer.from(credentials).toString('base64'), 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    assert.ok(response.ok, `Broker ${method} ${path}: ${response.status} ${await response.clone().text()}`);
    return response.status === 204 || response.status === 201 ? undefined : response.json();
  }
  const request = async (path, method = 'GET', body, headers = {}) => {
    const response = await fetch(base + path, {
      method, signal: AbortSignal.timeout(5000),
      headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json', ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    assert.ok(response.ok, `Runtime ${method} ${path}: ${response.status} ${await response.clone().text()}`);
    return response.json();
  };
  async function until(predicate, description) {
    for (let i = 0; i < 200; i++) {
      const result = await predicate(); if (result) return result;
      await delay(100);
    }
    assert.fail(description + '\n' + logs);
  }
  async function stop() {
    if (child && child.exitCode === null) { const exited = once(child, 'exit'); child.kill('SIGKILL'); await exited; }
  }
  async function start(workers) {
    const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
    base = `http://127.0.0.1:${reservation.address().port}`;
    await new Promise(resolve => reservation.close(resolve));
    child = spawn('dotnet', [dll, '--urls', base], {
      cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, Elwood__PipelinesDir: join(root, 'pipelines'), Elwood__StateDir: join(root, 'state'),
        Elwood__SecretsFile: join(root, 'secrets.json'), Elwood__Management__LocalDevelopment: 'false',
        Elwood__Durable__WorkerEnabled: String(workers), Elwood__AppConfiguration: '', ELWOOD_APP_CONFIGURATION: '',
        APPLICATIONINSIGHTS_CONNECTION_STRING: '' },
    });
    child.stdout.on('data', c => logs = (logs + c).slice(-6000));
    child.stderr.on('data', c => logs = (logs + c).slice(-6000));
    await until(async () => {
      assert.equal(child.exitCode, null, logs);
      try { return (await fetch(base + '/api/health', { signal: AbortSignal.timeout(1000) })).ok; } catch { return false; }
    }, 'Runtime did not start');
  }
  const content = label => ({ yaml: `name: broker-recovery
mode: async
sources:
  - name: event
    trigger: queue
    rabbitmq:
      connectionString: $secrets.broker
      queue: ${queue}
      requireMessageId: true
    map: '{ event: $ }'
outputs:
  - name: result
    map: '{ id: $idm.event.id, revision: "${label}" }'
    destinations:
      http:
        - url: ${destinationUrl}
`, scripts: {} });
  const save = (label, revision) => request('/api/pipelines/broker-recovery', revision ? 'PUT' : 'POST', content(label),
    revision ? { 'If-Match': `"${revision}"` } : {});
  const deploy = draft => request('/api/pipelines/broker-recovery/deploy', 'POST', { expectedRevisionId: draft.draftRevisionId });
  const send = async id => {
    const result = await broker(`/api/exchanges/${vhost}/amq.default/publish`, 'POST', {
      properties: { message_id: 'event-' + id, content_type: 'application/json', delivery_mode: 2 },
      routing_key: queue, payload: JSON.stringify({ id }), payload_encoding: 'string',
    });
    assert.equal(result.routed, true);
  };
  const history = () => request('/api/executions');
  const accepted = count => until(async () => { const runs = await history(); return runs.length === count && runs; }, 'Messages were not durably accepted');
  const connection = () => until(async () => {
    const q = await broker(queuePath);
    assert.ok(q.consumers <= 1, 'A deployed source must have only one subscription');
    return q.consumer_details?.[0]?.channel_details?.connection_name;
  }, 'Hosted worker did not subscribe');
  // Acknowledgement counters avoid treating a message merely removed from the
  // ready queue (but still unacked) as durable acceptance.
  const acknowledged = count => until(async () => {
    const q = await broker(queuePath);
    return q.messages === 0 && q.messages_unacknowledged === 0 && q.message_stats?.ack >= count;
  }, 'Broker did not observe acknowledgements');

  try {
    await broker(queuePath, 'PUT', { durable: false, auto_delete: false, arguments: {} }); declared = true;
    await writeFile(join(root, 'appsettings.json'), JSON.stringify({ Logging: { LogLevel: { Default: 'Warning' } }, Elwood: {
      Management: { Tokens: [{ Name: 'test', TokenSha256: createHash('sha256').update(token).digest('hex'),
        Permissions: ['read', 'write', 'deploy'], Pipelines: ['broker-recovery'] }] },
      SecretGrants: { 'broker-recovery': { Run: ['broker'] } },
    } }));
    await writeFile(join(root, 'secrets.json'), JSON.stringify({ broker: uri.href }));
    await start(false); // Acceptance works while execution workers are paused.
    const firstDraft = await save('v1');
    await send(1);
    // Allow at least one deployment scan: saving a draft must not subscribe.
    await delay(3500);
    assert.equal((await broker(queuePath)).consumers, 0);
    assert.deepEqual(await history(), []);
    await deploy(firstDraft);
    const firstConnection = await connection();
    const [first] = await accepted(1);
    await acknowledged(1);
    assert.equal(first.durable, true); assert.ok(first.triggerRef); assert.ok(first.pipelineSnapshotRef);
    assert.equal(first.revisionId, firstDraft.draftRevisionId);
    assert.equal(first.completedAt, null); assert.deepEqual(deliveries, []);

    // Drop only this test's consumer connection through the broker. The hosted
    // worker must reconnect without restarting the API or redeploying the bundle.
    await broker('/api/connections/' + encodeURIComponent(firstConnection), 'DELETE');
    await send(2);
    await until(async () => (await connection()) !== firstConnection, 'Consumer did not reconnect');
    const beforeRestart = await accepted(2); await acknowledged(2);
    assert.deepEqual(deliveries, []);

    // Deploy a different revision before recovery. Accepted work must retain v1.
    const secondDraft = await save('v2', firstDraft.draftRevisionId); await deploy(secondDraft);
    await stop(); // Abrupt process death also loses the in-memory wake-up queue.
    await start(true);
    await until(async () => {
      const runs = await history();
      assert.ok(runs.every(r => ![3, 5].includes(r.status)), JSON.stringify(runs));
      return runs.length === 2 && runs.every(r => r.status === 2);
    }, 'Accepted messages did not recover after restart');
    assert.deepEqual(deliveries.toSorted((a,b) => a.id-b.id), [{ id: 1, revision: 'v1' }, { id: 2, revision: 'v1' }]);
    assert.deepEqual((await history()).map(r => r.executionId).sort(), beforeRestart.map(r => r.executionId).sort());

    await connection();
    await send(1); await send(2); await acknowledged(4);
    assert.equal((await history()).length, 2, 'Redelivery after restart must reuse retained executions');
    assert.equal(deliveries.length, 2, 'Redelivery must not repeat completed outputs');
    await send(3); await acknowledged(5);
    await until(() => deliveries.length === 3, 'New event did not use the new deployment');
    assert.deepEqual(deliveries[2], { id: 3, revision: 'v2' });
    const runs = await accepted(3);
    assert.equal(runs.filter(r => r.revisionId === secondDraft.draftRevisionId).length, 1);
  } finally {
    await stop();
    destination.closeAllConnections(); await new Promise(resolve => destination.close(resolve));
    try { if (declared) await broker(queuePath, 'DELETE'); }
    finally { await rm(root, { recursive: true, force: true }); }
  }
});
