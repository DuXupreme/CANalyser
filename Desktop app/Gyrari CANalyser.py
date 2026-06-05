#!/usr/bin/env python3
"""
Fast CAN Log + DBC Signal Viewer

Single-file Dash app for uploading:
- .trc PEAK CAN log files
- .log / .txt BUSMASTER CAN log files
- .txt CSS / CL1000 CAN log files
- .dbc CAN database files

Speed-focused changes:
- keeps decoded data on the server instead of in huge browser dcc.Store JSON blobs
- precomputes label metadata and per-signal arrays once after upload
- downsamples traces before plotting to keep Plotly responsive
- uses Scattergl for WebGL rendering
- avoids rebuilding plot groups on every keystroke
- optional rangeslider is OFF by default because it is expensive on large datasets

Run:
    pip install dash plotly pandas cantools numpy
    python can_log_viewer_fast.py

Then open:
    http://127.0.0.1:8050
"""

import base64
import io
import json
from datetime import datetime
import re
import traceback
import uuid
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

import dash
import numpy as np
import pandas as pd
import plotly.graph_objects as go
from dash import Dash, Input, Output, State, dcc, html, dash_table, no_update, ctx
from dash.exceptions import PreventUpdate
from plotly.subplots import make_subplots
from plotly.colors import qualitative
import cantools


HEX_RE = re.compile(r"^[0-9A-Fa-f]+$")
CSS_TS_RE = re.compile(r"^(?P<day>\d{2})T(?P<hmsms>\d{9})$")
BUSMASTER_TS_RE = re.compile(r"^(?P<hh>\d{1,2}):(?P<mm>\d{1,2}):(?P<ss>\d{1,2}):(?P<frac>\d{1,4})$")
BUSMASTER_LINE_RE = re.compile(
    r"^(?P<ts>\d{1,2}:\d{1,2}:\d{1,2}:\d{1,4})\s+"
    r"(?P<dir>Rx|Tx)\s+"
    r"(?P<channel>\d+)\s+"
    r"(?P<id>0x[0-9A-Fa-f]+|[0-9A-Fa-f]+)\s+"
    r"(?P<frame_type>[A-Za-z]+)\s+"
    r"(?P<dlc>\d+)\s*"
    r"(?P<data>(?:[0-9A-Fa-f]{2}\s*){0,64})$"
)
PEAK_TRC_RE = re.compile(
    r"^\s*(?P<msgno>\d+\))?\s*"
    r"(?P<time_ms>\d+(?:[\.,]\d+)?)\s+"
    r"(?P<dir>Rx|Tx|DT|FD)?\s*"
    r"(?P<id>[0-9A-Fa-f]+)\s+"
    r"(?P<dlc>\d+)\s+"
    r"(?P<data>(?:[0-9A-Fa-f]{2}\s*){0,64})$"
)
CANDUMP_RE = re.compile(
    r"^\((?P<ts>\d+(?:\.\d+)?)\)\s+\S+\s+(?P<id>[0-9A-Fa-f]+)#(?P<data>[0-9A-Fa-f]*)$"
)

PEAK_TRC_TSV_RE = re.compile(
    r"^\s*(?P<time_ms>\d+(?:[\.,]\d+)?)\t"
    r"(?P<channel>[^\t]*)\t"
    r"(?P<unknown>[^\t]*)\t"
    r"(?P<id>[0-9A-Fa-f]+)\t"
    r"(?P<dlc>\d+)\t"
    r"(?P<data>(?:[0-9A-Fa-f]{2}(?:\s+[0-9A-Fa-f]{2})*)?)\t"
    r"(?P<tail>.*)$"
)


DBC_EXTENDED_FLAG = 0x80000000
CAN_EXTENDED_MASK = 0x1FFFFFFF


def extract_j1939_pgn(frame_id: int) -> Optional[int]:
    """Return J1939 PGN from a 29-bit CAN ID."""
    frame_id = int(frame_id) & CAN_EXTENDED_MASK
    if frame_id <= 0x7FF:
        return None
    pf = (frame_id >> 16) & 0xFF
    ps = (frame_id >> 8) & 0xFF
    dp = (frame_id >> 24) & 0x01
    if pf < 240:
        return (dp << 16) | (pf << 8)
    return (dp << 16) | (pf << 8) | ps


def normalize_dbc_frame_id(frame_id: int, is_extended: Optional[bool] = None) -> int:
    """Normalize cantools/DBC frame IDs to the real on-bus CAN ID."""
    frame_id = int(frame_id)
    if is_extended is None:
        is_extended = bool(frame_id & DBC_EXTENDED_FLAG) or frame_id > CAN_EXTENDED_MASK
    if is_extended:
        return frame_id & CAN_EXTENDED_MASK
    return frame_id & 0x7FF if frame_id <= 0x7FF else frame_id


def describe_dbc_message(msg) -> str:
    raw_id = int(getattr(msg, "frame_id", 0))
    is_ext = bool(getattr(msg, "is_extended_frame", False)) or bool(raw_id & DBC_EXTENDED_FLAG)
    norm_id = normalize_dbc_frame_id(raw_id, is_ext)
    pgn = extract_j1939_pgn(norm_id) if is_ext else None
    pgn_txt = f" | PGN 0x{pgn:X}" if pgn is not None else ""
    return f"- {getattr(msg, 'name', '<unnamed>')} : raw=0x{raw_id:X} -> norm=0x{norm_id:X} | {'extended' if is_ext else 'standard'}{pgn_txt}"


def build_decode_diagnostics(
    raw_df: pd.DataFrame,
    db,
    unmatched_counter: Dict[int, int],
    decoded_rows_count: int,
    exact_map: Optional[Dict[Tuple[bool, int], List[object]]] = None,
    manual_decode_counter: Optional[Dict[int, int]] = None,
) -> str:
    total_frames = len(raw_df)
    unique_raw_ids = int(raw_df["id"].nunique()) if not raw_df.empty and "id" in raw_df.columns else 0
    unmatched_total = int(sum(unmatched_counter.values()))
    unmatched_unique = len(unmatched_counter)

    dbc_messages = list(getattr(db, "messages", []))
    dbc_total = len(dbc_messages)
    exact_map = exact_map or {}
    manual_decode_counter = manual_decode_counter or {}

    std_msgs = []
    ext_msgs = []
    for msg in dbc_messages:
        raw_id = int(getattr(msg, "frame_id", 0))
        is_ext = bool(getattr(msg, "is_extended_frame", False)) or bool(raw_id & DBC_EXTENDED_FLAG)
        (ext_msgs if is_ext else std_msgs).append(msg)

    lines = [
        f"DBC berichten: {dbc_total}",
        f"Ruwe frames: {total_frames}",
        f"Unieke raw IDs: {unique_raw_ids}",
        f"Gedecodeerde meetpunten: {decoded_rows_count}",
        f"Niet-gematchte frames: {unmatched_total}",
        f"Niet-gematchte unieke IDs: {unmatched_unique}",
        f"Permissief/handmatig gedecodeerde frames: {int(sum(manual_decode_counter.values()))}",
        f"Permissief/handmatig gedecodeerde unieke IDs: {len(manual_decode_counter)}",
        f"DBC standaard berichten: {len(std_msgs)}",
        f"DBC extended berichten: {len(ext_msgs)}",
    ]

    if unmatched_counter:
        lines.append("")
        lines.append("Top onbekende frame IDs uit de log:")
        for frame_id, count in sorted(unmatched_counter.items(), key=lambda kv: (-kv[1], kv[0]))[:12]:
            is_ext = bool(frame_id > 0x7FF)
            norm_id = normalize_dbc_frame_id(frame_id, is_ext)
            pgn = extract_j1939_pgn(norm_id) if is_ext else None
            pgn_txt = f" | PGN 0x{pgn:X}" if pgn is not None else ""
            known_same_id = exact_map.get((is_ext, norm_id), [])
            if known_same_id:
                msg_names = ", ".join(getattr(m, "name", "<unnamed>") for m in known_same_id[:4])
                lines.append(f"- 0x{frame_id:X} : {count}x | exact genormaliseerde match aanwezig -> {msg_names}{pgn_txt}")
            else:
                lines.append(f"- 0x{frame_id:X} : {count}x | genormaliseerd=0x{norm_id:X} | {'extended' if is_ext else 'standard'}{pgn_txt}")

    if manual_decode_counter:
        lines.append("")
        lines.append("Frames die alleen via permissieve fallback decodeerden:")
        for frame_id, count in sorted(manual_decode_counter.items(), key=lambda kv: (-kv[1], kv[0]))[:12]:
            lines.append(f"- 0x{frame_id:X} : {count}x")

    if std_msgs:
        lines.append("")
        lines.append("Voorbeeld standaard DBC IDs (genormaliseerd):")
        for msg in sorted(std_msgs, key=lambda m: normalize_dbc_frame_id(int(getattr(m, 'frame_id', 0)), False))[:12]:
            lines.append(describe_dbc_message(msg))

    if ext_msgs:
        lines.append("")
        lines.append("Voorbeeld extended DBC IDs (genormaliseerd):")
        for msg in sorted(ext_msgs, key=lambda m: normalize_dbc_frame_id(int(getattr(m, 'frame_id', 0)), True))[:12]:
            lines.append(describe_dbc_message(msg))

    if decoded_rows_count == 0:
        lines.append("")
        lines.append("Geen enkel DBC-signaal kon worden gedecodeerd. Dat is nu niet meer blokkerend: raw frames blijven gewoon geladen.")

    return "\n".join(lines)


