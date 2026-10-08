import assert from 'node:assert/strict';
import { test } from 'node:test';
import { writeFile } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';

test('Native Storage Queue binding completes durable work and removes poison hints',
  { skip: process.env.ELWOOD_M6_HOSTED_STORAGE !== 'true', timeout: 600_000 }, async () => {
    const invoke = promisify(execFile);
    const group = 'elwood-m6-rg', app = 'elwood-m6-api-r3m5auolhstum';
    const base = `https://${app}.azurewebsites.net/`;
    const az = async args => (await invoke(process.env.ELWOOD_AZURE_CLI_PYTHON ?? 'C:/Program Files/Microsoft SDKs/Azure/CLI2/python.exe',
      ['-IBm', 'azure.cli', ...args, '--subscription', '79522cd2-4487-4289-b0b8-42ee8aa5d649'],
      { windowsHide: true, timeout: 180_000, maxBuffer: 1024 * 1024 })).stdout.trim();
    const current = JSON.parse(await az(['functionapp', 'config', 'appsettings', 'list', '-g', group, '-n', app, '-o', 'json']));
    const keys = ['ELWOOD_RUNTIME_CONFIG_JSON', 'AzureWebJobs.ExecutionStep.Disabled', 'AzureWebJobs.ExecutionPoisonHint.Disabled',
      'AzureWebJobs.ServiceBusExecutionStep.Disabled', 'AzureWebJobs.ServiceBusDeadLetters.Disabled'];
    const original = Object.fromEntries(keys.map(key => [key, current.find(s => s.name === key)?.value]));
    assert.ok(keys.every(key => typeof original[key] === 'string'));
    async function ready() {
      const end = Date.now() + 180_000;
      while (Date.now() < end) {
        try { if ((await fetch(new URL('api/readiness', base), { signal: AbortSignal.timeout(30_000) })).status === 200) return; } catch {}
        await delay(3000);
      }
      assert.fail('Hosted readiness did not pass after queue configuration change');
    }
    async function configure(settings) {
      await writeFile('.private/m6-queue-settings.json', JSON.stringify(settings));
      await az(['functionapp', 'config', 'appsettings', 'set', '-g', group, '-n', app, '--settings', '@.private/m6-queue-settings.json', '-o', 'none']);
      await ready();
    }
    try {
      const runtime = JSON.parse(original.ELWOOD_RUNTIME_CONFIG_JSON); runtime['Elwood:Azure:QueueProvider'] = 'storage';
      await configure({ ELWOOD_RUNTIME_CONFIG_JSON: JSON.stringify(runtime), 'AzureWebJobs.ExecutionStep.Disabled': 'false',
        'AzureWebJobs.ExecutionPoisonHint.Disabled': 'false', 'AzureWebJobs.ServiceBusExecutionStep.Disabled': 'true', 'AzureWebJobs.ServiceBusDeadLetters.Disabled': 'true' });
      const childEnvironment = { ...process.env, ELWOOD_M6_HOST_URL: base, ELWOOD_M6_HOST_TOKEN_FILE: '.private/m6-host-token' };
      delete childEnvironment.NODE_TEST_CONTEXT; // Use a separate test runner, not the parent's IPC reporter.
      const smoke = await invoke(process.execPath, ['--test', '--test-reporter=tap', 'scripts/m6-hosted.test.mjs'], { windowsHide: true, timeout: 240_000,
        env: childEnvironment });
      assert.match(smoke.stdout, /# pass 1/);
      console.log('Native Storage Queue execution, duplicate acceptance and pinned replay passed.');
      const account = ['--account-name', 'elwoodm6r3m5auolhstum', '--auth-mode', 'login'];
      await az(['storage', 'message', 'put', ...account, '--queue-name', 'contract-work-poison', '--content', Buffer.from('synthetic poison hint').toString('base64'), '--time-to-live', '600', '-o', 'none']);
      const end = Date.now() + 120_000; let remaining;
      do {
        remaining = Number(await az(['storage', 'message', 'peek', ...account, '--queue-name', 'contract-work-poison', '--num-messages', '32', '--query', 'length(@)', '-o', 'tsv']));
        if (remaining === 0) break;
        await delay(3000);
      } while (Date.now() < end);
      assert.equal(remaining, 0, 'Poison hint was not consumed before its TTL');
      console.log('Native poison binding removed the hint; indexed recovery remains authoritative.');
    } finally { await configure(original); }
  });
