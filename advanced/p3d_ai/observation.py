from __future__ import annotations

from collections import deque
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Any

import numpy as np

PHASES = (
    "Disarmed", "Armed", "TakeoffRoll", "Rotate", "InitialClimb", "Crosswind",
    "Downwind", "Base", "Final", "Flare", "Rollout", "GoAround", "Completed", "Failsafe",
)

CONTINUOUS_FEATURES = (
    "ias_kts", "tas_kts", "gs_kts", "alt_agl_ft", "alt_msl_ft", "vs_fpm",
    "pitch_deg", "bank_deg", "heading_error_deg", "aoa_deg", "beta_deg",
    "body_rate_p_dps", "body_rate_q_dps", "body_rate_r_dps",
    "accel_x_g", "accel_y_g", "accel_z_g", "wind_speed_kts", "wind_cross_kts",
    "along_m", "cross_m", "glide_error_ft", "distance_threshold_m",
    "target_ias_kts", "target_alt_msl_ft", "target_vs_fpm", "target_bank_deg",
    "rpm", "fuel_fraction", "weight_fraction", "cg_fraction",
    "pilot_yoke_x", "pilot_yoke_y", "pilot_rudder", "pilot_throttle",
    "baseline_aileron", "baseline_elevator", "baseline_rudder", "baseline_throttle",
    "applied_aileron", "applied_elevator", "applied_rudder", "applied_throttle",
    "previous_action_aileron", "previous_action_elevator", "previous_action_rudder",
    "previous_action_throttle", "safety_scale", "uncertainty", "bridge_latency_ms",
)

BOOLEAN_FEATURES = ("on_ground", "stall", "overspeed", "paused", "slew")

DEFAULT_SCALE = {
    "ias_kts": 200.0, "tas_kts": 200.0, "gs_kts": 200.0,
    "alt_agl_ft": 2500.0, "alt_msl_ft": 10000.0, "vs_fpm": 1800.0,
    "pitch_deg": 30.0, "bank_deg": 45.0, "heading_error_deg": 180.0,
    "aoa_deg": 20.0, "beta_deg": 15.0,
    "body_rate_p_dps": 60.0, "body_rate_q_dps": 40.0, "body_rate_r_dps": 40.0,
    "accel_x_g": 2.0, "accel_y_g": 2.0, "accel_z_g": 3.0,
    "wind_speed_kts": 40.0, "wind_cross_kts": 30.0,
    "along_m": 5000.0, "cross_m": 2500.0, "glide_error_ft": 1000.0,
    "distance_threshold_m": 5000.0, "target_ias_kts": 200.0,
    "target_alt_msl_ft": 10000.0, "target_vs_fpm": 1800.0,
    "target_bank_deg": 30.0, "rpm": 3000.0,
    "fuel_fraction": 1.0, "weight_fraction": 1.0, "cg_fraction": 1.0,
    "pilot_yoke_x": 1.0, "pilot_yoke_y": 1.0, "pilot_rudder": 1.0,
    "pilot_throttle": 1.0, "baseline_aileron": 1.0, "baseline_elevator": 1.0,
    "baseline_rudder": 1.0, "baseline_throttle": 1.0, "applied_aileron": 1.0,
    "applied_elevator": 1.0, "applied_rudder": 1.0, "applied_throttle": 1.0,
    "previous_action_aileron": 1.0, "previous_action_elevator": 1.0,
    "previous_action_rudder": 1.0, "previous_action_throttle": 1.0,
    "safety_scale": 1.0, "uncertainty": 1.0, "bridge_latency_ms": 500.0,
}


def heading_error(target: float, actual: float) -> float:
    return (target - actual + 180.0) % 360.0 - 180.0


def feature_names() -> tuple[str, ...]:
    return CONTINUOUS_FEATURES + BOOLEAN_FEATURES + tuple(f"phase_{name}" for name in PHASES)


def boolean_value(value: Any, name: str) -> bool:
    if isinstance(value, (bool, np.bool_)):
        return bool(value)
    if isinstance(value, (int, float, np.integer, np.floating)):
        numeric = float(value)
        if np.isfinite(numeric) and numeric in {0.0, 1.0}:
            return bool(numeric)
    raise ValueError(f"{name} must be a boolean")


def _command(raw: Mapping[str, Any], name: str, index: int) -> float:
    values = raw.get(name, [0.0, 0.0, 0.0, 0.0])
    array = np.asarray(values, dtype=np.float64)
    if array.shape != (4,) or not np.isfinite(array).all():
        raise ValueError(f"{name} must contain four finite values")
    return float(array[index])


