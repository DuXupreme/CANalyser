const MAX_BODY_BYTES = 32 * 1024;
const MAX_EXPORT_LIMIT = 10_000;

const ALLOWED_EVENTS = new Set([
  "app_started",
  "load_decode_completed",
  "load_decode_failed",
  "load_decode_cancelled",
  "export_decoded_csv",
  "settings_applied",
  "analysis_layout_exported",
  "analysis_layout_imported",
  "analysis_apply_plot_groups",
  "analysis_open_detached_plots",
  "analysis_lod_forced",
  "update_check_skipped",
  "update_check_completed",
  "update_prompt_declined",
  "update_apply_failed"
]);

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/health") {
      return json({ ok: true });
    }

    if (request.method === "GET" && url.pathname === "/dashboard") {
      return dashboard();
    }

    if (request.method === "POST" && (url.pathname === "/" || url.pathname === "/events")) {
      return ingestEvent(request, env);
    }

    if (request.method === "GET" && url.pathname === "/events") {
      return withAdminAuth(request, env, () => getRecentEvents(url, env));
    }

    if (request.method === "GET" && url.pathname === "/summary") {
      return withAdminAuth(request, env, () => getSummary(env));
    }

    if (request.method === "GET" && url.pathname === "/export.ndjson") {
      return withAdminAuth(request, env, () => exportEvents(url, env));
    }

    return json({ error: "not_found" }, 404);
  }
};

async function ingestEvent(request, env) {
  if (!env.DB) {
    return json({ error: "database_not_configured" }, 500);
  }

  const ingestKey = textOrEmpty(env.INGEST_KEY);
  if (ingestKey.length > 0 && request.headers.get("X-CANalyser-Telemetry-Key") !== ingestKey) {
    return json({ error: "unauthorized" }, 401);
  }

  const body = await request.text();
  if (body.length > MAX_BODY_BYTES) {
    return json({ error: "payload_too_large" }, 413);
  }

  let event;
  try {
    event = JSON.parse(body);
  } catch {
    return json({ error: "invalid_json" }, 400);
  }

  const validation = validateEvent(event);
  if (!validation.ok) {
    return json({ error: validation.error }, 400);
  }

  const normalized = normalizeEvent(event);
  const storedJson = JSON.stringify(normalized);
  const receivedAt = new Date().toISOString();

  await env.DB.prepare(`
    INSERT OR IGNORE INTO telemetry_events (
      received_at,
      event_id,
      event_name,
      timestamp_utc,
      app_version,
      installation_id,
      session_id,
      os_description,
      process_architecture,
      runtime_version,
      properties_json,
      event_json
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `).bind(
    receivedAt,
    normalized.event_id,
    normalized.event_name,
    normalized.timestamp_utc,
    normalized.app_version,
    normalized.installation_id,
    normalized.session_id,
    normalized.os_description,
    normalized.process_architecture,
    normalized.runtime_version,
    JSON.stringify(normalized.properties ?? {}),
    storedJson
  ).run();

  return json({ ok: true });
}

async function getSummary(env) {
  const byEvent = await env.DB.prepare(`
    SELECT event_name, COUNT(*) AS count, MAX(received_at) AS last_received_at
    FROM telemetry_events
    GROUP BY event_name
    ORDER BY event_name
  `).all();

  const totals = await env.DB.prepare(`
    SELECT
      COUNT(*) AS event_count,
      COUNT(DISTINCT installation_id) AS installation_count,
      MIN(received_at) AS first_received_at,
      MAX(received_at) AS last_received_at
    FROM telemetry_events
  `).first();

  return json({
    ok: true,
    totals,
    by_event: byEvent.results ?? []
  });
}

