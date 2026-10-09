"""Opt-in disposable-lab metadata probe; never writes Elwood's real control/state containers.

Prepare capacity-control/capacity-state containers and narrowly scoped data access first.
Run with the Azure CLI Python (requests installed): seed, select, measure, then restore.
After test-history expiry, clean runs the ordinary bounded maintenance function to completion.
All credentials/evidence stay in ignored .private.
The model is 49,000 cleanup obligations, not 1 TB of uploaded payloads or a database test.
"""
import concurrent.futures
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import uuid
import requests

assert os.environ.get('ELWOOD_CAPACITY_LIVE') == 'true', 'Explicit disposable-lab opt-in required'
GROUP = 'elwood-m6-rg'
ACCOUNT = 'elwr3m5auolhstum'
APP = 'elwood-m6-api-r3m5auolhstum'
ROOT = '/subscriptions/79522cd2-4487-4289-b0b8-42ee8aa5d649/resourceGroups/' + GROUP
ARM = 'https://management.azure.com' + ROOT
BLOB = 'https://' + ACCOUNT + '.blob.core.windows.net'
BASE = 'https://' + APP + '.azurewebsites.net'
CLI = [os.environ.get('ELWOOD_AZURE_CLI_PYTHON', 'C:/Program Files/Microsoft SDKs/Azure/CLI2/python.exe'), '-IBm', 'azure.cli']
PRIVATE = Path('.private')
LOCAL = threading.local()

def az(*args):
    result = subprocess.run(CLI + list(args), capture_output=True, text=True, timeout=180)
    if result.returncode:
        raise RuntimeError(result.stderr[:2000])
    return result.stdout.strip()

def request(url, method='GET', body=None, headers=None):
    data = None if body is None else body if isinstance(body, bytes) else json.dumps(body).encode()
    if not hasattr(LOCAL, 'http'): LOCAL.http = requests.Session()
    response = LOCAL.http.request(method, url, data=data, headers=headers or {}, timeout=(10, 90))
    response.raise_for_status()
    return response.json() if response.content else None

lab = json.loads(az('group', 'show', '-n', GROUP, '--query', '{id:id,tags:tags}', '-o', 'json'))
assert lab['id'].lower() == ROOT.lower() and lab['tags'].get('purpose') == 'capacity-verification'
mode = sys.argv[1]

