# CANalyser usage telemetry

Telemetry is enabled by default. Users can disable it in `Instellingen / Diagnostics` under `Gebruikstelemetrie`.

Users only see one setting: telemetry sharing on/off. Endpoint URLs, keys, installation IDs and local file paths are internal implementation details.

## Where events are stored

- Local JSONL file: `%AppData%\CanAnalyzer\telemetry-events.jsonl`. This file is appended immediately for every recorded event.
- Default Windows path: `%AppData%\CanAnalyzer\telemetry-events.jsonl`.
- Optional remote endpoint: set `TelemetryOptions.DefaultEndpointUrl` in the app before releasing. CANalyser sends one HTTP `POST` per event with `application/json` as soon as the event is recorded.
- Optional endpoint key: set `TelemetryOptions.EndpointKey` through internal settings only. CANalyser sends it as `X-CANalyser-Telemetry-Key`.

If no endpoint is configured, enabled telemetry is local-only.

## Delivery frequency

- Local: one JSON line is written immediately per event.
- Remote: one HTTP `POST` is sent immediately per event when `TelemetryOptions.DefaultEndpointUrl` or an internal endpoint setting is configured.
- Update checks: both automatic startup checks and manual checks record an event, so those checks also trigger local write and optional remote delivery.
- There is no central server configured by default; remote delivery starts only after a developer-configured endpoint URL is present.

## Receiver recommendation

The included free receiver lives in `tools/telemetry-worker`. It uses Cloudflare Workers + D1 and exposes:

- `POST /events` for CANalyser
- `GET /dashboard` for the visual browser dashboard
- `GET /summary` for an authenticated overview
- `GET /events` for authenticated recent events
- `GET /export.ndjson` for authenticated raw export

Do not use GitHub Pages as the receiver. GitHub Pages is static hosting and cannot safely receive telemetry events or append data server-side.

Do not call the GitHub API directly from CANalyser with a personal access token. Anything embedded in the desktop app can be extracted by users.

Recommended options:

- Small serverless endpoint such as Cloudflare Workers, Azure Functions, Vercel Functions or Netlify Functions.
- Store events in a database/object store such as Cloudflare D1/R2, Supabase, Azure Table/Blob, or another private analytics store.
- If GitHub must be involved, let the serverless endpoint hold the GitHub token as a secret and write batched data to a private repository. Avoid committing once per event because that creates conflicts, rate-limit risk and repository noise.

## Event envelope

Each line/request contains:

```json
{
  "schema_version": 1,
  "event_id": "random-guid",
  "event_name": "load_decode_completed",
  "timestamp_utc": "2026-06-26T12:00:00.0000000+00:00",
  "app_version": "2.0.2",
  "installation_id": "stable-random-install-id",
  "session_id": "random-guid-for-this-run",
  "os_description": "Microsoft Windows ...",
  "process_architecture": "X64",
  "runtime_version": "8.0.x",
  "properties": {
    "duration_ms": 12345,
    "duration_bucket": "5s-15s",
    "raw_frame_bucket": "1m-10m",
    "signal_bucket": "11-100"
  }
}
```

## Events currently recorded

- `app_started`
- `load_decode_completed`
- `load_decode_failed`
- `load_decode_cancelled`
- `export_decoded_csv`
- `settings_applied`
- `analysis_layout_exported`
- `analysis_layout_imported`
- `analysis_apply_plot_groups`
- `analysis_open_detached_plots`
- `analysis_lod_forced`
- `update_check_skipped`
- `update_check_completed`
- `update_prompt_declined`
- `update_apply_failed`

## Data that is allowed in telemetry

The implementation only records technical/product analytics:

- app version, runtime version, operating system description, process architecture
- anonymous installation ID and per-session ID
- event timestamps
- load/decode duration in milliseconds and a duration bucket
- import mode and dataset completeness
- bucketed counts for raw frames, decoded samples, signals, messages, unmatched frames and decode errors
- feature usage booleans and counts such as number of plot groups/subplots and whether linked axes or downsampling were used
- exception type for failed load/decode attempts

## Data that must not be recorded

Do not add these fields to telemetry events:

- CAN frame payloads or decoded signal values
- DBC contents
- filenames, folder paths or user/project names
- signal names, message names or frame IDs
- source hashes that can identify a customer's dataset
- raw exception messages or stack traces

## Retention

Local events are pruned to the configured retention window. The default is 180 days, with the UI clamped between 30 and 730 days.

For a remote endpoint, apply the same or shorter retention policy server-side.

## Minimal receiver contract

Your receiver only needs to accept:

- method: `POST`
- content type: `application/json`
- optional header: `X-CANalyser-Telemetry-Key`
- body: the event envelope shown above

Store the raw JSON or flatten it into a table. For average load/decode time, aggregate `properties.duration_ms` where `event_name = "load_decode_completed"`.
