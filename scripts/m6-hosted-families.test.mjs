import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile, writeFile, readdir } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';

// Deliberately lab-specific: this test creates an Event Grid subscription and
// uploads synthetic input. Never point it at a production installation.
const enabled = process.env.ELWOOD_M6_HOSTED_FAMILIES === 'true';
test('Hosted HTTP chain, external RabbitMQ → SQL → SAP and Event Grid → Blob → SQL + snapshot',
  { skip: !enabled, timeout: 900_000 }, async () => {
    const base = 'https://elwood-m6-api-r3m5auolhstum.azurewebsites.net';
    const fixture = 'https://elwood-m6-fixture-r3m5auolhstum.azurewebsites.net';
    const storage = 'elwoodm6r3m5auolhstum';
    const blob = `https://${storage}.blob.core.windows.net`;
    const sub = '79522cd2-4487-4289-b0b8-42ee8aa5d649', group = 'elwood-m6-rg';
    const token = (await readFile('.private/m6-host-token', 'utf8')).trim();
    const trigger = (await readFile('.private/m6-trigger-password', 'utf8')).trim();
    const password = (await readFile('.private/m6-fixture-password', 'utf8')).trim();
    const basic = value => 'Basic ' + Buffer.from(value).toString('base64');
    const auth = basic('fixture:' + password), triggerAuth = basic('webhook:' + trigger);
    const invoke = promisify(execFile);
    const cli = process.env.ELWOOD_AZURE_CLI_PYTHON ?? 'C:/Program Files/Microsoft SDKs/Azure/CLI2/python.exe';
    async function az(args) {
      try { return (await invoke(cli, ['-IBm', 'azure.cli', ...args, '--subscription', sub], { windowsHide: true, timeout: 300_000, maxBuffer: 1024 * 1024 })).stdout.trim(); }
      catch (error) { throw new Error(('Azure fixture operation failed: ' + (error.stderr ?? error.message)).replaceAll(password, '[redacted]').replaceAll(trigger, '[redacted]').replaceAll(triggerAuth, '[redacted]')); }
    }
    async function request(path, method = 'GET', body, authorization = 'Bearer ' + token, extra = {}) {
      return fetch(base + path, { method, headers: { Authorization: authorization, 'Content-Type': 'application/json', ...extra },
        body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(90_000) });
    }
    async function json(response, status = 200) {
      const text = await response.text(); assert.equal(response.status, status, text.slice(0, 1500)); return text ? JSON.parse(text) : null;
    }
    async function until(action, predicate, label, timeout = 180_000) {
      const end = Date.now() + timeout; let last;
      while (Date.now() < end) { last = await action(); if (predicate(last)) return last; await delay(2000); }
      assert.fail(label + ': ' + JSON.stringify(last).slice(0, 1500));
    }
    await until(async () => { try { return (await fetch(fixture + '/health', { signal: AbortSignal.timeout(10_000) })).status; } catch { return 0; } }, v => v === 200, 'Fixture startup');
    async function deploy(id, content) {
      const current = await request('/api/pipelines/' + id);
      const draft = current.status === 404 ? await json(await request('/api/pipelines/' + id, 'POST', content), 201)
        : await json(await request('/api/pipelines/' + id, 'PUT', content, 'Bearer ' + token, { 'If-Match': '"' + (await current.json()).draftRevisionId + '"' }));
      await json(await request('/api/pipelines/' + id + '/deploy', 'POST', { expectedRevisionId: draft.draftRevisionId }));
    }
    async function sample(name, id, replacements) {
      const directory = 'docs/examples/connectors/' + name;
      let yaml = (await readFile(directory + '/pipeline.elwood.yaml', 'utf8')).replaceAll(name, id).replace(/\r\n/g, '\n');
      yaml = yaml.replace(/ttlSeconds:.*\n/g, '').replace(/mode: (sync|async)\n/, '$&ttlSeconds: 600\n');
      yaml = yaml.replace(/(    endpoint: .*\n)/g, "$1    auth: { type: basic, user: webhook, password: $secrets.m6.triggerPassword }\n");
      for (const [from, to] of replacements) yaml = yaml.replaceAll(from, to);
      const scripts = Object.fromEntries(await Promise.all((await readdir(directory)).filter(f => f.endsWith('.elwood')).map(async f => [f, (await readFile(directory + '/' + f, 'utf8')).replaceAll('/containers/inbound/', '/containers/contract-inputs/')])));
      return { yaml, scripts };
    }
    await deploy('m6-order-chain', await sample('sample-order-auth', 'm6-order-chain', [
      ['$secrets.crm.url', fixture + '/crm'], ['$secrets.crm.tokenUrl', fixture + '/token'], ['$secrets.crm.clientId', 'client'],
      ['$secrets.crm.clientSecret', '$secrets.m6.fixturePassword'], ['$secrets.emarsys.url', fixture + '/emarsys'],
      ['$secrets.emarsys.user', 'fixture'], ['$secrets.emarsys.password', '$secrets.m6.fixturePassword']
    ]));
    const orderId = 'ORDER-' + randomUUID();
    const order = await json(await request('/api/v1/trigger/samples/m6-order-chain', 'POST', { id: orderId }, triggerAuth));
    assert.equal(order.crmId, 'CRM-42'); assert.equal(order.orderId, orderId);
    console.log('Hosted order chain passed: OAuth2 CRM response feeds Basic-auth Emarsys request.');

    await deploy('m6-product-sql', await sample('sample-product-sql', 'm6-product-sql', [
      ['$secrets.products.rabbitmq', '$secrets.m6.rabbitmq'], ['$secrets.products.sql', '$secrets.m6.sql'],
      ['$secrets.sap.url', fixture + '/sap'], ['$secrets.sap.user', 'fixture'], ['$secrets.sap.password', '$secrets.m6.fixturePassword']
    ]));
    const productId = Math.floor(Math.random() * 100000000), messageId = randomUUID();
    const publish = () => fetch(fixture + '/rabbit', { method: 'POST', headers: { Authorization: auth, 'Content-Type': 'application/json', 'Idempotency-Key': messageId }, body: JSON.stringify({ productId }) });
    assert.equal((await publish()).status, 202);
    const writes = () => fetch(fixture + '/writes', { headers: { Authorization: auth } }).then(r => json(r));
    await until(writes, rows => rows.some(w => w.path === '/sap' && w.body.productId === productId && w.body.sku === 'SKU-42' && w.body.quantity === 7), 'Rabbit/SQL/SAP delivery');
    assert.equal((await publish()).status, 202);
    await delay(5000);
    assert.equal((await writes()).filter(w => w.path === '/sap' && w.body.productId === productId).length, 1);
    console.log('Hosted RabbitMQ/SQL/SAP passed; identical MessageId redelivery produced one SAP write.');

    const deldo = await sample('sample-deldo-stock', 'm6-deldo-stock', [
      ['connectionString: $secrets.inbound.connectionString', 'accountUrl: ' + blob], ['container: inbound', 'container: contract-inputs'],
      ['container: analytics', 'container: contract-outputs'], ['filename: deldo/', 'filename: hostpreview/deldo/'],
      ['/containers/inbound/', '/containers/contract-inputs/'], ['$secrets.catalog.connectionString', '$secrets.m6.sql'], ['catalog.usp_ImportOffers', 'dbo.ImportOffers']
    ]);
    await deploy('m6-deldo-stock', deldo);
    await writeFile('.private/m6-event-grid-parameters.json', JSON.stringify({ parameters: { authorization: { value: triggerAuth }, endpointUrl: { value: base + '/api/v1/trigger/samples/m6-deldo-stock' } } }));
    assert.equal(await az(['deployment', 'group', 'create', '--resource-group', group, '--name', 'm6-event-grid', '--template-file', 'infra/azure-small/test-event-grid.bicep', '--parameters', '@.private/m6-event-grid-parameters.json', '--query', 'properties.provisioningState', '-o', 'tsv']), 'Succeeded');
    const csv = JSON.parse(await readFile('docs/examples/connectors/sample-deldo-stock/mocks.json', 'utf8')).sources.file.body;
    const fileName = 'stock-' + randomUUID() + '.csv';
    const uploadedAfter = Date.now();
    await writeFile('.private/m6-stock.csv', csv);
    await az(['storage', 'blob', 'upload', '--account-name', storage, '--auth-mode', 'login', '--container-name', 'contract-inputs', '--name', 'deldo/' + fileName, '--file', '.private/m6-stock.csv', '--overwrite', 'false', '--only-show-errors', '-o', 'none']);
    const history = () => request('/api/executions?pipeline=m6-deldo-stock').then(r => json(r));
    const executions = await until(history, states => states.some(s => s.status === 2 && Date.parse(s.startedAt) >= uploadedAfter), 'Event Grid delivery and completion');
    const state = executions.find(s => s.status === 2 && Date.parse(s.startedAt) >= uploadedAfter);
    const inspection = await json(await request('/api/executions/' + state.executionId + '/inspection'));
    assert.equal(inspection.outputs['offers-to-sql'].value.offers.length, 5);
    assert.equal(inspection.outputs['offers-to-sql'].value.import.rowsRejected, 6);
    // Re-send the exact provider event after durable acceptance, proving ingress deduplication.
    assert.match(state.triggerRef, /^exec\/[a-zA-Z0-9-]+\/trigger\/[a-f0-9]+$/);
    await az(['storage', 'blob', 'download', '--account-name', storage, '--auth-mode', 'login', '--container-name', 'contract-documents', '--name', 'hostpreview/' + state.triggerRef, '--file', '.private/m6-provider-event.json', '--overwrite', '--only-show-errors', '-o', 'none']);
    const event = JSON.parse(await readFile('.private/m6-provider-event.json', 'utf8'));
    assert.equal((await request('/api/v1/trigger/samples/m6-deldo-stock', 'POST', event, '', { 'aeg-event-type': 'Notification' })).status, 401);
    const duplicate = await json(await request('/api/v1/trigger/samples/m6-deldo-stock', 'POST', event, triggerAuth, { 'aeg-event-type': 'Notification' }), 202);
    assert.deepEqual(duplicate.executionIds, [state.executionId]);
    await writeFile('.private/m6-sql-results.sql', `SELECT (SELECT COUNT(*) FROM dbo.imports WHERE blob_path=N'deldo/${fileName}') AS imports, (SELECT COUNT(*) FROM dbo.imports CROSS APPLY OPENJSON(payload,'$.offers') WHERE blob_path=N'deldo/${fileName}') AS offers;`);
    const sql = await invoke('dotnet', ['.private/m6-fixture-build/publish/Elwood.M6.FixtureHost.dll', '--sql-file', '.private/m6-sql-results.sql'], { windowsHide: true, env: { ...process.env, ELWOOD_M6_SQL_HOST: 'elwood-m6-sql-r3m5auolhstum.database.windows.net' } });
    assert.match(sql.stdout, /"imports":1/); assert.match(sql.stdout, /"offers":5/);
    console.log('Hosted Deldo passed:', JSON.stringify({ executionId: state.executionId, fileName, offers: 5, rejected: 6, imports: 1, authenticatedEventGrid: true, duplicateAcceptedOnce: true }));
    await writeFile('.private/m6-families-evidence.json', JSON.stringify({ orderId, productId, messageId, deldoExecution: state.executionId, fileName, verifiedAt: new Date().toISOString() }, null, 2));
  });