if mode == 'seed':
    assert not (PRIVATE/'capacity-original-namespace.json').exists(), 'Never reseed while the synthetic model is active'
    access = az('account', 'get-access-token', '--resource', 'https://storage.azure.com/', '--query', 'accessToken', '-o', 'tsv')
    now = dt.datetime.now(dt.timezone.utc)
    stamp = lambda value: value.isoformat().replace('+00:00', 'Z')
    snapshots = [dict(formatVersion=1, entries={}, windowStartedAt=stamp(now), windowWrites=0, windowBytes=0) for _ in range(64)]
    uploads = []
    for execution in range(1000):
        name = f'model-{execution}'
        state_path = f'capacity-state/capacity-model/states/{name}.json'
        uploads.append((state_path, dict(executionId=name, pipelineId='capacity-model', pipelineName='Synthetic accounting',
            status=2, startedAt=stamp(now-dt.timedelta(days=1)), completedAt=stamp(now-dt.timedelta(hours=1)),
            expiresAt=stamp(now+dt.timedelta(days=30)), sources={}, outputs={}, errors=[])))
        entries = [('execution-'+name, 'execution', 12*1024*1024, 3, state_path)]
        entries += [(f'doc-{name}-{i}', 'document', 45_000_000, 1, f'documents/capacity-model/exec/{name}/attempt/{uuid.uuid4().hex}/output/{uuid.uuid4().hex}') for i in range(15)]
        entries += [(f'checkpoint-{name}-{i}', 'checkpoint', 8192, 1, state_path) for i in range(32)]
        entries += [('output-'+name, 'output', 93_000_000, 1, f'outputs/elwood/capacity-model/{name}.json')]
        for identity, kind, size, slots, path in entries:
            partition = hashlib.sha256(identity.encode()).digest()[0] % 64
            snapshots[partition]['entries'][identity] = dict(id=identity, kind=kind, executionId=name,
                blobUri=BLOB+'/'+path, registeredAt=stamp(now-dt.timedelta(hours=1)),
                expiresAt=stamp(now+dt.timedelta(days=30)), generation=uuid.uuid4().hex, bytes=size, objectSlots=slots,
                terminalSlotUsed=False, tombstoneSlotUsed=False, retentionSeconds=2592000, eTag='"0x8DF256496FE611E"',
                versionId=stamp(now), error=None, lastAttemptAt=None)
    payloads = [(path, json.dumps(value, separators=(',', ':')).encode()) for path, value in uploads]
    encoded = [json.dumps(snapshot, separators=(',', ':')).encode() for snapshot in snapshots]
    assert all(len(value) < 4*1024*1024 for value in encoded)
    payloads += [(f'capacity-control/capacity-model/retention/{i:02}.json', value) for i, value in enumerate(encoded)]
    def upload(item):
        path, content = item
        assert path.startswith(('capacity-control/capacity-model/', 'capacity-state/capacity-model/'))
        headers = {'Authorization': 'Bearer '+access, 'x-ms-version': '2023-11-03',
            'x-ms-blob-type': 'BlockBlob', 'If-None-Match': '*', 'Content-Type': 'application/json'}
        for attempt in range(3):
            try:
                request(BLOB+'/'+path, 'PUT', content, headers)
                return
            except requests.HTTPError as error:
                if error.response.status_code not in (409, 412): raise
                previous = request(BLOB+'/'+path, headers={'Authorization':'Bearer '+access,'x-ms-version':'2023-11-03'})
                expected = json.loads(content)
                assert previous.get('executionId') == expected.get('executionId') and previous.get('entries', {}).keys() == expected.get('entries', {}).keys()
                if path.startswith('capacity-control/'):
                    current = LOCAL.http.get(BLOB+'/'+path, headers={'Authorization':'Bearer '+access,'x-ms-version':'2023-11-03'}, timeout=(10,90))
                    current.raise_for_status()
                    updated = {k:v for k,v in headers.items() if k!='If-None-Match'}
                    updated['If-Match'] = current.headers['ETag']
                    request(BLOB+'/'+path, 'PUT', content, updated)
                return  # Preserve state stubs; refresh only the isolated model ledger under its ETag.
            except (requests.ConnectionError, requests.Timeout):
                if attempt == 2: raise
                time.sleep(attempt+1)
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
        list(executor.map(upload, payloads))
    evidence = dict(entries=49_000, syntheticStates=1000, ledgerBytes=sum(map(len, encoded)), maxPartitionBytes=max(map(len, encoded)),
        seededAt=stamp(now), payloadBytesActuallyUploaded=sum(len(value) for _, value in payloads))
    (PRIVATE/'capacity-model-seed.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')
    print(json.dumps(evidence))
elif mode in ('select', 'restore'):
    backup = PRIVATE/'capacity-original-namespace.json'
    policy_backup = PRIVATE/'capacity-original-policy.json'
    policy_url = ARM+'/providers/Microsoft.Storage/storageAccounts/'+ACCOUNT+'/managementPolicies/default?api-version=2025-06-01'
    if mode == 'select':
        assert not backup.exists(), 'Restore the earlier model before selecting another'
        values = json.loads(az('functionapp', 'config', 'appsettings', 'list', '-g', GROUP, '-n', APP, '-o', 'json'))
        runtime = next(item['value'] for item in values if item['name']=='ELWOOD_RUNTIME_CONFIG_JSON')
        config = json.loads(runtime)
        assert config['Elwood:Azure:Namespace']=='elwood' and config['Elwood:Azure:StateContainer']=='state' and config['Elwood:Azure:CatalogContainer']=='control'
        backup.write_text(json.dumps({'ELWOOD_RUNTIME_CONFIG_JSON': runtime}), encoding='utf-8')
        policy = json.loads(az('rest', '--method', 'get', '--url', policy_url, '-o', 'json'))['properties']
        policy_backup.write_text(json.dumps({'properties': policy}), encoding='utf-8')
        for rule in policy['policy']['rules']:
            if rule['name']=='owned-artifact-backstop': rule['definition']['filters']['prefixMatch'].append('capacity-state/')
            if rule['name']=='control-history': rule['definition']['filters']['prefixMatch'].append('capacity-control/')
        policy_file = PRIVATE/'capacity-model-policy.json'
        policy_file.write_text(json.dumps({'properties': policy}), encoding='utf-8')
        az('rest', '--method', 'put', '--url', policy_url, '--body', '@'+str(policy_file), '-o', 'none')
        config.update({'Elwood:Azure:Namespace':'capacity-model','Elwood:Azure:StateContainer':'capacity-state','Elwood:Azure:CatalogContainer':'capacity-control'})
        settings = PRIVATE/'capacity-model-settings.json'
        settings.write_text(json.dumps({'ELWOOD_RUNTIME_CONFIG_JSON':json.dumps(config)}), encoding='utf-8')
    else:
        assert backup.exists()
        settings = backup
    az('functionapp','config','appsettings','set','-g',GROUP,'-n',APP,'--settings','@'+str(settings),'-o','none')
    if mode == 'restore':
        az('rest','--method','put','--url',policy_url,'--body','@'+str(policy_backup),'-o','none')
        backup.rename(PRIVATE/'capacity-restored-namespace.json')
    print('Selected synthetic namespace.' if mode=='select' else 'Restored real verification namespace and original lifecycle policy.')
