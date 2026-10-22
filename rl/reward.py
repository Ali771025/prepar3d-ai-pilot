from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import numpy as np

PHASE_RANK = {
    "Disarmed": 0,
    "Armed": 1,
    "TakeoffRoll": 2,
    "Rotate": 3,
    "InitialClimb": 4,
    "Crosswind": 5,
    "Downwind": 6,
    "Base": 7,
    "Final": 8,
    "Flare": 9,
    "Rollout": 10,
    "Completed": 11,
    "GoAround": 4,
    "Failsafe": -1,
}


def heading_error(target: float, actual: float) -> float:
    return (target - actual + 180.0) % 360.0 - 180.0


@dataclass(frozen=True)
class RewardWeights:
    heading: float
    speed: float
    altitude: float
    vertical_speed: float
    cross_track: float
    attitude: float
    action_magnitude: float
    action_smoothness: float
    shield_rejection: float


WEIGHTS = {
    "takeoff": RewardWeights(1.8, 0.8, 0.0, 0.7, 2.0, 0.8, 0.12, 0.25, 0.8),
    "airwork": RewardWeights(1.2, 1.0, 1.0, 0.5, 0.1, 0.5, 0.08, 0.12, 0.4),
    "pattern": RewardWeights(1.2, 1.0, 0.9, 0.5, 0.5, 0.5, 0.10, 0.15, 0.5),
    "approach": RewardWeights(1.5, 1.5, 1.1, 1.2, 1.8, 0.8, 0.12, 0.20, 0.7),
    "full": RewardWeights(1.3, 1.2, 1.0, 0.8, 1.0, 0.6, 0.10, 0.16, 0.6),
}


def _boolean_value(value: Any, name: str) -> bool:
    if isinstance(value, (bool, np.bool_)):
        return bool(value)
    if isinstance(value, (int, float, np.integer, np.floating)):
        numeric = float(value)
        if np.isfinite(numeric) and numeric in {0.0, 1.0}:
            return bool(numeric)
    raise ValueError(f"{name} must be a boolean")


def compute_reward(
    previous: dict[str, Any],
    current: dict[str, Any],
    action: np.ndarray,
    previous_action: np.ndarray,
    stage: str,
) -> tuple[float, dict[str, float]]:
    if stage not in WEIGHTS:
        raise ValueError(f"unknown reward stage: {stage}")
    action = np.asarray(action, dtype=np.float64)
    previous_action = np.asarray(previous_action, dtype=np.float64)
    if action.shape != (4,) or previous_action.shape != (4,):
        raise ValueError("actions must have shape (4,)")
    if not np.isfinite(action).all() or not np.isfinite(previous_action).all():
        raise ValueError("actions must be finite")
    weights = WEIGHTS[stage]
    h_error = abs(heading_error(float(current["target_heading_deg"]), float(current["heading_deg"])))
    speed_error = abs(float(current["target_ias_kts"]) - float(current["ias_kts"]))
    altitude_error = abs(float(current["target_alt_msl_ft"]) - float(current["alt_msl_ft"]))
    target_vs = float(current["target_vs_fpm"])
    if abs(target_vs) < 1.0:
        target_vs = np.clip((float(current["target_alt_msl_ft"]) - float(current["alt_msl_ft"])) * 1.8, -650.0, 650.0)
    vertical_speed_error = abs(target_vs - float(current["vs_fpm"]))
    cross_track = abs(float(current["cross_m"]))
    phase = str(current["phase"])
    runtime_values = (h_error, speed_error, altitude_error, target_vs, vertical_speed_error, cross_track,
                      float(current["bank_deg"]), float(current["pitch_deg"]))
    if not np.isfinite(runtime_values).all():
        raise ValueError("reward telemetry must be finite")

    if stage == "takeoff":
        cross_cost = min((cross_track / 22.5) ** 2, 4.0)
    elif phase in {"Final", "Flare", "Rollout"}:
        cross_cost = min((cross_track / 60.0) ** 2, 4.0)
    elif phase == "Base":
        cross_cost = min((cross_track / 800.0) ** 2, 4.0)
    else:
        cross_cost = 0.0
    components = {
        "heading": -weights.heading * min((h_error / 25.0) ** 2, 4.0),
        "speed": -weights.speed * min((speed_error / 15.0) ** 2, 4.0),
        "altitude": -weights.altitude * min((altitude_error / 350.0) ** 2, 4.0),
        "vertical_speed": -weights.vertical_speed * min((vertical_speed_error / 700.0) ** 2, 4.0),
        "cross_track": -weights.cross_track * cross_cost,
        "attitude": -weights.attitude
        * (min((abs(float(current["bank_deg"])) / 30.0) ** 4, 4.0)
           + min((abs(float(current["pitch_deg"])) / 18.0) ** 4, 4.0)),
        "action_magnitude": -weights.action_magnitude * float(np.square(action).mean()),
        "action_smoothness": -weights.action_smoothness * float(np.square(action - previous_action).mean()),
        "shield": -weights.shield_rejection
        if (current.get("shield_intervened") or current.get("residual_rejection")) and np.linalg.norm(action) > 0.05 else 0.0,
    }

    previous_rank = PHASE_RANK.get(str(previous.get("phase", "Disarmed")), 0)
    current_rank = PHASE_RANK.get(phase, 0)
    progress = float(np.clip(current_rank - previous_rank, -1, 1))
    if phase == "GoAround":
        progress = 0.0
    components["phase_progress"] = progress * 4.0
    components["go_around"] = 2.0 if phase == "GoAround" and str(previous.get("phase")) != "GoAround" else 0.0
    components["stall"] = -150.0 if _boolean_value(current["stall"], "stall") else 0.0
    components["overspeed"] = -100.0 if _boolean_value(current["overspeed"], "overspeed") else 0.0
    components["failsafe"] = -250.0 if phase == "Failsafe" else 0.0
    components["success"] = 500.0 if _boolean_value(current.get("success", False), "success") else 0.0
    return float(sum(components.values())), components


def unsafe_terminal(observation: dict[str, Any]) -> bool:
    try:
        bank = float(observation["bank_deg"])
        pitch = float(observation["pitch_deg"])
    except (KeyError, TypeError, ValueError):
        return True
    if not np.isfinite([bank, pitch]).all():
        return True
    try:
        terminal = _boolean_value(observation.get("terminal", False), "terminal")
        stall = _boolean_value(observation.get("stall", False), "stall")
        overspeed = _boolean_value(observation.get("overspeed", False), "overspeed")
    except ValueError:
        return True
    return bool(
        terminal
        or stall
        or overspeed
        or abs(bank) > 45.0
        or pitch > 28.0
        or pitch < -20.0
    )
