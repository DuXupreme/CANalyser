# CSS Electronics MDF4 converter

CANalyser bundles `mdf2peak.exe` version `24.12.19` to convert raw CANedge MDF 4.11 logs to PEAK TRC 1.1 before parsing.

- Source and documentation: https://canlogger.csselectronics.com/tools-docs/converters_mf4/converters/trc/
- Upstream project: https://github.com/CSS-Electronics/mdf4-converters
- Upstream archive MD5: `69174A4384C844045FEC282E2BF5306B`
- Bundled executable SHA-256: `30B7524CC5CEAF7B46E64BB2F4E3AF90262D2DB0607122B08B83606C1CA8AE9C`
- License: MIT (see `LICENSE`)

At runtime CANalyser verifies the SHA-256 before executing the converter. The converter only translates the raw transport format; CANalyser performs parsing, chronological merging, DBC decoding and analysis itself.
