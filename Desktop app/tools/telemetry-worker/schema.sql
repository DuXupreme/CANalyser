CREATE TABLE IF NOT EXISTS telemetry_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    received_at TEXT NOT NULL,
    event_id TEXT NOT NULL UNIQUE,
    event_name TEXT NOT NULL,
    timestamp_utc TEXT NOT NULL,
    app_version TEXT,
    installation_id TEXT,
    session_id TEXT,
    os_description TEXT,
    process_architecture TEXT,
    runtime_version TEXT,
    properties_json TEXT NOT NULL,
    event_json TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_telemetry_events_received_at
    ON telemetry_events (received_at);

CREATE INDEX IF NOT EXISTS idx_telemetry_events_event_name
    ON telemetry_events (event_name);

CREATE INDEX IF NOT EXISTS idx_telemetry_events_installation
    ON telemetry_events (installation_id);
