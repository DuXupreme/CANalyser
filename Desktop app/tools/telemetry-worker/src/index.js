import DASHBOARD_HTML from "./dashboard.html";
import { EVENT_CATALOG, USAGE_EVENTS } from "./event-catalog.js";

const MAX_BODY_BYTES = 32 * 1024;
const MAX_EXPORT_LIMIT = 10_000;

const ALLOWED_EVENTS = new Set(Object.keys(EVENT_CATALOG));

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
      return withAdminAuth(request, env, () => getSummary(url, env));
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

function periodFor(url) {
  const days = url.searchParams.get("days") || "all";
  if (!["all", "7", "30", "90"].includes(days)) return null;
  const end = new Date();
  const start = new Date(end);
  start.setUTCHours(0, 0, 0, 0);
  if (days !== "all") start.setUTCDate(start.getUTCDate() - Number(days) + 1);
  return { days, from: days === "all" ? "0000" : start.toISOString(), to: end.toISOString() };
}

async function getSummary(url, env) {
  const period = periodFor(url);
  if (!period) return json({ error: "invalid_period" }, 400);
  // These SQL literals come exclusively from the source-controlled event catalog.
  const usage = "event_name IN (" + USAGE_EVENTS.map(name => "'" + name + "'").join(",") + ")";
  const category = "CASE event_name " + USAGE_EVENTS.map(name =>
    "WHEN '" + name + "' THEN '" + EVENT_CATALOG[name].category + "'"
  ).join(" ") + " END";
  const duration = "CASE WHEN json_valid(properties_json) THEN CASE WHEN json_type(properties_json, '$.duration_ms') IN ('integer', 'real') AND json_extract(properties_json, '$.duration_ms') >= 0 THEN json_extract(properties_json, '$.duration_ms') END END";
  const where = "FROM telemetry_events WHERE received_at >= ? AND received_at <= ?";
  const results = await env.DB.batch([
    env.DB.prepare(`SELECT COUNT(*) AS event_count,
      COUNT(DISTINCT NULLIF(installation_id, '')) AS installation_count,
      COUNT(DISTINCT CASE WHEN session_id != '' THEN installation_id || ':' || session_id END) AS session_count,
      COALESCE(SUM(CASE WHEN ${usage} THEN 1 ELSE 0 END), 0) AS usage_count,
      AVG(CASE WHEN event_name = 'load_decode_completed' THEN ${duration} END) AS avg_decode_ms,
      COUNT(CASE WHEN event_name = 'load_decode_completed' THEN ${duration} END) AS decode_sample_count,
      MIN(received_at) AS first_received_at, MAX(received_at) AS last_received_at ${where}`),
    env.DB.prepare(`SELECT event_name, COUNT(*) AS count,
      COUNT(DISTINCT NULLIF(installation_id, '')) AS installation_count,
      MAX(received_at) AS last_received_at ${where} GROUP BY event_name ORDER BY count DESC, event_name`),
    env.DB.prepare(`SELECT ${category} AS category, COUNT(*) AS count,
      COUNT(DISTINCT NULLIF(installation_id, '')) AS installation_count
      ${where} AND ${usage} GROUP BY category ORDER BY count DESC, category`),
    env.DB.prepare(`SELECT SUBSTR(received_at, 1, 10) AS day, COUNT(*) AS event_count,
      SUM(CASE WHEN ${usage} THEN 1 ELSE 0 END) AS usage_count
      ${where} GROUP BY day ORDER BY day`),
    env.DB.prepare(`SELECT COALESCE(NULLIF(app_version, ''), 'Onbekend') AS app_version,
      COUNT(*) AS count, COUNT(DISTINCT NULLIF(installation_id, '')) AS installation_count
      ${where} GROUP BY app_version ORDER BY count DESC, app_version`)
  ].map(statement => statement.bind(period.from, period.to)));
  return json({ ok: true, period, totals: results[0].results?.[0] ?? {},
    by_event: results[1].results ?? [], by_category: results[2].results ?? [],
    daily: results[3].results ?? [], by_version: results[4].results ?? [] });
}

async function getRecentEvents(url, env) {
  const period = periodFor(url);
  if (!period) return json({ error: "invalid_period" }, 400);
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
    WHERE received_at >= ? AND received_at <= ?
    ORDER BY received_at DESC
    LIMIT ?
  `).bind(period.from, period.to, limit).all();

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
  return new Response(DASHBOARD_HTML.replace("/* EVENT_CATALOG */", "const EVENT_CATALOG = " + JSON.stringify(EVENT_CATALOG) + ";"), {
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
