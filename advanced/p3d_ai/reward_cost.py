from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Any

import numpy as np

from .observation import boolean_value, heading_error

PHASE_RANK = {
    "Disarmed": 0, "Armed": 1, "TakeoffRoll": 2, "Rotate": 3,
    "InitialClimb": 4, "Crosswind": 5, "Downwind": 6, "Base": 7,
    "Final": 8, "Flare": 9, "Rollout": 10, "Completed": 11,
    "GoAround": 4, "Failsafe": -1,
}


@dataclass(frozen=True)
class RewardCost:
    reward: float
    costs: dict[str, float]
    components: dict[str, float]

    @property
    def unsafe(self) -> bool:
        return any(value > 0.0 for value in self.costs.values())


def _square_clip(value: float, scale: float, maximum: float = 4.0) -> float:
    return min((value / max(scale, 1e-9)) ** 2, maximum)


def compute(
    previous: Mapping[str, Any],
    current: Mapping[str, Any],
    action: Sequence[float],
    previous_action: Sequence[float],
) -> RewardCost:
    action_array = np.asarray(action, dtype=np.float64)
    previous_array = np.asarray(previous_action, dtype=np.float64)
    if action_array.shape != (4,) or previous_array.shape != (4,):
        raise ValueError("actions must have shape (4,)")
    if not np.isfinite(action_array).all() or not np.isfinite(previous_array).all():
        raise ValueError("actions must be finite")
    phase = str(current.get("phase", "Disarmed"))
    h_error = abs(heading_error(float(current.get("target_heading_deg", 0.0)), float(current.get("heading_deg", 0.0))))
    ias_error = abs(float(current.get("target_ias_kts", 0.0)) - float(current.get("ias_kts", 0.0)))
    altitude_error = abs(float(current.get("target_alt_msl_ft", 0.0)) - float(current.get("alt_msl_ft", 0.0)))
    vs_error = abs(float(current.get("target_vs_fpm", 0.0)) - float(current.get("vs_fpm", 0.0)))
    cross = abs(float(current.get("cross_m", 0.0))) if phase in {"Final", "Flare", "Rollout"} else 0.0
    previous_rank = PHASE_RANK.get(str(previous.get("phase", "Disarmed")), 0)
    current_rank = PHASE_RANK.get(phase, 0)
    phase_progress = float(np.clip(current_rank - previous_rank, -1, 1))
    if phase == "GoAround":
        # A validated go-around is a safety action, not backwards progress.
        phase_progress = 0.5 if str(previous.get("phase", "")) != "GoAround" else 0.0
    components = {
        "alive": 0.05,
        "tracking_heading": -1.2 * _square_clip(h_error, 20.0),
        "tracking_speed": -1.2 * _square_clip(ias_error, 12.0),
        "tracking_altitude": -0.8 * _square_clip(altitude_error, 300.0),
        "tracking_vertical_speed": -0.6 * _square_clip(vs_error, 600.0),
        "final_cross_track": -1.8 * _square_clip(cross, 45.0),
        "control_effort": -0.08 * float(np.square(action_array).mean()),
        "control_smoothness": -0.15 * float(np.square(action_array - previous_array).mean()),
        "phase_progress": 4.0 * phase_progress,
        "success": 500.0 if boolean_value(current.get("success", False), "success") else 0.0,
    }
    bank = abs(float(current.get("bank_deg", 0.0)))
    pitch = float(current.get("pitch_deg", 0.0))
    sink = -float(current.get("vs_fpm", 0.0))
    touchdown = boolean_value(current.get("on_ground", False), "on_ground") and not boolean_value(
        previous.get("on_ground", False), "previous.on_ground"
    )
    touchdown_sink = -float(previous.get("vs_fpm", current.get("vs_fpm", 0.0))) if touchdown else 0.0
    runtime_values = (h_error, ias_error, altitude_error, vs_error, cross, bank, pitch, sink, touchdown_sink)
    if not np.isfinite(runtime_values).all():
        raise ValueError("reward telemetry must be finite")
    costs = {
        "stall": float(boolean_value(current.get("stall", False), "stall")),
        "overspeed": float(boolean_value(current.get("overspeed", False), "overspeed")),
        "attitude": float(bank > 35.0 or pitch > 22.0 or pitch < -15.0),
        "unstable_approach": float(phase in {"Final", "Flare"} and (cross > 60.0 or ias_error > 15.0 or sink > 900.0)),
        "runway_excursion": float(phase in {"TakeoffRoll", "Rollout"} and abs(float(current.get("cross_m", 0.0))) > 30.0),
        "hard_landing": float(touchdown and touchdown_sink > 600.0),
        "shield_intervention": float(
            boolean_value(current.get("shield_intervened", False), "shield_intervened")
            or bool(current.get("residual_rejection", ""))
        ),
        "invalid_runtime": float(
            boolean_value(current.get("paused", False), "paused")
            or boolean_value(current.get("slew", False), "slew")
        ),
    }
    return RewardCost(float(sum(components.values())), costs, components)


def lagrangian_objective(result: RewardCost, multipliers: Mapping[str, float]) -> float:
    values = {name: float(multipliers.get(name, 0.0)) for name in result.costs}
    if any(not np.isfinite(value) or value < 0.0 for value in values.values()):
        raise ValueError("cost multipliers must be finite and non-negative")
    penalty = sum(values[name] * value for name, value in result.costs.items())
    return result.reward - penalty
