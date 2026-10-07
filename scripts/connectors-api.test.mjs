import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, readdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

test('M5 samples, authenticated HTTP chain, Event Grid acceptance and scoped connector identities', { timeout: 60000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'elwood-m5-api-'));
  const token = 'local-m5-management';
  let child, logs = '', writes = [];
  const remote = createServer(async (req, res) => {
    let body = ''; for await (const chunk of req) body += chunk;
    res.setHeader('Content-Type', 'application/json');
    if (req.url === '/token') {
      assert.equal(req.headers.authorization, 'Basic ' + Buffer.from('client:client-secret').toString('base64'));
      res.end(JSON.stringify({ access_token: 'secret-access-token', token_type: 'Bearer' }));
    } else {
      writes.push({ url: req.url, body: JSON.parse(body), authorization: req.headers.authorization });
      res.end(req.url === '/crm' ? '{"id":"CRM-42"}' : '{"accepted":true}');
    }
  });
  remote.listen(0, '127.0.0.1'); await once(remote, 'listening');
  const reservation = createServer(); reservation.listen(0, '127.0.0.1'); await once(reservation, 'listening');
  const base = `http://127.0.0.1:${reservation.address().port}`; await new Promise(r => reservation.close(r));
  const remoteBase = `http://127.0.0.1:${remote.address().port}`;
  try {
    await writeFile(join(root, 'appsettings.json'), JSON.stringify({ Elwood: {
      Management: { Tokens: [{ Name: 'test', TokenSha256: createHash('sha256').update(token).digest('hex'), Permissions: ['read', 'write', 'test', 'deploy'], Pipelines: ['*'] }] },
      SecretGrants: { 'sample-order-auth': { Test: ['crm.url','crm.tokenUrl','crm.clientId','crm.clientSecret','emarsys.url','emarsys.user','emarsys.password'] } },
    } }));
    await writeFile(join(root, 'secrets.json'), JSON.stringify({ 'crm.url': remoteBase + '/crm', 'crm.tokenUrl': remoteBase + '/token', 'crm.clientId': 'client', 'crm.clientSecret': 'client-secret', 'emarsys.url': remoteBase + '/emarsys', 'emarsys.user': 'user', 'emarsys.password': 'password' }));
    child = spawn('dotnet', [resolve(process.env.ELWOOD_TEST_RUNTIME_DLL ?? 'dotnet/src/Elwood.Runtime.Api/bin/Debug/net10.0/Elwood.Runtime.Api.dll'), '--urls', base], {
      cwd: root, windowsHide: true, env: { ...process.env, Elwood__PipelinesDir: join(root,'pipelines'), Elwood__StateDir: join(root,'state'), Elwood__SecretsFile: join(root,'secrets.json'), Elwood__Management__LocalDevelopment: 'false', Elwood__AppConfiguration: '', ELWOOD_APP_CONFIGURATION: '', APPLICATIONINSIGHTS_CONNECTION_STRING: '' }, stdio: ['ignore','pipe','pipe'],
    });
    child.stdout.on('data', c => logs = (logs + c).slice(-6000)); child.stderr.on('data', c => logs = (logs + c).slice(-6000));
    let ready = false;
    for (let n=0;n<100;n++) { try { ready = (await fetch(base+'/api/health')).ok; } catch {} if (ready) break; await delay(100); }
    assert.ok(ready, logs);
    const request = (path, method='GET', body, headers={}) => fetch(base+path, { method, headers: { Authorization: 'Bearer '+token, 'Content-Type':'application/json', ...headers }, body: body===undefined ? undefined : JSON.stringify(body) });
    const save = async (id, content) => {
      const response = await request('/api/pipelines/'+id,'POST',content); assert.equal(response.status,201, await response.clone().text()); return response.json();
    };
    for (const id of ['sample-order-auth','sample-product-sql','sample-deldo-stock']) {
      const dir = resolve('docs/examples/connectors',id);
      const scripts = Object.fromEntries(await Promise.all((await readdir(dir)).filter(f=>f.endsWith('.elwood')).map(async f=>[f,await readFile(join(dir,f),'utf8')])));
      const content = { yaml: await readFile(join(dir,'pipeline.elwood.yaml'),'utf8'), scripts };
      await save(id,content);
      const body = { pipelineId:id,testMode:'mock',payload:JSON.parse(await readFile(join(dir,'input.json'),'utf8')),mocks:JSON.parse(await readFile(join(dir,'mocks.json'),'utf8')) };
      const response = await request('/api/executions','POST',body); assert.equal(response.status,200,await response.clone().text());
      const run = await response.json(); assert.equal(run.success,true,JSON.stringify(run.errors));
      assert.ok(run.inspection.testContext.value.calls.length > 0);
      if (id==='sample-deldo-stock') {
        assert.deepEqual(run.outputs['offers-to-sql'],JSON.parse(await readFile(join(dir,'expected/offers-to-sql.json'),'utf8')));
        assert.ok(run.inspection.testContext.value.calls.some(c=>c.connector==='sql' && c.responseBody.includes('loaded')));
      }
      if (id==='sample-order-auth') {
        const real = await request('/api/executions','POST',{pipelineId:id,testMode:'real',payload:body.payload});
        const result=await real.json(); assert.equal(result.success,true,JSON.stringify(result.errors));
        const serialized=JSON.stringify(result.inspection); assert.ok(!serialized.includes('secret-access-token')); assert.ok(!serialized.includes('client-secret'));
      }
    }
    assert.deepEqual(writes.map(w=>w.url),['/crm','/emarsys']);
    assert.equal(writes[0].authorization,'Bearer secret-access-token');
    assert.equal(writes[1].authorization,'Basic '+Buffer.from('user:password').toString('base64'));
    assert.deepEqual(writes[1].body,{orderId:'ORDER-42',crmId:'CRM-42'});
    const draft=await save('events',{yaml:`name: events
mode: async
sources:
  - name: event
    trigger: http
    endpoint: /events
    auth: { type: basic, user: webhook, password: local-test }
    eventGrid: { topic: storage, subjectPrefix: /inbound/ }
    map: '{ event: $[0] }'
outputs: [{name: result}]
`,scripts:{}});
    const deployed=await request('/api/pipelines/events/deploy','POST',{expectedRevisionId:draft.draftRevisionId});
    assert.equal(deployed.status,200,await deployed.clone().text());
    const publicHeaders={Authorization:'Basic '+Buffer.from('webhook:local-test').toString('base64'),'aeg-event-type':'SubscriptionValidation'};
    const handshake=[{eventType:'Microsoft.EventGrid.SubscriptionValidationEvent',data:{validationCode:'test-code'}}];
    assert.equal((await request('/api/v1/trigger/events','POST',handshake,{'aeg-event-type':'SubscriptionValidation'})).status,401);
    const valid=await request('/api/v1/trigger/events','POST',handshake,publicHeaders); assert.deepEqual(await valid.json(),{validationResponse:'test-code'});
    assert.equal((await request('/api/v1/trigger/events','POST',[123],publicHeaders)).status,400);
    const events=[1,2].map(id=>({id:String(id),topic:'storage',subject:'/inbound/file'+id,eventType:'Microsoft.Storage.BlobCreated',data:{}}));
    const notify=()=>request('/api/v1/trigger/events','POST',events,{...publicHeaders,'aeg-event-type':'Notification'});
    const accepted=await notify(); assert.equal(accepted.status,202); const ids=(await accepted.json()).executionIds; assert.equal(new Set(ids).size,2);
    for (const id of ids) { for(let n=0;n<100;n++) {const state=await(await request('/api/executions/'+id)).json(); if(state.status===2)break; await delay(30);} }
    const duplicate=await notify(); assert.equal(duplicate.status,202); assert.deepEqual((await duplicate.json()).executionIds,ids);
    const history=await(await request('/api/executions')).json(); assert.equal(history.filter(e=>e.pipelineId==='events').length,2);
    await save('identity',{yaml:`name: identity
sources:
  - { name: input, trigger: http }
  - name: blob
    trigger: pull
    depends: input
    from:
      blobStorage: { accountUrl: 'https://ungranted.blob.core.windows.net', container: inbound, path: data }
outputs: [{name: result, response: true}]
`,scripts:{}});
    const denied=await(await request('/api/executions','POST',{pipelineId:'identity',testMode:'real',payload:{}})).json();
    assert.equal(denied.success,false); assert.match(denied.errors.join(' '),/not granted/);
    await save('sql-identity',{yaml:`name: sql-identity
sources:
  - { name: input, trigger: http }
  - name: sql
    trigger: pull
    depends: input
    from:
      sql: { connectionString: 'Server=ungranted.invalid;Integrated Security=true', storedProcedure: dbo.Lookup }
outputs: [{name: result, response: true}]
`,scripts:{}});
    const sqlDenied=await(await request('/api/executions','POST',{pipelineId:'sql-identity',testMode:'real',payload:{}})).json();
    assert.equal(sqlDenied.success,false); assert.match(sqlDenied.errors.join(' '),/not granted/);
  } catch (error) {
    error.message += '\nRuntime log:\n' + logs;
    throw error;
  } finally {
    if(child && child.exitCode===null){child.kill();await once(child,'exit');}
    await new Promise(r=>remote.close(r)); await rm(root,{recursive:true,force:true,maxRetries:5,retryDelay:100});
  }
});
