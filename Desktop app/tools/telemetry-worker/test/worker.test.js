import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { Script, createContext } from 'node:vm';

registerHooks({ load(url, context, nextLoad) {
  if (url.endsWith('.html')) return { format:'module', source:'export default ' + JSON.stringify(readFileSync(new URL(url),'utf8')), shortCircuit:true };
  return nextLoad(url,context);
} });
const { default: worker } = await import('../src/index.js');

test('crash events are accepted, deduplicated and shown as errors', async () => {
  const f = fixture();
  for (const name of ['app_crashed', 'app_unexpected_exit', 'previous_operation_interrupted']) {
    const payload = { event_id: name + '-test', event_name: name, timestamp_utc: new Date().toISOString(),
      app_version: '2.4.3-test', installation_id: 'test', session_id: 'previous-session',
      properties: { source: 'dispatcher', exception_type: 'InvalidOperationException' } };
    for (let attempt = 0; attempt < 2; attempt++) {
      const response = await worker.fetch(new Request('https://test.local/events', {
        method: 'POST', body: JSON.stringify(payload), headers: { 'content-type': 'application/json' }
      }), { DB: f.DB });
      assert.equal(response.status, 200);
    }
  }
  const rows = f.sqlite.prepare('SELECT event_name FROM telemetry_events').all();
  assert.equal(rows.length, 3);
  const { EVENT_CATALOG } = await import('../src/event-catalog.js');
  assert.equal(EVENT_CATALOG.app_crashed.outcome, 'error');
  assert.equal(EVENT_CATALOG.app_unexpected_exit.outcome, 'error');
  const dashboard = await (await f.request('/dashboard')).text();
  assert.match(dashboard, /App gecrasht/);
  assert.match(dashboard, /App onverwacht afgesloten/);
});

test('UI hangs and recovery survive ingestion, deduplication and dashboard filters with readable duration', async () => {
  const f = fixture();
  try {
    for (const [name, milliseconds] of [['app_ui_unresponsive',15000],['app_ui_responsive_again',23500]]) {
      const payload = { event_id:name + '-test',event_name:name,timestamp_utc:new Date().toISOString(),
        app_version:'2.4.13-test',installation_id:'test',session_id:'hung-session',
        properties:{source:'dispatcher_watchdog',unresponsive_ms:milliseconds,ui_activity:'plot_render',ui_activity_elapsed_ms:15020,working_set_bytes:104857600,
          managed_memory_bytes:123456,gen2_collections:3} };
      for (let attempt=0;attempt<2;attempt++) {
        const response = await worker.fetch(new Request('https://test.local/events',{
          method:'POST',body:JSON.stringify(payload),headers:{'content-type':'application/json'}
        }),{DB:f.DB});
        assert.equal(response.status,200);
      }
    }
    const summary = await (await f.request('/summary')).json();
    assert.equal(summary.totals.event_count,2);
    assert.equal(summary.totals.usage_count,0);
    assert.equal(summary.totals.avg_decode_ms,null);
    const events = (await (await f.request('/events')).json()).events;
    const html = await (await f.request('/dashboard')).text();
    const nodes = new Map();
    const element = id => {
      if (!nodes.has(id)) nodes.set(id,{value:id === 'eventFilter' ? 'error' : '',innerHTML:'',textContent:'',addEventListener(){}});
      return nodes.get(id);
    };
    const context = createContext({document:{getElementById:element},sessionStorage:{getItem(){return null;}},setInterval(){}});
    new Script(html.match(/<script>([\s\S]*?)<\/script>/)[1]).runInContext(context);
    new Script('recent = ' + JSON.stringify(events) + '; renderRecent();').runInContext(context);
    assert.match(element('recentEvents').innerHTML,/App reageert niet/);
    assert.doesNotMatch(element('recentEvents').innerHTML,/App reageert weer/);
    assert.match(element('recentEvents').innerHTML,/Duur zonder UI-reactie: <strong>15 s/);
    assert.match(element('recentEvents').innerHTML,/Controle van UI-respons/);
    assert.match(element('recentEvents').innerHTML,/Actieve UI-bewerking: <strong>Grafiek tekenen/);
    element('eventFilter').value = 'system';
    new Script('renderRecent();').runInContext(context);
    assert.match(element('recentEvents').innerHTML,/App reageert niet/);
    assert.match(element('recentEvents').innerHTML,/App reageert weer/);
    assert.match(element('recentEvents').innerHTML,/23,5 s/);
    element('eventFilter').value = 'usage';
    new Script('renderRecent();').runInContext(context);
    assert.match(element('recentEvents').innerHTML,/Geen gebeurtenissen gevonden/);
  } finally { f.sqlite.close(); }
});

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