elif mode == 'clean':
    assert not (PRIVATE/'capacity-original-namespace.json').exists(), 'Restore the real verification namespace before cleanup'
    token = (PRIVATE/'capacity-token').read_text(encoding='utf-8').strip()
    headers = {'Authorization':'Bearer '+token}
    key = az('functionapp','keys','list','-g',GROUP,'-n',APP,'--query','masterKey','-o','tsv')
    for sweep in range(6):
        before = request(BASE+'/api/retention', headers=headers)
        if before['storage']['registeredObjects']==0:
            assert before['storage']['healthy']
            assert request(BASE+'/api/executions',headers=headers)==[]
            evidence=dict(verifiedAt=dt.datetime.now(dt.timezone.utc).isoformat(),retention=before,historyCount=0)
            (PRIVATE/'capacity-cleanup-evidence.json').write_text(json.dumps(evidence,indent=2),encoding='utf-8')
            print(json.dumps(evidence)); break
        print('Cleanup obligations remaining:',before['storage']['registeredObjects'],flush=True)
        request(BASE+'/admin/functions/ExecutionRetention','POST',{'input':'{}'},{'x-functions-key':key,'Content-Type':'application/json'})
        for _ in range(90):
            time.sleep(1)
            after=request(BASE+'/api/retention',headers=headers)
            if after['storage']['lastSweepAt']!=before['storage']['lastSweepAt']: break
        else: raise TimeoutError('Cleanup sweep did not complete')
    else: raise RuntimeError('Cleanup has not settled; inspect retained lifetimes and retry later')
elif mode == 'measure':
    assert (PRIVATE/'capacity-original-namespace.json').exists(), 'Select the isolated model first'
    token = (PRIVATE/'capacity-token').read_text(encoding='utf-8').strip()
    headers = {'Authorization':'Bearer '+token}
    ready = request(BASE+'/api/readiness')
    assert ready['status']=='ready'
    before = request(BASE+'/api/retention', headers=headers)
    assert before['storage']['registeredObjects'] == 51_000  # 49k records, execution reserves 3 slots.
    key = az('functionapp','keys','list','-g',GROUP,'-n',APP,'--query','masterKey','-o','tsv')
    started = dt.datetime.now(dt.timezone.utc).isoformat()
    timer = time.monotonic()
    request(BASE+'/admin/functions/ExecutionRetention', 'POST', {'input': '{}'}, {'x-functions-key':key,'Content-Type':'application/json'})
    for _ in range(90):
        time.sleep(1)
        after = request(BASE+'/api/retention', headers=headers)
        if after['storage']['lastSweepAt'] != before['storage']['lastSweepAt']:
            assert after['storage']['healthy'] and after['storage']['registeredObjects']==51_000
            evidence = dict(startedAt=started, endedAt=dt.datetime.now(dt.timezone.utc).isoformat(), elapsedSeconds=time.monotonic()-timer,
                retained=after['storage'], note='1,000 state existence checks available; one bounded 100-reservation maintenance pass and full 49k-obligation scan; no payload deletion was due')
            (PRIVATE/'capacity-model-measurement.json').write_text(json.dumps(evidence,indent=2),encoding='utf-8')
            print(json.dumps(evidence)); break
    else: raise TimeoutError('Synthetic maintenance did not complete within the probe window')
else:
    raise ValueError('Use seed, select, measure, restore or clean')
