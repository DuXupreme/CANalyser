# CANalyser telemetry worker

Free telemetry receiver for CANalyser using Cloudflare Workers + D1.

Cloudflare Workers Free includes 100,000 requests per day. D1 has a free tier suitable for lightweight product telemetry. Keep payloads privacy-minimal and do not store CAN data, DBC data, filenames, paths, signal names or frame IDs.

## Deploy

Prerequisites:

- Cloudflare account.
- Node.js available locally.

Commands:

```powershell
cd "Desktop app/tools/telemetry-worker"
npm install
npx wrangler login
npx wrangler d1 create canalyser_telemetry
```

Copy the returned `database_id` into `wrangler.toml`, replacing `REPLACE_WITH_D1_DATABASE_ID`.

Initialize the database:

```powershell
npx wrangler d1 execute canalyser_telemetry --remote --file=./schema.sql
```

Set admin and optional ingest secrets:

```powershell
npx wrangler secret put ADMIN_TOKEN
npx wrangler secret put INGEST_KEY
```

Deploy:

```powershell
npm run deploy
```

Wrangler prints a Worker URL like:

```text
https://canalyser-telemetry.<your-subdomain>.workers.dev
```

Set CANalyser's release endpoint in `Desktop app/src/CanAnalyzer.App/State/TelemetryOptions.cs`:

```csharp
public const string DefaultEndpointUrl = "https://canalyser-telemetry.<your-subdomain>.workers.dev/events";
```

If you configured `INGEST_KEY`, also set a matching internal endpoint key before release. Do not expose this in the UI. Treat it as a lightweight spam guard, not as a true secret, because anything shipped inside a desktop app can be extracted.

## Endpoints

Visual dashboard:

```text
GET /dashboard
```

Open this in the browser:

```text
https://canalyser-telemetry.<your-subdomain>.workers.dev/dashboard
```

Paste `ADMIN_TOKEN` once. The dashboard stores it in browser `sessionStorage`, fetches `/summary` and `/events`, and refreshes every 30 seconds.

The Dutch usage overview includes period filters (7, 30, 90 days or all time),
feature rankings, a category donut, daily activity, load outcomes, app versions,
and searchable recent activity with readable event and property labels.
All totals, unique installation counts and average load durations are calculated
over the complete selected period. Only the recent activity table is limited to
100 events. Periods use receipt time and UTC calendar days, including today.

Feature actions exclude startup, updates, failed loads and cancelled loads.
An installation can perform an action repeatedly; installation counts are not
person counts. Category installation counts are deduplicated across actions.
Only already instrumented features appear. The actuator CSV comparison event is
also accepted by the receiver. Display labels live in `src/event-catalog.js`;
stored events and the technical NDJSON format remain unchanged.

`GET /summary?days=30` and `GET /events?days=30&limit=100` select a period;
omitting `days` preserves the all-time API default. NDJSON export remains
independent of dashboard filters (the dashboard downloads the first 5,000 events).

Run `npm test` with Node.js 24+ for SQLite-backed aggregate, period, auth and
export checks. Run `npx wrangler deploy --dry-run` to validate the Worker bundle.

Public ingest:

```text
POST /events
Content-Type: application/json
X-CANalyser-Telemetry-Key: optional INGEST_KEY
```

Admin summary:

```powershell
curl.exe -H "Authorization: Bearer <ADMIN_TOKEN>" `
  "https://canalyser-telemetry.<your-subdomain>.workers.dev/summary"
```

Recent events for the dashboard:

```powershell
curl.exe -H "Authorization: Bearer <ADMIN_TOKEN>" `
  "https://canalyser-telemetry.<your-subdomain>.workers.dev/events?limit=100"
```

Admin export:

```powershell
curl.exe -H "Authorization: Bearer <ADMIN_TOKEN>" `
  "https://canalyser-telemetry.<your-subdomain>.workers.dev/export.ndjson?limit=5000" `
  -o canalyser-telemetry.ndjson
```

Incremental export:

```powershell
curl.exe -H "Authorization: Bearer <ADMIN_TOKEN>" `
  "https://canalyser-telemetry.<your-subdomain>.workers.dev/export.ndjson?after=2026-06-26T00:00:00.000Z&limit=5000" `
  -o canalyser-telemetry.ndjson
```

Health check:

```text
GET /health
```

## Notes

- Do not put GitHub tokens in CANalyser.
- GitHub Pages is not a receiver; it cannot append events server-side.
- If you later want a GitHub archive, let this Worker or another server-side job write batches to a private GitHub repository using a server-side secret.