async function getRecentEvents(url, env) {
  const limit = clampInt(url.searchParams.get("limit"), 1, 500, 100);
  const result = await env.DB.prepare(`
    SELECT
      received_at,
      event_name,
      timestamp_utc,
      app_version,
      installation_id,
      session_id,
      properties_json
    FROM telemetry_events
    ORDER BY received_at DESC
    LIMIT ?
  `).bind(limit).all();

  return json({
    ok: true,
    events: (result.results ?? []).map((row) => ({
      received_at: row.received_at,
      event_name: row.event_name,
      timestamp_utc: row.timestamp_utc,
      app_version: row.app_version,
      installation_id: row.installation_id,
      session_id: row.session_id,
      properties: parseProperties(row.properties_json)
    }))
  });
}

async function exportEvents(url, env) {
  const limit = clampInt(url.searchParams.get("limit"), 1, MAX_EXPORT_LIMIT, 5000);
  const after = textOrEmpty(url.searchParams.get("after"));

  const result = after.length > 0
    ? await env.DB.prepare(`
        SELECT event_json
        FROM telemetry_events
        WHERE received_at > ?
        ORDER BY received_at ASC
        LIMIT ?
      `).bind(after, limit).all()
    : await env.DB.prepare(`
        SELECT event_json
        FROM telemetry_events
        ORDER BY received_at ASC
        LIMIT ?
      `).bind(limit).all();

  const lines = (result.results ?? []).map((row) => row.event_json).join("\n");
  return new Response(lines.length > 0 ? `${lines}\n` : "", {
    headers: {
      "content-type": "application/x-ndjson; charset=utf-8",
      "cache-control": "no-store",
      "content-disposition": "attachment; filename=\"canalyser-telemetry.ndjson\""
    }
  });
}

function dashboard() {
  return new Response(DASHBOARD_HTML, {
    headers: {
      "content-type": "text/html; charset=utf-8",
      "cache-control": "no-store"
    }
  });
}

async function withAdminAuth(request, env, action) {
  const adminToken = textOrEmpty(env.ADMIN_TOKEN);
  if (adminToken.length === 0) {
    return json({ error: "admin_token_not_configured" }, 503);
  }

  const auth = request.headers.get("authorization") ?? "";
  if (auth !== `Bearer ${adminToken}`) {
    return json({ error: "unauthorized" }, 401);
  }

  return action();
}

function validateEvent(event) {
  if (!event || typeof event !== "object" || Array.isArray(event)) {
    return { ok: false, error: "event_must_be_object" };
  }

  if (!ALLOWED_EVENTS.has(event.event_name)) {
    return { ok: false, error: "unknown_event_name" };
  }

  for (const key of ["event_id", "timestamp_utc", "installation_id", "session_id"]) {
    if (textOrEmpty(event[key]).length === 0) {
      return { ok: false, error: `missing_${key}` };
    }
  }

  if (Number.isNaN(Date.parse(event.timestamp_utc))) {
    return { ok: false, error: "invalid_timestamp_utc" };
  }

  const propertiesJson = JSON.stringify(event.properties ?? {});
  if (propertiesJson.length > 8 * 1024) {
    return { ok: false, error: "properties_too_large" };
  }

  return { ok: true };
}

function normalizeEvent(event) {
  return {
    schema_version: 1,
    event_id: truncate(textOrEmpty(event.event_id), 80),
    event_name: truncate(textOrEmpty(event.event_name), 80),
    timestamp_utc: new Date(event.timestamp_utc).toISOString(),
    app_version: truncate(textOrEmpty(event.app_version), 80),
    installation_id: truncate(textOrEmpty(event.installation_id), 80),
    session_id: truncate(textOrEmpty(event.session_id), 80),
    os_description: truncate(textOrEmpty(event.os_description), 160),
    process_architecture: truncate(textOrEmpty(event.process_architecture), 32),
    runtime_version: truncate(textOrEmpty(event.runtime_version), 80),
    properties: sanitizeProperties(event.properties)
  };
}

function sanitizeProperties(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return {};
  }

  const result = {};
  for (const [key, propertyValue] of Object.entries(value)) {
    const cleanKey = key.replace(/[^A-Za-z0-9_-]/g, "_").slice(0, 64);
    if (!cleanKey) {
      continue;
    }

    if (
      propertyValue === null ||
      typeof propertyValue === "boolean" ||
      typeof propertyValue === "number"
    ) {
      result[cleanKey] = propertyValue;
    } else {
      result[cleanKey] = truncate(String(propertyValue), 120);
    }
  }

  return result;
}