# Plotting guardrails: enough detail to see shape, not enough to melt the browser.
MAX_POINTS_PER_TRACE = 4000
DEFAULT_SELECTION_COUNT = 4
DATASETS: Dict[str, "DatasetCache"] = {}

LOGO_DATA_URI = "data:image/png;base64,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hPK0iwClJQESIAESIIEWEqAC2MJMo8gkQAIkQAIkQAJhCbQ9diqAbc9Byk8CJEACJEACJEACJQlQASwJjM5JgARIICHATxIgARJoLwEqgO3NO0pOAiRAAiRAAiRAApUIUAGshC3xxE8SIAESIAESIAESaCMBKoBtzDXKTAIkQAIkEJIA4yaB1hOgAtj6LGQCSIAESIAESIAESKAcASqA5XjRNQkkBPhJAiRAAiRAAi0mQAWwxZlH0UmABEiABEiABJol0JXYqAB2JSeZDhIgARIgARIgARIoSIAKYEFQdEYCJEACCQF+kgAJkED7CVABbH8eMgUkQAIkQAIkQAIkUIoAFcBSCebTFwAAAJFJREFUuBLH/CQBEiABEiABEiCBNhOgAtjm3KPsJEACJEACTRJgXCTQGQJUADuTlUwICZAACZAACZAACRQjQAWwGCe6IoGEAD9JgARIgARIoAMEqAB2IBOZBBIgARIgARIggXoJdC10KoBdy1GmhwRIgARIgARIgARWEKACuAIQb5MACZBAQoCfJEACJNAdAv8fAAD//9P9/a4AAAAGSURBVAMAh8Y5G9cvS0gAAAAASUVORK5CYII="


@dataclass
class DatasetCache:
    raw_df: pd.DataFrame
    decoded_df: pd.DataFrame
    summary_df: pd.DataFrame
    signal_options: List[Dict[str, str]]
    label_to_xy: Dict[str, Tuple[np.ndarray, np.ndarray]]
    signal_count: int
    raw_count: int
    extended_count: int
    unmatched_frame_count: int
    unmatched_unique_ids: int
    dbc_message_count: int
    decode_note: str


def parse_int_auto(s: str) -> int:
    s = s.strip()
    if s.lower().startswith("0x"):
        return int(s, 16)
    if any(c in "abcdefABCDEF" for c in s):
        return int(s, 16)
    return int(s)


def parse_data_bytes(tokens: List[str]) -> Optional[bytes]:
    data = []
    for tok in tokens:
        tok = tok.strip()
        if not tok:
            continue
        if tok.lower().startswith("0x"):
            tok = tok[2:]
        if len(tok) == 0:
            continue
        if len(tok) > 2:
            return None
        if not HEX_RE.fullmatch(tok):
            return None
        data.append(int(tok, 16))
    return bytes(data)


def parse_hex_payload(data_hex: str) -> bytes:
    data_hex = (data_hex or "").strip()
    if data_hex.lower().startswith("0x"):
        data_hex = data_hex[2:]
    if len(data_hex) % 2 != 0:
        raise ValueError(f"Payload hex length must be even, got {len(data_hex)}")
    if data_hex and not HEX_RE.fullmatch(data_hex):
        raise ValueError("Payload contains non-hex characters")
    return bytes.fromhex(data_hex) if data_hex else b""


def parse_css_timestamp_to_seconds(ts: str) -> float:
    ts = ts.strip()
    m = CSS_TS_RE.fullmatch(ts)
    if not m:
        raise ValueError(f"Unsupported CSS timestamp: {ts}")

    day = int(m.group("day"))
    hmsms = m.group("hmsms")
    hh = int(hmsms[0:2])
    mm = int(hmsms[2:4])
    ss = int(hmsms[4:6])
    ms = int(hmsms[6:9])
    return (day - 1) * 86400.0 + hh * 3600.0 + mm * 60.0 + ss + ms / 1000.0


def parse_css_semicolon_text(log_text: str) -> Optional[pd.DataFrame]:
    if "Timestamp;Type;ID;Data" not in log_text:
        return None

    rows = []
    base_time = None
    for raw_line in log_text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or line == "Timestamp;Type;ID;Data":
            continue

        parts = line.split(";")
        if len(parts) < 4:
            continue

        ts_s, frame_type_s, frame_id_s, data_hex = parts[0], parts[1], parts[2], parts[3]
        try:
            abs_time_s = parse_css_timestamp_to_seconds(ts_s)
            if base_time is None:
                base_time = abs_time_s
            time_s = abs_time_s - base_time
            frame_id = int(frame_id_s.strip(), 16)
            data = parse_hex_payload(data_hex)
            frame_type = int(frame_type_s.strip()) if frame_type_s.strip() else 0
            rows.append(
                {
                    "time_s": time_s,
                    "id": frame_id,
                    "dlc": len(data),
                    "data": data,
                    "type": "Ext" if frame_type == 1 else "Std",
                    "channel": "",
                    "is_extended": bool(frame_type == 1 or frame_id > 0x7FF),
                }
            )
        except Exception:
            continue

    if not rows:
        return None
    return pd.DataFrame(rows).sort_values("time_s", kind="stable").reset_index(drop=True)


def parse_candump_text(log_text: str) -> Optional[pd.DataFrame]:
    rows = []
    base_time = None
    for raw_line in log_text.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        m = CANDUMP_RE.fullmatch(line)
        if not m:
            continue

        ts = float(m.group("ts"))
        if base_time is None:
            base_time = ts
        time_s = ts - base_time
        frame_id = int(m.group("id"), 16)
        data = parse_hex_payload(m.group("data"))
        rows.append(
            {
                "time_s": time_s,
                "id": frame_id,
                "dlc": len(data),
                "data": data,
                "type": "",
                "channel": "",
                "is_extended": frame_id > 0x7FF,
            }
        )

    if not rows:
        return None
    return pd.DataFrame(rows).sort_values("time_s", kind="stable").reset_index(drop=True)


def parse_busmaster_timestamp_to_seconds(ts: str) -> float:
    m = BUSMASTER_TS_RE.fullmatch(ts.strip())
    if not m:
        raise ValueError(f"Unsupported BUSMASTER timestamp: {ts}")

    hh = int(m.group("hh"))
    mm = int(m.group("mm"))
    ss = int(m.group("ss"))
    frac_raw = m.group("frac")
    frac_s = int(frac_raw) / (10 ** len(frac_raw))
    return hh * 3600.0 + mm * 60.0 + ss + frac_s


def parse_busmaster_text(log_text: str) -> Optional[pd.DataFrame]:
    rows = []
    base_time = None
    for raw_line in log_text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("***") or line.startswith("//") or line.startswith("#"):
            continue

        m = BUSMASTER_LINE_RE.fullmatch(line)
        if not m:
            continue

        try:
            abs_time_s = parse_busmaster_timestamp_to_seconds(m.group("ts"))
            if base_time is None:
                base_time = abs_time_s
            time_s = abs_time_s - base_time
            frame_id = int(m.group("id"), 16)
            dlc = int(m.group("dlc"))
            data = parse_data_bytes(m.group("data").split())
            if data is None:
                continue
            if len(data) != dlc:
                continue
            rows.append(
                {
                    "time_s": time_s,
                    "id": frame_id,
                    "dlc": dlc,
                    "data": data,
                    "type": m.group("dir"),
                    "channel": m.group("channel"),
                    "is_extended": frame_id > 0x7FF,
                }
            )
        except Exception:
            continue

    if not rows:
        return None
    return pd.DataFrame(rows).sort_values("time_s", kind="stable").reset_index(drop=True)


def parse_peak_trc_text(log_text: str) -> Optional[pd.DataFrame]:
    rows = []
    for raw_line in log_text.splitlines():
        line = raw_line.strip()
        if (
            not line
            or line.startswith(";")
            or line.startswith("//")
            or line.startswith("#")
            or line.startswith("date")
            or line.startswith("base")
            or line.startswith("internal")
            or line.startswith("Begin")
            or line.startswith("End")
            or line.startswith(";$FILEVERSION")
            or line.startswith(";$STARTTIME")
            or line.startswith(";$COLUMNS")
            or line.startswith("@ ")
        ):
            continue

        parsed = None
        mt = PEAK_TRC_TSV_RE.fullmatch(line)
        if mt:
            try:
                time_s = float(mt.group("time_ms").replace(",", ".")) / 1000.0
                frame_id = int(mt.group("id"), 16)
                dlc = int(mt.group("dlc"))
                data = parse_data_bytes(mt.group("data").split())
                if data is not None and len(data) == dlc:
                    tail = (mt.group("tail") or "").lower()
                    parsed = {
                        "time_s": time_s,
                        "id": frame_id,
                        "dlc": dlc,
                        "data": data,
                        "type": "Tx" if "t" in tail else ("Rx" if "r" in tail else ""),
                        "channel": (mt.group("channel") or "").strip(),
                        "is_extended": ("x" in tail) or (frame_id > 0x7FF),
                    }
            except Exception:
                parsed = None

        if parsed is None:
            m = PEAK_TRC_RE.fullmatch(line)
            if not m:
                continue
            try:
                time_s = float(m.group("time_ms").replace(",", ".")) / 1000.0
                frame_id = int(m.group("id"), 16)
                dlc = int(m.group("dlc"))
                data = parse_data_bytes(m.group("data").split())
                if data is None or len(data) != dlc:
                    continue
                msg_type = (m.group("dir") or "").strip()
                parsed = {
                    "time_s": time_s,
                    "id": frame_id,
                    "dlc": dlc,
                    "data": data,
                    "type": msg_type,
                    "channel": "",
                    "is_extended": frame_id > 0x7FF,
                }
            except Exception:
                parsed = None

        if parsed is not None:
            rows.append(parsed)

    if not rows:
        return None
    return pd.DataFrame(rows).sort_values("time_s", kind="stable").reset_index(drop=True)


