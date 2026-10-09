import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile, writeFile } from 'node:fs/promises';
import { randomUUID, createHash } from 'node:crypto';
import { promisify } from 'node:util';
import { execFile } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';

// Explicitly disposable lab only. Uses the real supplied Deldo script, but two Blob
// destinations replace SQL + Blob so this measures runtime capacity, not SQL throughput.
test('25 MiB hosted Deldo executions, equal-file suppression, forced replay and bounded inspection',
  { skip: process.env.ELWOOD_CAPACITY_LIVE !== 'true', timeout: 1_500_000 }, async () => {
    const group = 'elwood-m6-rg', account = 'elwr3m5auolhstum';
    const base = 'https://elwood-m6-api-r3m5auolhstum.azurewebsites.net';
    const invoke = promisify(execFile);
    const az = async args => (await invoke(process.env.ELWOOD_AZURE_CLI_PYTHON ?? 'C:/Program Files/Microsoft SDKs/Azure/CLI2/python.exe',
      ['-IBm', 'azure.cli', ...args], { windowsHide: true, timeout: 180_000, maxBuffer: 4 * 1024 * 1024 })).stdout.trim();
    const lab = JSON.parse(await az(['group', 'show', '-n', group, '--query', '{tags:tags,subscription:id}', '-o', 'json']));
    assert.equal(lab.tags?.purpose, 'capacity-verification');
    assert.match(lab.subscription, /^\/subscriptions\/79522cd2-4487-4289-b0b8-42ee8aa5d649\//);
    const token = (await readFile('.private/capacity-token', 'utf8')).trim();
    const password = (await readFile('.private/capacity-trigger-password', 'utf8')).trim();
    const script = await readFile(process.env.ELWOOD_CAPACITY_SCRIPT, 'utf8');
    const [header, sampleRow] = (await readFile(process.env.ELWOOD_CAPACITY_SAMPLE, 'utf8')).replace(/^\uFEFF/, '').split(/\r?\n/);
    assert.ok(header && sampleRow);
    const run = randomUUID();
    const fields = sampleRow.split(';'), ean = header.split(';').indexOf('EAN');
    assert.ok(ean >= 0 && /^\d{8,14}$/.test(fields[ean]));
    fields[ean] = (BigInt('0x' + run.replaceAll('-', '')) % (10n ** BigInt(fields[ean].length))).toString().padStart(fields[ean].length, '0');
    const row = fields.join(';'); // Fresh valid synthetic content for each campaign; the equal-copy stays byte-identical.
    const rows = Math.floor((25 * 1024 * 1024 - Buffer.byteLength(header + '\n')) / Buffer.byteLength(row + '\n'));
    const csv = Buffer.from(header + '\n' + (row + '\n').repeat(rows));
    await writeFile('.private/capacity.csv', csv);
    const storageArgs = ['--account-name', account, '--auth-mode', 'login'];
    for (const suffix of ['original', 'equal-copy']) {
      await az(['storage', 'blob', 'upload', ...storageArgs, '--container-name', 'sources', '--name', `capacity/${run}-${suffix}.csv`,
        '--file', '.private/capacity.csv', '--content-type', 'text/csv', '--overwrite', 'false', '-o', 'none']);
    }
    async function request(path, method = 'GET', body, trigger = false, extra = {}) {
      const response = await fetch(base + path, { method, headers: {
        Authorization: trigger ? `Basic ${Buffer.from('capacity:' + password).toString('base64')}` : `Bearer ${token}`,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...extra,
      }, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(200_000) });
      const text = await response.text();
      assert.ok(response.ok, `${method} ${path}: ${response.status}: ${text.slice(0, 1500)}`);
      return text ? JSON.parse(text) : null;
    }
    const content = (id, skip = true) => ({ scripts: { 'normalize.elwood': script }, yaml: `name: ${id}
mode: async
ttlSeconds: 600
deduplication: { mode: ${skip ? 'skip' : 'process'}, source: file }
sources:
  - name: event
    trigger: http
    endpoint: /${id}
    auth: { type: basic, user: capacity, password: '$secrets.capacity.password' }
    map: '{ event: $ }'
  - name: file
    trigger: pull
    depends: event
    contentType: text
    from:
      blobStorage: { accountUrl: 'https://${account}.blob.core.windows.net', container: sources, path: '{$idm.event.blobPath}', maxBytes: 26214400, timeoutSeconds: 120 }
    map: normalize.elwood
outputs:
  - name: sqlPayload
    map: '{ import: $idm.import, offers: $idm.offers }'
    destinations:
      blobStorage:
        - { accountUrl: 'https://${account}.blob.core.windows.net', container: outputs, filename: 'elwood/capacity/${run}/{$execution.id}/sql-payload.json', ttlSeconds: 180, timeoutSeconds: 120 }
  - name: snapshot
    map: '{ import: $idm.import, offers: $idm.offers }'
    destinations:
      blobStorage:
        - { accountUrl: 'https://${account}.blob.core.windows.net', container: outputs, filename: 'elwood/capacity/${run}/{$execution.id}/snapshot.json', ttlSeconds: 180, timeoutSeconds: 120 }
` });
    for (let i = 0; i < 3; i++) {
      const id = `capacity-deldo-${i}`;
      const existing = await fetch(base + `/api/pipelines/${id}`, { headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(60_000) });
      assert.ok(existing.ok || existing.status === 404);
      const draft = existing.status === 404 ? await request(`/api/pipelines/${id}`, 'POST', content(id))
        : await request(`/api/pipelines/${id}`, 'PUT', content(id), false, { 'If-Match': `"${(await existing.json()).draftRevisionId}"` });
      await request(`/api/pipelines/${id}/deploy`, 'POST', { expectedRevisionId: draft.draftRevisionId });
    }
    const startedAt = new Date().toISOString();
    const payload = suffix => ({ supplier: 'deldo', blobPath: `capacity/${run}-${suffix}.csv`, fileName: `${run}-${suffix}.csv`, etag: suffix, time: startedAt });
    async function terminal(id, expected = 2) {
      const end = Date.now() + 600_000;
      while (Date.now() < end) {
        const state = await request(`/api/executions/${id}`);
        if ([2, 3, 5, 6].includes(state.status)) {
          assert.equal(state.status, expected, JSON.stringify({ id, errors: state.errors, sources: state.sources, outputs: state.outputs }).slice(0, 5000));
          return state;
        }
        await delay(3000);
      }
      assert.fail(`Execution ${id} did not settle within ten minutes`);
    }
    const accepted = await Promise.all([0, 1, 2].map(i => request(`/api/v1/trigger/capacity-deldo-${i}`, 'POST', payload('original'), true)));
    console.log('Accepted three large executions:', accepted.map(a => a.executionId).join(', '));
    const states = await Promise.all(accepted.map(a => terminal(a.executionId)));
    const first = states[0];
    const inspection = await request(`/api/executions/${first.executionId}/inspection`);
    assert.equal(inspection.sources.file.omitted, true);
    assert.equal(inspection.outputs.snapshot.omitted, true);
    assert.ok(Buffer.byteLength(JSON.stringify(inspection)) < 300_000);
    const duplicate = await request('/api/v1/trigger/capacity-deldo-0', 'POST', payload('equal-copy'), true);
    const skipped = await terminal(duplicate.executionId, 6);
    assert.equal(skipped.duplicateOf, first.executionId);
    const replay = await request(`/api/executions/${first.executionId}/replay`, 'POST', { forceProcessing: true });
    const replayed = await terminal(replay.executionId);
    assert.equal(replayed.forceProcessing, true);
    const outputFiles = JSON.parse(await az(['storage', 'blob', 'list', ...storageArgs, '--container-name', 'outputs', '--prefix', `elwood/capacity/${run}/`,
      '--query', '[].{name:name,bytes:properties.contentLength}', '-o', 'json']));
    assert.ok(outputFiles.filter(f => f.name.includes(replayed.executionId)).length === 2);
    assert.equal(outputFiles.filter(f => f.name.includes(skipped.executionId)).length, 0);
    const evidence = { run, startedAt, finishedAt: new Date().toISOString(), sourceBytes: csv.length, rows,
      scriptSha256: createHash('sha256').update(script).digest('hex'), workload: 'real normalization script; generated duplicate rows; two Blob outputs; no SQL benchmark',
      states, skipped, replayed, outputFiles, inspectionBytes: Buffer.byteLength(JSON.stringify(inspection)) };
    await writeFile('.private/capacity-hosted-evidence.json', JSON.stringify(evidence, null, 2));
    console.log('Hosted capacity evidence:', JSON.stringify({ sourceBytes: csv.length, rows, durationsMs: states.map(s => s.durationMs),
      sourceAttempts: states.map(s => s.sources.file.attempts.map(a => ({ startedAt: a.startedAt, completedAt: a.completedAt }))),
      duplicateSkipped: skipped.executionId, forcedReplay: replayed.executionId, inspectionBytes: evidence.inspectionBytes }));
  });
