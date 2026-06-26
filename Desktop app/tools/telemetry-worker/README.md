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