def parse_trc_or_generic_text(log_text: str) -> Optional[pd.DataFrame]:
    rows = []
    for raw_line in log_text.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        if (
            line.startswith(";")
            or line.startswith("//")
            or line.startswith("#")
            or line.startswith("date")
            or line.startswith("base")
            or line.startswith("internal")
            or line.startswith("Begin")
            or line.startswith("End")
            or line.startswith(";$FILEVERSION")
            or line.startswith(";$STARTTIME")
            or line.startswith(";$COLUMNS")
        ):
            continue

        parts = line.split()
        time_idx = None
        time_s = None
        for i, p in enumerate(parts[:5]):
            try:
                time_s = float(p.replace(",", "."))
                time_idx = i
                break
            except Exception:
                pass

        if time_idx is None:
            continue

        parsed = None
        for id_idx in range(time_idx + 1, min(len(parts), time_idx + 8)):
            tok_id = parts[id_idx]
            if tok_id.lower() in {"rx", "tx", "dt", "fd", "ch", "channel", "-"}:
                continue
            try:
                frame_id = parse_int_auto(tok_id)
            except Exception:
                continue

            for dlc_idx in range(id_idx + 1, min(len(parts), id_idx + 6)):
                tok_dlc = parts[dlc_idx]
                if not tok_dlc.isdigit():
                    continue
                dlc = int(tok_dlc)
                if not (0 <= dlc <= 64):
                    continue
                data_tokens = parts[dlc_idx + 1: dlc_idx + 1 + dlc]
                if len(data_tokens) != dlc:
                    continue
                data = parse_data_bytes(data_tokens)
                if data is None:
                    continue
                line_lower = [p.lower() for p in parts]
                msg_type = "Rx" if "rx" in line_lower else ("Tx" if "tx" in line_lower else "")
                parsed = {
                    "time_s": time_s,
                    "id": frame_id,
                    "dlc": dlc,
                    "data": data,
                    "type": msg_type,
                    "channel": "",
                    "is_extended": frame_id > 0x7FF,
                }
                break
            if parsed is not None:
                break
        if parsed is not None:
            rows.append(parsed)

    if not rows:
        return None
    return pd.DataFrame(rows).sort_values("time_s", kind="stable").reset_index(drop=True)


def parse_can_log_text(log_text: str, filename: Optional[str] = None) -> pd.DataFrame:
    name = (filename or "").lower()
    ordered_parsers = []
    if name.endswith(".trc"):
        ordered_parsers = [parse_peak_trc_text, parse_busmaster_text, parse_css_semicolon_text, parse_candump_text, parse_trc_or_generic_text]
    elif name.endswith(".log") or name.endswith(".log.txt"):
        ordered_parsers = [parse_busmaster_text, parse_css_semicolon_text, parse_peak_trc_text, parse_candump_text, parse_trc_or_generic_text]
    elif name.endswith(".txt"):
        ordered_parsers = [parse_css_semicolon_text, parse_busmaster_text, parse_peak_trc_text, parse_candump_text, parse_trc_or_generic_text]
    else:
        ordered_parsers = [parse_css_semicolon_text, parse_busmaster_text, parse_peak_trc_text, parse_candump_text, parse_trc_or_generic_text]

    for parser in ordered_parsers:
        df = parser(log_text)
        if df is not None and not df.empty:
            return df
    raise ValueError(
        "Kon geen CAN frames herkennen. Ondersteunde formaten: "
        "PEAK .trc, BUSMASTER .log/.txt, CSS/CL1000 .txt (Timestamp;Type;ID;Data), en candump."
    )


def make_signal_label(message_name: str, signal_name: str, frame_id: int) -> str:
    return f"{message_name}.{signal_name} [0x{frame_id:X}]"


def load_dbc_from_bytes(dbc_bytes: bytes):
    dbc_text = dbc_bytes.decode("utf-8", errors="replace")
    return cantools.database.load_string(dbc_text, database_format="dbc", strict=False)


def b64_to_bytes(contents: str) -> bytes:
    _, content_string = contents.split(",", 1)
    return base64.b64decode(content_string)