def raw_features(raw: Mapping[str, Any], previous_action: Sequence[float] | None = None) -> dict[str, float]:
    if previous_action is None:
        previous_action = (0.0, 0.0, 0.0, 0.0)
    previous_action = np.asarray(previous_action, dtype=np.float64)
    if previous_action.shape != (4,) or not np.isfinite(previous_action).all():
        raise ValueError("previous_action must contain four finite values")
    phase = str(raw.get("phase", "Disarmed"))
    if phase not in PHASES:
        raise ValueError(f"unknown flight phase: {phase}")
    values: dict[str, float] = {
        "ias_kts": float(raw.get("ias_kts", 0.0)),
        "tas_kts": float(raw.get("tas_kts", raw.get("ias_kts", 0.0))),
        "gs_kts": float(raw.get("gs_kts", 0.0)),
        "alt_agl_ft": float(raw.get("alt_agl_ft", 0.0)),
        "alt_msl_ft": float(raw.get("alt_msl_ft", 0.0)),
        "vs_fpm": float(raw.get("vs_fpm", 0.0)),
        "pitch_deg": float(raw.get("pitch_deg", 0.0)),
        "bank_deg": float(raw.get("bank_deg", 0.0)),
        "heading_error_deg": heading_error(float(raw.get("target_heading_deg", 0.0)), float(raw.get("heading_deg", 0.0))),
        "aoa_deg": float(raw.get("aoa_deg", 0.0)),
        "beta_deg": float(raw.get("beta_deg", 0.0)),
        "body_rate_p_dps": float(raw.get("body_rate_p_dps", 0.0)),
        "body_rate_q_dps": float(raw.get("body_rate_q_dps", 0.0)),
        "body_rate_r_dps": float(raw.get("body_rate_r_dps", 0.0)),
        "accel_x_g": float(raw.get("accel_x_g", 0.0)),
        "accel_y_g": float(raw.get("accel_y_g", 0.0)),
        "accel_z_g": float(raw.get("accel_z_g", 1.0)),
        "wind_speed_kts": float(raw.get("wind_speed_kts", 0.0)),
        "wind_cross_kts": float(raw.get("wind_cross_kts", 0.0)),
        "along_m": float(raw.get("along_m", 0.0)),
        "cross_m": float(raw.get("cross_m", 0.0)),
        "glide_error_ft": float(raw.get("glide_error_ft", 0.0)),
        "distance_threshold_m": float(raw.get("distance_threshold_m", 0.0)),
        "target_ias_kts": float(raw.get("target_ias_kts", 0.0)),
        "target_alt_msl_ft": float(raw.get("target_alt_msl_ft", 0.0)),
        "target_vs_fpm": float(raw.get("target_vs_fpm", 0.0)),
        "target_bank_deg": float(raw.get("target_bank_deg", 0.0)),
        "rpm": float(raw.get("rpm", 0.0)),
        "fuel_fraction": float(raw.get("fuel_fraction", 1.0)),
        "weight_fraction": float(raw.get("weight_fraction", 1.0)),
        "cg_fraction": float(raw.get("cg_fraction", 0.5)),
        "pilot_yoke_x": float(raw.get("pilot_yoke_x", 0.0)),
        "pilot_yoke_y": float(raw.get("pilot_yoke_y", 0.0)),
        "pilot_rudder": float(raw.get("pilot_rudder", 0.0)),
        "pilot_throttle": float(raw.get("pilot_throttle", 0.0)),
        "baseline_aileron": _command(raw, "baseline", 0),
        "baseline_elevator": _command(raw, "baseline", 1),
        "baseline_rudder": _command(raw, "baseline", 2),
        "baseline_throttle": _command(raw, "baseline", 3),
        "applied_aileron": _command(raw, "applied_residual", 0),
        "applied_elevator": _command(raw, "applied_residual", 1),
        "applied_rudder": _command(raw, "applied_residual", 2),
        "applied_throttle": _command(raw, "applied_residual", 3),
        "previous_action_aileron": float(previous_action[0]),
        "previous_action_elevator": float(previous_action[1]),
        "previous_action_rudder": float(previous_action[2]),
        "previous_action_throttle": float(previous_action[3]),
        "safety_scale": float(raw.get("safety_scale", 0.0)),
        "uncertainty": float(raw.get("uncertainty", 0.0)),
        "bridge_latency_ms": float(raw.get("bridge_latency_ms", 0.0)),
        "on_ground": float(boolean_value(raw.get("on_ground", False), "on_ground")),
        "stall": float(boolean_value(raw.get("stall", False), "stall")),
        "overspeed": float(boolean_value(raw.get("overspeed", False), "overspeed")),
        "paused": float(boolean_value(raw.get("paused", False), "paused")),
        "slew": float(boolean_value(raw.get("slew", False), "slew")),
    }
    values.update({f"phase_{name}": float(phase == name) for name in PHASES})
    return values


def encode(raw: Mapping[str, Any], previous_action: Sequence[float] | None = None) -> np.ndarray:
    values = raw_features(raw, previous_action)
    vector = []
    for name in CONTINUOUS_FEATURES:
        vector.append(values[name] / DEFAULT_SCALE[name])
    vector.extend(values[name] for name in BOOLEAN_FEATURES)
    vector.extend(values[f"phase_{name}"] for name in PHASES)
    result = np.asarray(vector, dtype=np.float32)
    if result.shape != (len(feature_names()),):
        raise ValueError(f"observation shape mismatch: {result.shape}")
    if not np.isfinite(result).all():
        raise ValueError("observation contains NaN or infinity")
    return np.clip(result, -10.0, 10.0)


@dataclass
class FrameStack:
    frames: int = 5

    def __post_init__(self) -> None:
        if self.frames < 1:
            raise ValueError("frames must be positive")
        self._values: deque[np.ndarray] = deque(maxlen=self.frames)

    def reset(self, first: np.ndarray) -> np.ndarray:
        first = np.asarray(first, dtype=np.float32)
        if first.shape != (len(feature_names()),) or not np.isfinite(first).all():
            raise ValueError("initial frame is invalid")
        self._values.clear()
        for _ in range(self.frames):
            self._values.append(first.copy())
        return self.value

    def append(self, value: np.ndarray) -> np.ndarray:
        value = np.asarray(value, dtype=np.float32)
        if value.shape != (len(feature_names()),) or not np.isfinite(value).all():
            raise ValueError("frame is invalid")
        self._values.append(value.copy())
        return self.value

    @property
    def value(self) -> np.ndarray:
        if not self._values:
            raise RuntimeError("frame stack is empty")
        return np.concatenate(tuple(self._values)).astype(np.float32, copy=False)