test('latency failure reasons are accepted, shown in Dutch and retain technical NDJSON codes', async () => {
  const f = fixture();
  try {
    const reasons = {
      data_gap:'Onderbreking in meetgegevens',
      duplicate_timestamp:'Dubbele tijdstempel',
      non_monotonic_timestamp:'Tijdstempels niet oplopend',
      non_finite_time:'Ongeldige tijdstempel (NaN of oneindig)',
      non_finite_value:'Ongeldige meetwaarde (NaN of oneindig)',
      invalid_time_window:'Ongeldig tijdvenster',
      other_invalid_input:'Andere ongeldige invoer'
    };
    for (const failure_reason of Object.keys(reasons)) {
      const payload = {event_id:'latency-' + failure_reason,event_name:'analytics_recompute_failed',
        timestamp_utc:new Date().toISOString(),app_version:'2.4.14-test',installation_id:'test',session_id:'test',
        properties:{analysis_stage:'Latency',exception_type:'ArgumentException',failure_reason}};
      const response = await worker.fetch(new Request('https://test.local/events',{
        method:'POST',body:JSON.stringify(payload),headers:{'content-type':'application/json'}
      }),{DB:f.DB});
      assert.equal(response.status,200);
    }
    const events = (await (await f.request('/events')).json()).events;
    const {context,element} = dashboardContext(await (await f.request('/dashboard')).text());
    element('eventFilter').value = 'error';
    new Script('recent = ' + JSON.stringify(events) + '; renderRecent();').runInContext(context);
    const rendered = element('recentEvents').innerHTML;
    assert.match(rendered,/Analyse mislukt/);
    assert.match(rendered,/Analysefase: <strong>Latentieanalyse<\/strong>/);
    for (const [code,label] of Object.entries(reasons)) {
      assert.ok(rendered.includes('Foutoorzaak: <strong>' + label + '</strong>'));
      assert.ok(!rendered.includes(code));
    }
    const exported = (await (await f.request('/export.ndjson')).text()).trim().split('\n').map(JSON.parse);
    assert.equal(exported.length,Object.keys(reasons).length);
    for (const row of exported) {
      assert.equal(row.event_name,'analytics_recompute_failed');
      assert.deepEqual(row.properties,{
        analysis_stage:'Latency',exception_type:'ArgumentException',failure_reason:row.properties.failure_reason
      });
      assert.ok(Object.hasOwn(reasons,row.properties.failure_reason));
    }
    const summary = await (await f.request('/summary')).json();
    assert.equal(summary.totals.event_count,7);
    assert.equal(summary.totals.usage_count,0);
  } finally { f.sqlite.close(); }
});

test('analysis labels support legacy events and hide unknown diagnostic values', async () => {
  const f = fixture();
  try {
    const legacy = {exception_type:'ArgumentException',analysis_stage:'Latency'};
    const response = await worker.fetch(new Request('https://test.local/events',{
      method:'POST',body:JSON.stringify({event_id:'legacy-latency',event_name:'analytics_recompute_failed',
        timestamp_utc:new Date().toISOString(),app_version:'2.4.13',installation_id:'test',session_id:'test',properties:legacy})
    }),{DB:f.DB});
    assert.equal(response.status,200);
    const events = (await (await f.request('/events')).json()).events;
    assert.deepEqual(events[0].properties,legacy);
    const {context,element} = dashboardContext(await (await f.request('/dashboard')).text());
    new Script('recent = ' + JSON.stringify(events) + '; renderRecent();').runInContext(context);
    assert.match(element('recentEvents').innerHTML,/Latentieanalyse/);
    assert.doesNotMatch(element('recentEvents').innerHTML,/Foutoorzaak/);
    for (const [code,label] of Object.entries({Gebruik:'Gebruiksanalyse',Actuatorvolging:'Actuatorvolging',CAN:'CAN-analyse'})) {
      assert.equal(new Script('propertyValue("analysis_stage",' + JSON.stringify(code) + ')').runInContext(context),label);
    }
    for (const value of ['unrecognized', '<script>private signal details</script>', 'constructor', 'toString', 123, true]) {
      assert.equal(new Script('propertyValue("analysis_stage",' + JSON.stringify(value) + ')').runInContext(context),'Onbekende analysefase');
      assert.equal(new Script('propertyValue("failure_reason",' + JSON.stringify(value) + ')').runInContext(context),'Onbekende foutoorzaak');
    }
    for (const key of ['analysis_stage','failure_reason']) {
      assert.equal(new Script('propertyValue(' + JSON.stringify(key) + ',null)').runInContext(context),'Onbekend');
    }
  } finally { f.sqlite.close(); }
});

function dashboardContext(html) {
  const nodes = new Map();
  const element = id => {
    if (!nodes.has(id)) nodes.set(id,{value:id === 'eventFilter' ? 'all' : '',innerHTML:'',textContent:'',addEventListener(){}});
    return nodes.get(id);
  };
  const context = createContext({document:{getElementById:element},sessionStorage:{getItem(){return null;}},setInterval(){}});
  new Script(html.match(/<script>([\s\S]*?)<\/script>/)[1]).runInContext(context);
  return {context,element};
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
