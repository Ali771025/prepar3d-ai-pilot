from __future__ import annotations

import json
import math
from collections.abc import Iterable
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np
import pandas as pd

from .contracts import EPISODE_VERSION, SUPPORTED_EPISODE_VERSIONS, sha256_file

REQUIRED_COLUMNS = {
    "schema_version", "telemetry_source", "utc", "sim_time_s", "phase", "ias_kts", "alt_agl_ft", "vs_fpm",
    "pitch_deg", "bank_deg", "heading_deg", "on_ground", "stall", "overspeed",
    "pilot_yoke_x", "pilot_yoke_y", "pilot_rudder", "pilot_throttle",
    "cmd_aileron", "cmd_elevator", "cmd_rudder", "cmd_throttle",
}


def boolean_series(values: pd.Series) -> pd.Series:
    if pd.api.types.is_bool_dtype(values):
        return values.fillna(False)
    if pd.api.types.is_numeric_dtype(values):
        numbers=pd.to_numeric(values,errors="coerce")
        if not numbers.isin([0,1]).all():raise ValueError("boolean columns must contain only 0 or 1")
        return numbers.eq(1)
    normalized = values.fillna("").astype(str).str.strip().str.lower()
    unknown = ~normalized.isin({"1", "0", "true", "false", "yes", "no", ""})
    if unknown.any():
        raise ValueError(f"unrecognized boolean values: {sorted(normalized[unknown].unique())}")
    return normalized.isin({"1", "true", "yes"})


@dataclass(frozen=True)
class EpisodeQuality:
    path: str
    rows: int
    missing_fraction: float
    non_finite_cells: int
    timestamp_regressions: int
    time_gap_count: int
    source_switches: int
    safe_rows_fraction: float
    accepted: bool
    reasons: tuple[str, ...]


def inspect_episode(path: Path, expected_hz: float = 25.0) -> EpisodeQuality:
    if not math.isfinite(expected_hz) or expected_hz <= 0.0:
        raise ValueError("expected_hz must be finite and positive")
    frame = pd.read_csv(path)
    missing = REQUIRED_COLUMNS.difference(frame.columns)
    if missing:
        return EpisodeQuality(str(path), len(frame), 1.0, 0, 0, 0, 0, 0.0, False, (f"missing:{','.join(sorted(missing))}",))
    numeric = frame[sorted(REQUIRED_COLUMNS-{"schema_version","telemetry_source","utc","phase"})].apply(pd.to_numeric,errors="coerce")
    non_finite = int((~np.isfinite(numeric.to_numpy(dtype=np.float64, copy=True))).sum()) if not numeric.empty else 0
    missing_fraction = float(frame[list(REQUIRED_COLUMNS)].isna().sum().sum() / max(1, len(frame)*len(REQUIRED_COLUMNS)))
    groups = list(frame.groupby("episode_id",sort=False)) if "episode_id" in frame else [(path.stem,frame)]
    delta = np.concatenate([np.diff(pd.to_numeric(group["sim_time_s"],errors="coerce").to_numpy(np.float64)) for _,group in groups]) if groups else np.array([])
    timestamp_regressions = int(np.sum(delta <= 0.0))
    expected_period = 1.0 / expected_hz
    time_gap_count = int(np.sum(delta > expected_period * 3.0))
    sources = frame["telemetry_source"].fillna("").astype(str).str.strip()
    source_switches = int((sources != sources.shift(1)).sum() - (1 if len(sources) else 0))
    safe = (~boolean_series(frame["stall"])) & (~boolean_series(frame["overspeed"]))
    safe_rows_fraction = float(safe.mean()) if len(frame) else 0.0
    reasons: list[str] = []
    schemas = set(frame["schema_version"].fillna("").astype(str))
    if not schemas or not schemas.issubset(SUPPORTED_EPISODE_VERSIONS):
        reasons.append("schema-version")
    if not len(sources) or (sources == "").any():
        reasons.append("telemetry-source")
    if source_switches:
        reasons.append("telemetry-source-switch")
    if len(frame) < 100:
        reasons.append("too-few-rows")
    if missing_fraction > 0.001:
        reasons.append("missing-fraction")
    if non_finite:
        reasons.append("non-finite")
    if timestamp_regressions:
        reasons.append("timestamp-regression")
    if time_gap_count:
        reasons.append("time-gap")
    if safe_rows_fraction < 1.0:
        reasons.append("unsafe-rows")
    return EpisodeQuality(str(path), len(frame), missing_fraction, non_finite, timestamp_regressions,
                          time_gap_count, source_switches, safe_rows_fraction, not reasons, tuple(reasons))


def build_manifest(paths: Iterable[Path], output: Path, expected_hz: float = 25.0) -> dict:
    results = [inspect_episode(path, expected_hz) for path in paths]
    payload = {
        "episode_schema": EPISODE_VERSION,
        "expected_hz": expected_hz,
        "episodes": [dict(asdict(item), sha256=sha256_file(Path(item.path))) for item in results],
        "accepted": sum(item.accepted for item in results),
        "rejected": sum(not item.accepted for item in results),
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")
    return payload


def split_by_episode(paths: list[Path], seed: int = 42, validation: float = 0.15, test: float = 0.15) -> dict[str, list[str]]:
    if not paths:
        raise ValueError("no episodes")
    if len({path.resolve() for path in paths}) != len(paths):
        raise ValueError("duplicate episode paths are not allowed")
    if not math.isfinite(validation) or not math.isfinite(test) or validation < 0.0 or test < 0.0 or validation + test >= 1.0:
        raise ValueError("validation and test fractions must be non-negative and sum to less than one")
    rng = np.random.default_rng(seed)
    shuffled = list(paths)
    rng.shuffle(shuffled)
    count = len(shuffled)
    test_count = max(1, round(count * test)) if count >= 3 else 0
    validation_count = max(1, round(count * validation)) if count >= 3 else 0
    if test_count + validation_count >= count:
        validation_count = max(0, count - test_count - 1)
    return {
        "test": [str(item) for item in shuffled[:test_count]],
        "validation": [str(item) for item in shuffled[test_count:test_count + validation_count]],
        "train": [str(item) for item in shuffled[test_count + validation_count:]],
    }


def convert_csv_to_parquet(source: Path, destination: Path) -> None:
    frame = pd.read_csv(source)
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        frame.to_parquet(destination, index=False)
    except ImportError as exc:
        raise RuntimeError("Install pyarrow from advanced/requirements.txt to write Parquet") from exc