function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store"
    }
  });
}

function clampInt(value, min, max, fallback) {
  const parsed = Number.parseInt(value ?? "", 10);
  if (!Number.isFinite(parsed)) {
    return fallback;
  }

  return Math.min(max, Math.max(min, parsed));
}

function textOrEmpty(value) {
  return typeof value === "string" ? value.trim() : "";
}

function truncate(value, maxLength) {
  return value.length <= maxLength ? value : value.slice(0, maxLength);
}

function parseProperties(value) {
  try {
    const parsed = JSON.parse(value ?? "{}");
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

const DASHBOARD_HTML = `<!doctype html>
<html lang="nl">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>CANalyser Telemetry</title>
  <style>
    :root {
      color-scheme: light;
      --bg: #f6f7f9;
      --panel: #ffffff;
      --text: #1d2733;
      --muted: #617084;
      --line: #dce2ea;
      --accent: #0f6b7a;
      --accent-soft: #dff3f6;
      --danger: #9f2f2f;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: Segoe UI, Arial, sans-serif;
      background: var(--bg);
      color: var(--text);
    }
    header {
      background: #ffffff;
      border-bottom: 1px solid var(--line);
      padding: 16px 24px;
      display: flex;
      gap: 16px;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
    }
    h1 {
      margin: 0;
      font-size: 22px;
      font-weight: 650;
    }
    main {
      padding: 18px 24px 28px;
      max-width: 1400px;
      margin: 0 auto;
    }
    .auth {
      display: flex;
      gap: 8px;
      align-items: center;
      flex-wrap: wrap;
    }
    input, button, select {
      font: inherit;
      border: 1px solid var(--line);
      border-radius: 4px;
      padding: 7px 9px;
      background: #fff;
      color: var(--text);
    }
    input[type="password"] {
      width: min(420px, 80vw);
    }
    button {
      cursor: pointer;
      background: var(--accent);
      color: white;
      border-color: var(--accent);
      font-weight: 600;
    }
    button.secondary {
      background: #fff;
      color: var(--accent);
    }
    button:disabled {
      opacity: .55;
      cursor: default;
    }
    .status {
      color: var(--muted);
      font-size: 13px;
    }
    .status.error {
      color: var(--danger);
      font-weight: 600;
    }
    .grid {
      display: grid;
      gap: 14px;
    }
    .cards {
      grid-template-columns: repeat(4, minmax(160px, 1fr));
      margin-bottom: 14px;
    }
    .card, .panel {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 14px;
    }
    .card .label {
      color: var(--muted);
      font-size: 13px;
      margin-bottom: 7px;
    }
    .card .value {
      font-size: 27px;
      line-height: 1.05;
      font-weight: 700;
    }
    .two-col {
      grid-template-columns: minmax(300px, 0.9fr) minmax(460px, 1.6fr);
    }
    .panel h2 {
      font-size: 16px;
      margin: 0 0 12px;
    }
    .bar-row {
      display: grid;
      grid-template-columns: minmax(160px, 220px) 1fr 54px;
      align-items: center;
      gap: 8px;
      margin: 8px 0;
      font-size: 13px;
    }
    .bar-track {
      height: 11px;
      border-radius: 999px;
      background: #eef2f5;
      overflow: hidden;
    }
    .bar {
      height: 100%;
      min-width: 2px;
      background: var(--accent);
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 13px;
    }
    th, td {
      text-align: left;
      padding: 8px 7px;
      border-bottom: 1px solid var(--line);
      vertical-align: top;
    }
    th {
      color: var(--muted);
      font-weight: 650;
      background: #fbfcfd;
      position: sticky;
      top: 0;
      z-index: 1;
    }
    .table-wrap {
      max-height: 620px;
      overflow: auto;
      border: 1px solid var(--line);
      border-radius: 4px;
    }
    code {
      font-family: Consolas, monospace;
      font-size: 12px;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .muted { color: var(--muted); }
    .controls {
      display: flex;
      justify-content: space-between;
      gap: 12px;
      flex-wrap: wrap;
      margin-bottom: 10px;
      align-items: center;
    }
    @media (max-width: 900px) {
      .cards, .two-col { grid-template-columns: 1fr; }
      header { align-items: flex-start; }
      .bar-row { grid-template-columns: 1fr; }
    }
  </style>
</head>
<body>
  <header>
    <div>
      <h1>CANalyser Telemetry</h1>
      <div class="status" id="lastUpdated">Nog niet geladen</div>
    </div>
    <div class="auth">
      <input id="token" type="password" autocomplete="current-password" placeholder="ADMIN_TOKEN">
      <button id="saveToken">Opslaan</button>
      <button class="secondary" id="refresh">Ververs</button>
      <button class="secondary" id="export">Export NDJSON</button>
    </div>
  </header>
  <main>
    <section class="grid cards">
      <div class="card"><div class="label">Events</div><div class="value" id="eventCount">-</div></div>
      <div class="card"><div class="label">Installaties</div><div class="value" id="installationCount">-</div></div>
      <div class="card"><div class="label">Gem. laden/decoderen</div><div class="value" id="avgDecode">-</div></div>
      <div class="card"><div class="label">Laatste event</div><div class="value" id="lastEvent">-</div></div>
    </section>
    <section class="grid two-col">
      <div class="panel">
        <div class="controls">
          <h2>Events per type</h2>
          <label class="muted"><input type="checkbox" id="autoRefresh" checked> auto-refresh 30s</label>
        </div>
        <div id="eventBars" class="muted">Geen data geladen.</div>
      </div>
      <div class="panel">
        <div class="controls">
          <h2>Recente events</h2>
          <span id="status" class="status"></span>
        </div>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Ontvangen</th>
                <th>Event</th>
                <th>Versie</th>
                <th>Installatie</th>
                <th>Properties</th>
              </tr>
            </thead>
            <tbody id="recentEvents">
              <tr><td colspan="5" class="muted">Vul je admin token in en klik Ververs.</td></tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>
  </main>
  <script>
    const tokenInput = document.getElementById('token');
    const statusEl = document.getElementById('status');
    const lastUpdatedEl = document.getElementById('lastUpdated');
    const savedToken = sessionStorage.getItem('canalyser_admin_token') || '';
    tokenInput.value = savedToken;

    document.getElementById('saveToken').addEventListener('click', () => {
      sessionStorage.setItem('canalyser_admin_token', tokenInput.value.trim());
      load();
    });
    document.getElementById('refresh').addEventListener('click', load);
    document.getElementById('export').addEventListener('click', exportNdjson);
    setInterval(() => {
      if (document.getElementById('autoRefresh').checked) load();
    }, 30000);

    if (savedToken) load();

    async function load() {
      const token = tokenInput.value.trim();
      if (!token) {
        setStatus('Vul je ADMIN_TOKEN in.', true);
        return;
      }

      setStatus('Data ophalen...', false);
      try {
        const [summary, events] = await Promise.all([
          fetchJson('/summary', token),
          fetchJson('/events?limit=100', token)
        ]);
        renderSummary(summary);
        renderEvents(events.events || []);
        setStatus('Bijgewerkt.', false);
        lastUpdatedEl.textContent = 'Laatst bijgewerkt: ' + new Date().toLocaleString();
      } catch (error) {
        setStatus(error.message || String(error), true);
      }
    }

    async function fetchJson(path, token) {
      const response = await fetch(path, {
        headers: { authorization: 'Bearer ' + token },
        cache: 'no-store'
      });
      const text = await response.text();
      let data = {};
      try { data = text ? JSON.parse(text) : {}; } catch { data = { error: text }; }
      if (!response.ok) {
        throw new Error(data.error || ('HTTP ' + response.status));
      }
      return data;
    }

    function renderSummary(summary) {
      const totals = summary.totals || {};
      document.getElementById('eventCount').textContent = number(totals.event_count);
      document.getElementById('installationCount').textContent = number(totals.installation_count);
      document.getElementById('lastEvent').textContent = shortDate(totals.last_received_at);
      renderBars(summary.by_event || []);
    }

    function renderBars(rows) {
      const container = document.getElementById('eventBars');
      if (!rows.length) {
        container.textContent = 'Nog geen events.';
        return;
      }
      const max = Math.max(...rows.map((row) => Number(row.count) || 0), 1);
      container.innerHTML = rows.map((row) => {
        const count = Number(row.count) || 0;
        const width = Math.max(2, Math.round((count / max) * 100));
        return '<div class="bar-row"><code>' + escapeHtml(row.event_name) + '</code><div class="bar-track"><div class="bar" style="width:' + width + '%"></div></div><div>' + number(count) + '</div></div>';
      }).join('');
    }

    function renderEvents(events) {
      const tbody = document.getElementById('recentEvents');
      if (!events.length) {
        tbody.innerHTML = '<tr><td colspan="5" class="muted">Nog geen events.</td></tr>';
        document.getElementById('avgDecode').textContent = '-';
        return;
      }

      const decodeDurations = events
        .filter((event) => event.event_name === 'load_decode_completed')
        .map((event) => Number(event.properties && event.properties.duration_ms))
        .filter((value) => Number.isFinite(value));
      document.getElementById('avgDecode').textContent = decodeDurations.length
        ? formatDuration(decodeDurations.reduce((sum, value) => sum + value, 0) / decodeDurations.length)
        : '-';

      tbody.innerHTML = events.map((event) => {
        return '<tr>'
          + '<td>' + escapeHtml(shortDate(event.received_at)) + '</td>'
          + '<td><code>' + escapeHtml(event.event_name) + '</code></td>'
          + '<td>' + escapeHtml(event.app_version || '-') + '</td>'
          + '<td><code>' + escapeHtml(shortId(event.installation_id)) + '</code></td>'
          + '<td><code>' + escapeHtml(JSON.stringify(event.properties || {})) + '</code></td>'
          + '</tr>';
      }).join('');
    }

    function exportNdjson() {
      const token = tokenInput.value.trim();
      if (!token) {
        setStatus('Vul je ADMIN_TOKEN in.', true);
        return;
      }
      fetch('/export.ndjson?limit=5000', {
        headers: { authorization: 'Bearer ' + token },
        cache: 'no-store'
      })
        .then((response) => {
          if (!response.ok) throw new Error('Export mislukt: HTTP ' + response.status);
          return response.blob();
        })
        .then((blob) => {
          const href = URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = href;
          link.download = 'canalyser-telemetry.ndjson';
          document.body.appendChild(link);
          link.click();
          link.remove();
          URL.revokeObjectURL(href);
        })
        .catch((error) => setStatus(error.message || String(error), true));
    }

    function setStatus(message, isError) {
      statusEl.textContent = message;
      statusEl.className = isError ? 'status error' : 'status';
    }

    function number(value) {
      const parsed = Number(value || 0);
      return Number.isFinite(parsed) ? parsed.toLocaleString('nl-NL') : '-';
    }

    function shortDate(value) {
      if (!value) return '-';
      const date = new Date(value);
      return Number.isNaN(date.getTime()) ? '-' : date.toLocaleString('nl-NL');
    }

    function formatDuration(ms) {
      if (ms < 1000) return Math.round(ms) + ' ms';
      if (ms < 60000) return (ms / 1000).toFixed(1) + ' s';
      return (ms / 60000).toFixed(1) + ' min';
    }

    function shortId(value) {
      return value ? String(value).slice(0, 10) : '-';
    }

    function escapeHtml(value) {
      return String(value ?? '').replace(/[&<>"']/g, (char) => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#039;'
      })[char]);
    }
  </script>
</body>
</html>`;