def downsample_minmax(x: np.ndarray, y: np.ndarray, max_points: int = MAX_POINTS_PER_TRACE) -> Tuple[np.ndarray, np.ndarray]:
    n = len(x)
    if n <= max_points or max_points < 8:
        return x, y

    bucket_count = max_points // 2
    if bucket_count < 2:
        step = max(1, n // max_points)
        return x[::step], y[::step]

    edges = np.linspace(0, n, bucket_count + 1, dtype=int)
    xs = []
    ys = []
    for i in range(bucket_count):
        start = edges[i]
        end = edges[i + 1]
        if end <= start:
            continue
        xb = x[start:end]
        yb = y[start:end]
        min_idx = int(np.argmin(yb))
        max_idx = int(np.argmax(yb))
        if min_idx <= max_idx:
            order = (min_idx, max_idx)
        else:
            order = (max_idx, min_idx)
        xs.extend([xb[order[0]], xb[order[1]]])
        ys.extend([yb[order[0]], yb[order[1]]])

    if len(xs) < 2:
        step = max(1, n // max_points)
        return x[::step], y[::step]
    return np.asarray(xs), np.asarray(ys)



def _get_bit_lsb0(data: bytes, bit_index: int) -> int:
    byte_index = bit_index // 8
    bit_in_byte = bit_index % 8
    if byte_index < 0 or byte_index >= len(data):
        return 0
    return (data[byte_index] >> bit_in_byte) & 1


def _iter_motorola_bit_indices(start_bit: int, length: int):
    bit = int(start_bit)
    for _ in range(int(length)):
        yield bit
        if bit % 8 == 0:
            bit += 15
        else:
            bit -= 1


def _extract_signal_raw_permissive(data: bytes, signal) -> int:
    start = int(getattr(signal, 'start', 0))
    length = int(getattr(signal, 'length', 0))
    if length <= 0:
        return 0

    byte_order = getattr(signal, 'byte_order', 'little_endian')
    if byte_order == 'little_endian':
        payload_int = int.from_bytes(data, byteorder='little', signed=False)
        mask = (1 << length) - 1
        return (payload_int >> start) & mask

    raw = 0
    for bit_index in _iter_motorola_bit_indices(start, length):
        raw = (raw << 1) | _get_bit_lsb0(data, bit_index)
    return raw


def _apply_signal_scaling(raw_value: int, signal):
    length = int(getattr(signal, 'length', 0))
    is_signed = bool(getattr(signal, 'is_signed', False))
    scale = float(getattr(signal, 'scale', 1) or 1)
    offset = float(getattr(signal, 'offset', 0) or 0)

    value = int(raw_value)
    if is_signed and length > 0 and (value & (1 << (length - 1))):
        value -= 1 << length

    return value * scale + offset


def decode_message_permissive(message, data: bytes) -> Optional[Dict[str, float]]:
    try:
        decoded = message.decode(data, decode_choices=False)
        if decoded:
            return decoded
    except Exception:
        pass

    decoded = {}
    multiplexer_values: Dict[str, int] = {}

    for signal in getattr(message, 'signals', []):
        if bool(getattr(signal, 'is_multiplexer', False)):
            raw = _extract_signal_raw_permissive(data, signal)
            value = _apply_signal_scaling(raw, signal)
            decoded[str(getattr(signal, 'name', '<mux>'))] = value
            multiplexer_values[str(getattr(signal, 'name', '<mux>'))] = int(round(value))

    for signal in getattr(message, 'signals', []):
        if bool(getattr(signal, 'is_multiplexer', False)):
            continue
        mux_ids = getattr(signal, 'multiplexer_ids', None)
        if mux_ids:
            matched_mux = False
            for mux_value in multiplexer_values.values():
                if mux_value in mux_ids:
                    matched_mux = True
                    break
            if not matched_mux:
                continue
        try:
            raw = _extract_signal_raw_permissive(data, signal)
            decoded[str(getattr(signal, 'name', '<signal>'))] = _apply_signal_scaling(raw, signal)
        except Exception:
            continue

    return decoded or None


def decode_can_dataframe(raw_df: pd.DataFrame, db) -> Tuple[pd.DataFrame, pd.DataFrame, Dict[str, object]]:
    decoded_rows = []
    summary_counts: Dict[Tuple[int, str], int] = {}
    unmatched_counter: Dict[int, int] = {}
    manual_decode_counter: Dict[int, int] = {}

    dbc_messages = list(getattr(db, "messages", []))

    exact_map: Dict[Tuple[bool, int], List[object]] = {}
    pgn_to_messages: Dict[int, List[object]] = {}
    for msg in dbc_messages:
        try:
            raw_msg_id = int(getattr(msg, "frame_id", 0))
            msg_is_extended = bool(getattr(msg, "is_extended_frame", False)) or bool(raw_msg_id & DBC_EXTENDED_FLAG)
            norm_msg_id = normalize_dbc_frame_id(raw_msg_id, msg_is_extended)
            exact_map.setdefault((msg_is_extended, norm_msg_id), []).append(msg)
            if msg_is_extended:
                pgn = extract_j1939_pgn(norm_msg_id)
                if pgn is not None:
                    pgn_to_messages.setdefault(pgn, []).append(msg)
        except Exception:
            continue

    for row in raw_df.itertuples(index=False):
        raw_frame_id = int(row.id)
        data = row.data
        is_extended = bool(getattr(row, "is_extended", raw_frame_id > 0x7FF))
        frame_id = normalize_dbc_frame_id(raw_frame_id, is_extended)

        candidate_messages: List[object] = []
        candidate_messages.extend(exact_map.get((is_extended, frame_id), []))
        if not candidate_messages and is_extended:
            pgn = extract_j1939_pgn(frame_id)
            if pgn is not None:
                candidate_messages.extend(pgn_to_messages.get(pgn, []))

        if not candidate_messages:
            unmatched_counter[raw_frame_id] = unmatched_counter.get(raw_frame_id, 0) + 1
            continue

        decoded = None
        decoded_message = None
        manual_used = False
        for message in candidate_messages:
            try:
                raw_msg_id = int(getattr(message, "frame_id", 0))
                msg_is_extended = bool(getattr(message, "is_extended_frame", False)) or bool(raw_msg_id & DBC_EXTENDED_FLAG)
                if msg_is_extended != is_extended:
                    continue
                decoded_try = decode_message_permissive(message, data)
                if decoded_try:
                    decoded = decoded_try
                    decoded_message = message
                    try:
                        message.decode(data, decode_choices=False)
                    except Exception:
                        manual_used = True
                    break
            except Exception:
                continue

        if decoded is None or decoded_message is None:
            unmatched_counter[raw_frame_id] = unmatched_counter.get(raw_frame_id, 0) + 1
            continue

        if manual_used:
            manual_decode_counter[raw_frame_id] = manual_decode_counter.get(raw_frame_id, 0) + 1

        message_name = decoded_message.name
        summary_counts[(frame_id, message_name)] = summary_counts.get((frame_id, message_name), 0) + 1
        time_s = float(row.time_s)
        for sig_name, value in decoded.items():
            if isinstance(value, (int, float, bool)):
                decoded_rows.append((time_s, frame_id, message_name, str(sig_name), float(value)))

    if decoded_rows:
        decoded_df = pd.DataFrame(
            decoded_rows,
            columns=["time_s", "frame_id", "message_name", "signal_name", "value"],
        )
        decoded_df["frame_id"] = decoded_df["frame_id"].astype(np.uint32)
        decoded_df["time_s"] = decoded_df["time_s"].astype(np.float32)
        decoded_df["value"] = decoded_df["value"].astype(np.float32)
        decoded_df["label"] = (
            decoded_df["message_name"] + "." + decoded_df["signal_name"] + " [0x" + decoded_df["frame_id"].map(lambda x: f"{int(x):X}") + "]"
        )
    else:
        decoded_df = pd.DataFrame(columns=["time_s", "frame_id", "message_name", "signal_name", "value", "label"])

    if summary_counts:
        summary_df = pd.DataFrame(
            [{"frame_id": fid, "message_name": mname, "count": cnt} for (fid, mname), cnt in summary_counts.items()]
        ).sort_values(["count", "frame_id"], ascending=[False, True])
    else:
        summary_df = pd.DataFrame(columns=["frame_id", "message_name", "count"])

    diagnostics = {
        "unmatched_frame_count": int(sum(unmatched_counter.values())),
        "unmatched_unique_ids": len(unmatched_counter),
        "dbc_message_count": len(dbc_messages),
        "manual_decode_frame_count": int(sum(manual_decode_counter.values())),
        "manual_decode_unique_ids": len(manual_decode_counter),
        "decode_note": build_decode_diagnostics(raw_df, db, unmatched_counter, len(decoded_rows), exact_map=exact_map, manual_decode_counter=manual_decode_counter),
    }

    return decoded_df, summary_df, diagnostics


def build_dataset_cache(raw_df: pd.DataFrame, decoded_df: pd.DataFrame, summary_df: pd.DataFrame, diagnostics: Optional[Dict[str, object]] = None) -> DatasetCache:
    diagnostics = diagnostics or {}

    if decoded_df.empty:
        signal_df = pd.DataFrame(columns=["message_name", "signal_name", "frame_id", "label"])
        decoded_sorted = decoded_df.copy()
    else:
        signal_df = (
            decoded_df[["message_name", "signal_name", "frame_id", "label"]]
            .drop_duplicates()
            .sort_values(["message_name", "signal_name", "frame_id"])
            .reset_index(drop=True)
        )
        decoded_sorted = decoded_df.sort_values(["label", "time_s"], kind="stable").reset_index(drop=True)

    signal_options = [{"label": row["label"], "value": row["label"]} for _, row in signal_df.iterrows()]

    label_to_xy: Dict[str, Tuple[np.ndarray, np.ndarray]] = {}
    if not decoded_sorted.empty:
        for label, group in decoded_sorted.groupby("label", sort=False):
            label_to_xy[str(label)] = (
                group["time_s"].to_numpy(dtype=np.float32, copy=True),
                group["value"].to_numpy(dtype=np.float32, copy=True),
            )

    return DatasetCache(
        raw_df=raw_df,
        decoded_df=decoded_sorted,
        summary_df=summary_df,
        signal_options=signal_options,
        label_to_xy=label_to_xy,
        signal_count=len(signal_options),
        raw_count=len(raw_df),
        extended_count=int(raw_df["is_extended"].sum()) if "is_extended" in raw_df.columns else 0,
        unmatched_frame_count=int(diagnostics.get("unmatched_frame_count", 0)),
        unmatched_unique_ids=int(diagnostics.get("unmatched_unique_ids", 0)),
        dbc_message_count=int(diagnostics.get("dbc_message_count", 0)),
        decode_note=str(diagnostics.get("decode_note", "")),
    )


def make_plot_group(title: str = "", signals: Optional[List[str]] = None, offsets: Optional[Dict[str, float]] = None) -> Dict:
    return {
        "title": title,
        "signals": list(signals or []),
        "offsets": dict(offsets or {}),
        "lock_y_axes": False,
    }


def default_plot_groups(signal_labels: List[str]) -> List[Dict]:
    return [make_plot_group(title=label, signals=[label]) for label in signal_labels]


def safe_group_title(group: Dict, idx: int) -> str:
    title = (group.get("title") or "").strip()
    if title:
        return title
    sigs = group.get("signals") or []
    if sigs:
        return " | ".join(sigs[:2]) + (" ..." if len(sigs) > 2 else "")
    return f"Plot {idx + 1}"


def make_empty_figure(message: str) -> go.Figure:
    fig = go.Figure()
    fig.update_layout(template="plotly_white", title="CAN signalen")
    fig.add_annotation(text=message, x=0.5, y=0.5, showarrow=False, xref="paper", yref="paper")
    return fig


def normalize_relayout_data(relayout_data: Optional[Dict]) -> Dict[str, List[float]]:
    if not relayout_data:
        return {}

    out: Dict[str, List[float]] = {}
    for key, value in relayout_data.items():
        if key.endswith('.autorange') and value:
            axis = key.split('.')[0]
            out.pop(axis, None)
            continue
        if key.endswith('.range') and isinstance(value, (list, tuple)) and len(value) == 2:
            axis = key.split('.')[0]
            out[axis] = [value[0], value[1]]
            continue
        if key.endswith('.range[0]'):
            axis = key.split('.')[0]
            out.setdefault(axis, [None, None])[0] = value
            continue
        if key.endswith('.range[1]'):
            axis = key.split('.')[0]
            out.setdefault(axis, [None, None])[1] = value
            continue

    return {k: v for k, v in out.items() if isinstance(v, list) and len(v) == 2 and v[0] is not None and v[1] is not None}


def apply_saved_ranges(fig: go.Figure, saved_ranges: Optional[Dict], row_count: int) -> None:
    if not saved_ranges:
        return

    x_range = saved_ranges.get('xaxis')
    if x_range is not None:
        fig.update_xaxes(range=x_range)

    for axis_name, axis_range in saved_ranges.items():
        if not axis_name.startswith('yaxis'):
            continue
        try:
            axis_index = 1 if axis_name == 'yaxis' else int(axis_name.replace('yaxis', ''))
        except Exception:
            continue
        if axis_index <= row_count:
            fig.layout[axis_name].update(range=axis_range, autorange=False)


COLOR_CYCLE = qualitative.Plotly

def color_for_trace(index: int) -> str:
    return COLOR_CYCLE[index % len(COLOR_CYCLE)]

def add_subplot_legends(fig: go.Figure, row_to_entries: Dict[int, List[Tuple[str, str]]], row_count: int) -> None:
    annotations = list(fig.layout.annotations) if fig.layout.annotations else []
    shapes = list(fig.layout.shapes) if fig.layout.shapes else []
    for row_idx in range(1, row_count + 1):
        entries = row_to_entries.get(row_idx, [])
        if not entries:
            continue
        yaxis_key = 'yaxis' if row_idx == 1 else f'yaxis{row_idx}'
        domain = getattr(fig.layout, yaxis_key).domain
        if not domain:
            continue
        y_top = float(domain[1]) - 0.03
        step = min(0.05, max(0.022, (float(domain[1]) - float(domain[0])) / max(len(entries) + 1, 6)))
        annotations.append(dict(x=1.02, y=min(0.99, float(domain[1]) - 0.005), xref='paper', yref='paper',
                                text='Signalen', showarrow=False, xanchor='left', yanchor='top',
                                font=dict(size=12)))
        for j, (label, color) in enumerate(entries):
            y = y_top - (j + 1) * step
            if y < float(domain[0]) + 0.01:
                break
            shapes.append(dict(type='line', xref='paper', yref='paper', x0=1.00, x1=1.03, y0=y, y1=y,
                               line=dict(color=color, width=2)))
            annotations.append(dict(x=1.035, y=y, xref='paper', yref='paper', text=label, showarrow=False,
                                    xanchor='left', yanchor='middle', font=dict(size=10, color=color)))
    fig.layout.annotations = annotations
    fig.layout.shapes = shapes



app = Dash(__name__)
server = app.server
app.title = "CAN Log Viewer"


app.layout = html.Div(
    [
        html.Div(
            [
                html.Img(src=LOGO_DATA_URI, style={"height": "72px", "marginBottom": "8px"}),
                html.H2("CANbus analyser Gyrari B.V.", style={"marginTop": "0"}),
            ],
            style={"marginBottom": "12px"},
        ),
        html.Div(
            [
                html.Div(id="progress-label", children="Klaar.", style={"marginBottom": "6px", "fontWeight": "bold"}),
                html.Div(
                    [
                        html.Div(
                            id="progress-bar",
                            style={
                                "height": "100%",
                                "width": "0%",
                                "backgroundColor": "#4a90e2",
                                "transition": "width 0.25s ease",
                                "borderRadius": "8px",
                            },
                        )
                    ],
                    style={
                        "width": "100%",
                        "height": "18px",
                        "backgroundColor": "#e5e5e5",
                        "borderRadius": "8px",
                        "overflow": "hidden",
                        "marginBottom": "14px",
                    },
                ),
            ]
        ),
        dcc.Store(id="store-dataset-id"),
        dcc.Store(id="store-plot-groups", storage_type="local"),
        dcc.Store(id="store-progress", data={"label": "Klaar.", "value": 0}),
        dcc.Store(id="store-axis-ranges", storage_type="memory"),
        dcc.Download(id="download-preset"),
        dcc.Loading(
            id="main-loading",
            type="default",
            children=html.Div(
                [
                    html.Div(
                        [
                            html.Div(
                                [
                                    dcc.Upload(
                                        id="upload-log",
                                        children=html.Div(["Sleep je .trc / .log / .txt log hierheen of klik om te uploaden"]),
                                        style={
                                            "width": "100%",
                                            "height": "80px",
                                            "lineHeight": "80px",
                                            "borderWidth": "1px",
                                            "borderStyle": "dashed",
                                            "borderRadius": "8px",
                                            "textAlign": "center",
                                            "marginBottom": "12px",
                                        },
                                        multiple=False,
                                    ),
                                    html.Div(id="log-status"),
                                ],
                                style={"width": "48%", "display": "inline-block", "verticalAlign": "top"},
                            ),
                            html.Div(
                                [
                                    dcc.Upload(
                                        id="upload-dbc",
                                        children=html.Div(["Sleep je .dbc hierheen of klik om te uploaden"]),
                                        style={
                                            "width": "100%",
                                            "height": "80px",
                                            "lineHeight": "80px",
                                            "borderWidth": "1px",
                                            "borderStyle": "dashed",
                                            "borderRadius": "8px",
                                            "textAlign": "center",
                                            "marginBottom": "12px",
                                        },
                                        multiple=False,
                                    ),
                                    html.Div(id="dbc-status"),
                                ],
                                style={"width": "48%", "display": "inline-block", "float": "right", "verticalAlign": "top"},
                            ),
                        ]
                    ),
                    html.Hr(),
                    html.Div(id="decode-status", style={"whiteSpace": "pre-wrap", "marginBottom": "10px"}),
                    html.Div(
                        [
                            html.Div(
                                [
                                    html.Label("Beschikbare signalen"),
                                    dcc.Dropdown(
                                        id="signal-dropdown",
                                        options=[],
                                        value=[],
                                        multi=True,
                                        placeholder="Kies één of meer signalen...",
                                        persistence=True,
                                        persistence_type="local",
                                        maxHeight=420,
                                        optionHeight=34,
                                    ),
                                    html.Div(
                                        [html.Button("Maak plotgroepen van selectie", id="btn-build-groups", n_clicks=0)],
                                        style={"marginTop": "8px"},
                                    ),
                                ],
                                style={"marginBottom": "10px"},
                            ),
                            html.H4("Plotgroepen"),
                            html.Div(
                                "Elke plotgroep wordt één subplot. Binnen een plotgroep kun je meerdere signalen zetten; die krijgen in dezelfde subplot aparte y-assen en offsets per lijn.",
                                style={"marginBottom": "10px"},
                            ),
                            html.Div(id="plot-groups-editor"),
                            html.Div(
                                [
                                    html.Div(
                                        [html.Button("Pas plotgroepen toe", id="btn-apply-groups", n_clicks=0)],
                                        style={"marginBottom": "8px"},
                                    ),
                                    html.Div(
                                        [
                                            html.Button("Exporteer layout", id="btn-export-preset", n_clicks=0),
                                            dcc.Upload(
                                                id="upload-preset",
                                                children=html.Button("Importeer layout"),
                                                multiple=False,
                                                style={"display": "inline-block", "marginLeft": "8px"},
                                            ),
                                            html.Span(id="preset-status", style={"marginLeft": "12px", "fontStyle": "italic"}),
                                        ]
                                    ),
                                ],
                                style={"marginTop": "10px", "marginBottom": "10px"},
                            ),
                            html.Div(
                                [
                                    html.Div(
                                        [
                                            html.Div(
                                                [
                                                    html.Label("Filter frame ID (optioneel, hex of decimaal)"),
                                                    dcc.Input(id="frame-id-filter", type="text", placeholder="bijv. 0x123 of 291", debounce=True, persistence=True, persistence_type="local", style={"width": "100%"}),
                                                ],
                                                style={"flex": "1.5"},
                                            ),
                                            html.Div(
                                                [
                                                    html.Label("Tijd start [s]"),
                                                    dcc.Input(id="time-start", type="number", placeholder="start", debounce=True, persistence=True, persistence_type="local", style={"width": "100%"}),
                                                ],
                                                style={"flex": "1"},
                                            ),
                                            html.Div(
                                                [
                                                    html.Label("Tijd einde [s]"),
                                                    dcc.Input(id="time-end", type="number", placeholder="einde", debounce=True, persistence=True, persistence_type="local", style={"width": "100%"}),
                                                ],
                                                style={"flex": "1"},
                                            ),
                                            html.Div(
                                                [
                                                    html.Label("Max punten / trace"),
                                                    dcc.Input(id="max-points", type="number", min=200, step=200, value=MAX_POINTS_PER_TRACE, debounce=True, persistence=True, persistence_type="local", style={"width": "100%"}),
                                                ],
                                                style={"flex": "0.95"},
                                            ),
                                            html.Div(
                                                [
                                                    html.Label("Subplot hoogte [px]"),
                                                    dcc.Input(id="subplot-height", type="number", min=160, step=20, value=300, debounce=True, persistence=True, persistence_type="local", style={"width": "100%"}),
                                                ],
                                                style={"flex": "0.95"},
                                            ),
                                            html.Div(
                                                [
                                                    html.Label("Lijst hoogte [px]"),
                                                    dcc.Input(id="dropdown-height", type="number", min=180, step=20, value=420, debounce=True, persistence=True, persistence_type="local", style={"width": "100%"}),
                                                ],
                                                style={"flex": "0.95"},
                                            ),
                                            html.Div(
                                                [
                                                    html.Label("Opties"),
                                                    dcc.Checklist(
                                                        id="plot-options",
                                                        persistence=True,
                                                        persistence_type="local",
                                                        options=[
                                                            {"label": " Normalizeer signalen (0..1)", "value": "normalize"},
                                                            {"label": " Trapjesplot", "value": "step"},
                                                            {"label": " Alleen markers", "value": "markers"},
                                                            {"label": " Toon rangeslider", "value": "rangeslider"},
                                                            {"label": " Verticale cursorlijn", "value": "cursorline"},
                                                        ],
                                                        value=[],
                                                        inline=False,
                                                    ),
                                                ],
                                                style={"flex": "1.2"},
                                            ),
                                        ],
                                        style={"display": "flex", "gap": "12px", "alignItems": "flex-start", "flexWrap": "nowrap"},
                                    ),
                                ]
                            ),
                        ]
                    ),
                    html.Br(),
                    dcc.Loading(id="graph-loading", type="default", children=dcc.Graph(id="signal-graph", clear_on_unhover=True, style={"height": "auto"})),
                    html.Br(),
                    html.Button("Exporteer gedecodeerde data naar CSV", id="btn-export"),
                    dcc.Download(id="download-decoded-csv"),
                    html.Hr(),
                    html.H4("Gedecodeerde berichten"),
                    dash_table.DataTable(
                        id="summary-table",
                        columns=[
                            {"name": "Frame ID", "id": "frame_id_hex"},
                            {"name": "Frame ID (dec)", "id": "frame_id"},
                            {"name": "Message", "id": "message_name"},
                            {"name": "Count", "id": "count"},
                        ],
                        data=[],
                        sort_action="native",
                        filter_action="native",
                        page_size=12,
                        style_table={"overflowX": "auto"},
                        style_cell={"textAlign": "left", "padding": "6px"},
                    ),
                    html.Details([
                        html.Summary("Debug / foutdetails"),
                        html.Pre(id="debug-output", style={"whiteSpace": "pre-wrap"}),
                    ]),
                ]
            ),
        ),
    ],
    style={"maxWidth": "1400px", "margin": "0 auto", "padding": "20px", "fontFamily": "Arial"},
)


@app.callback(
    Output("progress-label", "children"),
    Output("progress-bar", "style"),
    Input("store-progress", "data"),
)
def update_progress_ui(progress):
    progress = progress or {"label": "Klaar.", "value": 0}
    value = int(progress.get("value", 0))
    label = progress.get("label", "Klaar.")
    style = {
        "height": "100%",
        "width": f"{value}%",
        "backgroundColor": "#4a90e2" if value < 100 else "#2ca02c",
        "transition": "width 0.25s ease",
        "borderRadius": "8px",
    }
    return label, style


@app.callback(
    Output("log-status", "children"),
    Output("dbc-status", "children"),
    Output("decode-status", "children"),
    Output("debug-output", "children"),
    Output("store-dataset-id", "data"),
    Output("store-plot-groups", "data"),
    Output("signal-dropdown", "options"),
    Output("signal-dropdown", "value"),
    Output("store-progress", "data"),
    Input("upload-log", "contents"),
    Input("upload-log", "filename"),
    Input("upload-dbc", "contents"),
    Input("upload-dbc", "filename"),
    prevent_initial_call=True,
)
def handle_uploads(log_contents, log_filename, dbc_contents, dbc_filename):
    log_status = f"Log: {log_filename}" if log_filename else "Nog geen .trc / .log / .txt log geüpload"
    dbc_status = f"DBC: {dbc_filename}" if dbc_filename else "Nog geen .dbc geüpload"

    if not log_contents or not dbc_contents:
        return (
            log_status,
            dbc_status,
            "Upload zowel een CAN log (.trc, .log of .txt) als een .dbc file.",
            "",
            no_update,
            no_update,
            [],
            [],
            {"label": "Wachten op beide bestanden...", "value": 0},
        )

    try:
        log_bytes = b64_to_bytes(log_contents)
        dbc_bytes = b64_to_bytes(dbc_contents)

        log_text = log_bytes.decode("utf-8", errors="replace")
        raw_df = parse_can_log_text(log_text, log_filename)
        db = load_dbc_from_bytes(dbc_bytes)
        decoded_df, summary_df, diagnostics = decode_can_dataframe(raw_df, db)
        dataset_cache = build_dataset_cache(raw_df, decoded_df, summary_df, diagnostics)

        dataset_id = uuid.uuid4().hex
        DATASETS[dataset_id] = dataset_cache

        default_selection = [opt["value"] for opt in dataset_cache.signal_options[:DEFAULT_SELECTION_COUNT]]
        plot_groups = default_plot_groups(default_selection)

        if dataset_cache.signal_count > 0:
            status = (
                f"Bestanden geladen.\n"
                f"Ruwe frames geparsed: {dataset_cache.raw_count}\n"
                f"Extended frames: {dataset_cache.extended_count}\n"
                f"Gedecodeerde meetpunten: {len(dataset_cache.decoded_df)}\n"
                f"Unieke signalen: {dataset_cache.signal_count}\n"
                f"Gedecodeerde berichten: {len(dataset_cache.summary_df)}\n"
                f"Niet-gematchte frames: {dataset_cache.unmatched_frame_count}\n"
                f"Niet-gematchte unieke IDs: {dataset_cache.unmatched_unique_ids}\n\n"
                f"Snelheidsmodus actief: server-side cache + downsampling."
            )
        else:
            status = (
                f"Bestanden geladen, maar er is niets uit de DBC gedecodeerd.\n"
                f"Dat is niet blokkerend: de raw CAN-log is wel ingelezen.\n\n"
                f"Ruwe frames geparsed: {dataset_cache.raw_count}\n"
                f"Extended frames: {dataset_cache.extended_count}\n"
                f"DBC berichten: {dataset_cache.dbc_message_count}\n"
                f"Niet-gematchte frames: {dataset_cache.unmatched_frame_count}\n"
                f"Niet-gematchte unieke IDs: {dataset_cache.unmatched_unique_ids}\n\n"
                f"Zie Debug / foutdetails voor een overzicht van onbekende IDs en mogelijke PGN-matches."
            )

        return (
            log_status,
            dbc_status,
            status,
            dataset_cache.decode_note,
            dataset_id,
            json.dumps(plot_groups),
            dataset_cache.signal_options,
            default_selection,
            {"label": "Klaar.", "value": 100},
        )

    except Exception:
        dbg = traceback.format_exc()
        return (
            log_status,
            dbc_status,
            "Laden of decoderen mislukt. Kijk bij Debug / foutdetails voor de exacte fout.",
            dbg,
            no_update,
            no_update,
            [],
            [],
            {"label": "Fout tijdens laden/verwerken.", "value": 100},
        )


@app.callback(
    Output("summary-table", "data"),
    Input("store-dataset-id", "data"),
)
def update_summary_table(dataset_id):
    if not dataset_id or dataset_id not in DATASETS:
        return []
    df = DATASETS[dataset_id].summary_df.copy()
    if df.empty:
        return []
    df["frame_id_hex"] = df["frame_id"].apply(lambda x: f"0x{int(x):X}")
    return df[["frame_id_hex", "frame_id", "message_name", "count"]].to_dict("records")


@app.callback(
    Output("store-plot-groups", "data", allow_duplicate=True),
    Input("btn-build-groups", "n_clicks"),
    State("signal-dropdown", "value"),
    prevent_initial_call=True,
)
def build_groups_from_selection(_n_clicks, selected_labels):
    if not selected_labels:
        raise PreventUpdate
    return json.dumps(default_plot_groups(selected_labels))


@app.callback(
    Output("plot-groups-editor", "children"),
    Input("store-plot-groups", "data"),
    Input("signal-dropdown", "options"),
    Input("dropdown-height", "value"),
)
def render_plot_groups_editor(groups_json, signal_options, dropdown_height):
    if not groups_json:
        return html.Div("Nog geen plotgroepen aangemaakt.")

    groups = json.loads(groups_json)
    children = []
    for i, group in enumerate(groups):
        signals = group.get("signals", [])
        offsets = group.get("offsets") or {}
        offset_rows = [{"signal": sig, "offset": float(offsets.get(sig, 0.0) or 0.0)} for sig in signals]
        children.append(
            html.Div(
                [
                    html.Div(
                        [
                            html.B(f"Plotgroep {i + 1}"),
                            html.Div(
                                [
                                    html.Button("Omhoog", id={"type": "group-up", "index": i}, n_clicks=0),
                                    html.Button("Omlaag", id={"type": "group-down", "index": i}, n_clicks=0, style={"marginLeft": "6px"}),
                                    html.Button("Verwijder", id={"type": "group-delete", "index": i}, n_clicks=0, style={"marginLeft": "6px"}),
                                ],
                                style={"float": "right"},
                            ),
                        ],
                        style={"marginBottom": "6px"},
                    ),
                    dcc.Input(
                        id={"type": "group-title-edit", "index": i},
                        type="text",
                        value=group.get("title", ""),
                        placeholder="Titel van subplot",
                        debounce=True,
                        style={"width": "100%", "marginBottom": "6px"},
                    ),
                    dcc.Dropdown(
                        id={"type": "group-signals-edit", "index": i},
                        options=signal_options or [],
                        value=signals,
                        multi=True,
                        placeholder="Kies signalen voor deze subplot",
                        persistence=True,
                        persistence_type="local",
                        maxHeight=max(180, int(dropdown_height or 420)),
                        optionHeight=34,
                    ),
                    html.Div(
                        [
                            html.Div(
                                [
                                    dcc.Checklist(
                                        id={"type": "group-lock-edit", "index": i},
                                        options=[{"label": " Lock y-assen in subplot", "value": "lock"}],
                                        value=["lock"] if group.get("lock_y_axes") else [],
                                    ),
                                ],
                                style={"width": "32%", "display": "inline-block", "verticalAlign": "top"},
                            ),
                        ],
                        style={"marginTop": "8px"},
                    ),
                    html.Div("Offsets per lijn", style={"marginTop": "8px", "fontWeight": "bold"}),
                    dash_table.DataTable(
                        id={"type": "group-offset-table", "index": i},
                        columns=[
                            {"name": "Signaal", "id": "signal", "editable": False},
                            {"name": "Offset", "id": "offset", "editable": True, "type": "numeric"},
                        ],
                        data=offset_rows,
                        editable=True,
                        page_action="none",
                        style_table={"overflowX": "auto", "marginTop": "6px"},
                        style_cell={"textAlign": "left", "padding": "6px"},
                    ),
                ],
                style={"border": "1px solid #ccc", "borderRadius": "8px", "padding": "10px", "marginBottom": "10px", "backgroundColor": "#fafafa"},
            )
        )
    children.append(html.Button("Nieuwe lege plotgroep", id="btn-add-group", n_clicks=0))
    return children




@app.callback(
    Output("signal-dropdown", "maxHeight"),
    Input("dropdown-height", "value"),
)
def update_signal_dropdown_height(dropdown_height):
    return max(180, int(dropdown_height or 420))

@app.callback(
    Output("store-plot-groups", "data", allow_duplicate=True),
    Input("btn-add-group", "n_clicks"),
    Input({"type": "group-up", "index": dash.ALL}, "n_clicks"),
    Input({"type": "group-down", "index": dash.ALL}, "n_clicks"),
    Input({"type": "group-delete", "index": dash.ALL}, "n_clicks"),
    State("store-plot-groups", "data"),
    prevent_initial_call=True,
)
def mutate_plot_groups(_add_clicks, _up_clicks, _down_clicks, _delete_clicks, groups_json):
    if not groups_json:
        raise PreventUpdate
    groups = json.loads(groups_json)
    triggered = ctx.triggered_id

    if triggered == "btn-add-group":
        groups.append(make_plot_group())
        return json.dumps(groups)

    if isinstance(triggered, dict):
        idx = int(triggered["index"])
        action = triggered["type"]
        if action == "group-up" and idx > 0:
            groups[idx - 1], groups[idx] = groups[idx], groups[idx - 1]
        elif action == "group-down" and idx < len(groups) - 1:
            groups[idx + 1], groups[idx] = groups[idx], groups[idx + 1]
        elif action == "group-delete" and 0 <= idx < len(groups):
            groups.pop(idx)
        return json.dumps(groups)

    raise PreventUpdate


@app.callback(
    Output("store-plot-groups", "data", allow_duplicate=True),
    Input("btn-apply-groups", "n_clicks"),
    State({"type": "group-title-edit", "index": dash.ALL}, "value"),
    State({"type": "group-signals-edit", "index": dash.ALL}, "value"),
    State({"type": "group-lock-edit", "index": dash.ALL}, "value"),
    State({"type": "group-offset-table", "index": dash.ALL}, "data"),
    State("store-plot-groups", "data"),
    prevent_initial_call=True,
)
def sync_group_editor_to_store(_n, all_titles, all_signals, all_locks, all_offset_tables, groups_json):
    if not groups_json:
        raise PreventUpdate
    groups = json.loads(groups_json)
    n = min(len(groups), len(all_titles), len(all_signals), len(all_locks), len(all_offset_tables))
    for i in range(n):
        groups[i]["title"] = all_titles[i] or ""
        groups[i]["signals"] = all_signals[i] or []
        groups[i]["lock_y_axes"] = "lock" in (all_locks[i] or [])
        offsets = {}
        for row in (all_offset_tables[i] or []):
            sig = row.get("signal")
            if not sig:
                continue
            try:
                offsets[str(sig)] = float(row.get("offset", 0.0) or 0.0)
            except Exception:
                offsets[str(sig)] = 0.0
        groups[i]["offsets"] = offsets
    return json.dumps(groups)


def collect_selected_labels_from_groups(groups):
    seen = set()
    selected = []
    for group in groups or []:
        for label in group.get("signals") or []:
            if label not in seen:
                seen.add(label)
                selected.append(label)
    return selected


@app.callback(
    Output("download-preset", "data"),
    Input("btn-export-preset", "n_clicks"),
    State("store-plot-groups", "data"),
    State("frame-id-filter", "value"),
    State("time-start", "value"),
    State("time-end", "value"),
    State("max-points", "value"),
    State("subplot-height", "value"),
    State("dropdown-height", "value"),
    State("plot-options", "value"),
    prevent_initial_call=True,
)
def export_layout_preset(n_clicks, groups_json, frame_id_filter, time_start, time_end, max_points, subplot_height, dropdown_height, plot_options):
    if not n_clicks:
        raise PreventUpdate

    groups = json.loads(groups_json) if groups_json else []
    payload = {
        "preset_type": "can-log-viewer-layout",
        "version": 1,
        "saved_at": datetime.now().isoformat(timespec="seconds"),
        "plot_groups": groups,
        "view": {
            "frame_id_filter": frame_id_filter,
            "time_start": time_start,
            "time_end": time_end,
            "max_points": max_points,
            "subplot_height": subplot_height,
            "dropdown_height": dropdown_height,
            "plot_options": plot_options or [],
        },
    }
    return dict(content=json.dumps(payload, indent=2), filename="can_viewer_layout.json")


@app.callback(
    Output("store-plot-groups", "data", allow_duplicate=True),
    Output("signal-dropdown", "value", allow_duplicate=True),
    Output("frame-id-filter", "value", allow_duplicate=True),
    Output("time-start", "value", allow_duplicate=True),
    Output("time-end", "value", allow_duplicate=True),
    Output("max-points", "value", allow_duplicate=True),
    Output("subplot-height", "value", allow_duplicate=True),
    Output("dropdown-height", "value", allow_duplicate=True),
    Output("plot-options", "value", allow_duplicate=True),
    Output("preset-status", "children"),
    Input("upload-preset", "contents"),
    State("upload-preset", "filename"),
    prevent_initial_call=True,
)
def import_layout_preset(contents, filename):
    if not contents:
        raise PreventUpdate
    try:
        raw = b64_to_bytes(contents).decode("utf-8", errors="replace")
        payload = json.loads(raw)
        raw_groups = payload.get("plot_groups") or []
        groups = []
        for g in raw_groups:
            raw_offsets = g.get("offsets") or {}
            offsets = {}
            for key, value in raw_offsets.items():
                try:
                    offsets[str(key)] = float(value or 0.0)
                except Exception:
                    offsets[str(key)] = 0.0
            merged = make_plot_group(title=g.get("title", ""), signals=g.get("signals") or [], offsets=offsets)
            merged["lock_y_axes"] = bool(g.get("lock_y_axes"))
            groups.append(merged)
        view = payload.get("view") or {}
        selected = collect_selected_labels_from_groups(groups)
        return (
            json.dumps(groups),
            selected,
            view.get("frame_id_filter"),
            view.get("time_start"),
            view.get("time_end"),
            view.get("max_points", MAX_POINTS_PER_TRACE),
            view.get("subplot_height", 300),
            view.get("dropdown_height", 420),
            view.get("plot_options", []),
            f"Layout geïmporteerd: {filename}",
        )
    except Exception as exc:
        return (
            no_update,
            no_update,
            no_update,
            no_update,
            no_update,
            no_update,
            no_update,
            no_update,
            no_update,
            f"Import mislukt: {exc}",
        )




@app.callback(
    Output("store-axis-ranges", "data"),
    Input("signal-graph", "relayoutData"),
    State("store-axis-ranges", "data"),
    prevent_initial_call=True,
)
def capture_axis_ranges(relayout_data, existing):
    if not relayout_data:
        raise PreventUpdate

    current = dict(existing or {})
    updates = normalize_relayout_data(relayout_data)
    if updates:
        current.update(updates)

    for key, value in (relayout_data or {}).items():
        if key.endswith('.autorange') and value:
            axis = key.split('.')[0]
            current.pop(axis, None)

    return current

@app.callback(
    Output("signal-graph", "figure"),
    Input("store-dataset-id", "data"),
    Input("store-plot-groups", "data"),
    Input("frame-id-filter", "value"),
    Input("time-start", "value"),
    Input("time-end", "value"),
    Input("max-points", "value"),
    Input("subplot-height", "value"),
    Input("plot-options", "value"),
    State("store-axis-ranges", "data"),
)
def update_graph(dataset_id, plot_groups_json, frame_id_filter, time_start, time_end, max_points, subplot_height, plot_options, saved_ranges):
    if not dataset_id or dataset_id not in DATASETS:
        return make_empty_figure("Upload eerst een log en .dbc file.")
    if not plot_groups_json:
        return make_empty_figure("Maak eerst plotgroepen aan.")

    cache = DATASETS[dataset_id]
    plot_groups = json.loads(plot_groups_json)
    plot_groups = [g for g in plot_groups if (g.get("signals") or [])]
    if not plot_groups:
        return make_empty_figure("Geen signalen gekozen in de plotgroepen.")

    normalize = "normalize" in (plot_options or [])
    step = "step" in (plot_options or [])
    markers_only = "markers" in (plot_options or [])
    show_rangeslider = "rangeslider" in (plot_options or [])
    show_cursorline = "cursorline" in (plot_options or [])
    mode = "markers" if markers_only else "lines"
    line_shape = "hv" if step else "linear"
    hover_distance = 1 if not show_cursorline else -1
    max_points = int(max_points or MAX_POINTS_PER_TRACE)
    max_points = max(200, min(max_points, 20000))
    subplot_height = int(subplot_height or 300)
    subplot_height = max(160, min(subplot_height, 1200))

    frame_id_value = None
    if frame_id_filter:
        try:
            frame_id_value = parse_int_auto(frame_id_filter)
        except Exception:
            frame_id_value = None

    subplot_titles = [safe_group_title(group, i) for i, group in enumerate(plot_groups)]
    fig = make_subplots(
        rows=len(plot_groups),
        cols=1,
        shared_xaxes=True,
        vertical_spacing=0.04,
        subplot_titles=subplot_titles,
    )

    total_primary_axes = len(plot_groups)
    extra_axis_counter = 0
    trace_idx = 0
    row_to_entries = {}

    for row_idx, group in enumerate(plot_groups, start=1):
        signals = group.get("signals") or []
        offsets = group.get("offsets") or {}
        lock_y_axes = bool(group.get("lock_y_axes"))
        if signals:
            fig.update_yaxes(title_text=signals[0], row=row_idx, col=1)

        primary_yref = "y" if row_idx == 1 else f"y{row_idx}"
        xref = "x" if row_idx == 1 else f"x{row_idx}"

        row_entries = []
        for sig_idx, label in enumerate(signals):
            xy = cache.label_to_xy.get(label)
            if xy is None:
                continue
            x, y = xy

            if time_start is not None:
                mask = x >= float(time_start)
                x = x[mask]
                y = y[mask]
            if time_end is not None:
                mask = x <= float(time_end)
                x = x[mask]
                y = y[mask]
            if x.size == 0:
                continue

            if frame_id_value is not None and f"[0x{frame_id_value:X}]" not in label:
                continue

            if normalize:
                ymin = float(np.min(y))
                ymax = float(np.max(y))
                y_plot = (y - ymin) / (ymax - ymin) if ymax != ymin else np.zeros_like(y)
            else:
                y_plot = y.astype(np.float32, copy=False)

            offset_value = 0.0
            try:
                offset_value = float(offsets.get(label, 0.0) or 0.0)
            except Exception:
                offset_value = 0.0
            if offset_value != 0.0:
                y_plot = y_plot + np.float32(offset_value)

            x_plot, y_plot = downsample_minmax(x, y_plot, max_points=max_points)
            color = color_for_trace(trace_idx)

            trace = go.Scattergl(
                x=x_plot,
                y=y_plot,
                mode=mode,
                name=label,
                customdata=[[float(xx), float(yy)] for xx, yy in zip(x_plot.tolist(), y_plot.tolist())] if x_plot.size else None,
                meta={"label": label, "row": row_idx, "xaxis": xref, "yaxis": primary_yref if sig_idx == 0 else None, "color": color},
                line={"shape": line_shape, "color": color},
                hovertemplate=("%{fullData.name}: t=%{x:.3f}s<br>v=%{y:.6g}<extra></extra>"),
                showlegend=False,
            )
            fig.add_trace(trace, row=row_idx, col=1)
            row_entries.append((label, color))

            if sig_idx > 0:
                axis_num = total_primary_axes + extra_axis_counter + 1
                axis_key = f"yaxis{axis_num}"
                axis_ref = f"y{axis_num}"
                pos = max(0.82, 1.0 - 0.05 * (sig_idx - 1))
                axis_dict = dict(
                    overlaying=primary_yref,
                    anchor=xref,
                    side="right",
                    position=pos,
                    showgrid=False,
                    zeroline=False,
                    title=label,
                    color=color,
                )
                if lock_y_axes:
                    axis_dict["matches"] = primary_yref
                fig.layout[axis_key] = axis_dict
                fig.data[trace_idx].update(yaxis=axis_ref, meta={**(fig.data[trace_idx].meta or {}), "yaxis": axis_ref})
                extra_axis_counter += 1

            trace_idx += 1

        row_to_entries[row_idx] = row_entries

    fig.update_layout(
        template="plotly_white",
        title="CAN signalen",
        height=max(420, subplot_height * len(plot_groups)),
        hovermode=("x unified" if show_cursorline else "closest"),
        hoversubplots="axis",
        hoverdistance=(-1 if show_cursorline else hover_distance),
        spikedistance=(-1 if show_cursorline else hover_distance),
        showlegend=False,
        margin=dict(l=70, r=320, t=60, b=80),
        uirevision=f"{dataset_id}-stable",
    )

    if show_cursorline:
        fig.update_xaxes(
            showspikes=True,
            spikemode='across',
            spikesnap='cursor',
            spikecolor='#666',
            spikethickness=1,
            spikedash='dot',
        )
    else:
        fig.update_xaxes(showspikes=False)

    last_xaxis = "xaxis" if len(plot_groups) == 1 else f"xaxis{len(plot_groups)}"
    fig.layout[last_xaxis].title = "Tijd [s]"
    fig.layout[last_xaxis].rangeslider = dict(visible=show_rangeslider)
    apply_saved_ranges(fig, saved_ranges, len(plot_groups))
    add_subplot_legends(fig, row_to_entries, len(plot_groups))
    fig.update_layout(meta={
        "base_annotation_count": len(fig.layout.annotations) if fig.layout.annotations else 0,
        "base_shape_count": len(fig.layout.shapes) if fig.layout.shapes else 0,
        "show_cursorline": show_cursorline,
        "row_count": len(plot_groups),
    })
    return fig



app.clientside_callback(
    """
    function(hoverData, plotOptions, fig) {
        if (!fig) {
            return window.dash_clientside.no_update;
        }
        const out = JSON.parse(JSON.stringify(fig));
        const meta = out.layout && out.layout.meta ? out.layout.meta : {};
        const baseAnn = meta.base_annotation_count || 0;
        const baseShape = meta.base_shape_count || 0;
        out.layout.annotations = (out.layout.annotations || []).slice(0, baseAnn);
        out.layout.shapes = (out.layout.shapes || []).slice(0, baseShape);

        const showCursor = (plotOptions || []).includes('cursorline');
        if (!showCursor || !hoverData || !hoverData.points || !hoverData.points.length) {
            return out;
        }

        const x = hoverData.points[0].x;
        const rowCount = meta.row_count || 1;
        const rowDomains = {};
        for (let row = 1; row <= rowCount; row++) {
            const key = row === 1 ? 'yaxis' : `yaxis${row}`;
            const axis = out.layout[key];
            if (axis && axis.domain) {
                rowDomains[row] = axis.domain;
                const xref = row === 1 ? 'x' : `x${row}`;
                out.layout.shapes.push({
                    type: 'line',
                    xref: xref,
                    yref: 'paper',
                    x0: x, x1: x,
                    y0: axis.domain[0], y1: axis.domain[1],
                    line: {color: '#666', width: 1, dash: 'dot'}
                });
            }
        }

        const byRow = {};
        for (const trace of (out.data || [])) {
            const tmeta = trace.meta || {};
            const row = tmeta.row || 1;
            const xs = trace.x || [];
            const ys = trace.y || [];
            if (!xs.length || !ys.length) continue;

            let lo = 0, hi = xs.length - 1;
            while (lo < hi) {
                const mid = Math.floor((lo + hi) / 2);
                if (xs[mid] < x) lo = mid + 1; else hi = mid;
            }
            let idx = lo;
            if (idx > 0 && Math.abs(xs[idx] - x) >= Math.abs(xs[idx - 1] - x)) idx = idx - 1;
            if (idx < 0 || idx >= ys.length) continue;

            if (!byRow[row]) byRow[row] = [];
            byRow[row].push({
                x: xs[idx],
                y: ys[idx],
                name: trace.name || '',
                color: (tmeta.color || (trace.line && trace.line.color) || '#444'),
                yaxis: tmeta.yaxis || (row === 1 ? 'y' : `y${row}`)
            });
        }

        Object.keys(byRow).forEach(function(rowKey) {
            const row = parseInt(rowKey);
            const items = byRow[row] || [];
            const domain = rowDomains[row];
            if (!domain || !items.length) return;

            items.sort(function(a,b){ return b.y - a.y; });
            const yTop = domain[1] - 0.04;
            const yBottom = domain[0] + 0.03;
            const step = items.length > 1 ? Math.min(0.04, Math.max(0.018, (yTop - yBottom) / items.length)) : 0.02;

            items.forEach(function(item, i) {
                const ay = Math.max(yBottom, yTop - i * step);
                out.layout.annotations.push({
                    x: x,
                    y: item.y,
                    xref: row === 1 ? 'x' : `x${row}`,
                    yref: item.yaxis,
                    text: `${item.name}<br>t=${Number(item.x).toFixed(3)}s<br>v=${Number(item.y).toFixed(6)}`,
                    showarrow: true,
                    arrowhead: 2,
                    ax: 55,
                    ay: 0,
                    bgcolor: item.color,
                    bordercolor: item.color,
                    font: {color: 'white', size: 10},
                    align: 'left'
                });
            });
        });

        return out;
    }
    """,
    Output("signal-graph", "figure", allow_duplicate=True),
    Input("signal-graph", "hoverData"),
    Input("plot-options", "value"),
    State("signal-graph", "figure"),
    prevent_initial_call=True,
)


@app.callback(
    Output("download-decoded-csv", "data"),
    Input("btn-export", "n_clicks"),
    State("store-dataset-id", "data"),
    prevent_initial_call=True,
)
def export_csv(n_clicks, dataset_id):
    if not n_clicks or not dataset_id or dataset_id not in DATASETS:
        raise PreventUpdate
    df = DATASETS[dataset_id].decoded_df
    return dcc.send_data_frame(df.to_csv, "decoded_can_signals.csv", index=False)


if __name__ == "__main__":
    app.run(debug=False, dev_tools_hot_reload=False)
