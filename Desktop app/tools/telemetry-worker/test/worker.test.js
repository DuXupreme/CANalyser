import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { Script } from 'node:vm';

registerHooks({ load(url, context, nextLoad) {
  if (url.endsWith('.html')) return { format:'module', source:'export default ' + JSON.stringify(readFileSync(new URL(url),'utf8')), shortCircuit:true };
  return nextLoad(url,context);
} });
const { default: worker } = await import('../src/index.js');

function fixture() {
  const sqlite = new DatabaseSync(':memory:');
  sqlite.exec(readFileSync(new URL('../schema.sql',import.meta.url),'utf8'));
  const DB = {
    prepare(sql) {
      let values = [];
      return {
        bind(...args) { values = args; return this; },
        async all() { return { results:sqlite.prepare(sql).all(...values) }; },
        async first() { return sqlite.prepare(sql).get(...values); },
        async run() { return sqlite.prepare(sql).run(...values); }
      };
    },
    async batch(statements) { return Promise.all(statements.map(statement => statement.all())); }
  };
  let sequence = 0;
  function insert(name, {daysAgo=0,installation='one',session='session',properties={}} = {}) {
    const received = new Date(); received.setUTCDate(received.getUTCDate()-daysAgo); received.setUTCHours(0,0,0,0);
    const id = String(++sequence);
    sqlite.prepare(`INSERT INTO telemetry_events (received_at,event_id,event_name,timestamp_utc,app_version,installation_id,session_id,properties_json,event_json) VALUES (?,?,?,?,?,?,?,?,?)`)
      .run(received.toISOString(),id,name,received.toISOString(),'2.4.1',installation,session,JSON.stringify(properties),JSON.stringify({event_id:id,event_name:name,properties}));
  }
  async function request(path, authenticated=true) {
    return worker.fetch(new Request('https://test.local'+path,{headers:authenticated ? {authorization:'Bearer test-token'} : {}}),{ DB,ADMIN_TOKEN:'test-token' });
  }
  return {sqlite,DB,insert,request};
}

test('period totals use all rows, exclude system events from usage and deduplicate installations', async () => {
  const f = fixture();
  for (let i=0;i<120;i++) f.insert('analysis_apply_plot_groups');
  f.insert('analysis_open_detached_plots');
  f.insert('app_started'); f.insert('update_check_completed');
  f.insert('load_decode_completed',{installation:'two',properties:{duration_ms:1000}});
  f.insert('load_decode_completed',{installation:'two',properties:{duration_ms:3000}});
  f.insert('load_decode_completed',{properties:{duration_ms:null}});
  f.insert('load_decode_completed',{properties:{duration_ms:'invalid'}});
  f.insert('load_decode_failed'); f.insert('load_decode_cancelled');
  f.insert('export_decoded_csv',{daysAgo:40,installation:'old'});
  const s = await (await f.request('/summary?days=30')).json();
  assert.equal(s.totals.usage_count,125);
  assert.equal(s.totals.event_count,129);
  assert.equal(s.totals.installation_count,2);
  assert.equal(s.totals.session_count,2);
  assert.equal(s.totals.avg_decode_ms,2000);
  assert.equal(s.totals.decode_sample_count,2);
  const graphs = s.by_category.find(row => row.category === 'Grafieken');
  assert.equal(graphs.count,121); assert.equal(graphs.installation_count,1);
  assert.equal(s.by_category.reduce((sum,row) => sum+row.count,0),s.totals.usage_count);
  assert.equal(s.daily.reduce((sum,row) => sum+row.event_count,0),129);
  assert.equal((await (await f.request('/events?days=30&limit=100')).json()).events.length,100);
  const all = await (await f.request('/summary')).json();
  assert.equal(all.totals.event_count,130); assert.equal(all.totals.installation_count,3);
  f.sqlite.close();
});

test('period boundaries, empty database, validation and authentication', async () => {
  const f = fixture();
  const empty = await (await f.request('/summary?days=7')).json();
  assert.equal(empty.totals.event_count,0); assert.equal(empty.totals.usage_count,0);
  assert.equal(empty.totals.avg_decode_ms,null); assert.deepEqual(empty.daily,[]);
  f.insert('app_started',{daysAgo:6}); f.insert('export_decoded_csv',{daysAgo:7});
  assert.equal((await (await f.request('/summary?days=7')).json()).totals.event_count,1);
  assert.equal((await (await f.request('/events?days=7')).json()).events.length,1);
  for (const path of ['/summary','/events','/export.ndjson']) assert.equal((await f.request(path,false)).status,401);
  for (const path of ['/summary?days=bad','/events?days=0']) assert.equal((await f.request(path)).status,400);
  f.sqlite.close();
});

test('NDJSON preserves technical payloads and actuator comparison is accepted', async () => {
  const f = fixture();
  const properties = {duration_ms:1234,group_count:2};
  f.insert('analysis_apply_plot_groups',{properties});
  const raw = JSON.parse((await (await f.request('/export.ndjson')).text()).trim());
  assert.equal(raw.event_name,'analysis_apply_plot_groups'); assert.deepEqual(raw.properties,properties);
  const response = await worker.fetch(new Request('https://test.local/events',{method:'POST',body:JSON.stringify({event_id:'new',event_name:'actuator_csv_comparison_loaded',timestamp_utc:new Date().toISOString(),installation_id:'test',session_id:'test',properties:{}})}),{DB:f.DB});
  assert.equal(response.status,200);
  f.sqlite.close();
});

test('dashboard serves standalone HTML with valid script and a complete event catalog', async () => {
  const f = fixture();
  const response = await f.request('/dashboard',false), html = await response.text();
  assert.match(response.headers.get('content-type'),/text\/html/);
  assert.match(html,/Grafiekgroepen toegepast/); assert.doesNotMatch(html,/\/\* EVENT_CATALOG \*\//);
  new Script(html.match(/<script>([\s\S]*?)<\/script>/)[1]);
  f.sqlite.close();
});
